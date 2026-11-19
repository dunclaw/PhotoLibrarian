// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using PhotoLibrarian.Services;
using System.Numerics;
using Windows.Graphics.DirectX;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class PostCropEffectTests
{
    [Fact]
    public void NeutralBypassesAndInvalidMappingOrPrecisionReturnsAnErrorWithoutAnImage()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var source = CreateSource(device, 7, 5, false);
        Assert.True(PostCropEffect.TryCreate(source, new(7, 5), new(), new(7, 5),
            Vector2.Zero, Vector2.One, out var neutral, out var error), error);
        Assert.Same(source, neutral);
        Assert.False(PostCropEffect.TryCreate(source, new(7, 5), new(), new(7, 5),
            Vector2.Zero, new(0, 1), out var invalid, out error));
        Assert.Null(invalid);
        Assert.NotEmpty(error);
        Assert.False(PostCropEffect.TryCreate(source, new(7, 5), new() { GrainAmount = 1 },
            new(float.MaxValue, 1), new(float.MaxValue / 2, 0), Vector2.One, out invalid, out error));
        Assert.Null(invalid);
        Assert.Contains("precision", error);
    }

    [Fact]
    public void ShaderMatchesIndependentReferenceForBothSignsControlsFullSeedAndPartialAlpha()
    {
        const int width = 23, height = 17;
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var source = CreateSource(device, width, height, true);
        var input = source.GetPixelBytes();
        var settings = new[]
        {
            new PostCropEffectSettings { VignetteAmount = -1, Feather = 0 },
            new PostCropEffectSettings { VignetteAmount = 0.8, Feather = 1, Roundness = 0.8 },
            new PostCropEffectSettings { VignetteAmount = -1, HighlightPriority = 1 },
            new PostCropEffectSettings { GrainAmount = 1, Seed = uint.MaxValue },
            new PostCropEffectSettings
            {
                VignetteAmount = -0.7, GrainAmount = 0.8, GrainSize = 1, Roughness = 1, Seed = 0xabcdef01
            }
        };
        foreach (var recipe in settings)
        {
            var output = Render(device, source, width, height, recipe, new(width, height), Vector2.Zero, Vector2.One);
            AssertReference(input, output, width, height, recipe, new(width, height), Vector2.Zero, Vector2.One);
        }
    }

    [Fact]
    public void GlobalOriginsProduceIdenticalTilesAndPresentationTranslationDoesNotChangeGrain()
    {
        const int width = 31, height = 19;
        var settings = new PostCropEffectSettings
        {
            VignetteAmount = -0.6, GrainAmount = 1, GrainSize = 0.6, Roughness = 0.7, Seed = 0x80000001
        };
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var full = CreateSource(device, width, height, false);
        var whole = Render(device, full, width, height, settings, new(width, height), Vector2.Zero, Vector2.One);
        for (var startY = 0; startY < height; startY += 7)
        for (var startX = 0; startX < width; startX += 11)
        {
            var tileWidth = Math.Min(11, width - startX);
            var tileHeight = Math.Min(7, height - startY);
            using var tile = CreateSource(device, tileWidth, tileHeight, false);
            var pixels = Render(device, tile, tileWidth, tileHeight, settings,
                new(width, height), new(startX, startY), Vector2.One);
            for (var y = 0; y < tileHeight; y++)
            for (var x = 0; x < tileWidth; x++)
            for (var channel = 0; channel < 4; channel++)
                Assert.Equal(whole[((startY + y) * width + startX + x) * 4 + channel],
                    pixels[(y * tileWidth + x) * 4 + channel]);
        }

        Assert.True(PostCropEffect.TryCreate(full, new(width, height), settings, new(width, height),
            Vector2.Zero, Vector2.One, out var image, out var error), error);
        using var effect = Assert.IsType<Microsoft.Graphics.Canvas.Effects.CropEffect>(image);
        using var translated = new CanvasRenderTarget(device, width + 6, height + 10, 96);
        using (var session = translated.CreateDrawingSession())
        {
            session.Transform = Matrix3x2.CreateTranslation(3, 5);
            session.DrawImage(effect);
        }
        var translatedPixels = translated.GetPixelBytes();
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        for (var channel = 0; channel < 4; channel++)
            Assert.Equal(whole[(y * width + x) * 4 + channel],
                translatedPixels[((y + 5) * (width + 6) + x + 3) * 4 + channel]);
    }

    [Fact]
    public void ReducedOutputUsesTheFullResolutionPixelFootprintRatherThanThumbnailNoise()
    {
        const int width = 19, height = 13;
        var settings = new PostCropEffectSettings { GrainAmount = 1, GrainSize = 0.2, Roughness = 1 };
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var source = CreateSource(device, width, height, false);
        var outputSize = new Vector2(4000, 3000);
        var origin = new Vector2(1130, 910);
        var scale = new Vector2(20, 20);
        var pixels = Render(device, source, width, height, settings, outputSize, origin, scale);
        AssertReference(source.GetPixelBytes(), pixels, width, height, settings, outputSize, origin, scale);
    }

    [Fact]
    public void PresentationZoomKeepsGrainAtTheSameLogicalPixelCenters()
    {
        const int width = 23, height = 17, zoom = 3;
        var settings = new PostCropEffectSettings { GrainAmount = 1, VignetteAmount = -0.7, Seed = 0xfedcba98 };
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var source = CreateSource(device, width, height, false);
        var whole = Render(device, source, width, height, settings, new(width, height), Vector2.Zero, Vector2.One);
        Assert.True(PostCropEffect.TryCreate(source, new(width, height), settings, new(width, height),
            Vector2.Zero, Vector2.One, out var image, out var error), error);
        using var effect = Assert.IsType<Microsoft.Graphics.Canvas.Effects.CropEffect>(image);
        using var target = new CanvasRenderTarget(device, width * zoom, height * zoom, 96);
        using (var session = target.CreateDrawingSession())
        {
            session.Transform = Matrix3x2.CreateScale(zoom);
            session.DrawImage(effect);
        }
        var enlarged = target.GetPixelBytes();
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        for (var channel = 0; channel < 4; channel++)
            Assert.Equal(whole[(y * width + x) * 4 + channel],
                enlarged[((y * zoom + 1) * width * zoom + x * zoom + 1) * 4 + channel]);
    }

    [Fact]
    public void ModernCameraOutputRendersWithoutChangingGrainSizeOrAddingADimensionLimit()
    {
        const int width = 6000, height = 4000;
        using var device = new CanvasDevice();
        using var source = CreateSource(device, width, height, false);
        var settings = new PostCropEffectSettings { GrainAmount = 0.6, VignetteAmount = -0.5, GrainSize = 0.5 };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var pixels = Render(device, source, width, height, settings, new(width, height), Vector2.Zero, Vector2.One);
        stopwatch.Stop();
        Assert.Equal(width * height * 4, pixels.Length);
        Assert.Equal((byte)255, pixels[^1]);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Post-crop shader {width}x{height}: {stopwatch.Elapsed.TotalMilliseconds:F1} ms including readback.");
    }

    [Fact]
    public void SmallOddImageSupportsTheMinimumGrainSizeWithoutASettingOrDimensionCutoff()
    {
        const int width = 17, height = 13;
        var settings = new PostCropEffectSettings { GrainAmount = 1, GrainSize = 0, Roughness = 1 };
        using var device = new CanvasDevice();
        using var source = CreateSource(device, width, height, false);
        var pixels = Render(device, source, width, height, settings, new(width, height), Vector2.Zero, Vector2.One);
        AssertReference(source.GetPixelBytes(), pixels, width, height, settings,
            new(width, height), Vector2.Zero, Vector2.One);
    }

    private static CanvasBitmap CreateSource(CanvasDevice device, int width, int height, bool alpha)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var opacity = alpha ? (byte)((i / 4 % 6) * 51) : (byte)255;
            pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)(128 * opacity / 255);
            pixels[i + 3] = opacity;
        }
        return CanvasBitmap.CreateFromBytes(device, pixels, width, height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
    }

    private static byte[] Render(CanvasDevice device, CanvasBitmap source, int width, int height,
        PostCropEffectSettings settings, Vector2 outputSize, Vector2 origin, Vector2 scale)
    {
        Assert.True(PostCropEffect.TryCreate(source, new(width, height), settings, outputSize,
            origin, scale, out var image, out var error), error);
        using var effect = Assert.IsType<Microsoft.Graphics.Canvas.Effects.CropEffect>(image);
        using var target = new CanvasRenderTarget(device, width, height, 96);
        using (var session = target.CreateDrawingSession()) session.DrawImage(effect);
        return target.GetPixelBytes();
    }

    private static void AssertReference(byte[] input, byte[] output, int width, int height,
        PostCropEffectSettings settings, Vector2 outputSize, Vector2 origin, Vector2 scale)
    {
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            var rgba = new Vector4(input[i + 2], input[i + 1], input[i], input[i + 3]) / 255;
            Assert.True(PostCropEffectReference.TryEvaluate(rgba, settings, outputSize.X, outputSize.Y,
                origin.X + (x + 0.5) * scale.X, origin.Y + (y + 0.5) * scale.Y, scale.X, scale.Y,
                out var expected, out var error, TestContext.Current.CancellationToken), error);
            var channels = new[] { expected.Z, expected.Y, expected.X, expected.W };
            for (var channel = 0; channel < 4; channel++)
                Assert.InRange(Math.Abs(output[i + channel] - Math.Round(channels[channel] * 255)),
                    0, channel == 3 ? 0 : 1);
        }
    }
}
