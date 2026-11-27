// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Runtime.InteropServices;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace PhotoLibrarian.Core.Services;

public sealed record VideoEditOptions(
    int ClockwiseQuarterTurns = 0,
    bool RemoveAudio = false,
    int Brightness = 0,
    int Contrast = 100,
    StabilizationOptions? Stabilization = null,
    TimeSpan? TrimStart = null,
    TimeSpan? TrimEnd = null,
    int PlaybackSpeedPercent = 100,
    string? SoundtrackPath = null);

public enum VideoPreviewPreparationResult
{
    Ready,
    UnsupportedSoundtrack
}

/// <summary>Prepares one local MP4; preview and save use those exact bytes.</summary>
public sealed class VideoEditingService : IDisposable
{
    public static IReadOnlyList<string> SoundtrackExtensions { get; } = Array.AsReadOnly(new[] { ".wav", ".mp3" });

    private static readonly Guid RotationKey = new("C380465D-2271-428C-9B83-ECEA3B4A85C1");
    private readonly StorageFile _source;
    private readonly MediaEncodingProfile _profile;
    private readonly VideoEditOptions _options;
    private readonly TimeSpan _sourceDuration;
    private readonly string _stagingPath = Path.Combine(Path.GetTempPath(), $"photolibrarian-edit-{Guid.NewGuid():N}.mp4");
    private string? _metadata;
    private bool _disposed;
    private bool _ownsStagingFile;
    internal string TemporaryPreviewPath => _stagingPath;
    public TimeSpan Duration { get; }
    public string? PreviewPath { get; private set; }

    private VideoEditingService(
        StorageFile source, TimeSpan sourceDuration, TimeSpan duration,
        MediaEncodingProfile profile, VideoEditOptions options)
    {
        _source = source;
        _sourceDuration = sourceDuration;
        Duration = duration;
        _profile = profile;
        _options = options;
    }

