using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using System.Numerics;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using XmpCore;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class NoiseReductionTests
{
    [Fact]
    public void Parameters_CloneResetAndXmpRoundTrip()
    {
        var parameters = new EditParameters { NoiseReduction = 0.65 };
        Assert.True(parameters.HasAdjustments);
        var clone = parameters.Clone();
        parameters.Reset();
        Assert.False(parameters.HasAdjustments);
        Assert.Equal(0, parameters.NoiseReduction);
        Assert.Equal(0.65, clone.NoiseReduction);

        var xmp = XmpMetaFactory.Create();
        Assert.Equal(0, EditParametersSerializer.ReadFromXmp(xmp).NoiseReduction);
        EditParametersSerializer.WriteToXmp(xmp, clone);
        var packet = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(packet));
        Assert.Equal(0.65, EditParametersSerializer.ReadFromXmp(XmpMetaFactory.Parse(stream)).NoiseReduction);
        xmp.SetProperty(EditParameters.XmpNamespace, "Unrelated", "preserve");
        EditParametersSerializer.ClearFromXmp(xmp);
        Assert.False(xmp.DoesPropertyExist(EditParameters.XmpNamespace, "NoiseReduction"));
        Assert.Equal("preserve", xmp.GetPropertyString(EditParameters.XmpNamespace, "Unrelated"));
        Assert.False(EditParametersSerializer.ReadFromXmp(xmp).HasAdjustments);
    }

    [Fact]
    public void NeutralBypassesShader_AndAmountIsBounded()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var source = CreateBitmap(device, NoisyEdge(8, 8), 8, 8);
        Assert.Same(source, EditEffectGraph.Build(source, new(8, 8), new()));
        Assert.Same(source, NoiseReductionEffect.Create(source, new(8, 8), -1));
        using var graph = (CropEffect)NoiseReductionEffect.Create(source, new(8, 8), 2);
        var shader = Assert.IsType<PixelShaderEffect>(graph.Source);
        Assert.Equal(1f, shader.Properties["Strength"]);
        Assert.Equal(2, shader.MaxSamplerOffset);
        Assert.Equal(SamplerCoordinateMapping.Offset, shader.Source1Mapping);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NoiseReductionEffect.Create(source, new(8, 8), double.NaN));
    }

    [Fact]
    public void Bilateral_ReducesNoiseMonotonically_WithoutBlurringStrongEdges()
    {
        const int width = 64, height = 32;
        var input = NoisyEdge(width, height);
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var source = CreateBitmap(device, input, width, height);
        var low = Render(device, source, width, height, new() { NoiseReduction = 0.3 });
        var high = Render(device, source, width, height, new() { NoiseReduction = 1 });
        var inputError = NoiseError(input, width, height);
        Assert.True(NoiseError(low, width, height) < inputError);
        Assert.True(NoiseError(high, width, height) < NoiseError(low, width, height));
        Assert.True(NoiseError(high, width, height) < inputError * 0.35);
        for (var y = 0; y < height; y++)
        {
            Assert.InRange(high[(y * width + width / 2 - 1) * 4], (byte)25, (byte)55);
            Assert.InRange(high[(y * width + width / 2) * 4], (byte)200, (byte)230);
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 7)]
    [InlineData(7, 1)]
    [InlineData(12, 9)]
    public void FlatColorAndBoundsArePreserved_AtImageBorders(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 40;
            pixels[i + 1] = 100;
            pixels[i + 2] = 160;
            pixels[i + 3] = 255;
        }
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var source = CreateBitmap(device, pixels, width, height);
        using var graph = (CropEffect)EditEffectGraph.Build(source, new(width, height), new() { NoiseReduction = 1 });
        Assert.Equal(new Rect(0, 0, width, height), graph.GetBounds(device));
        Assert.Equal(pixels, Render(device, source, width, height, new() { NoiseReduction = 1 }));
    }

    [Fact]
    public void AlphaIsUnchanged_AndTransparentNeighborsDoNotDarkenColor()
    {
        const int width = 8, height = 8;
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            var alpha = (byte)(i % 3 == 0 ? 0 : i % 3 == 1 ? 128 : 255);
            pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = pixels[i * 4 + 3] = alpha;
        }
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var source = CreateBitmap(device, pixels, width, height);
        Assert.Equal(pixels, Render(device, source, width, height, new() { NoiseReduction = 1 }));
    }

    [Fact]
    public void DenoisingPrecedesExistingToneSharpeningAndRotation()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var source = CreateBitmap(device, NoisyEdge(8, 8), 8, 8);
        using var graph = (Transform2DEffect)EditEffectGraph.Build(source, new(8, 8),
            new() { NoiseReduction = 0.5, Exposure = 0.2, Sharpness = 0.3, RotationAngle = 90 });
        var sharpen = Assert.IsType<SharpenEffect>(graph.Source);
        var exposure = Assert.IsType<ExposureEffect>(sharpen.Source);
        var crop = Assert.IsType<CropEffect>(exposure.Source);
        Assert.Same(source, Assert.IsType<PixelShaderEffect>(crop.Source).Source1);
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(2f)]
    public void PreviewZoomScalesTheFilteredImage_NotTheKernel(float scale)
    {
        const int width = 64, height = 32;
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var source = CreateBitmap(device, NoisyEdge(width, height), width, height);
        var parameters = new EditParameters { NoiseReduction = 0.8 };
        using var filtered = CreateBitmap(device, Render(device, source, width, height, parameters), width, height);
        using var graph = (CropEffect)EditEffectGraph.Build(source, new(width, height), parameters);

        byte[] DrawScaled(ICanvasImage image)
        {
            using var target = new CanvasRenderTarget(device, width * scale, height * scale, 96);
            using (var session = target.CreateDrawingSession())
            {
                session.Transform = Matrix3x2.CreateScale(scale);
                session.DrawImage(image, Vector2.Zero, new Rect(0, 0, width, height),
                    1, CanvasImageInterpolation.NearestNeighbor);
            }
            return target.GetPixelBytes();
        }

        var expected = DrawScaled(filtered);
        var preview = DrawScaled(graph);
        Assert.Equal(expected.Length, preview.Length);
        for (var i = 0; i < expected.Length; i++)
            Assert.InRange(Math.Abs(preview[i] - expected[i]), 0, 1);
    }

    [Fact]
    public async Task SavedPngMatchesFullResolutionPreview_AndDoesNotReapplyPendingEdits()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-denoise-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "noise.png");
            await WicTestImage.CreateAsync(path, 32, 24, false, (x, y) =>
            {
                var value = (byte)((x < 16 ? 40 : 215) + ((x + y) % 2 == 0 ? 12 : -12));
                return (value, value, value);
            });
            var parameters = new EditParameters { NoiseReduction = 0.8, Exposure = 0.1, Sharpness = 0.2 };
            var device = CanvasDevice.GetSharedDevice();
            byte[] expected;
            using (var source = await ImageEditRenderer.LoadOrientedAsync(device, path))
                expected = Render(device, source, 32, 24, parameters);
            var backups = new OriginalBackupService(Path.Combine(directory, "originals"));
            var service = new ImageEditService(backups);
            await service.SaveEditParametersAsync(path, parameters);
            var backupPath = await backups.GetBackupPathAsync(path);
            Assert.NotNull(backupPath);
            Assert.Equal((32u, 24u), await service.ApplyEditsAsync(path, parameters, ImageEditRenderer.RenderToFileAsync));
            using var saved = await ImageEditRenderer.LoadOrientedAsync(device, path);
            Assert.Equal(expected, saved.GetPixelBytes());
            Assert.False(ImageEditService.ReadEditParameters(path).HasAdjustments);
            Assert.True(File.Exists(backupPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static CanvasBitmap CreateBitmap(CanvasDevice device, byte[] pixels, int width, int height) =>
        CanvasBitmap.CreateFromBytes(device, pixels, width, height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96);

    private static byte[] Render(CanvasDevice device, CanvasBitmap source, int width, int height, EditParameters parameters)
    {
        var graph = EditEffectGraph.Build(source, new Vector2(width, height), parameters);
        try
        {
            using var target = new CanvasRenderTarget(device, width, height, 96);
            using (var session = target.CreateDrawingSession()) session.DrawImage(graph);
            return target.GetPixelBytes();
        }
        finally
        {
            if (!ReferenceEquals(graph, source)) ((IDisposable)graph).Dispose();
        }
    }

    private static byte[] NoisyEdge(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        var random = new Random(26);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = (y * width + x) * 4;
            for (var channel = 0; channel < 3; channel++)
                pixels[offset + channel] = (byte)((x < width / 2 ? 40 : 215) + random.Next(-14, 15));
            pixels[offset + 3] = 255;
        }
        return pixels;
    }

    private static double NoiseError(byte[] pixels, int width, int height)
    {
        double sum = 0;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        for (var channel = 0; channel < 3; channel++)
        {
            var error = pixels[(y * width + x) * 4 + channel] - (x < width / 2 ? 40 : 215);
            sum += error * error;
        }
        return sum / (width * height * 3);
    }
}
