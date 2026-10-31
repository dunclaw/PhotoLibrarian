// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using System.Diagnostics;
using System.Numerics;
using Windows.Graphics.DirectX;
using XmpCore;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class UnsharpMaskTests
{
    [Fact]
    public void Parameters_CloneResetAndRoundTripXmp_AndOldRecipesDefaultToLegacy()
    {
        var parameters = new EditParameters
        {
            Sharpness = 0.35,
            SharpeningMode = SharpeningMode.UnsharpMask,
            UnsharpMaskAmount = 1.25,
            UnsharpMaskRadius = 2.4,
            UnsharpMaskThreshold = 0.04
        };
        var clone = parameters.Clone();
        parameters.Reset();

        Assert.False(parameters.HasAdjustments);
        Assert.Equal(SharpeningMode.Legacy, parameters.SharpeningMode);
        Assert.Equal(0, parameters.UnsharpMaskAmount);
        Assert.Equal(1, parameters.UnsharpMaskRadius);
        Assert.Equal(0.02, parameters.UnsharpMaskThreshold);
        Assert.Equal(SharpeningMode.UnsharpMask, clone.SharpeningMode);
        Assert.Equal(1.25, clone.UnsharpMaskAmount);

        var oldRecipe = XmpMetaFactory.Create();
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(
            EditParameters.XmpNamespace, EditParameters.XmpPrefix);
        oldRecipe.SetPropertyDouble(EditParameters.XmpNamespace, "pledit:Sharpness", 0.35);
        var legacy = EditParametersSerializer.ReadFromXmp(oldRecipe);
        Assert.Equal(SharpeningMode.Legacy, legacy.SharpeningMode);
        Assert.Equal(0.35, legacy.Sharpness);
        Assert.Equal(1, legacy.UnsharpMaskRadius);
        Assert.Equal(0.02, legacy.UnsharpMaskThreshold);

        var xmp = XmpMetaFactory.Create();
        EditParametersSerializer.WriteToXmp(xmp, clone);
        var packet = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(packet));
        var restored = EditParametersSerializer.ReadFromXmp(XmpMetaFactory.Parse(stream));
        Assert.Equal(SharpeningMode.UnsharpMask, restored.SharpeningMode);
        Assert.Equal(1.25, restored.UnsharpMaskAmount);
        Assert.Equal(2.4, restored.UnsharpMaskRadius);
        Assert.Equal(0.04, restored.UnsharpMaskThreshold);
        EditParametersSerializer.ClearFromXmp(xmp);
        Assert.False(xmp.DoesPropertyExist(EditParameters.XmpNamespace, "SharpeningMode"));
        Assert.False(EditParametersSerializer.ReadFromXmp(xmp).HasAdjustments);
    }

    [Fact]
    public void LegacyIsTheDefaultAndRetainsTheExistingEffectMapping()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var source = CreateBitmap(device, RenderQualityFixtures.CreatePixels(
            RenderQualityFixtureKind.Edge, 7, 5), 7, 5);

        Assert.Equal(SharpeningMode.Legacy, new EditParameters().SharpeningMode);
        Assert.Same(source, EditEffectGraph.Build(source, new(7, 5), new()));
        using var graph = Assert.IsType<SharpenEffect>(
            EditEffectGraph.Build(source, new(7, 5), new() { Sharpness = 0.4 }));
        Assert.Equal(2f, graph.Amount);
        Assert.Equal(0.05f, graph.Threshold);
    }

    [Fact]
    public void UnsharpMask_ZeroAmountBypassesExactly_AndEffectBoundsAndInputsAreDefined()
    {
        const int width = 7, height = 5;
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var source = CreateBitmap(device, RenderQualityFixtures.CreatePixels(
            RenderQualityFixtureKind.Edge, width, height, alpha: true), width, height);

        Assert.Same(source, EditEffectGraph.Build(source, new(width, height), new()
        {
            SharpeningMode = SharpeningMode.UnsharpMask
        }));

        using var graph = Assert.IsType<CropEffect>(EditEffectGraph.Build(
            source,
            new(width, height),
            new()
            {
                SharpeningMode = SharpeningMode.UnsharpMask,
                UnsharpMaskAmount = 4,
                UnsharpMaskRadius = 20,
                UnsharpMaskThreshold = -1
            }));
        Assert.Equal(new Windows.Foundation.Rect(0, 0, width, height), graph.GetBounds(device));
        var shader = Assert.IsType<PixelShaderEffect>(graph.Source);
        Assert.Same(source, shader.Source1);
        Assert.Equal(2f, shader.Properties["Amount"]);
        Assert.Equal(0f, shader.Properties["Threshold"]);
        var blurCrop = Assert.IsType<CropEffect>(shader.Source2);
        var blur = Assert.IsType<GaussianBlurEffect>(blurCrop.Source);
        Assert.Equal(5f, blur.BlurAmount);
        Assert.Equal(new Windows.Foundation.Rect(0, 0, width, height), blurCrop.GetBounds(device));
    }

    [Fact]
    public void UnsharpMask_IncreasesEdgeContrastWithoutLeavingByteRange()
    {
        const int width = 33, height = 9;
        var input = RenderQualityFixtures.CreatePixels(RenderQualityFixtureKind.Edge, width, height);
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var source = CreateBitmap(device, input, width, height);
        var output = Render(device, source, width, height, new()
        {
            SharpeningMode = SharpeningMode.UnsharpMask,
            UnsharpMaskAmount = 2,
            UnsharpMaskRadius = 1.5,
            UnsharpMaskThreshold = 0
        });

        var originalContrast = Math.Abs(input[(width / 2) * 4] - input[(width / 2 - 1) * 4]);
        var sharpenedContrast = Math.Abs(output[(width / 2) * 4] - output[(width / 2 - 1) * 4]);
        Assert.True(sharpenedContrast > originalContrast);
        Assert.All(output, channel => Assert.InRange(channel, (byte)0, (byte)255));
    }

    [Fact]
    public void NoiseThresholdSuppressesSmoothGradientDifferences()
    {
        const int width = 65, height = 33;
        var input = RenderQualityFixtures.CreatePixels(
            RenderQualityFixtureKind.Gradient, width, height);
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var source = CreateBitmap(device, input, width, height);
        var output = Render(device, source, width, height, new()
        {
            SharpeningMode = SharpeningMode.UnsharpMask,
            UnsharpMaskAmount = 2,
            UnsharpMaskRadius = 1,
            UnsharpMaskThreshold = 0.25
        });

        Assert.Equal(input, output);
    }

    [Fact]
    public void NoiseThresholdPreventsLowAmplitudeTextureFromBeingBoosted()
    {
        const int width = 33, height = 21;
        var input = new byte[width * height * 4];
        var random = new Random(42);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var value = (byte)(108 + random.Next(0, 41));
            var offset = (y * width + x) * 4;
            input[offset] = input[offset + 1] = input[offset + 2] = value;
            input[offset + 3] = 255;
        }

        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var source = CreateBitmap(device, input, width, height);
        var withoutThreshold = Render(device, source, width, height, new()
        {
            SharpeningMode = SharpeningMode.UnsharpMask,
            UnsharpMaskAmount = 2,
            UnsharpMaskRadius = 2.5
        });
        var withThreshold = Render(device, source, width, height, new()
        {
            SharpeningMode = SharpeningMode.UnsharpMask,
            UnsharpMaskAmount = 2,
            UnsharpMaskRadius = 2.5,
            UnsharpMaskThreshold = 0.17
        });

        Assert.NotEqual(input, withoutThreshold);
        Assert.Equal(input, withThreshold);
    }

    [Fact]
    public void UnsharpMask_UsesSourcePixelRadiusAndPreservesAlphaOnSmallOddImages()
    {
        const int width = 7, height = 5;
        var input = RenderQualityFixtures.CreatePixels(
            RenderQualityFixtureKind.FineTexture, width, height, alpha: true);
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var source = CreateBitmap(device, input, width, height);
        var smallRadius = Render(device, source, width, height, new()
        {
            SharpeningMode = SharpeningMode.UnsharpMask,
            UnsharpMaskAmount = 1.25,
            UnsharpMaskRadius = 0.5
        });
        var largeRadius = Render(device, source, width, height, new()
        {
            SharpeningMode = SharpeningMode.UnsharpMask,
            UnsharpMaskAmount = 1.25,
            UnsharpMaskRadius = 4
        });

        Assert.NotEqual(smallRadius, largeRadius);
        Assert.Equal(width * height * 4, largeRadius.Length);
        for (var pixel = 0; pixel < width * height; pixel++)
            Assert.Equal(input[pixel * 4 + 3], largeRadius[pixel * 4 + 3]);
    }

    [Fact]
    public void NoiseReductionRunsBeforeUnsharpMask()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var source = CreateBitmap(device, RenderQualityFixtures.CreatePixels(
            RenderQualityFixtureKind.FlatNoise, 9, 7), 9, 7);
        using var graph = Assert.IsType<CropEffect>(EditEffectGraph.Build(
            source,
            new(9, 7),
            new()
            {
                NoiseReduction = 0.5,
                Exposure = 0.2,
                SharpeningMode = SharpeningMode.UnsharpMask,
                UnsharpMaskAmount = 1
            }));

        var shader = Assert.IsType<PixelShaderEffect>(graph.Source);
        var exposure = Assert.IsType<ExposureEffect>(shader.Source1);
        var denoiseCrop = Assert.IsType<CropEffect>(exposure.Source);
        Assert.IsType<PixelShaderEffect>(denoiseCrop.Source);
        var blurredSource = Assert.IsType<CropEffect>(shader.Source2);
        Assert.Same(exposure, Assert.IsType<GaussianBlurEffect>(blurredSource.Source).Source);
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(2f)]
    public void PreviewZoomScalesTheSharpenedImage_NotItsSourcePixelRadius(float scale)
    {
        const int width = 32, height = 24;
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var source = CreateBitmap(device, RenderQualityFixtures.CreatePixels(
            RenderQualityFixtureKind.FineTexture, width, height), width, height);
        var parameters = new EditParameters
        {
            SharpeningMode = SharpeningMode.UnsharpMask,
            UnsharpMaskAmount = 1,
            UnsharpMaskRadius = 1.5
        };
        using var filtered = CreateBitmap(
            device, Render(device, source, width, height, parameters), width, height);
        using var graph = Assert.IsType<CropEffect>(
            EditEffectGraph.Build(source, new(width, height), parameters));

        byte[] DrawScaled(ICanvasImage image)
        {
            using var target = new CanvasRenderTarget(device, width * scale, height * scale, 96);
            using (var session = target.CreateDrawingSession())
            {
                session.Transform = Matrix3x2.CreateScale(scale);
                session.DrawImage(image, Vector2.Zero, new Windows.Foundation.Rect(0, 0, width, height),
                    1, CanvasImageInterpolation.NearestNeighbor);
            }
            return target.GetPixelBytes();
        }

        Assert.Equal(DrawScaled(filtered), DrawScaled(graph));
    }

    private static CanvasBitmap CreateBitmap(CanvasDevice device, byte[] pixels, int width, int height) =>
        CanvasBitmap.CreateFromBytes(
            device, pixels, width, height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96);

    private static byte[] Render(
        CanvasDevice device, CanvasBitmap source, int width, int height, EditParameters parameters)
    {
        var graph = EditEffectGraph.Build(source, new Vector2(width, height), parameters);
        try
        {
            using var target = new CanvasRenderTarget(device, width, height, 96);
            using (var session = target.CreateDrawingSession())
                session.DrawImage(graph);
            return target.GetPixelBytes();
        }
        finally
        {
            if (!ReferenceEquals(graph, source)) ((IDisposable)graph).Dispose();
        }
    }
}

