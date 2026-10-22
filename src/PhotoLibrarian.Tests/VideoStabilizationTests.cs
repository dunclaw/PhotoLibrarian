using System.Runtime.InteropServices.WindowsRuntime;
using PhotoLibrarian.Core.Services;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class VideoStabilizationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-Video-{Guid.NewGuid():N}");
    private string Source => Path.Combine(_directory, "source & original.mp4");
    private string Output => Path.Combine(_directory, "stabilized copy.mp4");

    public VideoStabilizationTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Source, "original");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void RejectsMissingRelativeAndNonMp4Paths()
    {
        Assert.Throws<ArgumentException>(() => VideoStabilizationService.ValidatePaths(Output, Output));
        Assert.Throws<ArgumentException>(() => VideoStabilizationService.ValidatePaths(Source, "out.mp4"));
        Assert.Throws<ArgumentException>(() => VideoStabilizationService.ValidatePaths(Source, Output + ".avi"));
    }

    [Fact]
    public async Task NeverOverwritesSourceOrExistingOutput()
    {
        var service = new VideoStabilizationService();
        await Assert.ThrowsAsync<IOException>(() => service.StabilizeAsync(Source, Source,
            cancellationToken: TestContext.Current.CancellationToken));
        File.WriteAllText(Output, "existing");
        await Assert.ThrowsAsync<IOException>(() => service.StabilizeAsync(Source, Output,
            cancellationToken: TestContext.Current.CancellationToken));
        var sidecarOnlyDestination = Path.Combine(_directory, "sidecar collision.mp4");
        var existingSidecar = Path.ChangeExtension(sidecarOnlyDestination, ".xmp");
        File.WriteAllText(existingSidecar, "existing metadata");
        await Assert.ThrowsAsync<IOException>(() => service.StabilizeAsync(Source, sidecarOnlyDestination,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("original", File.ReadAllText(Source));
        Assert.Equal("existing", File.ReadAllText(Output));
        Assert.Equal("existing metadata", File.ReadAllText(existingSidecar));
    }

    [Fact]
    public async Task PreCanceledExportDoesNotStart()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new VideoStabilizationService().StabilizeAsync(Source, Output, cancellationToken: cancellation.Token));
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProfilePreservesSizeFrameRateAndAudioPresence(bool withAudio)
    {
        var input = FixtureProfile(withAudio);
        input.Video.Width = 641;
        input.Video.Height = 481;
        input.Video.FrameRate.Numerator = 30_000;
        input.Video.FrameRate.Denominator = 1001;
        var rotationKey = new Guid("C380465D-2271-428C-9B83-ECEA3B4A85C1");
        input.Video.Properties[rotationKey] = 90u;
        var profile = VideoStabilizationService.CreateOutputProfile(input);
        Assert.Equal(MediaEncodingSubtypes.H264, profile.Video.Subtype);
        Assert.Equal(642u, profile.Video.Width);
        Assert.Equal(482u, profile.Video.Height);
        Assert.Equal(30_000u, profile.Video.FrameRate.Numerator);
        Assert.Equal(1001u, profile.Video.FrameRate.Denominator);
        Assert.Equal(90u, profile.Video.Properties[rotationKey]);
        Assert.Equal(withAudio, profile.Audio is not null);
        if (withAudio) Assert.Equal(MediaEncodingSubtypes.Aac, profile.Audio!.Subtype, ignoreCase: true);
    }

    [Fact]
    public void RejectsAudioOnlyInput()
    {
        var input = MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High);
        Assert.Throws<ArgumentException>(() => VideoStabilizationService.CreateOutputProfile(input));
    }

    [Theory]
    [InlineData(640, 480)]
    [InlineData(1280, 720)]
    [InlineData(3840, 2160)]
    public void FrameStabilizerReducesMotionAtDifferentResolutions(int width, int height)
    {
        var stabilizer = new VideoFrameStabilizer(width, height);
        byte[]? previousInput = null, previousOutput = null;
        long before = 0, after = 0;
        for (int frame = 0; frame < 12; frame++)
        {
            var input = MotionFrame(width, height, frame % 2 == 0 ? 6 : -6, frame % 3 == 0 ? 4 : -4);
            var output = stabilizer.Process(input, TestContext.Current.CancellationToken);
            if (previousInput is not null && previousOutput is not null)
                for (int y = 64; y < height - 64; y += 8)
                for (int x = 64; x < width - 64; x += 8)
                {
                    int index = y * width + x;
                    before += Math.Abs(input[index] - previousInput[index]);
                    after += Math.Abs(output[index] - previousOutput[index]);
                }
            previousInput = input;
            previousOutput = output;
        }
        Assert.True(after < before * 0.8, $"Motion ratio: {after / (double)before:F3}");
    }

    [Fact]
    public void StabilizesSubpixelTranslationAndRotationDespiteMovingForeground()
    {
        const int width = 640, height = 360;
        var stabilizer = new VideoFrameStabilizer(width, height);
        byte[]? previousInput = null, previousOutput = null;
        long before = 0, after = 0;
        for (int i = 0; i < 45; i++)
        {
            var frame = NaturalMotionFrame(width, height, i);
            var output = stabilizer.Process(frame, TestContext.Current.CancellationToken);
            if (previousInput is not null && previousOutput is not null)
                for (int y = 40; y < height - 40; y += 2)
                for (int x = 40; x < width - 40; x += 2)
                {
                    if (x > 190 && x < 450 && y > 110 && y < 270) continue;
                    int index = y * width + x;
                    before += Math.Abs(frame[index] - previousInput[index]);
                    after += Math.Abs(output[index] - previousOutput[index]);
                }
            previousInput = frame;
            previousOutput = output;
        }
        Assert.True(after < before * 0.6, $"Natural texture/rotation motion ratio: {after / (double)before:F3}");
    }

    private static byte[] NaturalMotionFrame(int width, int height, int index)
    {
        var frame = new byte[width * height * 3 / 2];
        double angle = Math.Sin(index * 2.1) * 0.018;
        double dx = Math.Sin(index * 1.7) * 4.7, dy = Math.Cos(index * 2.3) * 3.3;
        var (sin, cos) = Math.SinCos(angle);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            double sx = cos * (x - width / 2.0) - sin * (y - height / 2.0) + width / 2.0 + dx;
            double sy = sin * (x - width / 2.0) + cos * (y - height / 2.0) + height / 2.0 + dy;
            double value = 110 + 27 * Math.Sin(sx * 0.12) * Math.Cos(sy * 0.17) +
                           24 * Math.Sin(sx * 0.041 + sy * 0.071) +
                           17 * Math.Cos(sx * 0.23 - sy * 0.09) +
                           13 * Math.Sin(sx * 0.39 + sy * 0.31);
            if (x > 220 + index % 20 * 2 && x < 400 + index % 20 * 2 && y > 140 && y < 240)
                value = 80 + 50 * Math.Sin(x * 0.3 + index) * Math.Cos(y * 0.2);
            frame[y * width + x] = (byte)Math.Clamp(value, 16, 235);
        }
        Array.Fill(frame, (byte)128, width * height, width * height / 2);
        return frame;
    }

    [Fact]
    public void FrameStabilizerResetsAtSceneCutsAndPreservesFlatFrames()
    {
        var stabilizer = new VideoFrameStabilizer(640, 480);
        stabilizer.Process(MotionFrame(640, 480, 0, 0), TestContext.Current.CancellationToken);
        stabilizer.Process(MotionFrame(640, 480, 8, -8), TestContext.Current.CancellationToken);
        var flat = Enumerable.Repeat((byte)235, 640 * 480 * 3 / 2).ToArray();
        Assert.Same(flat, stabilizer.Process(flat, TestContext.Current.CancellationToken));
        Assert.Equal(flat, stabilizer.Process(flat, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AutoCropLimitsCorrectionToPixelsInsideTheSourceFrame()
    {
        const int width = 640, height = 360;
        int cropX = VideoFrameStabilizer.GetCropOffset(width);
        int cropY = VideoFrameStabilizer.GetCropOffset(height);
        const double offsetX = 50, offsetY = 28, angle = 0.06;

        Assert.False(VideoFrameStabilizer.IsCropValid(width, height, cropX, cropY, offsetX, offsetY, angle));
        var limited = VideoFrameStabilizer.LimitCorrectionToCrop(
            width, height, cropX, cropY, offsetX, offsetY, angle);
        Assert.InRange(limited.X, 0d, offsetX);
        Assert.InRange(limited.Y, 0d, offsetY);
        Assert.InRange(limited.Angle, 0d, angle);
        Assert.True(VideoFrameStabilizer.IsCropValid(
            width, height, cropX, cropY, limited.X, limited.Y, limited.Angle));
        Assert.True(limited.X > 0 && limited.Y > 0 && limited.Angle > 0);

        var stabilizer = new VideoFrameStabilizer(width, height, autoCrop: true);
        byte[] frame = MotionFrame(width, height, 0, 0);
        byte[] output = stabilizer.Process(frame, TestContext.Current.CancellationToken);
        Assert.Equal(frame.Length, output.Length);
        Assert.NotSame(frame, output);
    }

    [Fact]
    public void FrameStabilizerRejectsInvalidLayoutsAndHonorsCancellation()
    {
        Assert.Throws<ArgumentException>(() => new VideoFrameStabilizer(3, 4).Process(new byte[18],
            TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => new VideoFrameStabilizer(4, 4).Process(new byte[1],
            TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            new VideoFrameStabilizer(4, 4).Process(new byte[24], cancellation.Token));
    }

    [Theory]
    [InlineData(false, 640, 480)]
    [InlineData(true, 640, 480)]
    [InlineData(false, 1920, 1080)]
    public async Task WindowsExportsAndReducesSyntheticShakeWithoutExternalTools(bool withAudio, int width, int height)
    {
        await CreateFixtureAsync(withAudio, width: width, height: height);
        byte[] original = File.ReadAllBytes(Source);
        var progress = new CaptureProgress();
        await new VideoStabilizationService().StabilizeAsync(Source, Output, progress,
            TestContext.Current.CancellationToken);
        Assert.Equal(original, File.ReadAllBytes(Source));
        Assert.True(new FileInfo(Output).Length > 0);
        var profile = await MediaEncodingProfile.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(Output));
        Assert.Equal(MediaEncodingSubtypes.H264, profile.Video.Subtype);
        Assert.Equal(withAudio, profile.Audio is not null);
        Assert.Equal((uint)width, profile.Video.Width);
        Assert.Equal((uint)height, profile.Video.Height);
        var outputClip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(Output));
        Assert.InRange(outputClip.OriginalDuration.TotalSeconds, 2.95, 3.1);
        Assert.NotEmpty(progress.Values);
        Assert.All(progress.Values, value => Assert.InRange(value, 0, 100));
        double before = await TemporalDifferenceAsync(Source);
        double after = await TemporalDifferenceAsync(Output);
        double ratio = after / before;
        TestContext.Current.TestOutputHelper?.WriteLine($"Temporal difference ratio: {ratio:F3}");
        Assert.True(ratio < 0.8,
            $"Stabilization must reduce central temporal differences by at least 20%; actual ratio {ratio:F3}.");
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task WindowsExportPreservesPortraitOrientation()
    {
        await CreateFixtureAsync(false, rotation: 90);
        var sourceProperties = await (await StorageFile.GetFileFromPathAsync(Source)).Properties.GetVideoPropertiesAsync();
        Assert.Equal(Windows.Storage.FileProperties.VideoOrientation.Rotate90, sourceProperties.Orientation);
        await new VideoStabilizationService().StabilizeAsync(Source, Output,
            cancellationToken: TestContext.Current.CancellationToken);
        var outputProperties = await (await StorageFile.GetFileFromPathAsync(Output)).Properties.GetVideoPropertiesAsync();
        Assert.Equal(sourceProperties.Orientation, outputProperties.Orientation);
        Assert.Equal(sourceProperties.Width, outputProperties.Width);
        Assert.Equal(sourceProperties.Height, outputProperties.Height);
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task WindowsExportPreservesAndIndexesXmpSidecarMetadata()
    {
        await CreateFixtureAsync(false);
        var dateTaken = new DateTime(2024, 4, 18, 14, 22, 10, DateTimeKind.Utc);
        await MetadataWriterService.WriteRatingAsync(Source, 4);
        await MetadataWriterService.WriteFlagAsync(Source, true);
        await MetadataWriterService.WriteCaptionAsync(Source, "A tagged video");
        await MetadataWriterService.WriteDateTakenAsync(Source, dateTaken);
        await TagWriterService.WriteTagsToSidecarAsync(Source, ["family", "vacation"]);
        var sourceSidecar = Path.ChangeExtension(Source, ".xmp");

        await new VideoStabilizationService().StabilizeAsync(Source, Output,
            cancellationToken: TestContext.Current.CancellationToken);

        var outputSidecar = Path.ChangeExtension(Output, ".xmp");
        Assert.Equal(File.ReadAllBytes(sourceSidecar), File.ReadAllBytes(outputSidecar));
        var metadata = new MetadataReaderService();
        var entry = metadata.ReadMetadata(Output);
        Assert.Equal(4, entry.Rating);
        Assert.True(entry.IsFlagged);
        Assert.Equal("A tagged video", entry.Caption);
        Assert.Equal(dateTaken, entry.DateTaken);
        Assert.Equal(["family", "vacation"], metadata.ReadTags(Output));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task InvalidMediaIsReportedAndCleanedUp()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => new VideoStabilizationService().StabilizeAsync(Source, Output,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Output));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task OutputCreatedDuringExportIsNotOverwritten()
    {
        await CreateFixtureAsync(false);
        await Assert.ThrowsAsync<IOException>(() =>
            new VideoStabilizationService().StabilizeAsync(Source, Output,
                new CreateOutputOnProgress(Output), TestContext.Current.CancellationToken));
        Assert.Equal("another file", File.ReadAllText(Output));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task WindowsCancellationRemovesPartialCopy()
    {
        await CreateFixtureAsync(false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var progress = new CancelOnProgress(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new VideoStabilizationService().StabilizeAsync(Source, Output, progress, cancellation.Token));
        Assert.True(progress.WasReported);
        Assert.False(File.Exists(Output));
        AssertNoTemporaryFiles();
    }

    private void AssertNoTemporaryFiles() =>
        Assert.Empty(Directory.GetFiles(_directory, ".photolibrarian-*"));

    private static MediaEncodingProfile FixtureProfile(bool withAudio)
    {
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
        profile.Video.Width = 640;
        profile.Video.Height = 480;
        profile.Video.FrameRate.Numerator = 24;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.Bitrate = 4_000_000;
        if (!withAudio) profile.Audio = null;
        return profile;
    }

    private static byte[] MotionFrame(int width, int height, int dx, int dy)
    {
        var frame = new byte[width * height * 3 / 2];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int blockX = (x + dx + 64) / 16;
            int blockY = (y + dy + 64) / 16;
            frame[y * width + x] = (byte)(32 + (uint)(blockX * 12345 ^ blockY * 54321) % 192);
        }
        Array.Fill(frame, (byte)128, width * height, width * height / 2);
        return frame;
    }

    private async Task CreateFixtureAsync(bool withAudio, uint rotation = 0, int width = 640, int height = 480)
    {
        const int frameCount = 72;
        var video = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)width, (uint)height);
        video.FrameRate.Numerator = 24;
        video.FrameRate.Denominator = 1;
        var videoDescriptor = new VideoStreamDescriptor(video);
        var audioDescriptor = new AudioStreamDescriptor(AudioEncodingProperties.CreatePcm(48_000, 1, 16));
        var source = withAudio
            ? new MediaStreamSource(videoDescriptor, audioDescriptor)
            : new MediaStreamSource(videoDescriptor);
        source.BufferTime = TimeSpan.Zero;
        source.Duration = TimeSpan.FromSeconds(3);
        source.Starting += (_, args) => args.Request.SetActualStartPosition(TimeSpan.Zero);
        int videoFrame = 0, audioFrame = 0;
        var random = new Random(77);
        int blockWidth = (width + 64) / 16;
        byte[] blocks = new byte[blockWidth * ((height + 64) / 16)];
        random.NextBytes(blocks);
        source.SampleRequested += (_, args) =>
        {
            bool isVideo = args.Request.StreamDescriptor is VideoStreamDescriptor;
            int frame = isVideo ? videoFrame++ : audioFrame++;
            if (frame >= frameCount) return;
            byte[] data;
            if (isVideo)
            {
                data = new byte[width * height * 4];
                int dx = 24 + (int)(8 * Math.Sin(frame * 1.7));
                int dy = 24 + (int)(8 * Math.Cos(frame * 1.7));
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    byte value = blocks[(y + dy) / 16 * blockWidth + (x + dx) / 16];
                    int index = (y * width + x) * 4;
                    data[index] = data[index + 1] = data[index + 2] = value;
                    data[index + 3] = 255;
                }
            }
            else
            {
                data = new byte[2000 * 2];
                for (int i = 0; i < 2000; i++)
                {
                    short value = (short)(4000 * Math.Sin(2 * Math.PI * 440 * (frame * 2000 + i) / 48_000));
                    data[i * 2] = (byte)value;
                    data[i * 2 + 1] = (byte)(value >> 8);
                }
            }
            var sample = MediaStreamSample.CreateFromBuffer(data.AsBuffer(), TimeSpan.FromSeconds(frame / 24.0));
            sample.Duration = TimeSpan.FromSeconds(1 / 24.0);
            args.Request.Sample = sample;
        };
        using var file = new FileStream(Source, FileMode.Create, FileAccess.ReadWrite);
        using var output = file.AsRandomAccessStream();
        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = false };
        var profile = FixtureProfile(withAudio);
        profile.Video.Width = (uint)width;
        profile.Video.Height = (uint)height;
        if (rotation != 0)
            profile.Video.Properties[new Guid("C380465D-2271-428C-9B83-ECEA3B4A85C1")] = rotation;
        var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, output, profile)
            .AsTask(TestContext.Current.CancellationToken);
        Assert.True(prepared.CanTranscode, prepared.FailureReason.ToString());
        await prepared.TranscodeAsync().AsTask(TestContext.Current.CancellationToken);
    }

    private static async Task<double> TemporalDifferenceAsync(string path)
    {
        var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(path));
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        byte[]? previous = null;
        long difference = 0;
        for (int frame = 20; frame < 60; frame += 2)
        {
            using var thumbnail = await composition.GetThumbnailAsync(
                TimeSpan.FromSeconds(frame / 24.0), 640, 480, VideoFramePrecision.NearestFrame);
            var decoder = await BitmapDecoder.CreateAsync(thumbnail);
            var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            byte[] current = pixels.DetachPixelData();
            int maximumColorDifference = 0;
            for (int pixel = 0; pixel < current.Length; pixel += 4)
            {
                maximumColorDifference = Math.Max(maximumColorDifference, Math.Max(
                    Math.Abs(current[pixel] - current[pixel + 1]), Math.Abs(current[pixel] - current[pixel + 2])));
            }
            Assert.InRange(maximumColorDifference, 0, 12);
            if (previous is not null)
                for (int y = 80; y < 400; y++)
                for (int x = 80; x < 560; x++)
                {
                    int index = (y * 640 + x) * 4;
                    difference += Math.Abs(current[index] - previous[index]);
                }
            previous = current;
        }
        return difference;
    }

    private sealed class CaptureProgress : IProgress<double>
    {
        public List<double> Values { get; } = [];
        public void Report(double value) => Values.Add(value);
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

    private sealed class CreateOutputOnProgress(string output) : IProgress<double>
    {
        public void Report(double value) => File.WriteAllText(output, "another file");
    }
}
