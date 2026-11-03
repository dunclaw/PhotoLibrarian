// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Text;
using PhotoLibrarian.Core.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class PanoramaStitchingTests
{
    [Fact]
    public void Stitch_OverlappingExposureAdjustedPhotos_ProducesCroppedPanorama()
    {
        const int sceneWidth = 480;
        const int height = 240;
        const int photoWidth = 360;
        var scene = CreateScene(sceneWidth, height, 813);
        var first = Crop(scene, sceneWidth, 0, photoWidth, height);
        var second = Crop(scene, sceneWidth, 120, photoWidth, height);
        Darken(second, 0.72);
        AddOcclusion(second, photoWidth, 150, 48, 62, 92, seed: 258);
        var firstOriginal = (byte[])first.Clone();
        var secondOriginal = (byte[])second.Clone();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = PanoramaStitcher.Stitch(
            Image(first, photoWidth, height),
            Image(second, photoWidth, height),
            "first.jpg",
            "second.jpg",
            progress: null,
            CancellationToken.None);
        stopwatch.Stop();

        Assert.InRange(result.Width, 475, 485);
        Assert.InRange(result.Height, 235, 245);
        Assert.True(result.MatchCount >= 12);
        Assert.True(result.InlierCount >= 12);
        Assert.True(result.InlierRatio >= 0.2);
        Assert.InRange(result.ExposureGain, 1.2, 1.55);
        Assert.True(
            FindBestSceneMeanError(result, scene, sceneWidth, height, 270, 332) < 30,
            "The panorama should preserve the known scene outside the occluded patch.");
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"A 360 × 240 pair took {stopwatch.Elapsed.TotalMilliseconds:N0} ms.");
        Assert.Equal(firstOriginal, first);
        Assert.Equal(secondOriginal, second);
    }

    [Fact]
    public void Stitch_PhotosWithoutOverlap_RejectsAlignment()
    {
        const int width = 360;
        const int height = 240;
        var first = CreateScene(width, height, 1341);
        var second = CreateScene(width, height, 9217);

        var exception = Assert.Throws<InvalidDataException>(() =>
            PanoramaStitcher.Stitch(
                Image(first, width, height),
                Image(second, width, height),
                "first.jpg",
                "unrelated.jpg",
                progress: null,
                CancellationToken.None));

        Assert.Contains("overlap", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Stitch_CancellationDuringBlending_IsObserved()
    {
        const int width = 300;
        const int height = 180;
        const int sceneWidth = 390;
        var scene = CreateScene(sceneWidth, height, 124);
        var cancellation = new CancellationTokenSource();
        var progress = new CancelOnProgress(cancellation, "Blending and cropping panorama");

        Assert.Throws<OperationCanceledException>(() =>
            PanoramaStitcher.Stitch(
                Image(Crop(scene, sceneWidth, 0, width, height), width, height),
                Image(Crop(scene, sceneWidth, 90, width, height), width, height),
                "first.jpg",
                "second.jpg",
                progress,
                cancellation.Token));
    }

    [Fact]
    public void Stitch_ConflictingParallaxPlanes_RejectsAmbiguousAlignment()
    {
        const int width = 360;
        const int height = 240;
        var scene = CreateScene(600, height, 3812);
        var first = Crop(scene, 600, 0, width, height);
        var second = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sceneX = x < width / 2 ? x + 100 : x;
                Buffer.BlockCopy(
                    scene,
                    (y * 600 + sceneX) * 4,
                    second,
                    (y * width + x) * 4,
                    4);
            }
        }

        var exception = Assert.Throws<InvalidDataException>(() =>
            PanoramaStitcher.Stitch(
                Image(first, width, height),
                Image(second, width, height),
                "first.jpg",
                "parallax.jpg",
                progress: null,
                CancellationToken.None));

        Assert.True(
            exception.Message.Contains("align", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("overlap", StringComparison.OrdinalIgnoreCase),
            exception.Message);
    }

    [Fact]
    public async Task SaveAsync_CreatesDistinctJpegsWithSourceProvenance()
    {
        const int width = 300;
        const int height = 180;
        const int sceneWidth = 390;
        var scene = CreateScene(sceneWidth, height, 124);
        var panorama = PanoramaStitcher.Stitch(
            Image(Crop(scene, sceneWidth, 0, width, height), width, height),
            Image(Crop(scene, sceneWidth, 90, width, height), width, height),
            "first.jpg",
            "second.jpg",
            progress: null,
            CancellationToken.None);
        var outputFolder = Path.Combine(Path.GetTempPath(), $"panorama-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputFolder);

        try
        {
            var firstOutput = await PanoramaStitchingService.SaveAsync(
                panorama, outputFolder, TestContext.Current.CancellationToken);
            var secondOutput = await PanoramaStitchingService.SaveAsync(
                panorama, outputFolder, TestContext.Current.CancellationToken);

            Assert.NotEqual(firstOutput, secondOutput);
            Assert.Equal("first - Panorama.jpg", Path.GetFileName(firstOutput));
            Assert.Equal("first - Panorama (2).jpg", Path.GetFileName(secondOutput));
            var bytes = await File.ReadAllBytesAsync(
                firstOutput, TestContext.Current.CancellationToken);
            Assert.Equal((byte)0xff, bytes[0]);
            Assert.Equal((byte)0xd8, bytes[1]);
            var jpegText = Encoding.ASCII.GetString(bytes);
            Assert.Contains("http://ns.adobe.com/xap/1.0/", jpegText);
            Assert.Contains("first.jpg", jpegText);
            Assert.Contains("second.jpg", jpegText);
            Assert.Empty(Directory.GetFiles(outputFolder, "*.tmp"));
        }
        finally
        {
            foreach (var file in Directory.GetFiles(outputFolder))
                File.Delete(file);
            Directory.Delete(outputFolder);
        }
    }

    [Fact]
    public void Stitch_Cancellation_IsObservedBeforeProcessing()
    {
        using var cancellation = new CancellationTokenSource();
        var pixels = CreateScene(120, 120, 7);
        var progress = new CancelOnProgress(cancellation, "Finding image features");

        Assert.Throws<OperationCanceledException>(() =>
            PanoramaStitcher.Stitch(
                Image(pixels, 120, 120),
                Image(pixels, 120, 120),
                "first.jpg",
                "second.jpg",
                progress,
                cancellation.Token));
    }

    [Theory]
    [InlineData("photo.jpg", true)]
    [InlineData("photo.TIFF", true)]
    [InlineData("photo.dng", false)]
    [InlineData("clip.mp4", false)]
    public void IsSupported_OnlyAcceptsTheDocumentedStillFormats(string path, bool supported)
    {
        Assert.Equal(supported, PanoramaStitchingService.IsSupported(path));
    }

    private static WindowsImagingCodec.DecodedImage Image(byte[] pixels, int width, int height) =>
        new(pixels, (uint)width, (uint)height);

    private static byte[] CreateScene(int width, int height, int seed)
    {
        var random = new Random(seed);
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var noise = random.Next(256);
            pixels[index] = (byte)noise;
            pixels[index + 1] = (byte)((noise * 3 + random.Next(256)) / 4);
            pixels[index + 2] = (byte)((noise + random.Next(256)) / 2);
            pixels[index + 3] = 255;
        }
        return pixels;
    }

    private static byte[] Crop(byte[] source, int sourceWidth, int left, int width, int height)
    {
        var result = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            Buffer.BlockCopy(source, (y * sourceWidth + left) * 4, result, y * width * 4, width * 4);
        return result;
    }

    private static double FindBestSceneMeanError(
        PanoramaStitchResult result,
        byte[] scene,
        int sceneWidth,
        int sceneHeight,
        int occlusionLeft,
        int occlusionRight)
    {
        var bestError = double.PositiveInfinity;
        for (var offsetY = -5; offsetY <= 5; offsetY++)
        {
            for (var offsetX = -5; offsetX <= 5; offsetX++)
            {
                var totalError = 0L;
                var sampleCount = 0;
                for (var y = 8; y < result.Height - 8; y++)
                {
                    var sceneY = y + offsetY;
                    if (sceneY < 0 || sceneY >= sceneHeight)
                        continue;
                    for (var x = 8; x < result.Width - 8; x++)
                    {
                        var sceneX = x + offsetX;
                        if (sceneX < 0 || sceneX >= sceneWidth ||
                            sceneX >= occlusionLeft && sceneX < occlusionRight && sceneY >= 48 && sceneY < 140)
                            continue;
                        var resultOffset = (y * result.Width + x) * 4;
                        var sceneOffset = (sceneY * sceneWidth + sceneX) * 4;
                        for (var channel = 0; channel < 3; channel++)
                            totalError += Math.Abs(
                                result.Pixels[resultOffset + channel] - scene[sceneOffset + channel]);
                        sampleCount += 3;
                    }
                }
                if (sampleCount > 0)
                    bestError = Math.Min(bestError, (double)totalError / sampleCount);
            }
        }
        return bestError;
    }

    private static void Darken(byte[] pixels, double factor)
    {
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = (byte)Math.Round(pixels[index] * factor);
            pixels[index + 1] = (byte)Math.Round(pixels[index + 1] * factor);
            pixels[index + 2] = (byte)Math.Round(pixels[index + 2] * factor);
        }
    }

    private static void AddOcclusion(
        byte[] image,
        int width,
        int left,
        int top,
        int patchWidth,
        int patchHeight,
        int seed)
    {
        var random = new Random(seed);
        for (var y = top; y < top + patchHeight; y++)
        {
            for (var x = left; x < left + patchWidth; x++)
            {
                var offset = (y * width + x) * 4;
                image[offset] = (byte)random.Next(256);
                image[offset + 1] = (byte)random.Next(256);
                image[offset + 2] = (byte)random.Next(256);
            }
        }
    }

    private sealed class CancelOnProgress(
        CancellationTokenSource cancellation,
        string targetStage) :
        IProgress<PanoramaStitchProgress>
    {
        public void Report(PanoramaStitchProgress value)
        {
            if (value.Stage == targetStage)
                cancellation.Cancel();
        }
    }
}
