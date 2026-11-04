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
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class VideoEditingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-Edits-{Guid.NewGuid():N}");
    private string Source => Path.Combine(_directory, "source.mp4");
    private string Output => Path.Combine(_directory, "edited.mp4");
    private static readonly Guid RotationKey = new("C380465D-2271-428C-9B83-ECEA3B4A85C1");
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

    [Fact]
    public async Task CancellationFailureAndCollisionsNeverPublishOrChangeExistingFiles()
    {
        await CreateFixtureAsync(0, withAudio: true);
        using var editing = await VideoEditingService.CreateAsync(Source, new(1, true), Token);
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

    private async Task CreateFixtureAsync(uint orientation, bool withAudio)
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
        if (!withAudio) profile.Audio = null;
        using var file = new FileStream(Source, FileMode.CreateNew, FileAccess.ReadWrite);
        using var output = file.AsRandomAccessStream();
        var prepared = await new MediaTranscoder { HardwareAccelerationEnabled = false }
            .PrepareMediaStreamSourceTranscodeAsync(stream, output, profile).AsTask(Token);
        Assert.True(prepared.CanTranscode, prepared.FailureReason.ToString());
        await prepared.TranscodeAsync().AsTask(Token);
    }
}
