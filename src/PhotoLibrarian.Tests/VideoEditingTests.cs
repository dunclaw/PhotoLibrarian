// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using PhotoLibrarian.Core.Services;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Vortice.MediaFoundation;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class VideoEditingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-Edits-{Guid.NewGuid():N}");
    private string Source => Path.Combine(_directory, "source.mp4");
    private string Output => Path.Combine(_directory, "edited.mp4");
    private static readonly Guid RotationKey = new("C380465D-2271-428C-9B83-ECEA3B4A85C1");
    private static readonly Guid VideoNominalRangeKey = MediaTypeAttributeKeys.VideoNominalRange;
    private CancellationToken Token => TestContext.Current.CancellationToken;

    public VideoEditingTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    public static IEnumerable<object[]> RotationCases()
    {
        foreach (int orientation in new[] { 0, 90, 180, 270 })
        foreach (int turns in new[] { 0, 1, 2, 3 })
            yield return [orientation, turns, turns % 2 == 0];
    }

    [Theory]
    [MemberData(nameof(RotationCases))]
    public async Task PreviewAndExportRotatePixelsPreserveTimingAndHandleAudio(
        int orientation, int turns, bool removeAudio)
    {
        await CreateFixtureAsync((uint)orientation, withAudio: true);
        byte[] sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(Source, Token));
        string sidecar = Path.ChangeExtension(Source, ".xmp");
        const string metadata = """
            <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
            <rdf:Description xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:subject><rdf:Bag><rdf:li>fixture-tag</rdf:li></rdf:Bag></dc:subject></rdf:Description>
            </rdf:RDF></x:xmpmeta>
            """;
        await File.WriteAllTextAsync(sidecar, metadata, Token);
        using var editing = await VideoEditingService.CreateAsync(Source, new(turns, removeAudio), Token);
        int totalTurns = (orientation / 90 + turns) % 4;
        uint width = totalTurns % 2 == 0 ? 192u : 128u;
        uint height = totalTurns % 2 == 0 ? 128u : 192u;
        int[] expected = totalTurns switch
        {
            0 => [0, 1, 2, 3],
            1 => [2, 0, 3, 1],
            2 => [3, 2, 1, 0],
            _ => [1, 3, 0, 2]
        };
        var progress = new List<double>();
        await editing.PreparePreviewAsync(new CallbackProgress(value => progress.Add(value)), Token);
        var preview = new MediaComposition();
        preview.Clips.Add(await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(editing.PreviewPath!)));
        Assert.Equal(expected, await ReadQuadrantsAsync(preview, width, height));
        byte[] previewHash = SHA256.HashData(await File.ReadAllBytesAsync(editing.PreviewPath!, Token));
        await editing.ExportAsync(Output, Token);
        var file = await StorageFile.GetFileFromPathAsync(Output);
        var profile = await MediaEncodingProfile.CreateFromFileAsync(file);
        Assert.Equal(192u, profile.Video.Width);
        Assert.Equal(128u, profile.Video.Height);
        Assert.Equal(!removeAudio, profile.Audio is not null);
        Assert.Equal(24u, profile.Video.FrameRate.Numerator / profile.Video.FrameRate.Denominator);
        var properties = await file.Properties.GetVideoPropertiesAsync();
        Assert.InRange(properties.Duration.TotalSeconds, 1.9, 2.1);
        Assert.Equal((uint)totalTurns * 90, (uint)properties.Orientation);
        var composition = new MediaComposition();
        composition.Clips.Add(await MediaClip.CreateFromFileAsync(file));
        Assert.Equal(expected, await ReadQuadrantsAsync(composition, width, height));
        AssertDecodedTimingAndAudio(Output, withAudio: !removeAudio);
        Assert.NotEmpty(progress);
        Assert.All(progress, value => Assert.InRange(value, 0, 100));
        Assert.Contains("fixture-tag", await File.ReadAllTextAsync(Path.ChangeExtension(Output, ".xmp"), Token));
        Assert.Equal(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(Source, Token)));
        Assert.Equal(metadata, await File.ReadAllTextAsync(sidecar, Token));
        Assert.Equal(previewHash, SHA256.HashData(await File.ReadAllBytesAsync(Output, Token)));
        string previewPath = editing.PreviewPath!;
        editing.Dispose();
        Assert.False(File.Exists(previewPath));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task SilentInputRemainsSilentAndNamingSkipsBothKindsOfCollision()
    {
        await CreateFixtureAsync(0, withAudio: false);
        using var editing = await VideoEditingService.CreateAsync(Source, new(1), Token);
        await editing.PreparePreviewAsync(token: Token);
        await editing.ExportAsync(Output, token: Token);
        var profile = await MediaEncodingProfile.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(Output));
        Assert.Null(profile.Audio);
        string first = VideoEditingService.GetAvailableOutputPath(Source);
        File.WriteAllText(Path.ChangeExtension(first, ".xmp"), "occupied");
        string second = VideoEditingService.GetAvailableOutputPath(Source);
        Assert.NotEqual(first, second);
        Directory.CreateDirectory(second);
        Assert.NotEqual(second, VideoEditingService.GetAvailableOutputPath(Source));
    }

    [Theory]
    [InlineData(50, 2.0, 220)]
    [InlineData(75, 4.0 / 3.0, 330)]
    public async Task SlowMotionKeepsAudioSynchronizedAndExportsThePreparedPreview(
        int playbackSpeedPercent, double expectedDurationSeconds, int expectedToneFrequency)
    {
        await CreateFixtureAsync(0, withAudio: true);
        byte[] sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(Source, Token));
        using var editing = await VideoEditingService.CreateAsync(Source,
            new(TrimStart: TimeSpan.FromSeconds(0.5), TrimEnd: TimeSpan.FromSeconds(1.5),
                PlaybackSpeedPercent: playbackSpeedPercent), Token);

        Assert.InRange(editing.Duration.TotalSeconds, expectedDurationSeconds - 0.01, expectedDurationSeconds + 0.01);
        await editing.PreparePreviewAsync(token: Token);
        var preview = await StorageFile.GetFileFromPathAsync(editing.PreviewPath!);
        var previewProperties = await preview.Properties.GetVideoPropertiesAsync();
        Assert.InRange(previewProperties.Duration.TotalSeconds,
            expectedDurationSeconds - 0.1, expectedDurationSeconds + 0.1);
        byte[] previewHash = SHA256.HashData(await File.ReadAllBytesAsync(editing.PreviewPath!, Token));

        await editing.ExportAsync(Output, Token);
        Assert.Equal(previewHash, SHA256.HashData(await File.ReadAllBytesAsync(Output, Token)));
        var outputFile = await StorageFile.GetFileFromPathAsync(Output);
        var outputProfile = await MediaEncodingProfile.CreateFromFileAsync(outputFile);
        Assert.Equal(24u, outputProfile.Video.FrameRate.Numerator / outputProfile.Video.FrameRate.Denominator);
        Assert.NotNull(outputProfile.Audio);
        var outputProperties = await outputFile.Properties.GetVideoPropertiesAsync();
        Assert.InRange(outputProperties.Duration.TotalSeconds,
            expectedDurationSeconds - 0.1, expectedDurationSeconds + 0.1);

        using (var reader = new MediaFoundationFrameReader(Output, 192, 128, includeAudio: true))
        {
            long lastVideoEnd = 0;
            while (reader.Read(video: true, Token) is { } frame)
                lastVideoEnd = frame.Timestamp + frame.Duration;
            Assert.InRange(TimeSpan.FromTicks(lastVideoEnd).TotalSeconds,
                expectedDurationSeconds - 0.1, expectedDurationSeconds + 0.1);

            long lastAudioEnd = 0;
            long energy = 0;
            long samples = 0;
            int crossings = 0;
            int previous = 0;
            while (reader.Read(video: false, Token) is { } audio)
            {
                lastAudioEnd = audio.Timestamp + audio.Duration;
                for (int offset = 0; offset < audio.Data.Length; offset += checked((int)reader.AudioChannels * 2))
                {
                    short value = BitConverter.ToInt16(audio.Data, offset);
                    energy += (long)value * value;
                    if (previous < 0 && value >= 0) crossings++;
                    previous = value;
                    samples++;
                }
            }

            Assert.InRange(TimeSpan.FromTicks(lastAudioEnd).TotalSeconds,
                expectedDurationSeconds - 0.1, expectedDurationSeconds + 0.1);
            Assert.InRange(Math.Abs(TimeSpan.FromTicks(lastVideoEnd - lastAudioEnd).TotalSeconds), 0, 0.1);
            Assert.InRange(Math.Sqrt(energy / (double)samples), 1500, 4000);
            double audioDuration = samples / (double)reader.AudioRate;
            Assert.InRange(crossings / audioDuration, expectedToneFrequency - 25, expectedToneFrequency + 25);
        }

        Assert.Equal(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(Source, Token)));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task BrightnessContrastPreviewAndExportMatchAndPreserveSourceMetadata()
    {
        await CreateFixtureAsync(0, withAudio: true);
        using (var reader = new MediaFoundationFrameReader(Source, 192, 128, includeAudio: false))
            Assert.Equal(2u, reader.VideoNominalRange);
        byte[] sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(Source, Token));
        string sourceSidecar = Path.ChangeExtension(Source, ".xmp");
        const string metadata = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:subject><rdf:Bag><rdf:li>fixture-tag</rdf:li></rdf:Bag></dc:subject></rdf:Description></rdf:RDF></x:xmpmeta>";
        await File.WriteAllTextAsync(sourceSidecar, metadata, Token);
        int[] sourceLuma = ReadQuadrantLuma(Source);
        var edits = new[]
        {
            new VideoEditOptions(),
            new VideoEditOptions(1, Brightness: 30),
            new VideoEditOptions(2, Brightness: -30),
            new VideoEditOptions(RemoveAudio: true, Contrast: 50),
            new VideoEditOptions(3, RemoveAudio: true, Contrast: 150)
        };
        int[]? neutralLuma = null;
        int[]? lowContrastLuma = null;
        int[]? highContrastLuma = null;

        for (int index = 0; index < edits.Length; index++)
        {
            VideoEditOptions options = edits[index];
            string output = Path.Combine(_directory, $"adjusted-{index}.mp4");
            using var editing = await VideoEditingService.CreateAsync(Source, options, Token);
            await editing.PreparePreviewAsync(token: Token);
            int[] previewLuma = ReadQuadrantLuma(editing.PreviewPath!);
            byte[] previewHash = SHA256.HashData(await File.ReadAllBytesAsync(editing.PreviewPath!, Token));
            await editing.ExportAsync(output, Token);

            Assert.Equal(previewLuma, ReadQuadrantLuma(output));
            Assert.Equal(previewHash, SHA256.HashData(await File.ReadAllBytesAsync(output, Token)));
            AssertDecodedTimingAndAudio(output, withAudio: !options.RemoveAudio);
            Assert.Contains("fixture-tag", await File.ReadAllTextAsync(Path.ChangeExtension(output, ".xmp"), Token));

            switch (index)
            {
                case 0:
                    neutralLuma = previewLuma;
                    Assert.Equal(sourceLuma.Length, neutralLuma.Length);
                    for (int quadrant = 0; quadrant < sourceLuma.Length; quadrant++)
                        Assert.InRange(Math.Abs(sourceLuma[quadrant] - neutralLuma[quadrant]), 0, 10);
                    break;
                case 1:
                    Assert.True(AverageLuma(previewLuma) > AverageLuma(neutralLuma!));
                    break;
                case 2:
                    Assert.True(AverageLuma(previewLuma) < AverageLuma(neutralLuma!));
                    break;
                case 3:
                    lowContrastLuma = previewLuma;
                    break;
                case 4:
                    highContrastLuma = previewLuma;
                    break;
            }
        }

        Assert.True(LumaSpread(lowContrastLuma!) < LumaSpread(neutralLuma!));
        Assert.True(LumaSpread(highContrastLuma!) > LumaSpread(lowContrastLuma!));
        Assert.Equal(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(Source, Token)));
        Assert.Equal(metadata, await File.ReadAllTextAsync(sourceSidecar, Token));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task CombinedToneRotationStabilizationAndTrimUseTheExactPreparedFile()
    {
        await CreateFixtureAsync(90, withAudio: true);
        byte[] sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(Source, Token));
        using var editing = await VideoEditingService.CreateAsync(Source,
            new(ClockwiseQuarterTurns: 1, Brightness: 20, Contrast: 120,
                Stabilization: StabilizationOptions.Default,
                TrimStart: TimeSpan.FromSeconds(0.5), TrimEnd: TimeSpan.FromSeconds(1.5)), Token);
        await editing.PreparePreviewAsync(token: Token);
        byte[] previewHash = SHA256.HashData(await File.ReadAllBytesAsync(editing.PreviewPath!, Token));
        await editing.ExportAsync(Output, Token);
        Assert.Equal(previewHash, SHA256.HashData(await File.ReadAllBytesAsync(Output, Token)));
        var properties = await (await StorageFile.GetFileFromPathAsync(Output)).Properties.GetVideoPropertiesAsync();
        Assert.Equal(180u, (uint)properties.Orientation);
        Assert.InRange(properties.Duration.TotalSeconds, 0.95, 1.05);
        using var reader = new MediaFoundationFrameReader(Output, 192, 128, includeAudio: true);
        int frames = 0;
        long first = -1;
        long lastEnd = 0;
        while (reader.Read(video: true, Token) is { } frame)
        {
            if (frames++ == 0) first = frame.Timestamp;
            lastEnd = frame.Timestamp + frame.Duration;
        }
        Assert.Equal(24, frames);
        Assert.InRange(first, 0, TimeSpan.TicksPerSecond / 24);
        Assert.InRange(lastEnd, TimeSpan.FromSeconds(0.95).Ticks, TimeSpan.FromSeconds(1.05).Ticks);
        long firstAudio = -1;
        long lastAudioEnd = 0;
        while (reader.Read(video: false, Token) is { } audio)
        {
            if (firstAudio < 0) firstAudio = audio.Timestamp;
            lastAudioEnd = audio.Timestamp + audio.Duration;
        }
        Assert.InRange(firstAudio, 0, TimeSpan.FromMilliseconds(30).Ticks);
        Assert.InRange(lastAudioEnd, TimeSpan.FromSeconds(0.95).Ticks, TimeSpan.FromSeconds(1.05).Ticks);
        Assert.Equal(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(Source, Token)));
    }

    [Fact]
    public async Task FullRangeSourceRangeIsPreservedForAdjustment()
    {
        await CreateFixtureAsync(0, withAudio: false, videoNominalRange: 1);
        using (var reader = new MediaFoundationFrameReader(Source, 192, 128, includeAudio: false))
        {
            Assert.Equal(1u, reader.SourceVideoNominalRange);
            Assert.Equal(1u, reader.VideoNominalRange);
        }
        Assert.True(ReadQuadrantLuma(Source)[3] > 245);

        byte[] sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(Source, Token));
        using var editing = await VideoEditingService.CreateAsync(Source, new(Brightness: 20), Token);
        await editing.PreparePreviewAsync(token: Token);
        using (var reader = new MediaFoundationFrameReader(editing.PreviewPath!, 192, 128, includeAudio: false))
        {
            Assert.Equal(1u, reader.SourceVideoNominalRange);
            Assert.Equal(1u, reader.VideoNominalRange);
        }
        byte[] previewHash = SHA256.HashData(await File.ReadAllBytesAsync(editing.PreviewPath!, Token));
        await editing.ExportAsync(Output, Token);
        Assert.Equal(previewHash, SHA256.HashData(await File.ReadAllBytesAsync(Output, Token)));
        Assert.Equal(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(Source, Token)));
    }

    [Fact]
    public void Nv12ToneAdjustmentLeavesNeutralFramesUnchangedAndPreservesChroma()
    {
        byte[] source = [16, 64, 128, 235, 80, 180];
        byte[] neutral = (byte[])source.Clone();
        VideoToneAdjustment.ApplyBrightnessContrastNv12(neutral, 2, 2, 2, 0, 100);
        Assert.Equal(source, neutral);

        byte[] brighter = (byte[])source.Clone();
        VideoToneAdjustment.ApplyBrightnessContrastNv12(brighter, 2, 2, 2, 20, 100);
        Assert.True(brighter[1] > source[1]);
        Assert.Equal(source.AsSpan(4).ToArray(), brighter.AsSpan(4).ToArray());

        byte[] darker = (byte[])source.Clone();
        VideoToneAdjustment.ApplyBrightnessContrastNv12(darker, 2, 2, 2, -20, 100);
        Assert.True(darker[1] < source[1]);
        Assert.Equal(source.AsSpan(4).ToArray(), darker.AsSpan(4).ToArray());

        byte[] lowerContrast = (byte[])source.Clone();
        VideoToneAdjustment.ApplyBrightnessContrastNv12(lowerContrast, 2, 2, 2, 0, 50);
        Assert.True(lowerContrast[0] > source[0]);
        Assert.True(lowerContrast[3] < source[3]);
        Assert.Equal(source.AsSpan(4).ToArray(), lowerContrast.AsSpan(4).ToArray());

        byte[] fullRange = [0, 64, 128, 255, 80, 180];
        VideoToneAdjustment.ApplyBrightnessContrastNv12(fullRange, 2, 2, 1, 0, 50);
        Assert.True(fullRange[0] > 0);
        Assert.True(fullRange[3] < 255);
        Assert.Equal(new byte[] { 80, 180 }, fullRange.AsSpan(4).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VideoToneAdjustment.ApplyBrightnessContrastNv12(source, 0, 2, 2, 0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VideoToneAdjustment.ApplyBrightnessContrastNv12(source, 2, -2, 2, 0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VideoToneAdjustment.ApplyBrightnessContrastNv12(source, 3, 2, 2, 0, 100));
        Assert.Throws<InvalidDataException>(() =>
            VideoToneAdjustment.ApplyBrightnessContrastNv12(source[..^1], 2, 2, 2, 0, 100));
        Assert.Throws<InvalidDataException>(() =>
            VideoToneAdjustment.ApplyBrightnessContrastNv12([.. source, 0], 2, 2, 2, 0, 100));
    }

    [Fact]
    public async Task CancellationFailureAndCollisionsNeverPublishOrChangeExistingFiles()
    {
        await CreateFixtureAsync(0, withAudio: true);
        using var editing = await VideoEditingService.CreateAsync(Source, new(1, true, 25, 120), Token);
        byte[] source = await File.ReadAllBytesAsync(Source, Token);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => editing.PreparePreviewAsync(token: canceled.Token));
        using var during = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task? cancelTask = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            editing.PreparePreviewAsync(new CallbackProgress(_ =>
            {
                cancelTask ??= Task.Run(() => during.Cancel(), CancellationToken.None);
            }), during.Token));
        if (cancelTask is not null) await cancelTask;
        Assert.Null(editing.PreviewPath);
        Assert.False(File.Exists(editing.TemporaryPreviewPath));
        Assert.False(File.Exists(Output));
        Assert.False(File.Exists(Path.ChangeExtension(Output, ".xmp")));
        AssertNoTemporaryFiles();
        await editing.PreparePreviewAsync(token: Token);
        await Assert.ThrowsAsync<IOException>(() => editing.ExportAsync(Source, token: Token));
        File.WriteAllText(Output, "existing");
        await Assert.ThrowsAsync<IOException>(() => editing.ExportAsync(Output, token: Token));
        Assert.Equal("existing", File.ReadAllText(Output));
        File.Delete(Output);
        File.WriteAllText(Path.ChangeExtension(Output, ".xmp"), "existing sidecar");
        await Assert.ThrowsAsync<IOException>(() => editing.ExportAsync(Output, token: Token));
        Assert.Equal("existing sidecar", File.ReadAllText(Path.ChangeExtension(Output, ".xmp")));
        File.Delete(Path.ChangeExtension(Output, ".xmp"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => editing.ExportAsync(Output, canceled.Token));
        Assert.False(File.Exists(Output));
        Assert.Equal(source, await File.ReadAllBytesAsync(Source, Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => VideoEditingService.CreateAsync(Source, new(4), Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            VideoEditingService.CreateAsync(Source, new(Brightness: 101), Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            VideoEditingService.CreateAsync(Source, new(Contrast: 201), Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            VideoEditingService.CreateAsync(Source, new(PlaybackSpeedPercent: 60), Token));
        string invalid = Path.Combine(_directory, "invalid.mp4");
        File.WriteAllText(invalid, "not media");
        await Assert.ThrowsAnyAsync<Exception>(() => VideoEditingService.CreateAsync(invalid, new(1), Token));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task PreparationAndDisposalNeverDeleteAnUnownedTemporaryFile()
    {
        await CreateFixtureAsync(0, withAudio: false);
        using var editing = await VideoEditingService.CreateAsync(Source, new(1), Token);
        string occupied = editing.TemporaryPreviewPath;
        try
        {
            File.WriteAllText(occupied, "preexisting file");
            await Assert.ThrowsAsync<IOException>(() => editing.PreparePreviewAsync(token: Token));
            editing.Dispose();
            Assert.Equal("preexisting file", File.ReadAllText(occupied));
        }
        finally
        {
            File.Delete(occupied);
        }
    }

    [Fact]
    public async Task MalformedSidecarCannotProducePreviewAndDisposedSessionCannotRestart()
    {
        await CreateFixtureAsync(0, withAudio: false);
        File.WriteAllText(Path.ChangeExtension(Source, ".xmp"), "<invalid");
        using var editing = await VideoEditingService.CreateAsync(Source, new(1), Token);
        await Assert.ThrowsAsync<XmpCore.XmpException>(() => editing.PreparePreviewAsync(token: Token));
        Assert.False(File.Exists(editing.TemporaryPreviewPath));
        editing.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => editing.PreparePreviewAsync(token: Token));
    }

    private void AssertDecodedTimingAndAudio(string path, bool withAudio)
    {
        using var reader = new MediaFoundationFrameReader(path, 192, 128, includeAudio: withAudio);
        int frames = 0;
        long lastTimestamp = -1;
        while (reader.Read(video: true, Token) is { } frame)
        {
            Assert.True(frame.Timestamp > lastTimestamp);
            Assert.InRange(frame.Timestamp, 0, TimeSpan.FromSeconds(2.1).Ticks);
            Assert.True(frame.Duration > 0);
            lastTimestamp = frame.Timestamp;
            frames++;
        }
        Assert.Equal(48, frames);
        if (!withAudio) return;
        lastTimestamp = long.MinValue;
        long lastEnd = 0, energy = 0, samples = 0;
        int crossings = 0, previous = 0;
        while (reader.Read(video: false, Token) is { } audio)
        {
            Assert.True(audio.Timestamp > lastTimestamp);
            Assert.InRange(audio.Timestamp, -TimeSpan.FromSeconds(0.1).Ticks, TimeSpan.FromSeconds(2.2).Ticks);
            lastTimestamp = audio.Timestamp;
            lastEnd = audio.Timestamp + audio.Duration;
            for (int offset = 0; offset < audio.Data.Length; offset += checked((int)reader.AudioChannels * 2))
            {
                short value = BitConverter.ToInt16(audio.Data, offset);
                energy += (long)value * value;
                if (previous < 0 && value >= 0) crossings++;
                previous = value;
                samples++;
            }
        }
        Assert.InRange(TimeSpan.FromTicks(lastEnd).TotalSeconds, 1.9, 2.2);
        Assert.InRange(Math.Sqrt(energy / (double)samples), 1500, 4000);
        double duration = samples / (double)reader.AudioRate;
        Assert.InRange(crossings / duration, 400, 480);
    }

    private void AssertNoTemporaryFiles() =>
        Assert.Empty(Directory.GetFiles(_directory, ".photolibrarian-*"));

    private sealed class CallbackProgress(Action<double> callback) : IProgress<double>
    {
        public void Report(double value) => callback(value);
    }

    private int[] ReadQuadrantLuma(string path)
    {
        using var reader = new MediaFoundationFrameReader(path, 192, 128, includeAudio: false);
        byte[] frame = reader.Read(video: true, Token)!.Value.Data;
        int[] means = new int[4];
        for (int quadrant = 0; quadrant < means.Length; quadrant++)
        {
            int xStart = quadrant % 2 * 96;
            int yStart = quadrant / 2 * 64;
            long sum = 0;
            for (int y = yStart; y < yStart + 64; y++)
            for (int x = xStart; x < xStart + 96; x++)
                sum += frame[y * 192 + x];
            means[quadrant] = (int)(sum / (96 * 64));
        }
        return means;
    }

    private static int LumaSpread(int[] values) => values.Max() - values.Min();

    private static double AverageLuma(int[] values) => values.Average();

    private async Task<int[]> ReadQuadrantsAsync(MediaComposition composition, uint width, uint height)
    {
        using var image = await composition.GetThumbnailAsync(
            TimeSpan.FromSeconds(0.5), (int)width, (int)height, VideoFramePrecision.NearestFrame).AsTask(Token);
        var decoder = await BitmapDecoder.CreateAsync(image);
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        byte[] data = pixels.DetachPixelData();
        var result = new List<int>();
        foreach (int y in new[] { (int)height / 4, (int)height * 3 / 4 })
        foreach (int x in new[] { (int)width / 4, (int)width * 3 / 4 })
        {
            int offset = (y * (int)width + x) * 4;
            int b = data[offset], g = data[offset + 1], r = data[offset + 2];
            result.Add(r > 180 && g > 180 && b > 180 ? 3 : r > g && r > b ? 0 : g > b ? 1 : 2);
            Assert.True(Math.Max(r, Math.Max(g, b)) > 180, $"Unexpected dark/letterboxed quadrant: {r},{g},{b}");
        }
        return result.ToArray();
    }

    private async Task CreateFixtureAsync(uint orientation, bool withAudio, uint? videoNominalRange = null)
    {
        const int width = 192, height = 128, frames = 48;
        var video = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, width, height);
        video.FrameRate.Numerator = 24;
        video.FrameRate.Denominator = 1;
        var descriptor = new VideoStreamDescriptor(video);
        var audio = new AudioStreamDescriptor(AudioEncodingProperties.CreatePcm(48_000, 1, 16));
        var stream = withAudio ? new MediaStreamSource(descriptor, audio) : new MediaStreamSource(descriptor);
        stream.BufferTime = TimeSpan.Zero;
        stream.Duration = TimeSpan.FromSeconds(2);
        stream.Starting += (_, args) => args.Request.SetActualStartPosition(TimeSpan.Zero);
        int videoIndex = 0, audioIndex = 0;
        stream.SampleRequested += (_, args) =>
        {
            bool isVideo = args.Request.StreamDescriptor is VideoStreamDescriptor;
            int frame = isVideo ? videoIndex++ : audioIndex++;
            if (frame >= frames) return;
            byte[] data = new byte[isVideo ? width * height * 4 : 4000];
            if (isVideo)
            {
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int quadrant = (y >= height / 2 ? 2 : 0) + (x >= width / 2 ? 1 : 0);
                    // RGB Media Foundation buffers use a bottom-up row layout.
                    int offset = ((height - 1 - y) * width + x) * 4;
                    data[offset] = quadrant is 2 or 3 ? (byte)255 : (byte)0;
                    data[offset + 1] = quadrant is 1 or 3 ? (byte)255 : (byte)0;
                    data[offset + 2] = quadrant is 0 or 3 ? (byte)255 : (byte)0;
                    data[offset + 3] = 255;
                }
            }
            else
            {
                for (int sampleIndex = 0; sampleIndex < 2000; sampleIndex++)
                {
                    short value = (short)(4000 * Math.Sin(2 * Math.PI * 440 * (frame * 2000 + sampleIndex) / 48_000));
                    data[sampleIndex * 2] = (byte)value;
                    data[sampleIndex * 2 + 1] = (byte)(value >> 8);
                }
            }
            var sample = MediaStreamSample.CreateFromBuffer(data.AsBuffer(), TimeSpan.FromSeconds(frame / 24.0));
            sample.Duration = TimeSpan.FromSeconds(1 / 24.0);
            args.Request.Sample = sample;
        };
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
        profile.Video.Width = width;
        profile.Video.Height = height;
        profile.Video.FrameRate.Numerator = 24;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.Bitrate = 1_000_000;
        profile.Video.Properties[RotationKey] = orientation;
        if (videoNominalRange is { } range)
            profile.Video.Properties[VideoNominalRangeKey] = range;
        if (!withAudio) profile.Audio = null;
        using var file = new FileStream(Source, FileMode.CreateNew, FileAccess.ReadWrite);
        using var output = file.AsRandomAccessStream();
        var prepared = await new MediaTranscoder { HardwareAccelerationEnabled = false }
            .PrepareMediaStreamSourceTranscodeAsync(stream, output, profile).AsTask(Token);
        Assert.True(prepared.CanTranscode, prepared.FailureReason.ToString());
        await prepared.TranscodeAsync().AsTask(Token);
    }
}
