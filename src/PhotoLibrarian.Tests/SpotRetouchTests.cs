// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Numerics;
using System.Xml.Linq;
using Microsoft.Graphics.Canvas;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using PhotoLibrarian.ViewModels;
using XmpCore;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class SpotRetouchTests : IDisposable
{
    private const int Size = 128;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-retouch-{Guid.NewGuid():N}");

    public SpotRetouchTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static RetouchStroke Stroke(int radius = 8, params RetouchPoint[] points) =>
        new(Size, Size, radius, points.Length == 0 ? [new(64, 64)] : points);

    private static byte[] Surface(bool gradient)
    {
        var pixels = new byte[Size * Size * 4];
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var offset = (y * Size + x) * 4;
            pixels[offset] = (byte)(gradient ? 30 + x : 70);
            pixels[offset + 1] = (byte)(gradient ? 40 + y : 120);
            pixels[offset + 2] = (byte)(gradient ? 20 + (x + y) / 2 : 180);
            pixels[offset + 3] = 255;
        }
        return pixels;
    }

    private static void Blemish(byte[] pixels, int left = 60, int right = 68)
    {
        for (var y = 61; y <= 67; y++)
        for (var x = left; x <= right; x++)
        {
            var offset = (y * Size + x) * 4;
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 10;
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SmoothFixture_RemovesBlemishWithOneLevelErrorAndNoOutsideChanges(bool gradient, bool drag)
    {
        var reference = Surface(gradient);
        var damaged = reference.ToArray();
        Blemish(damaged, 60, drag ? 85 : 68);
        var before = damaged.ToArray();
        var stroke = drag ? Stroke(8, new(64, 64), new(82, 64)) : Stroke();
        Assert.True(SpotRetouchProcessor.Apply(damaged, Size, Size, [stroke]).Succeeded);
        var changed = 0;
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var offset = (y * Size + x) * 4;
            var dx = x - Math.Clamp(x, 64, drag ? 82 : 64);
            var inside = dx * dx + (y - 64) * (y - 64) <= 64;
            for (var c = 0; c < 4; c++)
            {
                if (inside) Assert.InRange(Math.Abs(damaged[offset + c] - reference[offset + c]), 0, 1);
                else Assert.Equal(before[offset + c], damaged[offset + c]);
                if (damaged[offset + c] != before[offset + c]) changed++;
            }
        }
        Assert.True(changed > 100);
    }

    [Theory]
    [InlineData("alpha")]
    [InlineData("ring-alpha")]
    public void Transparency_ReturnsAnExpectedResultWithoutThrowingOrWriting(string kind)
    {
        var pixels = Surface(false);
        var stroke = Stroke();
        if (kind == "alpha") pixels[(64 * Size + 64) * 4 + 3] = 128;
        if (kind == "ring-alpha") pixels[(64 * Size + 73) * 4 + 3] = 128;
        var before = pixels.ToArray();
        var result = SpotRetouchProcessor.Apply(pixels, Size, Size, [stroke]);
        Assert.Equal(RetouchStatus.TransparentPixels, result.Status);
        Assert.NotEmpty(result.Message!);
        Assert.Equal(before, pixels);
    }

    [Fact]
    public void InvalidRecipesFailButStrokeLengthPointCountAndAreaAreNotCapped()
    {
        SpotRetouchProcessor.Validate(Enumerable.Repeat(Stroke(), 32).ToList());
        SpotRetouchProcessor.Validate([new(6000, 4000, 24, [new(60, 60)])]);
        SpotRetouchProcessor.Validate(Enumerable.Repeat(Stroke(), 256).ToList());
        Assert.Throws<InvalidDataException>(() => SpotRetouchProcessor.Validate([Stroke(25)]));
        Assert.Throws<InvalidDataException>(() => SpotRetouchProcessor.Validate([Stroke(1)]));
        SpotRetouchProcessor.Validate([new(12000, 8000, 8, [new(60, 60)])]);
        Assert.Throws<InvalidDataException>(() => SpotRetouchProcessor.Validate([Stroke(8, new RetouchPoint(-1, 3))]));
        SpotRetouchProcessor.Validate([Stroke(8, Enumerable.Repeat(new RetouchPoint(64, 64), 1024).ToArray())]);
        Assert.Throws<InvalidDataException>(() => SpotRetouchProcessor.Apply(
            Surface(false), Size, Size, [new(127, 128, 8, [new(64, 64)])]));
        var large = new byte[400 * 400 * 4];
        Array.Fill(large, (byte)255);
        var tooLong = new RetouchStroke(400, 400, 8, [new(30, 30), new(220, 30)]);
        Assert.True(SpotRetouchProcessor.Apply(large, 400, 400, [tooLong]).Succeeded);
        var tooWide = new RetouchStroke(400, 400, 24, [new(40, 40), new(175, 40), new(175, 175), new(40, 175)]);
        Assert.True(SpotRetouchProcessor.Apply(large, 400, 400, [tooWide]).Succeeded);
        var cancelled = new CancellationToken(canceled: true);
        Assert.Throws<OperationCanceledException>(() => SpotRetouchProcessor.Apply(Surface(false), Size, Size, [Stroke()], cancelled));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(127, 64)]
    [InlineData(64, 127)]
    public void ImageBoundary_UsesAvailableSurroundings(int x, int y)
    {
        var pixels = Surface(false);
        var reference = pixels.ToArray();
        pixels[(y * Size + x) * 4] = 0;
        Assert.True(SpotRetouchProcessor.Apply(pixels, Size, Size, [Stroke(8, new RetouchPoint(x, y))]).Succeeded);
        Assert.Equal(reference, pixels);
    }

    [Fact]
    public void EntireImagePainted_ReturnsNoSurroundingsWithoutAnException()
    {
        var pixels = Enumerable.Repeat((byte)255, 4 * 4 * 4).ToArray();
        var before = pixels.ToArray();
        var result = SpotRetouchProcessor.Apply(pixels, 4, 4, [new(4, 4, 8, [new(2, 2)])]);
        Assert.Equal(RetouchStatus.NoSurroundingPixels, result.Status);
        Assert.NotEmpty(result.Message!);
        Assert.Equal(before, pixels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoisyNonplanarBackground_HealsPaintedStoplightWithoutTextureRejection(bool denseStroke)
    {
        var random = new Random(23175);
        var reference = Surface(false);
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        for (var channel = 0; channel < 3; channel++)
        {
            var noise = random.Next(-7, 8);
            if ((x + y) % 19 == 0) noise += 16; // Noise/compression-like boundary outliers defeated the old max-error gate.
            var value = 100 + channel * 35 + 8 * Math.Sin(x * 0.1) + y * 0.2 + noise;
            reference[(y * Size + x) * 4 + channel] = (byte)Math.Clamp(Math.Round(value), 0, 255);
        }
        var damaged = reference.ToArray();
        // A small red light and dark housing, completely covered by the painted capsule.
        for (var y = 59; y <= 69; y++)
        for (var x = 60; x <= 80; x++)
        {
            var offset = (y * Size + x) * 4;
            damaged[offset] = 10;
            damaged[offset + 1] = 10;
            damaged[offset + 2] = x < 67 ? (byte)230 : (byte)10;
        }
        var before = damaged.ToArray();
        var points = denseStroke
            ? Enumerable.Range(0, 512).Select(i => new RetouchPoint(64 + i * 12 / 511, 64)).ToArray()
            : new[] { new RetouchPoint(64, 64), new RetouchPoint(76, 64) };
        Assert.True(SpotRetouchProcessor.Apply(damaged, Size, Size, [Stroke(10, points)]).Succeeded);
        double error = 0;
        var count = 0;
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var dx = x - Math.Clamp(x, 64, 76);
            var inside = dx * dx + (y - 64) * (y - 64) <= 100;
            for (var channel = 0; channel < 4; channel++)
            {
                var offset = (y * Size + x) * 4 + channel;
                if (!inside || channel == 3) Assert.Equal(before[offset], damaged[offset]);
                else
                {
                    error += Math.Pow(damaged[offset] - reference[offset], 2);
                    count++;
                }
            }
        }
        Assert.InRange(Math.Sqrt(error / count), 0, 7);
        Assert.InRange(damaged[(64 * Size + 64) * 4 + 2], 120, 220);
    }

    [Theory]
    [InlineData("vertical")]
    [InlineData("horizontal")]
    [InlineData("diagonal")]
    [InlineData("texture")]
    public void NearbyExemplars_PreserveStructureRatherThanBlurringThePaintedArea(string pattern)
    {
        var reference = Surface(false);
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var bright = pattern switch
            {
                "vertical" => x >= 64,
                "horizontal" => y >= 64,
                "diagonal" => x >= y,
                _ => (x / 4 + y / 4) % 2 == 0
            };
            for (var channel = 0; channel < 3; channel++)
                reference[(y * Size + x) * 4 + channel] = (byte)(bright ? 190 + channel * 10 : 40 + channel * 10);
        }
        var pixels = reference.ToArray();
        Blemish(pixels);
        var before = pixels.ToArray();
        Assert.True(SpotRetouchProcessor.Apply(pixels, Size, Size, [Stroke()]).Succeeded);
        double error = 0;
        var count = 0;
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        for (var channel = 0; channel < 4; channel++)
        {
            var offset = (y * Size + x) * 4 + channel;
            if ((x - 64) * (x - 64) + (y - 64) * (y - 64) > 64 || channel == 3)
                Assert.Equal(before[offset], pixels[offset]);
            else
            {
                error += Math.Pow(pixels[offset] - reference[offset], 2);
                count++;
            }
        }
        // Harmonic-only filling loses these edges/texture (39-78 levels RMS).
        Assert.InRange(Math.Sqrt(error / count), 0, 12);
    }

    [Fact]
    public void ExemplarDonors_NeverReadPaintedContentAndReplayDeterministically()
    {
        var first = Surface(true);
        var second = first.ToArray();
        for (var y = 56; y <= 72; y++)
        for (var x = 56; x <= 84; x++)
        {
            var dx = x - Math.Clamp(x, 64, 76);
            if (dx * dx + (y - 64) * (y - 64) > 64) continue;
            for (var channel = 0; channel < 3; channel++)
            {
                first[(y * Size + x) * 4 + channel] = 0;
                second[(y * Size + x) * 4 + channel] = 255;
            }
        }
        Assert.True(SpotRetouchProcessor.Apply(first, Size, Size,
            [Stroke(8, new(64, 64), new(76, 64))]).Succeeded);
        Assert.True(SpotRetouchProcessor.Apply(second, Size, Size,
            [Stroke(8, Enumerable.Range(64, 13).Select(x => new RetouchPoint(x, 64)).ToArray())]).Succeeded);
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(true, 23175)]
    [InlineData(true, 42)]
    [InlineData(false, 23175)]
    [InlineData(false, 42)]
    public void NearbyUnrelatedDetails_AreNotCopiedIntoNoisyPaintedBackground(bool band, int seed)
    {
        var random = new Random(seed);
        int[] lightCenters = [24, 88, 112];
        var reference = Surface(false);
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var blueBand = band && y >= 52 && y <= 73;
            var offset = (y * Size + x) * 4;
            reference[offset] = (byte)((blueBand ? 75 : 135) + random.Next(-6, 7));
            reference[offset + 1] = (byte)((blueBand ? 45 : 145) + random.Next(-6, 7));
            reference[offset + 2] = (byte)((blueBand ? 30 : 130) + random.Next(-6, 7));
        }
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var detail = band
                ? x >= 60 && x <= 73 && y >= 27 && y <= 44 && (x <= 64 || y >= 39)
                : lightCenters.Any(cx => (x - cx) * (x - cx) + (y - 64) * (y - 64) <= 36);
            if (!detail) continue;
            var offset = (y * Size + x) * 4;
            reference[offset] = 15;
            reference[offset + 1] = band ? (byte)80 : (byte)20;
            reference[offset + 2] = band ? (byte)25 : (byte)235;
        }
        var damaged = reference.ToArray();
        var stroke = band ? Stroke(12, new(59, 67), new(73, 67)) : Stroke(10);
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var insideBlemish = band
                ? x >= 56 && x <= 76 && y >= 61 && y <= 73
                : (x - 64) * (x - 64) + (y - 64) * (y - 64) <= 36;
            if (!insideBlemish) continue;
            var offset = (y * Size + x) * 4;
            damaged[offset] = 10;
            damaged[offset + 1] = band ? (byte)150 : (byte)20;
            damaged[offset + 2] = band ? (byte)190 : (byte)235;
        }
        var before = damaged.ToArray();
        Assert.True(SpotRetouchProcessor.Apply(damaged, Size, Size, [stroke]).Succeeded);
        double error = 0;
        var count = 0;
        var maximumError = 0;
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        for (var channel = 0; channel < 4; channel++)
        {
            var dx = x - Math.Clamp(x, band ? 59 : 64, band ? 73 : 64);
            var dy = y - (band ? 67 : 64);
            var offset = (y * Size + x) * 4 + channel;
            if (dx * dx + dy * dy > stroke.Radius * stroke.Radius || channel == 3)
                Assert.Equal(before[offset], damaged[offset]);
            else
            {
                var difference = Math.Abs(damaged[offset] - reference[offset]);
                error += difference * difference;
                maximumError = Math.Max(maximumError, difference);
                count++;
            }
        }
        Assert.InRange(Math.Sqrt(error / count), 0, 10);
        Assert.InRange(maximumError, 0, 45);
    }

    [Theory]
    [InlineData(23175)]
    [InlineData(42)]
    public void RepeatingBackground_DoesNotSmuggleUnseenLightPixelsThroughAMatchingPatch(int seed)
    {
        var random = new Random(seed);
        int[] lightCenters = [16, 64, 88, 112];
        var texture = Enumerable.Range(0, 24 * Size).Select(_ => random.Next(-6, 7)).ToArray();
        var reference = Surface(false);
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var light = y >= 58 && y <= 70 &&
                lightCenters.Any(cx => (x - cx) * (x - cx) + (y - 64) * (y - 64) <= 36);
            var offset = (y * Size + x) * 4;
            reference[offset] = light ? (byte)15 : (byte)(135 + texture[y * 24 + x % 24]);
            reference[offset + 1] = light ? (byte)20 : (byte)(145 + texture[y * 24 + x % 24]);
            reference[offset + 2] = light ? (byte)235 : (byte)(130 + texture[y * 24 + x % 24]);
        }
        var pixels = reference.ToArray();
        for (var y = 58; y <= 70; y++)
        for (var x = 34; x <= 46; x++)
        {
            if ((x - 40) * (x - 40) + (y - 64) * (y - 64) > 36) continue;
            var offset = (y * Size + x) * 4;
            pixels[offset] = 15;
            pixels[offset + 1] = 20;
            pixels[offset + 2] = 235;
        }
        var before = pixels.ToArray();
        Assert.True(SpotRetouchProcessor.Apply(pixels, Size, Size, [Stroke(10, new RetouchPoint(40, 64))]).Succeeded);
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        for (var channel = 0; channel < 4; channel++)
        {
            var offset = (y * Size + x) * 4 + channel;
            if ((x - 40) * (x - 40) + (y - 64) * (y - 64) <= 100 && channel < 3)
                Assert.InRange(Math.Abs(pixels[offset] - reference[offset]), 0, 30);
            else
                Assert.Equal(before[offset], pixels[offset]);
        }
    }

    [Fact]
    public void DistantTransparency_IsExcludedFromDonorsWithoutRejectingOpaqueSelection()
    {
        var pixels = Surface(false);
        pixels[(52 * Size + 64) * 4 + 3] = 0;
        var reference = pixels.ToArray();
        Blemish(pixels);
        Assert.True(SpotRetouchProcessor.Apply(pixels, Size, Size, [Stroke()]).Succeeded);
        Assert.Equal(reference, pixels);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void NarrowImages_UseClippedSourcePatches(int width)
    {
        const int height = 80;
        var pixels = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
        pixels[(40 * width) * 4] = 0;
        Assert.True(SpotRetouchProcessor.Apply(pixels, width, height,
            [new(width, height, 8, [new(0, 40)])]).Succeeded);
        Assert.All(pixels, value => Assert.Equal((byte)255, value));
    }

    [Fact]
    public void BroadMaskWithOnlyOneSourceRow_AdaptsPatchesAndPropagatesDonors()
    {
        const int size = 40;
        var pixels = Enumerable.Repeat((byte)255, size * size * 4).ToArray();
        for (var i = size * 4; i < pixels.Length; i += 4)
            pixels[i] = pixels[i + 1] = pixels[i + 2] = 0;
        var points = new List<RetouchPoint>();
        for (var y = 3; y < size; y += 4)
        {
            var reverse = points.Count % 4 != 0;
            points.Add(new(reverse ? size - 1 : 0, y));
            points.Add(new(reverse ? 0 : size - 1, y));
        }
        Assert.True(SpotRetouchProcessor.Apply(pixels, size, size,
            [new(size, size, 2, points.ToArray())]).Succeeded);
        Assert.All(pixels, value => Assert.Equal((byte)255, value));
    }

    [Fact]
    public void StrokeFeedback_IsOneFilledCapsuleWithoutRingsOrDarkerOverlaps()
    {
        using var device = new CanvasDevice();
        using var target = new CanvasRenderTarget(device, Size, Size, 96);
        using var selection = RetouchStrokeGeometry.Create(device,
            [new(30, 64), new(45, 64), new(60, 64), new(45, 64), new(90, 64)], 8);
        using (var session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Transparent);
            session.FillGeometry(selection, Windows.UI.Color.FromArgb(72, 255, 255, 255));
        }
        var pixels = target.GetPixelBytes();
        for (var x = 24; x <= 96; x++)
        for (var y = 61; y <= 67; y++)
            Assert.Equal(new byte[] { 72, 72, 72, 72 }, pixels.AsSpan((y * Size + x) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, pixels.AsSpan((40 * Size + 60) * 4, 4).ToArray());
    }

    [Fact]
    public async Task UnsupportedPreview_ReturnsAStatusWithoutPublishingAPartialBitmap()
    {
        var pixels = Surface(false);
        pixels[(64 * Size + 64) * 4 + 3] = 128;
        using var device = new CanvasDevice();
        using var source = CanvasBitmap.CreateFromBytes(device, pixels, Size, Size,
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96,
            CanvasAlphaMode.Premultiplied);
        var original = source.GetPixelBytes();
        var result = await ImageEditRenderer.TryCreateRetouchedAsync(device, source,
            [Stroke(4, new RetouchPoint(32, 32)), Stroke()]);
        Assert.Equal(RetouchStatus.TransparentPixels, result.Result.Status);
        Assert.Null(result.Bitmap);
        Assert.Equal(original, source.GetPixelBytes());
    }

    [Fact]
    public async Task JpegNoiseAndCompression_DoNotPreventPreviewOrSavedInfill()
    {
        var path = Path.Combine(_directory, "noisy-stoplight.jpg");
        var pixels = Surface(true);
        var random = new Random(23175);
        for (var i = 0; i < pixels.Length; i += 4)
        for (var channel = 0; channel < 3; channel++)
            pixels[i + channel] = (byte)Math.Clamp(pixels[i + channel] + random.Next(-12, 13), 0, 255);
        Blemish(pixels);
        await RenderQualityFixtures.WriteImageAsync(path, Size, Size, pixels);
        using var device = new CanvasDevice();
        using var source = await ImageEditRenderer.LoadOrientedAsync(device, path);
        var result = await ImageEditRenderer.TryCreateRetouchedAsync(device, source, [Stroke(12)]);
        Assert.True(result.Result.Succeeded);
        using var preview = result.Bitmap!;
        var center = preview.GetPixelBytes(64, 64, 1, 1);
        Assert.InRange(center[0], 80, 110);
        Assert.InRange(center[1], 90, 120);
        Assert.InRange(center[2], 70, 100);
        var parameters = new EditParameters { RetouchStrokes = [Stroke(12)] };
        var backups = new OriginalBackupService(Path.Combine(_directory, "originals"));
        await new ImageEditService(backups).ApplyEditsAsync(path, parameters,
            (file, edits) => ImageEditRenderer.RenderToFileAsync(file, edits, device));
        using var saved = await ImageEditRenderer.LoadOrientedAsync(device, path);
        var savedCenter = saved.GetPixelBytes(64, 64, 1, 1);
        for (var channel = 0; channel < 3; channel++)
            Assert.InRange(Math.Abs(center[channel] - savedCenter[channel]), 0, 5);
        Assert.Empty(ImageEditService.ReadEditParameters(path).RetouchStrokes);
    }

    [Theory]
    [InlineData("photo.jpg")]
    [InlineData("photo.JPEG")]
    [InlineData("photo.png")]
    [InlineData("photo.tif")]
    [InlineData("photo.TIFF")]
    [InlineData("photo.bmp")]
    public void SupportedRasterFormats_AreEligibleForTheExistingSafeSavePipeline(string path) =>
        Assert.True(ImageEditRenderer.IsSupported(path));

    [Fact]
    public void Recipes_RoundTripDeepCloneResetAndClearWithoutRemovingOtherMetadata()
    {
        var xmp = XmpMetaFactory.Create();
        const string dc = "http://purl.org/dc/elements/1.1/";
        xmp.SetProperty(dc, "dc:format", "image/png");
        var p = new EditParameters { RetouchStrokes = [Stroke()], Exposure = 0.1 };
        EditParametersSerializer.WriteToXmp(xmp, p);
        var read = EditParametersSerializer.ReadFromXmp(xmp);
        Assert.True(RetouchStroke.SequenceEqual(p.RetouchStrokes, read.RetouchStrokes));
        var clone = p.Clone();
        p.RetouchStrokes[0].Points[0] = new(2, 2);
        Assert.Equal(new RetouchPoint(64, 64), clone.RetouchStrokes[0].Points[0]);
        Assert.True(clone.HasAdjustments);
        clone.Reset();
        Assert.False(clone.HasAdjustments);
        EditParametersSerializer.ClearFromXmp(xmp);
        Assert.Empty(EditParametersSerializer.ReadFromXmp(xmp).RetouchStrokes);
        Assert.Equal("image/png", xmp.GetPropertyString(dc, "dc:format"));
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:SpotRetouchV1", "[null]");
        Assert.Throws<InvalidDataException>(() => EditParametersSerializer.ReadFromXmp(xmp));
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:SpotRetouchV1", "{invalid}");
        Assert.Throws<InvalidDataException>(() => EditParametersSerializer.ReadFromXmp(xmp));
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:SpotRetouchV1", " ");
        Assert.Throws<InvalidDataException>(() => EditParametersSerializer.ReadFromXmp(xmp));
    }

    [Theory]
    [InlineData(0, 0.25f)]
    [InlineData(90, 2f)]
    [InlineData(-90, 0.75f)]
    [InlineData(23, 8f)]
    [InlineData(180, 1f)]
    public void Mapping_InvertsRotationZoomPanAndLetterbox(double angle, float scale)
    {
        var size = new Vector2(128, 96); // Already EXIF-oriented; never invert EXIF a second time.
        var origin = new Vector2(-51, 37);
        var transform = EditorImageCoordinates.SourceToCanvas(size, angle, scale, origin);
        var sourcePoint = new Vector2(31.5f, 60.5f);
        Assert.True(EditorImageCoordinates.TryGetSourcePoint(Vector2.Transform(sourcePoint, transform),
            size, angle, scale, origin, out var point));
        Assert.Equal(new RetouchPoint(31, 60), point);
        Assert.False(EditorImageCoordinates.TryGetSourcePoint(Vector2.Transform(new(-10, -10), transform),
            size, angle, scale, origin, out _));
    }

    [Fact]
    public void Gesture_ClickDragCancelCaptureLossAndLongDrawingNeverLeakPartialEdits()
    {
        var gesture = new RetouchGesture();
        gesture.Begin(1, Size, Size, 8, new(60, 64));
        gesture.Add(new(64, 64));
        Assert.Null(gesture.Complete(2));
        var stroke = gesture.Complete(1);
        Assert.Equal(2, stroke!.Points.Length);
        Assert.Null(gesture.Complete(1));
        gesture.Begin(2, Size, Size, 4, new(32, 32));
        gesture.Cancel(); // Escape, pointer cancellation, capture loss, navigation and resize use this path.
        Assert.Null(gesture.Complete(2));
        gesture.Begin(3, Size, Size, 4, new(32, 32));
        Assert.Single(gesture.Complete(3)!.Points);
        gesture.Begin(4, Size, Size, 4, new(32, 32));
        Assert.False(gesture.Add(new(-1, 32)));
        Assert.Null(gesture.Complete(4));
        gesture.Begin(5, Size, Size, 4, new(32, 32));
        for (var i = 1; i < 1024; i++) Assert.True(gesture.Add(new(i % 2 + 32, 33)));
        Assert.Equal(1024, gesture.Complete(5)!.Points.Length);
    }

    [Fact]
    public async Task History_PendingSidecarSaveAndRawGateUseExistingSafeWorkflow()
    {
        var path = Path.Combine(_directory, "history.png");
        await RenderQualityFixtures.WriteImageAsync(path, Size, Size, Surface(false));
        var backups = new OriginalBackupService(Path.Combine(_directory, "originals"));
        var vm = new ImageEditorViewModel(backups);
        await vm.OpenForEditAsync(new ImageEntry { FilePath = path, FileName = "history.png" });
        vm.AddRetouchStroke(Stroke());
        Assert.Equal(new[] { "Spot retouch" }, vm.UndoHistory);
        Assert.True(vm.HasChanges);
        vm.UndoSteps(1);
        Assert.Empty(vm.GetCurrentParameters().RetouchStrokes);
        Assert.False(vm.HasChanges);
        vm.RedoSteps(1);
        vm.ResetAllCommand.Execute(null);
        Assert.Empty(vm.GetCurrentParameters().RetouchStrokes);
        vm.UndoSteps(1);
        await new ImageEditService(backups).SaveEditParametersAsync(path, vm.GetCurrentParameters());
        var reopened = new ImageEditorViewModel(backups);
        await reopened.OpenForEditAsync(new ImageEntry { FilePath = path, FileName = "history.png" });
        Assert.Single(reopened.GetCurrentParameters().RetouchStrokes);
        using var device = new CanvasDevice();
        Assert.True(await reopened.SaveAsync((file, parameters) =>
            ImageEditRenderer.RenderToFileAsync(file, parameters, device)));
        Assert.Empty(ImageEditService.ReadEditParameters(path).RetouchStrokes);
        Assert.False(reopened.HasChanges);
        Assert.True(reopened.HasBackup);
        var rawPath = Path.Combine(_directory, "source.dng");
        await File.WriteAllBytesAsync(rawPath, [1, 2, 3]);
        await vm.OpenForEditAsync(new ImageEntry { FilePath = rawPath, FileName = "source.dng" });
        Assert.Throws<InvalidOperationException>(() => vm.AddRetouchStroke(Stroke()));
        Assert.False(ImageEditRenderer.IsSupported(rawPath));
        Assert.False(ImageEditRenderer.IsSupported("animated.gif"));
    }

    [Fact]
    public async Task MoreThan32Strokes_RoundTripUndoRedoAndSaveWithoutACountOrRecipeLengthLimit()
    {
        var path = Path.Combine(_directory, "many-strokes.png");
        await RenderQualityFixtures.WriteImageAsync(path, Size, Size, Surface(false));
        var backups = new OriginalBackupService(Path.Combine(_directory, "originals"));
        var vm = new ImageEditorViewModel(backups);
        await vm.OpenForEditAsync(new ImageEntry { FilePath = path, FileName = Path.GetFileName(path) });
        for (var i = 0; i < 80; i++)
            vm.AddRetouchStroke(Stroke(2, Enumerable.Repeat(new RetouchPoint(64, 64), 128).ToArray()));
        Assert.Equal(80, vm.UndoHistory.Count);
        await new ImageEditService(backups).SaveEditParametersAsync(path, vm.GetCurrentParameters());
        Assert.True(new FileInfo(Path.ChangeExtension(path, ".xmp")).Length > 160_000);
        Assert.Equal(80, ImageEditService.ReadEditParameters(path).RetouchStrokes.Count);
        vm.UndoSteps(50);
        Assert.Equal(30, vm.GetCurrentParameters().RetouchStrokes.Count);
        vm.RedoSteps(50);
        Assert.Equal(80, vm.GetCurrentParameters().RetouchStrokes.Count);
        using var device = new CanvasDevice();
        Assert.True(await vm.SaveAsync((file, parameters) =>
            ImageEditRenderer.RenderToFileAsync(file, parameters, device)));
        Assert.Empty(ImageEditService.ReadEditParameters(path).RetouchStrokes);
    }

    [Fact]
    public async Task FortyFiveMegapixelSource_RetouchesAtFullResolutionWithoutAnImageSizeGate()
    {
        const int width = 8256;
        const int height = 5504;
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)255);
        var center = new RetouchPoint(width / 2, height / 2);
        var offset = (center.Y * width + center.X) * 4;
        pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 0;
        using var device = new CanvasDevice();
        using var source = CanvasBitmap.CreateFromBytes(device, pixels, width, height,
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96,
            CanvasAlphaMode.Premultiplied);
        using var result = await ImageEditRenderer.CreateRetouchedAsync(device, source,
            [new RetouchStroke(width, height, 8, [center])]);
        Assert.NotNull(result);
        Assert.Equal((uint)width, result.SizeInPixels.Width);
        Assert.Equal((uint)height, result.SizeInPixels.Height);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, result.GetPixelBytes(center.X, center.Y, 1, 1));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, source.GetPixelBytes(center.X, center.Y, 1, 1));
    }

    [Fact]
    public void FullPhotoRibbon_ExposesRetouchAndBrushControlsAreOutsideAdjustments()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var ribbon = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "TopRibbon.xaml"));
        var command = Assert.Single(ribbon.Descendants().Where(element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "RibbonRetouch"));
        Assert.Equal("OnRetouchClick", (string?)command.Attribute("Click"));
        Assert.Equal("ViewerTools", (string?)command.Parent?.Attribute(x + "Name"));

        var editor = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "ImageEditorView.xaml"));
        var toolbar = Assert.Single(editor.Descendants().Where(element =>
            (string?)element.Attribute(x + "Name") == "RetouchToolbar"));
        Assert.Contains(toolbar.Descendants(), element => (string?)element.Attribute(x + "Name") == "RetouchRadius");
        Assert.DoesNotContain(toolbar.Ancestors(), element =>
            (string?)element.Attribute(x + "Name") == "AdjustmentsPanel");
        var canvas = Assert.Single(editor.Descendants().Where(element =>
            (string?)element.Attribute(x + "Name") == "EditCanvas"));
        Assert.Equal("OnEditCanvasPointerEntered", (string?)canvas.Attribute("PointerEntered"));
        Assert.Equal("OnEditCanvasPointerExited", (string?)canvas.Attribute("PointerExited"));
        var warning = Assert.Single(editor.Descendants().Where(element =>
            (string?)element.Attribute(x + "Name") == "RetouchStatus"));
        Assert.Equal((string?)canvas.Attribute("Grid.Row"), (string?)warning.Attribute("Grid.Row"));
        Assert.Equal("1", (string?)warning.Attribute("Canvas.ZIndex"));
    }

    [Fact]
    public async Task IncrementalPreviewAndFullRecipeReplayProduceIdenticalPixels()
    {
        var pixels = Surface(true);
        Blemish(pixels);
        using var device = new CanvasDevice();
        using var source = CanvasBitmap.CreateFromBytes(device, pixels, Size, Size,
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96,
            CanvasAlphaMode.Premultiplied);
        var strokes = new[] { Stroke(), Stroke(4, new RetouchPoint(32, 32)) };
        using var first = await ImageEditRenderer.CreateRetouchedAsync(device, source, [strokes[0]]);
        using var incremental = await ImageEditRenderer.CreateRetouchedAsync(device, first!, [strokes[1]]);
        using var replayed = await ImageEditRenderer.CreateRetouchedAsync(device, source, strokes);
        Assert.Equal(replayed!.GetPixelBytes(), incremental!.GetPixelBytes());
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(6, 90)]
    [InlineData(8, 23)]
    public async Task PreviewSaveAndLosslessReopenAgreeAtSourceResolution(int orientation, double rotation)
    {
        var path = Path.Combine(_directory, $"source-{orientation}.{(orientation == 1 ? "png" : "jpg")}");
        var pixels = Surface(true);
        Blemish(pixels);
        await RenderQualityFixtures.WriteImageAsync(path, Size, Size, pixels, dpi: 300, orientation: (ushort)orientation);
        var originalBytes = await File.ReadAllBytesAsync(path);
        using var device = new CanvasDevice();
        using var source = await ImageEditRenderer.LoadOrientedAsync(device, path);
        // Use the actually oriented blemish position; orientation 6/8 rotate around the square's centre.
        var center = orientation switch { 6 => new RetouchPoint(63, 64), 8 => new(64, 63), _ => new(64, 64) };
        var p = new EditParameters { RetouchStrokes = [Stroke(10, center)], RotationAngle = rotation, Exposure = 0.1 };
        using var retouched = await ImageEditRenderer.CreateRetouchedAsync(device, source, p.RetouchStrokes);
        var extent = EditEffectGraph.ComputeOutputExtent(Size, Size, rotation);
        using var target = new CanvasRenderTarget(device, extent.Width, extent.Height, 96);
        using (var session = target.CreateDrawingSession())
        {
            session.Clear(orientation == 1 ? Microsoft.UI.Colors.Transparent : Microsoft.UI.Colors.White);
            ImageEditRenderPass.Draw(session, retouched!, p, new(Size, Size), Matrix3x2.CreateTranslation(extent.Offset));
        }
        var expected = target.GetPixelBytes();
        var rendered = await ImageEditRenderer.RenderToPixelsAsync(device, path, p);
        Assert.Equal(expected, rendered.Pixels);
        var backups = new OriginalBackupService(Path.Combine(_directory, "originals"));
        var hash = await OriginalBackupService.ComputeFileHashAsync(path);
        await new ImageEditService(backups).ApplyEditsAsync(path, p,
            (file, parameters) => ImageEditRenderer.RenderToFileAsync(file, parameters, device));
        using var reopened = await ImageEditRenderer.LoadOrientedAsync(device, path);
        var savedPixels = reopened.GetPixelBytes();
        if (orientation == 1) Assert.Equal(expected, savedPixels);
        else
        {
            // JPEG re-encoding is lossy; the source-resolution pre-encode comparison above is exact.
            var meanSquaredError = expected.Zip(savedPixels, (a, b) => (double)(a - b) * (a - b)).Average();
            Assert.InRange(Math.Sqrt(meanSquaredError), 0, 5);
        }
        Assert.True(await backups.RestoreOriginalAsync(path, hash));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task NavigationAndClose_RespectPendingStrokesAndBusyEvaluation()
    {
        using var device = new CanvasDevice();
        Task<(uint Width, uint Height)> RenderAsync(string file, EditParameters parameters) =>
            ImageEditRenderer.RenderToFileAsync(file, parameters, device);
        var backups = new OriginalBackupService(Path.Combine(_directory, "originals"));
        var vm = new ImageEditorViewModel(backups);
        var entries = new List<ImageEntry>();
        foreach (var name in new[] { "first.png", "second.png" })
        {
            var path = Path.Combine(_directory, name);
            await RenderQualityFixtures.WriteImageAsync(path, Size, Size, Surface(false));
            entries.Add(new ImageEntry { FilePath = path, FileName = name });
        }
        vm.UpdateLibraryImages(entries);
        await vm.OpenForEditAsync(entries[0]);
        vm.AddRetouchStroke(Stroke());
        Assert.False(await vm.NavigateAsync(1, () => Task.FromResult(PendingEditDecision.Cancel),
            RenderAsync));
        Assert.Same(entries[0], vm.CurrentEntry);
        Assert.Single(vm.GetCurrentParameters().RetouchStrokes);
        vm.IsRetouching = true;
        Assert.False(vm.CanNavigate);
        Assert.False(vm.CanUndo);
        Assert.False(await vm.SaveAsync(RenderAsync));
        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.OpenForEditAsync(entries[1]));
        vm.IsRetouching = false;
        Assert.True(await vm.NavigateAsync(1, () => Task.FromResult(PendingEditDecision.Save),
            RenderAsync));
        Assert.Empty(vm.GetCurrentParameters().RetouchStrokes);
        Assert.False(vm.HasChanges);
        Assert.Empty(ImageEditService.ReadEditParameters(entries[0].FilePath).RetouchStrokes);
        vm.AddRetouchStroke(Stroke());
        Assert.False(await vm.TryCloseAsync(() => Task.FromResult(PendingEditDecision.Cancel),
            RenderAsync));
        Assert.True(vm.HasChanges);
        Assert.True(await vm.TryCloseAsync(() => Task.FromResult(PendingEditDecision.Discard),
            RenderAsync));
        Assert.False(vm.IsOpen);
    }
}
