// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Text;
using PhotoLibrarian.Core.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class PanoramaStitchingTests
{
    [Theory]
    [InlineData(32, 1, 360, 240, 220, 0)]
    [InlineData(1, 24, 180, 180, 0, 80)]
    [InlineData(8, 8, 180, 140, 64, 56)]
    public void Stitch_DozensOfUnorderedPhotos_CoversTheWholeScene(
        int columns, int rows, int photoWidth, int photoHeight, int stepX, int stepY)
    {
        var sceneWidth = photoWidth + (columns - 1) * stepX;
        var sceneHeight = photoHeight + (rows - 1) * stepY;
        var scene = CreateScene(sceneWidth, sceneHeight, 771);
        var inputs = new List<(WindowsImagingCodec.DecodedImage Image, string Path)>();
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var pixels = Crop(scene, sceneWidth, column * stepX, photoWidth, photoHeight, row * stepY);
                inputs.Add((Image(pixels, photoWidth, photoHeight), $"row-{row}-column-{column}.png"));
            }
        }
        var random = new Random(91);
        random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(inputs));
        var originals = inputs.Select(input => (byte[])input.Image.Pixels.Clone()).ToArray();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = PanoramaStitcher.Stitch(
            inputs.Select(input => input.Image).ToArray(),
            inputs.Select(input => input.Path).ToArray(),
            null, TestContext.Current.CancellationToken);
        stopwatch.Stop();

        Assert.Equal(columns * rows, result.SourcePaths.Count);
        Assert.Equal(inputs.Select(input => input.Path), result.SourcePaths);
        Assert.InRange(result.Width, sceneWidth - 8, sceneWidth + 8);
        Assert.InRange(result.Height, sceneHeight - 8, sceneHeight + 8);
        Assert.True(result.RetainedArea > 0.95);
        Assert.True(
            FindBestSceneMeanError(result, scene, sceneWidth, sceneHeight, -1, -1) < 15,
            $"The {inputs.Count}-photo panorama must preserve the known scene, not just its dimensions.");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(120),
            $"{inputs.Count} photos took {stopwatch.Elapsed.TotalSeconds:F2}s.");
        for (var index = 0; index < inputs.Count; index++)
            Assert.Equal(originals[index], inputs[index].Image.Pixels);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{inputs.Count} photos -> {result.Width} x {result.Height}, " +
            $"{stopwatch.Elapsed.TotalSeconds:F2}s, matches={result.MatchCount}, inliers={result.InlierCount}");
    }

    [Fact]
    public void Stitch_DisconnectedPhoto_RejectsRatherThanDroppingIt()
    {
        var scene = CreateScene(480, 240, 771);
        var exception = Assert.Throws<InvalidDataException>(() => PanoramaStitcher.Stitch(
            [
                Image(Crop(scene, 480, 0, 360, 240), 360, 240),
                Image(Crop(scene, 480, 120, 360, 240), 360, 240),
                Image(CreateScene(360, 240, 123), 360, 240)
            ],
            ["left.png", "right.png", "unrelated.png"], null, CancellationToken.None));
        Assert.Contains("unrelated.png", exception.Message);
        Assert.Contains("No photos were omitted", exception.Message);
    }

    [Fact]
    public void Stitch_RotatedPerspectiveViews_AlignsAllSources()
    {
        const int sceneWidth = 640;
        const int sceneHeight = 340;
        var noise = CreateScene(sceneWidth, sceneHeight, 771);
        var scene = new byte[noise.Length];
        for (var y = 1; y < sceneHeight - 1; y++)
        {
            for (var x = 1; x < sceneWidth - 1; x++)
            {
                var offset = (y * sceneWidth + x) * 4;
                for (var channel = 0; channel < 3; channel++)
                {
                    var sum = 0;
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                            sum += noise[((y + dy) * sceneWidth + x + dx) * 4 + channel];
                    scene[offset + channel] = (byte)(sum / 9);
                }
                scene[offset + 3] = 255;
            }
        }
        var images = new List<WindowsImagingCodec.DecodedImage>();
        for (var index = 0; index < 3; index++)
        {
            var pixels = new byte[360 * 240 * 4];
            var angle = (index - 1) * 0.035;
            for (var y = 0; y < 240; y++)
            {
                for (var x = 0; x < 360; x++)
                {
                    var denominator = 1 + index * 0.00003 * x;
                    var sx = 30 + index * 100 + (Math.Cos(angle) * x - Math.Sin(angle) * y) / denominator;
                    var sy = 40 + (Math.Sin(angle) * x + Math.Cos(angle) * y) / denominator;
                    var left = (int)sx;
                    var top = (int)sy;
                    var offset = (y * 360 + x) * 4;
                    for (var channel = 0; channel < 3; channel++)
                    {
                        var a = scene[(top * sceneWidth + left) * 4 + channel];
                        var b = scene[(top * sceneWidth + left + 1) * 4 + channel];
                        var c = scene[((top + 1) * sceneWidth + left) * 4 + channel];
                        var d = scene[((top + 1) * sceneWidth + left + 1) * 4 + channel];
                        var upper = a + (b - a) * (sx - left);
                        var lower = c + (d - c) * (sx - left);
                        pixels[offset + channel] = (byte)Math.Round(upper + (lower - upper) * (sy - top));
                    }
                    pixels[offset + 3] = 255;
                }
            }
            images.Add(Image(pixels, 360, 240));
        }
        var result = PanoramaStitcher.Stitch(
            images, ["left.png", "middle.png", "right.png"], null, TestContext.Current.CancellationToken);
        Assert.Equal(3, result.SourcePaths.Count);
        Assert.InRange(result.Width, 520, 580);
        Assert.InRange(result.Height, 200, 250);
        Assert.True(result.InlierCount >= 36);
        var errors = new List<double>();
        for (var index = 0; index < 3; index++)
        {
            var angle = (index - 1) * 0.035;
            for (var y = 40; y <= 200; y += 80)
            {
                for (var x = 40; x <= 320; x += 140)
                {
                    var denominator = 1 + index * 0.00003 * x;
                    var worldX = 30 + index * 100 + (Math.Cos(angle) * x - Math.Sin(angle) * y) / denominator;
                    var worldY = 40 + (Math.Sin(angle) * x + Math.Cos(angle) * y) / denominator;
                    var referenceX = (worldX - 130) / (1 - 0.00003 * (worldX - 130));
                    var referenceY = (worldY - 40) * (1 + 0.00003 * referenceX);
                    var expected = Project(result.SourcePlacements[1].Transform, referenceX / 359, referenceY / 239);
                    var actual = Project(result.SourcePlacements[index].Transform, x / 359d, y / 239d);
                    errors.Add(Math.Sqrt(
                        Math.Pow(expected.X - actual.X, 2) + Math.Pow(expected.Y - actual.Y, 2)));
                }
            }
        }
        Assert.True(errors.Average() < 3.5,
            $"Known perspective landmarks have {errors.Average():F2}px mean error.");
        Assert.True(result.RetainedArea > 0.75,
            $"The rotated-view crop retains {result.RetainedArea:P1}.");
    }

    [Fact]
    public async Task CreatePreviewAndSave_DozenSourceFiles_PreservesSourcesAndAllProvenance()
    {
        const int count = 12;
        const int width = 180;
        const int height = 140;
        const int sceneWidth = width + (count - 1) * 64;
        var scene = CreateScene(sceneWidth, height, 771);
        var folder = Path.Combine(Path.GetTempPath(), $"panorama-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var token = TestContext.Current.CancellationToken;
        try
        {
            var paths = new string[count];
            var originals = new byte[count][];
            for (var index = 0; index < count; index++)
            {
                paths[index] = Path.Combine(folder, $"source-{index:00}.png");
                originals[index] = await WindowsImagingCodec.EncodeAsync(
                    paths[index], Crop(scene, sceneWidth, index * 64, width, height),
                    width, height, token);
                await File.WriteAllBytesAsync(paths[index], originals[index], token);
            }
            var result = await PanoramaStitchingService.CreatePreviewAsync(paths, cancellationToken: token);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                PanoramaStitchingService.SaveAsync(result, folder, cancelled.Token));
            Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
            Assert.Empty(Directory.GetFiles(folder, "*.jpg"));

            var output = await PanoramaStitchingService.SaveAsync(result, folder, token);
            var bytes = await File.ReadAllBytesAsync(output, token);
            var text = Encoding.UTF8.GetString(bytes);
            for (var index = 0; index < count; index++)
            {
                Assert.Contains(Path.GetFileName(paths[index]), text);
                Assert.Equal(originals[index], await File.ReadAllBytesAsync(paths[index], token));
            }
            Assert.Contains("SourceCount", text);
            Assert.Contains("source edit recipes not applied", text);
            var decoded = await WindowsImagingCodec.DecodeAsync(output, cancellationToken: token);
            Assert.Equal((uint)result.Width, decoded.Width);
            Assert.Equal((uint)result.Height, decoded.Height);
            Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        }
        finally
        {
            foreach (var file in Directory.GetFiles(folder))
                File.Delete(file);
            Directory.Delete(folder);
        }
    }

    [Fact]
    public void Stitch_LargeCanvas_ScalesWholeViewWithinOutputBudget()
    {
        const int count = 9;
        const int width = 2400;
        const int height = 1800;
        const int step = 1500;
        const int featureSceneWidth = 640 + (count - 1) * 400;
        var featureScene = CreateScene(featureSceneWidth, 480, 771);
        var images = new List<WindowsImagingCodec.DecodedImage>();
        for (var index = 0; index < count; index++)
        {
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var sourceOffset = ((y * 4 / 15) * featureSceneWidth + index * 400 + x * 4 / 15) * 4;
                    var targetOffset = (y * width + x) * 4;
                    Buffer.BlockCopy(featureScene, sourceOffset, pixels, targetOffset, 4);
                }
            }
            images.Add(Image(pixels, width, height));
        }
        var result = PanoramaStitcher.Stitch(
            images, Enumerable.Range(0, count).Select(index => $"frame-{index}.png").ToArray(),
            null, TestContext.Current.CancellationToken);
        Assert.Equal(count, result.SourcePaths.Count);
        Assert.InRange(result.OutputScale, 0.7, 0.9);
        Assert.True((long)result.Width * result.Height <= PanoramaStitchingService.MaximumOutputPixels);
        Assert.InRange(result.Width / result.OutputScale, width + (count - 1) * step - 12, width + (count - 1) * step + 12);
        Assert.InRange(result.Height / result.OutputScale, height - 12, height + 12);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Large canvas: {result.Width} x {result.Height}, scale={result.OutputScale:F4}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65)]
    public async Task CreatePreviewAsync_InvalidPhotoCount_IsRejectedBeforeReadingFiles(int count)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => PanoramaStitchingService.CreatePreviewAsync(
            Enumerable.Range(0, count).Select(index => $"photo-{index}.png").ToArray()));
    }

    [Fact]
    public async Task CreatePreviewAsync_DuplicateSource_IsRejectedBeforeReadingFiles()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            PanoramaStitchingService.CreatePreviewAsync(["same.png", "same.png"]));
    }

    [Fact]
    public void Stitch_CancellationDuringOverlapSearch_IsObserved()
    {
        using var cancellation = new CancellationTokenSource();
        var scene = CreateScene(390, 180, 124);
        var progress = new CancelOnProgress(cancellation, "Checking overlap 1 of 1");
        Assert.Throws<OperationCanceledException>(() => PanoramaStitcher.Stitch(
            Image(Crop(scene, 390, 0, 300, 180), 300, 180),
            Image(Crop(scene, 390, 90, 300, 180), 300, 180),
            "first.png", "second.png", progress, cancellation.Token));
    }

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
        Assert.InRange(result.ExposureGains[1], 1.2, 1.55);
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

    private static (double X, double Y) Project(IReadOnlyList<double> matrix, double x, double y)
    {
        var denominator = matrix[6] * x + matrix[7] * y + matrix[8];
        return (
            (matrix[0] * x + matrix[1] * y + matrix[2]) / denominator,
            (matrix[3] * x + matrix[4] * y + matrix[5]) / denominator);
    }

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

    private static byte[] Crop(byte[] source, int sourceWidth, int left, int width, int height, int top = 0)
    {
        var result = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            Buffer.BlockCopy(source, ((y + top) * sourceWidth + left) * 4, result, y * width * 4, width * 4);
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
