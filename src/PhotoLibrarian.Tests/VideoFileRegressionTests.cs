// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Security.Cryptography;
using PhotoLibrarian.Core.Services;
using Vortice.MediaFoundation;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Xunit;
using static Vortice.MediaFoundation.MediaFactory;

namespace PhotoLibrarian.Tests;

public sealed class VideoFileRegressionTests
{
    [Fact]
    public async Task CameraClipDecodesEditsStabilizesAndTrimsWithoutChangingOriginal()
    {
        string? path = Environment.GetEnvironmentVariable("PHOTOLIBRARIAN_VIDEO_FIXTURE");
        Assert.SkipWhen(string.IsNullOrEmpty(path), "Set PHOTOLIBRARIAN_VIDEO_FIXTURE to run a local camera-clip regression.");
        Assert.True(File.Exists(path), "The supplied camera-clip fixture must exist.");
        var token = TestContext.Current.CancellationToken;
        byte[] sourceHash = await HashFileAsync(path, token);
        DateTime sourceWriteTime = File.GetLastWriteTimeUtc(path);
        string sidecar = Path.ChangeExtension(path, ".xmp");
        byte[]? sidecarContent = File.Exists(sidecar) ? await File.ReadAllBytesAsync(sidecar, token) : null;
        string directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-Camera-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
            var profile = await MediaEncodingProfile.CreateFromFileAsync(file).AsTask(token);
            var properties = await file.Properties.GetVideoPropertiesAsync().AsTask(token);
            int width = checked((int)profile.Video.Width);
            int height = checked((int)profile.Video.Height);
            TestContext.Current.TestOutputHelper!.WriteLine(
                $"Camera clip: {width}x{height}, {profile.Video.Subtype}, {properties.Duration}, rotation {properties.Orientation}");
            LogDecoderRange(path);
            int frames = 0;
            uint range;
            using (var reader = new MediaFoundationFrameReader(path, width, height, includeAudio: false))
            {
                uint initialRange = reader.VideoNominalRange;
                while (reader.Read(video: true, token) is { } frame)
                {
                    Assert.Equal(checked(width * height * 3 / 2), frame.Data.Length);
                    frames++;
                }
                range = reader.VideoNominalRange;
                Assert.Equal(initialRange, range);
            }
            Assert.True(frames > 0);
            TestContext.Current.TestOutputHelper.WriteLine($"Decoded {frames} frames, nominal range {range}.");

            using var editing = await VideoEditingService.CreateAsync(path,
                new(ClockwiseQuarterTurns: 1, Brightness: 15, Contrast: 120,
                    Stabilization: StabilizationOptions.Default,
                    TrimStart: TimeSpan.FromSeconds(properties.Duration.TotalSeconds / 4),
                    TrimEnd: TimeSpan.FromSeconds(properties.Duration.TotalSeconds * 3 / 4)), token);
            await editing.PreparePreviewAsync(token: token);
            byte[] preparedHash = await HashFileAsync(editing.PreviewPath!, token);
            string edited = Path.Combine(directory, "edited.mp4");
            await editing.ExportAsync(edited, token);
            Assert.Equal(preparedHash, await HashFileAsync(edited, token));
            var editedProperties = await (await StorageFile.GetFileFromPathAsync(edited).AsTask(token))
                .Properties.GetVideoPropertiesAsync().AsTask(token);
            Assert.Equal(((uint)properties.Orientation + 90) % 360, (uint)editedProperties.Orientation);

            string stabilized = Path.Combine(directory, "stabilized.mp4");
            await new VideoStabilizationService().StabilizeAsync(path, stabilized, cancellationToken: token);
            Assert.True(new FileInfo(stabilized).Length > 0);
            string trimmed = Path.Combine(directory, "trimmed.mp4");
            var trimming = new VideoTrimmingService();
            var info = await trimming.GetVideoInfoAsync(path, token);
            TimeSpan start = TimeSpan.FromSeconds(info.Duration.TotalSeconds / 4);
            TimeSpan end = TimeSpan.FromSeconds(info.Duration.TotalSeconds * 3 / 4);
            await trimming.TrimAsync(path, trimmed, start, end, cancellationToken: token);
            Assert.True(new FileInfo(trimmed).Length > 0);
        }
        finally
        {
            try
            {
                Assert.Equal(sourceHash, await HashFileAsync(path, token));
                Assert.Equal(sourceWriteTime, File.GetLastWriteTimeUtc(path));
                Assert.Equal(sidecarContent, File.Exists(sidecar) ? await File.ReadAllBytesAsync(sidecar, token) : null);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return await SHA256.HashDataAsync(stream, token);
    }

    private static void LogDecoderRange(string path)
    {
        MFStartup().CheckError();
        try
        {
            using var reader = MFCreateSourceReaderFromURL(path, null);
            using var native = reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, 0);
            using var requested = MFCreateMediaType();
            requested.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            requested.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12).CheckError();
            reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, requested);
            using var before = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
            using var sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None,
                out _, out var flags, out _);
            using var after = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
            native.GetUInt32(MediaTypeAttributeKeys.VideoNominalRange, out uint sourceRange);
            before.GetUInt32(MediaTypeAttributeKeys.VideoNominalRange, out uint beforeRange);
            after.GetUInt32(MediaTypeAttributeKeys.VideoNominalRange, out uint afterRange);
            TestContext.Current.TestOutputHelper!.WriteLine(
                $"MF nominal range: source {sourceRange}, before first sample {beforeRange}, after {afterRange}; flags {flags}.");
        }
        finally
        {
            MFShutdown().CheckError();
        }
    }
}
