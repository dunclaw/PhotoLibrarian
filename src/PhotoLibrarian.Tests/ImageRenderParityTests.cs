// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Services;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class ImageRenderParityTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-RenderParity-{Guid.NewGuid():N}");

    public ImageRenderParityTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task OffscreenPreviewAndSaveMatchBeforeEncodingAcrossRecipesAndFixtures()
    {
        var recipes = new (string Name, EditParameters Parameters)[]
        {
            ("neutral", new()),
            ("tone", new()
            {
                Exposure = 0.35, Brightness = 0.08, Contrast = 0.22, Saturation = -0.15,
                Temperature = 0.12, Tint = -0.08, Highlights = -0.2, Shadows = 0.25,
                BlackPoint = 0.03, WhitePoint = 0.96, Midtones = 0.54
            }),
            ("detail", new() { Clarity = 0.25 }),
            ("legacy-sharpen", new() { Sharpness = 0.35 }),
            ("unsharp-mask", new()
            {
                SharpeningMode = SharpeningMode.UnsharpMask,
                UnsharpMaskAmount = 0.8,
                UnsharpMaskRadius = 1.4,
                UnsharpMaskThreshold = 0.02
            }),
            ("denoise-unsharp", new()
            {
                NoiseReduction = 0.4,
                SharpeningMode = SharpeningMode.UnsharpMask,
                UnsharpMaskAmount = 0.8,
                UnsharpMaskRadius = 1.4,
                UnsharpMaskThreshold = 0.02
            }),
            ("rotation", new() { RotationAngle = 23 })
        };

        foreach (var fixture in RenderQualityFixtures.Kinds)
        {
            var pixels = RenderQualityFixtures.CreatePixels(fixture, 47, 31);
            foreach (var recipe in recipes)
            {
                await AssertPreviewSaveParityAsync(
                    $"{fixture}-{recipe.Name}.png",
                    47,
                    31,
                    pixels,
                    recipe.Parameters);
            }
        }

        await AssertPreviewSaveParityAsync(
            "portrait-high-dpi-oriented.jpg",
            31,
            47,
            RenderQualityFixtures.CreatePixels(RenderQualityFixtureKind.Gradient, 31, 47),
            new EditParameters { Exposure = 0.2, RotationAngle = 7 },
            dpi: 300,
            orientation: 6);
        await AssertPreviewSaveParityAsync(
            "small-odd-alpha.png",
            7,
            5,
            RenderQualityFixtures.CreatePixels(
                RenderQualityFixtureKind.Edge, 7, 5, alpha: true),
            new EditParameters { RotationAngle = -90 });
    }

    [Fact]
    public async Task NeutralRenderPreservesSourcePixelsExactly()
    {
        const uint width = 19;
        const uint height = 13;
        var sourcePixels = RenderQualityFixtures.CreatePixels(
            RenderQualityFixtureKind.Gradient, width, height);
        var path = Path.Combine(_directory, "neutral.png");
        await RenderQualityFixtures.WriteImageAsync(path, width, height, sourcePixels);

        using var device = CreateGraphicsDevice();
        using var source = await ImageEditRenderer.LoadOrientedAsync(device, path);
        var rendered = await ImageEditRenderer.RenderToPixelsAsync(
            device, path, new EditParameters());

        Assert.Equal(width, rendered.Width);
        Assert.Equal(height, rendered.Height);
        byte[] originalPass;
        using (var target = new CanvasRenderTarget(device, width, height, 96f))
        {
            using (var session = target.CreateDrawingSession())
            {
                session.Clear(Microsoft.UI.Colors.Transparent);
                ImageEditRenderPass.Draw(
                    session,
                    source,
                    new EditParameters(),
                    new Vector2((float)source.Size.Width, (float)source.Size.Height),
                    Matrix3x2.Identity);
            }
            originalPass = target.GetPixelBytes();
        }
        Assert.Equal(originalPass, rendered.Pixels);
        Assert.Same(
            source,
            EditEffectGraph.Build(
                source,
                new Vector2((float)source.Size.Width, (float)source.Size.Height),
                new EditParameters()));
    }

    [Theory]
    [InlineData("png", (byte)0, (byte)0, (byte)0, (byte)0)]
    [InlineData("jpg", (byte)255, (byte)255, (byte)255, (byte)255)]
    public async Task RotatedCanvasCornersFollowFormatAlphaPolicy(
        string extension,
        byte blue,
        byte green,
        byte red,
        byte alpha)
    {
        const uint width = 41;
        const uint height = 29;
        var path = Path.Combine(_directory, $"corners.{extension}");
        await RenderQualityFixtures.WriteImageAsync(
            path,
            width,
            height,
            RenderQualityFixtures.CreatePixels(RenderQualityFixtureKind.Edge, width, height),
            jpeg: extension == "jpg");
        var rendered = await ImageEditRenderer.RenderToPixelsAsync(
            path, new EditParameters { RotationAngle = 23 });

        Assert.Equal((byte)blue, rendered.Pixels[0]);
        Assert.Equal((byte)green, rendered.Pixels[1]);
        Assert.Equal((byte)red, rendered.Pixels[2]);
        Assert.Equal((byte)alpha, rendered.Pixels[3]);
    }

    [Fact]
    public async Task PngSaveCanBeReopenedAndMatchesItsPreEncodingPixels()
    {
        const uint width = 33;
        const uint height = 21;
        var path = Path.Combine(_directory, "reopen.png");
        await RenderQualityFixtures.WriteImageAsync(
            path,
            width,
            height,
            RenderQualityFixtures.CreatePixels(
                RenderQualityFixtureKind.Gradient, width, height, alpha: true));
        var parameters = new EditParameters { Contrast = 0.2, RotationAngle = 19 };
        var beforeEncoding = await ImageEditRenderer.RenderToPixelsAsync(path, parameters);

        var saved = await ImageEditRenderer.RenderToFileAsync(path, parameters);
        var reopened = await RenderQualityFixtures.ReadPngPixelsAsync(path);
        await using var stream = File.OpenRead(path);
        var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());

        Assert.Equal((beforeEncoding.Width, beforeEncoding.Height), saved);
        Assert.Equal(beforeEncoding.Width, decoder.PixelWidth);
        Assert.Equal(beforeEncoding.Height, decoder.PixelHeight);
        Assert.Equal(beforeEncoding.Pixels.Length, reopened.Length);
        for (var index = 0; index < reopened.Length; index++)
        {
            if (index % 4 == 3)
                Assert.Equal(beforeEncoding.Pixels[index], reopened[index]);
            else
                Assert.InRange(Math.Abs(beforeEncoding.Pixels[index] - reopened[index]), 0, 1);
        }
        var orientation = await decoder.BitmapProperties.GetPropertiesAsync(["System.Photo.Orientation"]);
        Assert.Equal((ushort)1, orientation["System.Photo.Orientation"].Value);
    }

    private async Task AssertPreviewSaveParityAsync(
        string fileName,
        uint width,
        uint height,
        byte[] pixels,
        EditParameters parameters,
        double dpi = 96,
        ushort orientation = 1)
    {
        var path = Path.Combine(_directory, fileName);
        await RenderQualityFixtures.WriteImageAsync(path, width, height, pixels, dpi, orientation);
        if (dpi != 96)
        {
            using var stream = File.OpenRead(path);
            var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
            Assert.InRange(decoder.DpiX, dpi - 1, dpi + 1);
        }

        using var device = CreateGraphicsDevice();
        using var source = await ImageEditRenderer.LoadOrientedAsync(device, path);
        using var duplicateSource = await ImageEditRenderer.LoadOrientedAsync(device, path);
        Assert.Equal(source.GetPixelBytes(), duplicateSource.GetPixelBytes());
        Assert.Equal(
            new Vector2((float)source.Size.Width, (float)source.Size.Height),
            new Vector2(
                (float)source.SizeInPixels.Width,
                (float)source.SizeInPixels.Height));
        if (orientation == 6)
        {
            Assert.Equal((double)height, source.SizeInPixels.Width);
            Assert.Equal((double)width, source.SizeInPixels.Height);
        }
        var sourceWidth = source.SizeInPixels.Width;
        var sourceHeight = source.SizeInPixels.Height;
        var extent = EditEffectGraph.ComputeOutputExtent(
            sourceWidth,
            sourceHeight,
            parameters.RotationAngle);
        using var previewTarget = new CanvasRenderTarget(
            device, extent.Width, extent.Height, 96f);
        using (var session = previewTarget.CreateDrawingSession())
        {
            session.Clear(Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)
                ? Microsoft.UI.Colors.Transparent
                : Microsoft.UI.Colors.White);
            ImageEditRenderPass.Draw(
                session,
                source,
                parameters,
                new Vector2((float)source.Size.Width, (float)source.Size.Height),
                Matrix3x2.CreateTranslation(extent.Offset));
        }

        var previewPixels = previewTarget.GetPixelBytes();
        using var repeatedTarget = new CanvasRenderTarget(device, extent.Width, extent.Height, 96f);
        using (var session = repeatedTarget.CreateDrawingSession())
        {
            session.Clear(Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)
                ? Microsoft.UI.Colors.Transparent
                : Microsoft.UI.Colors.White);
            ImageEditRenderPass.Draw(
                session,
                source,
                parameters,
                new Vector2((float)source.Size.Width, (float)source.Size.Height),
                Matrix3x2.CreateTranslation(extent.Offset));
        }

        Assert.Equal(previewPixels, repeatedTarget.GetPixelBytes());
        var savedPixels = await ImageEditRenderer.RenderToPixelsAsync(device, path, parameters);

        Assert.Equal((extent.Width, extent.Height), (savedPixels.Width, savedPixels.Height));
        var firstDifference = Enumerable.Range(0, previewPixels.Length)
            .FirstOrDefault(index => previewPixels[index] != savedPixels.Pixels[index], -1);
        Assert.True(firstDifference < 0, firstDifference < 0
            ? string.Empty
            : $"Pixel mismatch for {fileName} with recipe {parameters}; first difference at byte " +
              $"{firstDifference}: preview={previewPixels[firstDifference]}, save={savedPixels.Pixels[firstDifference]}.");
    }

    private static CanvasDevice CreateGraphicsDevice()
    {
        try
        {
            return CanvasDevice.GetSharedDevice();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "The source-resolution render parity suite requires a working Win2D graphics device; " +
                "device initialization failures are test failures, not skipped parity results.",
                exception);
        }
    }
}
