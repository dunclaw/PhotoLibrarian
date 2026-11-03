// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using XmpCore;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class VideoTrimmingTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-Trim-{Guid.NewGuid():N}");
    private string Source => Path.Combine(_directory, "source.mp4");
    private string Output => Path.Combine(_directory, "trimmed.mp4");

    public VideoTrimmingTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Source, "original video");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void DefaultOutputSkipsVideosAndSidecars()
    {
        string first = Path.Combine(_directory, "source-trimmed.mp4");
        Assert.Equal(first, VideoTrimmingService.GetAvailableOutputPath(Source));
        File.WriteAllText(first, "occupied");
        string second = Path.Combine(_directory, "source-trimmed (1).mp4");
        File.WriteAllText(FaceMetadataStore.GetSidecarPathForDestinationImage(second), "<xmp/>");

        Assert.Equal(Path.Combine(_directory, "source-trimmed (2).mp4"),
            VideoTrimmingService.GetAvailableOutputPath(Source));
    }

    [Fact]
    public void RejectsInvalidPathsRangesAndTimebase()
    {
        Assert.Throws<ArgumentException>(() => VideoTrimmingService.ValidatePaths(Output, Output));
        Assert.Throws<ArgumentException>(() => VideoTrimmingService.ValidatePaths(Source, "relative.mp4"));
        Assert.Throws<ArgumentException>(() => VideoTrimmingService.ValidatePaths(Source, Output + ".avi"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VideoTrimmingService.ValidateSelection(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(2), 24));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VideoTrimmingService.ValidateSelection(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(0.5),
                TimeSpan.FromSeconds(2), 24));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VideoTrimmingService.ValidateSelection(TimeSpan.Zero, TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(2), 24));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VideoTrimmingService.ValidateSelection(TimeSpan.Zero, TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2), double.NaN));

        VideoTrimmingService.ValidateSelection(TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), 24);
    }

    [Fact]
    public void NormalizesTrimPointsToNearestSourceFrameAndRejectsAnEmptyFrameRange()
    {
        var (start, end) = VideoTrimmingService.NormalizeSelection(
            TimeSpan.FromSeconds(0.512), TimeSpan.FromSeconds(1.51),
            TimeSpan.FromSeconds(2.005), 24);

        Assert.Equal(TimeSpan.FromSeconds(0.5), start);
        Assert.Equal(TimeSpan.FromSeconds(1.5), end);
        var (midpointStart, midpointEnd) = VideoTrimmingService.NormalizeSelection(
            TimeSpan.FromSeconds(0.5 / 24), TimeSpan.FromSeconds(1.5 / 24),
            TimeSpan.FromSeconds(2), 24);
        Assert.Equal(TimeSpan.FromTicks(416_667), midpointStart);
        Assert.Equal(TimeSpan.FromTicks(833_333), midpointEnd);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VideoTrimmingService.NormalizeSelection(TimeSpan.FromSeconds(0.51),
                TimeSpan.FromSeconds(0.52), TimeSpan.FromSeconds(2), 24));
    }

    [Fact]
    public async Task PreCanceledExportDoesNotCreateOutputOrTemporaryFiles()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new VideoTrimmingService().TrimAsync(Source, Output, TimeSpan.Zero, TimeSpan.FromSeconds(1),
                cancellationToken: cancellation.Token));

        Assert.Equal("original video", File.ReadAllText(Source));
        Assert.False(File.Exists(Output));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task FailedDecodeAndCollisionsNeverOverwriteSourceOrExistingFiles()
    {
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(Source));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            new VideoTrimmingService().TrimAsync(Source, Source, TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        File.WriteAllText(Output, "existing output");
        await Assert.ThrowsAsync<IOException>(() =>
            new VideoTrimmingService().TrimAsync(Source, Output, TimeSpan.Zero, TimeSpan.FromSeconds(1)));

        string sidecarCollision = Path.Combine(_directory, "metadata-collision.mp4");
        File.WriteAllText(FaceMetadataStore.GetSidecarPathForDestinationImage(sidecarCollision), "existing metadata");
        await Assert.ThrowsAsync<IOException>(() =>
            new VideoTrimmingService().TrimAsync(Source, sidecarCollision, TimeSpan.Zero, TimeSpan.FromSeconds(1)));

        Assert.Equal(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(Source)));
        Assert.Equal("existing output", File.ReadAllText(Output));
        Assert.Equal("existing metadata", File.ReadAllText(FaceMetadataStore.GetSidecarPathForDestinationImage(sidecarCollision)));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task TrimTranscodesSelectedRangeWithAudioAndLeavesOriginalUnchanged()
    {
        await CreateVideoFixtureAsync(withAudio: true);
        await MetadataWriterService.WriteCaptionAsync(Source, "Preserved trim caption");
        await TagWriterService.WriteTagsToSidecarAsync(Source,
            ["family", "vacation"],
            [new ImageTag { Tag = "family", Source = TagSource.Manual }],
            "test-scan-version");
        byte[] sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(Source));
        var service = new VideoTrimmingService();

        var info = await service.GetVideoInfoAsync(Source);
        Assert.InRange(info.Duration.TotalSeconds, 1.98, 2.02);
        Assert.True(info.HasAudio);
        await service.TrimAsync(Source, Output, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5));

        var result = await StorageFile.GetFileFromPathAsync(Output);
        var profile = await MediaEncodingProfile.CreateFromFileAsync(result);
        var properties = await result.Properties.GetVideoPropertiesAsync();
        Assert.Equal(MediaEncodingSubtypes.H264, profile.Video.Subtype);
        Assert.Equal(MediaEncodingSubtypes.Aac, profile.Audio!.Subtype, ignoreCase: true);
        Assert.InRange(properties.Duration.TotalSeconds, 0.9, 1.1);
        Assert.Contains("Preserved trim caption",
            await File.ReadAllTextAsync(FaceMetadataStore.GetSidecarPathForImage(Output)));
        Assert.Equal(["family", "vacation"], TagWriterService.ReadTagsFromSidecar(Output));
        var metadata = XmpMetaFactory.ParseFromString(
            await File.ReadAllTextAsync(FaceMetadataStore.GetSidecarPathForImage(Output)));
        string? tagMetadata = metadata.GetPropertyString(
            MetadataWriterService.PhotoLibrarianNamespace, EmbeddedMetadataWriter.PhotoTagMetadataPropertyName);
        Assert.Contains("test-scan-version", tagMetadata);
        Assert.Equal(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(Source)));
        AssertNoTemporaryFiles();

        using var reader = new MediaFoundationFrameReader(Output, 64, 64, includeAudio: true);
        byte firstLuma = 0, lastLuma = 0;
        int frames = 0;
        while (reader.Read(video: true, TestContext.Current.CancellationToken) is { } frame)
        {
            if (frames++ == 0) firstLuma = frame.Data[0];
            lastLuma = frame.Data[0];
        }
        Assert.InRange(frames, 22, 26);
        Assert.InRange(firstLuma, (byte)68, (byte)105);
        Assert.InRange(lastLuma, (byte)140, (byte)190);
        Assert.NotNull(reader.Read(video: false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TrimWithoutAudioKeepsOutputSilent()
    {
        await CreateVideoFixtureAsync(withAudio: false);
        await new VideoTrimmingService().TrimAsync(
            Source, Output, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5));

        var result = await StorageFile.GetFileFromPathAsync(Output);
        var profile = await MediaEncodingProfile.CreateFromFileAsync(result);
        Assert.Null(profile.Audio);
    }

    [Fact]
    public async Task CancellationDuringTranscodeRemovesTemporaryOutput()
    {
        await CreateVideoFixtureAsync(withAudio: false, durationSeconds: 30);
        byte[] sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(Source));
        using var cancellation = new CancellationTokenSource();
        var progress = new CancelOnProgress(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new VideoTrimmingService().TrimAsync(
                Source, Output, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(29),
                progress, cancellation.Token));

        Assert.True(progress.WasReported);
        Assert.Equal(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(Source)));
        Assert.False(File.Exists(Output));
        AssertNoTemporaryFiles();
    }

    private void AssertNoTemporaryFiles() =>
        Assert.Empty(Directory.GetFiles(_directory, ".photolibrarian-*"));

    private async Task CreateVideoFixtureAsync(bool withAudio, int durationSeconds = 2)
    {
        const int width = 64, height = 64, framesPerSecond = 24, audioSamplesPerFrame = 2_000;
        int frameCount = durationSeconds * framesPerSecond;
        var video = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, width, height);
        video.FrameRate.Numerator = framesPerSecond;
        video.FrameRate.Denominator = 1;
        var videoDescriptor = new VideoStreamDescriptor(video);
        var audioDescriptor = new AudioStreamDescriptor(AudioEncodingProperties.CreatePcm(48_000, 1, 16));
        var source = withAudio
            ? new MediaStreamSource(videoDescriptor, audioDescriptor)
            : new MediaStreamSource(videoDescriptor);
        source.BufferTime = TimeSpan.Zero;
        source.Duration = TimeSpan.FromSeconds(durationSeconds);
        source.Starting += (_, args) => args.Request.SetActualStartPosition(TimeSpan.Zero);
        int videoFrame = 0;
        int audioFrame = 0;
        source.SampleRequested += (_, args) =>
        {
            bool isVideo = args.Request.StreamDescriptor is VideoStreamDescriptor;
            int frame = isVideo ? videoFrame++ : audioFrame++;
            if (frame >= frameCount) return;

            byte[] data;
            TimeSpan timestamp;
            TimeSpan sampleDuration;
            if (isVideo)
            {
                byte luma = (byte)(32 + frame * 4);
                data = new byte[width * height * 4];
                for (int pixel = 0; pixel < data.Length; pixel += 4)
                {
                    data[pixel] = luma;
                    data[pixel + 1] = luma;
                    data[pixel + 2] = luma;
                    data[pixel + 3] = 255;
                }
                timestamp = TimeSpan.FromSeconds(frame / (double)framesPerSecond);
                sampleDuration = TimeSpan.FromSeconds(1d / framesPerSecond);
            }
            else
            {
                data = new byte[audioSamplesPerFrame * 2];
                for (int sample = 0; sample < audioSamplesPerFrame; sample++)
                {
                    short value = (short)(4000 * Math.Sin(
                        2 * Math.PI * 440 * (frame * audioSamplesPerFrame + sample) / 48_000));
                    data[sample * 2] = (byte)value;
                    data[sample * 2 + 1] = (byte)(value >> 8);
                }
                timestamp = TimeSpan.FromSeconds(frame / (double)framesPerSecond);
                sampleDuration = TimeSpan.FromSeconds(1d / framesPerSecond);
            }

            var mediaSample = MediaStreamSample.CreateFromBuffer(data.AsBuffer(), timestamp);
            mediaSample.Duration = sampleDuration;
            args.Request.Sample = mediaSample;
        };

        using var file = new FileStream(Source, FileMode.Create, FileAccess.ReadWrite);
        using var output = file.AsRandomAccessStream();
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
        profile.Video.Width = width;
        profile.Video.Height = height;
        profile.Video.FrameRate.Numerator = framesPerSecond;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.Bitrate = 1_000_000;
        if (!withAudio) profile.Audio = null;
        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = false };
        var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, output, profile)
            .AsTask(TestContext.Current.CancellationToken);
        Assert.True(prepared.CanTranscode, prepared.FailureReason.ToString());
        await prepared.TranscodeAsync().AsTask(TestContext.Current.CancellationToken);
    }

    private sealed class CancelOnProgress(CancellationTokenSource cancellation) : IProgress<double>
    {
        public bool WasReported { get; private set; }

        public void Report(double value)
        {
            WasReported = true;
            cancellation.Cancel();
        }
    }
}
