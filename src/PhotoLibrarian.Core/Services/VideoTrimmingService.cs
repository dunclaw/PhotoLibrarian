// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Runtime.InteropServices;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace PhotoLibrarian.Core.Services;

public readonly record struct VideoTrimInfo(TimeSpan Duration, double FramesPerSecond, bool HasAudio);

/// <summary>Exports a frame-accurate, locally transcoded MP4 clip without changing its source.</summary>
public sealed class VideoTrimmingService
{
    private static readonly Guid VideoRotationKey = new("C380465D-2271-428C-9B83-ECEA3B4A85C1");

    public async Task<VideoTrimInfo> GetVideoInfoAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(sourcePath) || !File.Exists(sourcePath))
            throw new ArgumentException("The source video no longer exists.", nameof(sourcePath));
        var source = await StorageFile.GetFileFromPathAsync(sourcePath).AsTask(cancellationToken);
        var profile = await MediaEncodingProfile.CreateFromFileAsync(source).AsTask(cancellationToken);
        var properties = await source.Properties.GetVideoPropertiesAsync().AsTask(cancellationToken);
        if (profile.Video is not { Width: > 0, Height: > 0 } video ||
            video.FrameRate.Numerator == 0 || video.FrameRate.Denominator == 0 ||
            properties.Duration <= TimeSpan.Zero)
            throw new NotSupportedException("The selected file does not contain a supported video track.");
        double framesPerSecond = video.FrameRate.Numerator / (double)video.FrameRate.Denominator;
        return new VideoTrimInfo(properties.Duration, framesPerSecond, profile.Audio is not null);
    }

    public Task TrimAsync(
        string sourcePath, string destinationPath, TimeSpan start, TimeSpan end,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            ValidatePaths(sourcePath, destinationPath);
            cancellationToken.ThrowIfCancellationRequested();
            string temporaryPath = Path.Combine(Path.GetDirectoryName(destinationPath)!,
                $".photolibrarian-{Guid.NewGuid():N}.mp4");
            bool ownsTemporaryFile = false;
            try
            {
                var source = await StorageFile.GetFileFromPathAsync(sourcePath).AsTask(cancellationToken);
                var inputProfile = await MediaEncodingProfile.CreateFromFileAsync(source).AsTask(cancellationToken);
                if (inputProfile.Video is not { Width: > 0, Height: > 0 } sourceVideo)
                    throw new NotSupportedException("The selected file does not contain a supported video track.");
                var properties = await source.Properties.GetVideoPropertiesAsync().AsTask(cancellationToken);
                var frameRate = sourceVideo.FrameRate;
                if (frameRate.Numerator == 0 || frameRate.Denominator == 0 || properties.Duration <= TimeSpan.Zero)
                    throw new NotSupportedException("The selected file does not contain a supported video timebase.");
                double framesPerSecond = frameRate.Numerator / (double)frameRate.Denominator;
                var selection = NormalizeSelection(start, end, properties.Duration, framesPerSecond);

                var metadata = await VideoDerivativeMetadata.ReadAsync(source, cancellationToken);
                var outputProfile = VideoStabilizationService.CreateOutputProfile(inputProfile);
                SetProfileRotation(outputProfile, (uint)properties.Orientation);

                var folder = await StorageFolder.GetFolderFromPathAsync(
                    Path.GetDirectoryName(temporaryPath)!).AsTask(cancellationToken);
                var temporaryFile = await folder.CreateFileAsync(
                    Path.GetFileName(temporaryPath), CreationCollisionOption.FailIfExists).AsTask(cancellationToken);
                ownsTemporaryFile = true;
                var transcoder = new MediaTranscoder
                {
                    AlwaysReencode = true,
                    HardwareAccelerationEnabled = false,
                    TrimStartTime = selection.Start,
                    TrimStopTime = selection.End
                };
                var prepared = await transcoder.PrepareFileTranscodeAsync(source, temporaryFile, outputProfile)
                    .AsTask(cancellationToken);
                if (!prepared.CanTranscode)
                    throw new InvalidOperationException(
                        $"Windows cannot trim this video ({prepared.FailureReason}). " +
                        "The file may be damaged or use an unsupported video or audio format.");

                await prepared.TranscodeAsync().AsTask(cancellationToken, progress);
                cancellationToken.ThrowIfCancellationRequested();
                await VerifyOutputAsync(temporaryPath, selection.End - selection.Start, framesPerSecond,
                    inputProfile.Audio is not null, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                VideoStabilizationService.PublishOutput(metadata, temporaryPath, destinationPath);
                ownsTemporaryFile = false;
            }
            catch (Exception ex) when (ex is COMException or SharpGen.Runtime.SharpGenException)
            {
                throw new InvalidOperationException(
                    $"Windows could not trim this video (0x{ex.HResult:X8}). " +
                    "The file may be damaged or its media format is not supported on this system.", ex);
            }
            finally
            {
                if (ownsTemporaryFile)
                    File.Delete(temporaryPath);
            }
        }, cancellationToken);

    /// <summary>Returns the first "-trimmed" MP4 name whose video and metadata sidecar are unused.</summary>
    public static string GetAvailableOutputPath(string sourcePath, string? directory = null)
    {
        directory ??= Path.GetDirectoryName(sourcePath)!;
        string name = Path.GetFileNameWithoutExtension(sourcePath) + "-trimmed";
        for (int index = 0; ; index++)
        {
            string candidate = Path.Combine(directory, (index == 0 ? name : $"{name} ({index})") + ".mp4");
            if (!File.Exists(candidate) && !Directory.Exists(candidate) &&
                !File.Exists(FaceMetadataStore.GetSidecarPathForDestinationImage(candidate)))
                return candidate;
        }
    }

    internal static void ValidatePaths(string sourcePath, string destinationPath)
    {
        if (!Path.IsPathFullyQualified(sourcePath) || !File.Exists(sourcePath))
            throw new ArgumentException("The source video no longer exists.");
        if (!Path.IsPathFullyQualified(destinationPath) ||
            !Directory.Exists(Path.GetDirectoryName(destinationPath)) ||
            !string.Equals(Path.GetExtension(destinationPath), ".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose an MP4 filename in an existing output folder.");
        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destinationPath),
                StringComparison.OrdinalIgnoreCase) || File.Exists(destinationPath) || Directory.Exists(destinationPath))
            throw new IOException("The output already exists. Choose a new filename; files are never overwritten.");
        if (File.Exists(FaceMetadataStore.GetSidecarPathForDestinationImage(destinationPath)))
            throw new IOException("The output's metadata sidecar already exists. Choose a new filename; files are never overwritten.");
    }

    public static void ValidateSelection(TimeSpan start, TimeSpan end, TimeSpan duration, double framesPerSecond)
    {
        if (duration <= TimeSpan.Zero || !double.IsFinite(framesPerSecond) || framesPerSecond <= 0 ||
            start < TimeSpan.Zero || end <= start || end > duration)
            throw new ArgumentOutOfRangeException(nameof(start),
                "Choose a start and end within the video, with the end after the start.");
    }

    public static (TimeSpan Start, TimeSpan End) NormalizeSelection(
        TimeSpan start, TimeSpan end, TimeSpan duration, double framesPerSecond)
    {
        ValidateSelection(start, end, duration, framesPerSecond);
        double frameTicks = TimeSpan.TicksPerSecond / framesPerSecond;
        long lastFrame = (long)Math.Floor(duration.Ticks / frameTicks + 1e-9);
        // TimeSpan rounds to whole ticks, so half a tick restores exact midpoint ties.
        double halfTickInFrames = 0.5 / frameTicks;
        long startFrame = Math.Clamp(
            (long)Math.Round(start.Ticks / frameTicks + halfTickInFrames, MidpointRounding.AwayFromZero),
            0, lastFrame);
        long endFrame = Math.Clamp(
            (long)Math.Round(end.Ticks / frameTicks + halfTickInFrames, MidpointRounding.AwayFromZero),
            0, lastFrame);
        if (endFrame <= startFrame)
            throw new ArgumentOutOfRangeException(nameof(end),
                "The selected range must include at least one source video frame.");
        return (
            TimeSpan.FromTicks((long)Math.Round(startFrame * frameTicks)),
            TimeSpan.FromTicks((long)Math.Round(endFrame * frameTicks)));
    }

    private static void SetProfileRotation(MediaEncodingProfile profile, uint orientation) =>
        profile.Video.Properties[VideoRotationKey] = orientation;

    private static async Task VerifyOutputAsync(
        string path, TimeSpan expectedDuration, double framesPerSecond, bool expectAudio,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new InvalidDataException("Windows did not produce a video.");

        var output = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken);
        var profile = await MediaEncodingProfile.CreateFromFileAsync(output).AsTask(cancellationToken);
        var properties = await output.Properties.GetVideoPropertiesAsync().AsTask(cancellationToken);
        if (profile.Video is not { Width: > 0, Height: > 0 } video || properties.Duration <= TimeSpan.Zero)
            throw new InvalidDataException("The exported file does not contain a readable video track.");
        if (expectAudio != (profile.Audio is not null))
            throw new InvalidDataException("The exported file's audio track does not match the source.");

        long frameTicks = Math.Max(1, (long)(TimeSpan.TicksPerSecond / framesPerSecond));
        long tolerance = Math.Max(frameTicks * 2, TimeSpan.FromMilliseconds(100).Ticks);
        if (Math.Abs(properties.Duration.Ticks - expectedDuration.Ticks) > tolerance)
            throw new InvalidDataException(
                $"The exported duration ({properties.Duration}) differs from the selected range ({expectedDuration}).");

        bool hasVideoFrame = false;
        bool hasAudioSample = !expectAudio;
        long firstFrame = -1;
        long lastFrameEnd = -1;
        long firstAudioSample = -1;
        long lastAudioEnd = -1;
        int frameCount = 0;
        using (var reader = new MediaFoundationFrameReader(path,
                   checked((int)video.Width), checked((int)video.Height), expectAudio))
        {
            while (reader.Read(video: true, cancellationToken) is { } frame)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hasVideoFrame = true;
                if (frameCount++ == 0)
                    firstFrame = frame.Timestamp;
                lastFrameEnd = frame.Timestamp + Math.Max(frame.Duration, frameTicks);
            }
            if (expectAudio)
            {
                while (reader.Read(video: false, cancellationToken) is { } sample)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    hasAudioSample |= sample.Data.Length > 0;
                    if (firstAudioSample < 0)
                        firstAudioSample = sample.Timestamp;
                    lastAudioEnd = sample.Timestamp + sample.Duration;
                }
            }
        }

        int expectedFrameCount = Math.Max(1, (int)Math.Round(expectedDuration.TotalSeconds * framesPerSecond));
        if (!hasVideoFrame || firstFrame < 0 || firstFrame > tolerance ||
            Math.Abs(lastFrameEnd - expectedDuration.Ticks) > tolerance ||
            Math.Abs(frameCount - expectedFrameCount) > 2)
            throw new InvalidDataException("The exported video frames do not match the selected time range.");
        if (expectAudio && (!hasAudioSample || firstAudioSample < 0 || firstAudioSample > tolerance ||
            Math.Abs(lastAudioEnd - expectedDuration.Ticks) > tolerance))
            throw new InvalidDataException("The exported audio track does not match the selected time range.");
    }
}
