// Copyright (c) Duncan Lawler. All Rights Reserved.

using Microsoft.Graphics.Canvas;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Services;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;
using static PhotoLibrarian.Tests.DeblurExperiment;

namespace PhotoLibrarian.Tests;

public sealed class DeblurExperimentTests(ITestOutputHelper output)
{
    [Fact]
    public async Task MeasureKnownKernelRecoveryAndFailureCases()
    {
        const int width = 96, height = 64;
        var sharp = Scene(width, height);
        var line = Convolve(sharp, width, height, Kernel.HorizontalLine);
        var disk = Convolve(sharp, width, height, Kernel.Disk);
        var subject = (double[])sharp.Clone();
        for (var y = 16; y < 48; y++)
            Array.Copy(line, y * width + 24, subject, y * width + 24, 48);

        var cases = new (string Name, double[] Observed, Kernel Kernel)[]
        {
            ("camera-known", line, Kernel.HorizontalLine),
            ("defocus-known", disk, Kernel.Disk),
            ("camera-noise", AddNoise(line), Kernel.HorizontalLine),
            ("defocus-noise", AddNoise(disk), Kernel.Disk),
            ("camera-jpeg", await JpegRoundTripAsync(line, width, height), Kernel.HorizontalLine),
            ("defocus-jpeg", await JpegRoundTripAsync(disk, width, height), Kernel.Disk),
            ("wrong-direction", line, Kernel.VerticalLine),
            ("wrong-shape", disk, Kernel.HorizontalLine),
            ("moving-subject", subject, Kernel.HorizontalLine),
            ("clean-control", sharp, Kernel.HorizontalLine)
        };

        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        output.WriteLine("case,method,PSNR-dB,block-SSIM,raw-out-of-range-percent,raw-peak-excess");
        foreach (var item in cases)
        {
            var observed = FromBgra(ToBgra(item.Observed));
            var baseline = LegacySharpen(device, observed, width, height);
            Report(item.Name, "no-op", sharp, observed, width, height);
            Report(item.Name, "legacy-0.35", sharp, baseline, width, height);
            foreach (var iterations in new[] { 5, 10, 20 })
            {
                var raw = Restore(observed, width, height, item.Kernel, iterations, TestContext.Current.CancellationToken);
                Assert.All(raw, value => Assert.True(double.IsFinite(value) && value >= 0));
                Report(item.Name, $"RL-{iterations}", sharp, raw, width, height);
                if (item.Name is "wrong-direction" or "wrong-shape" or "moving-subject" or "clean-control")
                    Assert.True(Psnr(sharp, FromBgra(ToBgra(raw))) < Psnr(sharp, observed));
            }
        }

        var recovered = FromBgra(ToBgra(Restore(
            line, width, height, Kernel.HorizontalLine, 20, TestContext.Current.CancellationToken)));
        Assert.True(Psnr(sharp, recovered) > Psnr(sharp, line) + 1);
        var recoveredDisk = FromBgra(ToBgra(Restore(
            disk, width, height, Kernel.Disk, 20, TestContext.Current.CancellationToken)));
        Assert.True(Psnr(sharp, recoveredDisk) > Psnr(sharp, disk) + 1);

        var subjectRestored = FromBgra(ToBgra(Restore(
            subject, width, height, Kernel.HorizontalLine, 20, TestContext.Current.CancellationToken)));
        // A global camera kernel must not be mistaken for a spatially varying subject model.
        Assert.True(Psnr(sharp, subjectRestored) < Psnr(sharp, subject));
    }