    public static async Task<VideoEditingService> CreateAsync(
        string sourcePath, VideoEditOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ClockwiseQuarterTurns is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.Brightness is < -100 or > 100)
            throw new ArgumentOutOfRangeException(nameof(options), "Brightness must be between -100 and 100.");
        if (options.Contrast is < 0 or > 200)
            throw new ArgumentOutOfRangeException(nameof(options), "Contrast must be between 0 and 200.");
        if (options.PlaybackSpeedPercent is not (50 or 75 or 100))
            throw new ArgumentOutOfRangeException(nameof(options),
                "Playback speed must be 50, 75, or 100 percent.");
        token.ThrowIfCancellationRequested();
        var source = await StorageFile.GetFileFromPathAsync(sourcePath).AsTask(token);
        var input = await MediaEncodingProfile.CreateFromFileAsync(source).AsTask(token);
        var profile = VideoStabilizationService.CreateOutputProfile(input);
        var properties = await source.Properties.GetVideoPropertiesAsync().AsTask(token);
        if (options.TrimStart is not null || options.TrimEnd is not null)
        {
            double framesPerSecond = input.Video.FrameRate.Numerator / (double)input.Video.FrameRate.Denominator;
            var selection = VideoTrimmingService.NormalizeSelection(
                options.TrimStart ?? TimeSpan.Zero, options.TrimEnd ?? properties.Duration,
                properties.Duration, framesPerSecond);
            options = options with { TrimStart = selection.Start, TrimEnd = selection.End };
        }
        double durationTicks = ((options.TrimEnd ?? properties.Duration) -
                                (options.TrimStart ?? TimeSpan.Zero)).Ticks *
                               (100d / options.PlaybackSpeedPercent);
        if (!double.IsFinite(durationTicks) || durationTicks > TimeSpan.MaxValue.Ticks)
            throw new ArgumentOutOfRangeException(nameof(options), "The edited video duration is too long.");
        var editedDuration = TimeSpan.FromTicks((long)Math.Round(durationTicks));
        // MP4 rotation is standard track metadata, not a resampling of the source pixels.
        // Keep encoded dimensions and aspect ratio; compose with the existing orientation.
        profile.Video.Properties[RotationKey] = ((uint)properties.Orientation + (uint)options.ClockwiseQuarterTurns * 90) % 360;
        if (options.RemoveAudio)
            profile.Audio = null;
        return new VideoEditingService(source, properties.Duration, editedDuration, profile, options);
    }

    public Task<VideoPreviewPreparationResult> PreparePreviewAsync(IProgress<double>? progress = null,
        CancellationToken token = default) => Task.Run(async () =>
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (PreviewPath is not null) return VideoPreviewPreparationResult.Ready;
        token.ThrowIfCancellationRequested();
        using var sourceLock = new FileStream(_source.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _metadata = await VideoDerivativeMetadata.ReadAsync(_source, token);
        bool preparedSuccessfully = false;
        try
        {
            string? soundtrackPath = _options.SoundtrackPath;
            if (soundtrackPath is not null && (!Path.IsPathFullyQualified(soundtrackPath) ||
                !SoundtrackExtensions.Contains(Path.GetExtension(soundtrackPath), StringComparer.OrdinalIgnoreCase)))
                return VideoPreviewPreparationResult.UnsupportedSoundtrack;
            using var soundtrackLock = soundtrackPath is not null
                ? new FileStream(soundtrackPath, FileMode.Open, FileAccess.Read, FileShare.Read)
                : null;
            VideoSoundtrackSource? replacement = null;
            if (soundtrackPath is not null &&
                !VideoSoundtrackSource.TryOpen(soundtrackPath, Duration, token, out replacement))
                return VideoPreviewPreparationResult.UnsupportedSoundtrack;
            using var soundtrack = replacement;
            if (soundtrack is not null)
                _profile.Audio = AudioEncodingProperties.CreateAac(48_000, soundtrack.Channels, 192_000);

            VideoFrameStabilizer? stabilizer = null;
            VideoFrameStabilizer.Correction[] corrections = [];
            IProgress<double>? encodeProgress = progress;
            if (_options.Stabilization is { } stabilization)
            {
                stabilizer = new VideoFrameStabilizer(
                    checked((int)_profile.Video.Width), checked((int)_profile.Video.Height), stabilization);
                corrections = VideoStabilizationService.AnalyzeCameraPath(
                    _source.Path, _profile, _sourceDuration, stabilizer, progress, token);
                if (progress is not null) encodeProgress = new EncodingProgress(progress);
            }
            // Decode to an orientation-free stream. File-to-file transcoding may bake in the
            // input rotation and swap dimensions depending on the requested output rotation.
            using (var input = new WindowsStabilizedVideoSource(
                       _source.Path, _profile, _sourceDuration, stabilizer, corrections, token,
                       _options.Brightness, _options.Contrast,
                       _options.TrimStart ?? TimeSpan.Zero, _options.TrimEnd,
                       _options.PlaybackSpeedPercent, soundtrack))
            using (var fileStream = new FileStream(_stagingPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                _ownsStagingFile = true;
                using var output = fileStream.AsRandomAccessStream();
                var transcoder = new MediaTranscoder { AlwaysReencode = true, HardwareAccelerationEnabled = false };
                var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(input.StreamSource, output, _profile)
                    .AsTask(token);
                if (!prepared.CanTranscode)
                    throw new InvalidOperationException($"Windows could not edit this video ({prepared.FailureReason}).");
                try
                {
                    await prepared.TranscodeAsync().AsTask(token, encodeProgress);
                }
                catch (Exception) when (input.Failure is not null)
                {
                    token.ThrowIfCancellationRequested();
                    throw new InvalidOperationException($"Windows could not decode this video: {input.Failure.Message}", input.Failure);
                }
                if (input.Failure is { } failure)
                    throw new InvalidOperationException($"Windows could not decode this video: {failure.Message}", failure);
                await output.FlushAsync().AsTask(token);
            }
            token.ThrowIfCancellationRequested();
            var file = await StorageFile.GetFileFromPathAsync(_stagingPath).AsTask(token);
            var encoded = await MediaEncodingProfile.CreateFromFileAsync(file).AsTask(token);
            var properties = await file.Properties.GetVideoPropertiesAsync().AsTask(token);
            if (new FileInfo(_stagingPath).Length == 0 ||
                encoded.Video.Width != _profile.Video.Width || encoded.Video.Height != _profile.Video.Height ||
                (encoded.Audio is null) != (_profile.Audio is null) ||
                (uint)properties.Orientation != (uint)_profile.Video.Properties[RotationKey] ||
                Math.Abs((properties.Duration - Duration).TotalMilliseconds) > 250)
                throw new InvalidOperationException("Windows produced an incomplete or unexpected video. No copy was saved. " +
                    $"Size {encoded.Video.Width}x{encoded.Video.Height}; audio {encoded.Audio is not null}; " +
                    $"rotation {(uint)properties.Orientation}; duration {properties.Duration.TotalSeconds:F3}s " +
                    $"(expected {_profile.Video.Width}x{_profile.Video.Height}, audio {_profile.Audio is not null}, " +
                    $"rotation {_profile.Video.Properties[RotationKey]}, {Duration.TotalSeconds:F3}s).");
            token.ThrowIfCancellationRequested();
            PreviewPath = _stagingPath;
            preparedSuccessfully = true;
            return VideoPreviewPreparationResult.Ready;
        }
        catch (Exception ex) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException("Video preparation canceled.", ex, token);
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException($"Windows could not edit this video (0x{ex.HResult:X8}). " +
                "The file may be damaged or its video or audio format is not supported on this system.", ex);
        }
        finally
        {
            if (!preparedSuccessfully && _ownsStagingFile)
            {
                File.Delete(_stagingPath);
                _ownsStagingFile = false;
            }
        }
    }, token);

    private sealed class EncodingProgress(IProgress<double> progress) : IProgress<double>
    {
        public void Report(double value) => progress.Report(30 + Math.Clamp(value, 0, 100) * 0.7);
    }

    public async Task ExportAsync(string destinationPath, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        VideoStabilizationService.ValidatePaths(_source.Path, destinationPath);
        if (PreviewPath is null || _metadata is null)
            throw new InvalidOperationException("Prepare and review the edited preview before saving.");
        token.ThrowIfCancellationRequested();
        string temporaryPath = Path.Combine(Path.GetDirectoryName(destinationPath)!,
            $".photolibrarian-{Guid.NewGuid():N}.tmp");
        bool temporaryCreated = false;
        try
        {
            using (var input = new FileStream(_stagingPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                temporaryCreated = true;
                await input.CopyToAsync(output, token);
                await output.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            VideoStabilizationService.PublishOutput(_metadata, temporaryPath, destinationPath);
        }
        finally
        {
            if (temporaryCreated) File.Delete(temporaryPath);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_ownsStagingFile) File.Delete(_stagingPath);
        PreviewPath = null;
        _disposed = true;
    }

    public static string GetAvailableOutputPath(string sourcePath, string? directory = null)
    {
        directory ??= Path.GetDirectoryName(sourcePath)!;
        string name = Path.GetFileNameWithoutExtension(sourcePath) + "-edited";
        for (int index = 0; ; index++)
        {
            string candidate = Path.Combine(directory, (index == 0 ? name : $"{name} ({index})") + ".mp4");
            if (!File.Exists(candidate) && !Directory.Exists(candidate) &&
                !File.Exists(FaceMetadataStore.GetSidecarPathForDestinationImage(candidate)))
                return candidate;
        }
    }
}
