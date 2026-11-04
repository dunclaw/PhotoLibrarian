// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Runtime.InteropServices;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace PhotoLibrarian.Core.Services;

public sealed record VideoEditOptions(int ClockwiseQuarterTurns = 0, bool RemoveAudio = false);

/// <summary>Prepares one local MP4; preview and save use those exact bytes.</summary>
public sealed class VideoEditingService : IDisposable
{
    private static readonly Guid RotationKey = new("C380465D-2271-428C-9B83-ECEA3B4A85C1");
    private readonly StorageFile _source;
    private readonly MediaEncodingProfile _profile;
    private readonly string _stagingPath = Path.Combine(Path.GetTempPath(), $"photolibrarian-edit-{Guid.NewGuid():N}.mp4");
    private string? _metadata;
    private bool _disposed;
    private bool _ownsStagingFile;
    internal string TemporaryPreviewPath => _stagingPath;
    public TimeSpan Duration { get; }
    public string? PreviewPath { get; private set; }

    private VideoEditingService(StorageFile source, TimeSpan duration, MediaEncodingProfile profile)
    {
        _source = source;
        Duration = duration;
        _profile = profile;
    }

    public static async Task<VideoEditingService> CreateAsync(
        string sourcePath, VideoEditOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ClockwiseQuarterTurns is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(options));
        token.ThrowIfCancellationRequested();
        var source = await StorageFile.GetFileFromPathAsync(sourcePath).AsTask(token);
        var input = await MediaEncodingProfile.CreateFromFileAsync(source).AsTask(token);
        var profile = VideoStabilizationService.CreateOutputProfile(input);
        if (input.Video.Width > 4096 || input.Video.Height > 4096)
            throw new NotSupportedException("Video editing supports up to 4096 pixels per dimension.");
        var properties = await source.Properties.GetVideoPropertiesAsync().AsTask(token);
        // MP4 rotation is standard track metadata, not a resampling of the source pixels.
        // Keep encoded dimensions and aspect ratio; compose with the existing orientation.
        profile.Video.Properties[RotationKey] = ((uint)properties.Orientation + (uint)options.ClockwiseQuarterTurns * 90) % 360;
        if (options.RemoveAudio)
            profile.Audio = null;
        return new VideoEditingService(source, properties.Duration, profile);
    }

    public Task PreparePreviewAsync(IProgress<double>? progress = null,
        CancellationToken token = default) => Task.Run(async () =>
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (PreviewPath is not null) return;
        token.ThrowIfCancellationRequested();
        using var sourceLock = new FileStream(_source.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _metadata = await VideoDerivativeMetadata.ReadAsync(_source, token);
        bool preparedSuccessfully = false;
        try
        {
            // Decode to an orientation-free stream. File-to-file transcoding may bake in the
            // input rotation and swap dimensions depending on the requested output rotation.
            using (var input = new WindowsStabilizedVideoSource(_source.Path, _profile, Duration, null, [], token))
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
                    await prepared.TranscodeAsync().AsTask(token, progress);
                }
                catch (Exception) when (input.Failure is not null)
                {
                    token.ThrowIfCancellationRequested();
                    throw new InvalidOperationException("Windows could not decode this video.", input.Failure);
                }
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