public sealed class UnsharpMaskPerformanceTests
{
    [Theory]
    [InlineData(1024, 1024)]
    [InlineData(4000, 3000)]
    [InlineData(6000, 4000)]
    public void SourceResolutionRenderCompletes(int width, int height)
    {
        using var device = CanvasDevice.GetSharedDevice();
        var pixels = CreatePerformancePixels(width, height);
        using var source = CanvasBitmap.CreateFromBytes(
            device, pixels, width, height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96);
        var parameters = new EditParameters
        {
            SharpeningMode = SharpeningMode.UnsharpMask,
            UnsharpMaskAmount = 1,
            UnsharpMaskRadius = 1.5,
            UnsharpMaskThreshold = 0.02
        };

        RenderAndMeasure(device, source, width, height, parameters);
        var samples = new double[3];
        for (var index = 0; index < samples.Length; index++)
            samples[index] = RenderAndMeasure(device, source, width, height, parameters);
        Array.Sort(samples);

        Console.WriteLine(
            $"Unsharp mask {width}x{height} ({width * (long)height / 1_000_000d:0.##} MP): " +
            $"warm render median {samples[1]:0.0} ms; samples {string.Join(", ", samples.Select(x => $"{x:0.0}"))} ms.");
    }

    private static double RenderAndMeasure(
        CanvasDevice device, CanvasBitmap source, int width, int height, EditParameters parameters)
    {
        using var target = new CanvasRenderTarget(device, width, height, 96);
        var graph = EditEffectGraph.Build(source, new Vector2(width, height), parameters);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            using (var session = target.CreateDrawingSession())
                session.DrawImage(graph);
            var pixels = target.GetPixelBytes();
            stopwatch.Stop();
            GC.KeepAlive(pixels);
            return stopwatch.Elapsed.TotalMilliseconds;
        }
        finally
        {
            if (!ReferenceEquals(graph, source)) ((IDisposable)graph).Dispose();
        }
    }

    private static byte[] CreatePerformancePixels(int width, int height)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = (y * width + x) * 4;
            var value = (byte)((x * 13 + y * 7) & 0xff);
            pixels[offset] = value;
            pixels[offset + 1] = (byte)(255 - value);
            pixels[offset + 2] = value;
            pixels[offset + 3] = 255;
        }
        return pixels;
    }
}
