using System.Runtime.InteropServices;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace PhotoLibrarian.Core.Services;

/// <summary>Exports a stabilized copy in-process using Windows video decoding and encoding.</summary>
public sealed class VideoStabilizationService
{
    private static readonly Guid VideoRotationKey = new("C380465D-2271-428C-9B83-ECEA3B4A85C1");

    public Task StabilizeAsync(
        string sourcePath, string destinationPath,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            ValidatePaths(sourcePath, destinationPath);
            cancellationToken.ThrowIfCancellationRequested();
            string temporaryPath = Path.Combine(Path.GetDirectoryName(destinationPath)!,
                $".photolibrarian-{Guid.NewGuid():N}.tmp");
            try
            {
                var source = await StorageFile.GetFileFromPathAsync(sourcePath).AsTask(cancellationToken);
                var inputProfile = await MediaEncodingProfile.CreateFromFileAsync(source).AsTask(cancellationToken);
                var profile = CreateOutputProfile(inputProfile);
                var properties = await source.Properties.GetVideoPropertiesAsync().AsTask(cancellationToken);
                profile.Video.Properties[VideoRotationKey] = (uint)properties.Orientation;
                using (var input = new WindowsStabilizedVideoSource(sourcePath, inputProfile, properties.Duration, cancellationToken))
                using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                using (var output = file.AsRandomAccessStream())
                {
                    var transcoder = new MediaTranscoder { AlwaysReencode = true, HardwareAccelerationEnabled = false };
                    var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(input.StreamSource, output, profile)
                        .AsTask(cancellationToken);
                    if (!prepared.CanTranscode)
                        throw new InvalidOperationException(
                            $"Windows cannot stabilize this video ({prepared.FailureReason}). " +
                            "The file may be damaged or use an unsupported video or audio format.");
                    try
                    {
                        await prepared.TranscodeAsync().AsTask(cancellationToken, progress);
                    }
                    catch (Exception) when (input.Failure is not null)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new InvalidOperationException("Could not decode or stabilize this video.", input.Failure);
                    }
                    await output.FlushAsync().AsTask(cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (new FileInfo(temporaryPath).Length == 0)
                    throw new IOException("Windows did not produce a video.");
                PublishOutput(sourcePath, temporaryPath, destinationPath);
            }
            catch (Exception ex) when (ex is COMException or SharpGen.Runtime.SharpGenException)
            {
                throw new InvalidOperationException(
                    $"Windows could not stabilize this video (0x{ex.HResult:X8}). " +
                    "The file may be damaged or its media format is not supported on this system.", ex);
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }, cancellationToken);

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

    private static void PublishOutput(string sourcePath, string temporaryPath, string destinationPath)
    {
        var sourceSidecar = FaceMetadataStore.GetSidecarPathForImage(sourcePath);
        var destinationSidecar = FaceMetadataStore.GetSidecarPathForDestinationImage(destinationPath);
        if (File.Exists(destinationSidecar))
            throw new IOException("The output's metadata sidecar already exists. Choose a new filename; files are never overwritten.");

        if (!File.Exists(sourceSidecar))
        {
            File.Move(temporaryPath, destinationPath, overwrite: false);
            return;
        }

        var temporarySidecar = Path.Combine(
            Path.GetDirectoryName(destinationPath)!,
            $".photolibrarian-{Guid.NewGuid():N}.xmp.tmp");
        var sidecarPublished = false;
        try
        {
            File.Copy(sourceSidecar, temporarySidecar, overwrite: false);
            File.Move(temporarySidecar, destinationSidecar, overwrite: false);
            sidecarPublished = true;
            try
            {
                File.Move(temporaryPath, destinationPath, overwrite: false);
            }
            catch
            {
                File.Delete(destinationSidecar);
                sidecarPublished = false;
                throw;
            }
        }
        finally
        {
            File.Delete(temporarySidecar);
            if (sidecarPublished && !File.Exists(destinationPath))
                File.Delete(destinationSidecar);
        }
    }

    internal static MediaEncodingProfile CreateOutputProfile(MediaEncodingProfile input)
    {
        if (input.Video is not { Width: > 0, Height: > 0 } video ||
            video.FrameRate.Numerator == 0 || video.FrameRate.Denominator == 0)
            throw new ArgumentException("The selected file does not contain a supported video track.");

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
        profile.Video.Width = (video.Width + 1) & ~1u;
        profile.Video.Height = (video.Height + 1) & ~1u;
        profile.Video.FrameRate.Numerator = video.FrameRate.Numerator;
        profile.Video.FrameRate.Denominator = video.FrameRate.Denominator;
        profile.Video.PixelAspectRatio.Numerator = video.PixelAspectRatio.Numerator;
        profile.Video.PixelAspectRatio.Denominator = video.PixelAspectRatio.Denominator;
        profile.Video.Bitrate = (uint)Math.Clamp(
            (double)video.Width * video.Height * video.FrameRate.Numerator / video.FrameRate.Denominator * 0.2,
            1_000_000, 80_000_000);
        if (video.Properties.TryGetValue(VideoRotationKey, out var rotation))
            profile.Video.Properties[VideoRotationKey] = rotation;
        if (input.Audio is null)
            profile.Audio = null;
        else
            profile.Audio = AudioEncodingProperties.CreateAac(48_000, Math.Min(input.Audio.ChannelCount, 2), 192_000);
        return profile;
    }
}