    [Fact]
    public void NoiseAmplificationAndEdgeRingingAreMeasuredBeforeClamping()
    {
        const int width = 96, height = 64;
        var flat = Enumerable.Repeat(0.5, width * height).ToArray();
        var noisy = AddNoise(flat);
        var restoredNoise = Restore(noisy, width, height, Kernel.HorizontalLine, 20, TestContext.Current.CancellationToken);
        var gain = Math.Pow(10, (Psnr(flat, noisy) - Psnr(flat, restoredNoise)) / 20);
        output.WriteLine($"Flat noise RMS amplification: {gain:F3}x");
        Assert.True(gain > 1.5);

        var edge = FromBgra(RenderQualityFixtures.CreatePixels(RenderQualityFixtureKind.Edge, width, height));
        var blurred = Convolve(edge, width, height, Kernel.HorizontalLine);
        var restoredEdge = Restore(blurred, width, height, Kernel.HorizontalLine, 20, TestContext.Current.CancellationToken);
        var overshoot = restoredEdge.Max() - edge.Max();
        var undershoot = edge.Min() - restoredEdge.Min();
        output.WriteLine($"Step ringing above/below reference: {overshoot:F4}/{undershoot:F4}");
        Assert.True(overshoot > 0.01);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 7)]
    [InlineData(7, 1)]
    [InlineData(13, 9)]
    public void ConstantSmallOddInputsStayConstant(int width, int height)
    {
        var source = Enumerable.Repeat(0.4, width * height).ToArray();
        foreach (var kernel in Enum.GetValues<Kernel>())
        {
            var restored = Restore(source, width, height, kernel, MaximumIterations, TestContext.Current.CancellationToken);
            Assert.All(restored, value => Assert.InRange(Math.Abs(value - 0.4), 0, 1e-12));
        }
    }

    [Fact]
    public void BoundsCancellationAndZeroIterationsAreExplicit()
    {
        var token = TestContext.Current.CancellationToken;
        double[] source = [0, 0.25, 0.75, 1];
        var neutral = Restore(source, 2, 2, Kernel.Disk, 0, token);
        Assert.Equal(source, neutral);
        Assert.NotSame(source, neutral);
        Restore(source, 2, 2, Kernel.Disk, 20, token);
        Assert.Equal(new double[4], Restore(new double[4], 2, 2, Kernel.Disk, 20, token));
        Assert.Throws<ArgumentOutOfRangeException>(() => Restore(source, 2, 2, Kernel.Disk, 21, token));
        Assert.Throws<ArgumentOutOfRangeException>(() => Restore(source, 2, 2, Kernel.Disk, -1, token));
        Assert.Throws<ArgumentOutOfRangeException>(() => Restore(source, 1, 3, Kernel.Disk, 1, token));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Restore(new double[257 * 256], 257, 256, Kernel.Disk, 1, token));
        Assert.Throws<ArgumentOutOfRangeException>(() => Restore(source, 2, 2, (Kernel)99, 1, token));
        Assert.Throws<ArgumentException>(() => Restore([double.NaN], 1, 1, Kernel.Disk, 1, token));
        Assert.Throws<ArgumentException>(() => Restore([double.PositiveInfinity], 1, 1, Kernel.Disk, 1, token));
        Assert.Throws<ArgumentException>(() => Restore([-0.1], 1, 1, Kernel.Disk, 1, token));
        Assert.Throws<ArgumentException>(() => Restore([1.1], 1, 1, Kernel.Disk, 1, token));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            Restore(source, 2, 2, Kernel.Disk, 20, cancellation.Token));
        Assert.Equal(new double[] { 0, 0.25, 0.75, 1 }, source);
    }

    [Fact]
    public void MetricsAndIrrecoverableLineNullAreExplicit()
    {
        const int width = 70, height = 7;
        var flat = Enumerable.Repeat(0.5, width * height).ToArray();
        var detail = new double[flat.Length];
        for (var i = 0; i < detail.Length; i++)
            detail[i] = 0.5 + 0.2 * Math.Cos(2 * Math.PI * (i % width) / 7);
        var blurred = Convolve(detail, width, height, Kernel.HorizontalLine);
        Assert.All(blurred, value => Assert.InRange(Math.Abs(value - 0.5), 0, 1e-12));
        var restored = Restore(blurred, width, height, Kernel.HorizontalLine, 20, TestContext.Current.CancellationToken);
        Assert.All(restored, value => Assert.InRange(Math.Abs(value - 0.5), 0, 1e-12));
        Assert.True(double.IsPositiveInfinity(Psnr(flat, flat)));
        Assert.Equal(1, Ssim(flat, flat, width, height), 12);
        Assert.Equal(20, Psnr([0.5], [0.6]), 10);
    }

    [Fact]
    public void KernelsPreserveEnergyAndUseTheCorrectAdjoint()
    {
        const int width = 17, height = 11;
        var impulse = new double[width * height];
        impulse[0] = 1;
        var scene = Scene(width, height);
        foreach (var kernel in Enum.GetValues<Kernel>())
        {
            var blurredImpulse = Convolve(impulse, width, height, kernel);
            Assert.Equal(1, blurredImpulse.Sum(), 12);
            Assert.All(blurredImpulse, value => Assert.True(value >= 0));
            var blurredScene = Convolve(scene, width, height, kernel);
            var forwardInnerProduct = blurredImpulse.Zip(scene, (a, b) => a * b).Sum();
            var adjointInnerProduct = impulse.Zip(blurredScene, (a, b) => a * b).Sum();
            Assert.Equal(forwardInnerProduct, adjointInnerProduct, 12);
        }
    }

    private void Report(string name, string method, double[] sharp, double[] raw, int width, int height)
    {
        var pixels = FromBgra(ToBgra(raw));
        var clipped = raw.Count(value => value < 0 || value > 1) * 100.0 / raw.Length;
        var excess = raw.Max(value => Math.Max(0, Math.Max(-value, value - 1)));
        output.WriteLine($"{name},{method},{Psnr(sharp, pixels):F3},{Ssim(sharp, pixels, width, height):F4},{clipped:F3},{excess:F4}");
    }

    private static double[] LegacySharpen(CanvasDevice device, double[] pixels, int width, int height)
    {
        using var source = CanvasBitmap.CreateFromBytes(device, ToBgra(pixels), width, height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var target = new CanvasRenderTarget(device, width, height, 96);
        using (var session = target.CreateDrawingSession())
        {
            ImageEditRenderPass.Draw(session, source, new EditParameters { Sharpness = 0.35 },
                new Vector2(width, height), Matrix3x2.Identity);
        }
        return FromBgra(target.GetPixelBytes());
    }

    private static async Task<double[]> JpegRoundTripAsync(double[] pixels, int width, int height)
    {
        using var stream = new InMemoryRandomAccessStream();
        var options = new BitmapPropertySet
        {
            ["ImageQuality"] = new BitmapTypedValue(0.75f, Windows.Foundation.PropertyType.Single)
        };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, options);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            (uint)width, (uint)height, 96, 96, ToBgra(pixels));
        await encoder.FlushAsync();
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return FromBgra(data.DetachPixelData());
    }
}
