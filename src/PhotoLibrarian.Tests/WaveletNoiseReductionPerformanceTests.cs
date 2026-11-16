// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Services;
using System.Numerics;
using Windows.Graphics.DirectX;
using Xunit;

namespace PhotoLibrarian.Tests;

[Collection(WaveletNoiseReductionExperimentCollection.Name)]
public sealed class WaveletNoiseReductionPerformanceTests
{
    [Fact]
    public void SourceResolutionRenderCostsAreMeasured()
    {
        using var device = new CanvasDevice();
        foreach (var (width, height, baselineStrength) in new[]
        {
            (1024, 1024, 0.49),
            (4000, 3000, 0.59),
            (6000, 4000, 0.67)
        })
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();

            var pixels = new byte[checked(width * height * 4)];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var value = (byte)((i / 4) % 251);
                pixels[i] = value;
                pixels[i + 1] = value;
                pixels[i + 2] = value;
                pixels[i + 3] = 255;
            }

            using var source = CanvasBitmap.CreateFromBytes(
                device, pixels, width, height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96);
            Measure(device, source, width, height, "Wavelet", () =>
                WaveletNoiseReductionExperiment.Create(source, new(width, height), 0.8));
            Measure(device, source, width, height, "Bilateral", () =>
                EditEffectGraph.Build(source, new Vector2(width, height), new EditParameters
                {
                    NoiseReduction = baselineStrength
                }));
        }
    }

    private static void Measure(
        CanvasDevice device,
        CanvasBitmap source,
        int width,
        int height,
        string name,
        Func<ICanvasImage> createGraph)
    {
        var graph = createGraph();
        try
        {
            using (var warmup = new CanvasRenderTarget(device, width, height, 96))
            {
                using (var session = warmup.CreateDrawingSession()) session.DrawImage(graph);
                _ = warmup.GetPixelBytes();
            }

            var timings = new List<double>(3);
            for (var iteration = 0; iteration < 3; iteration++)
            {
                using var target = new CanvasRenderTarget(device, width, height, 96);
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                using (var session = target.CreateDrawingSession()) session.DrawImage(graph);
                _ = target.GetPixelBytes();
                timings.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            var median = timings.Order().ElementAt(timings.Count / 2);
            var workingSetMb = Environment.WorkingSet / 1_000_000.0;
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{name} {width}x{height}: warm render/readback median {median:F1} ms; " +
                $"process working set {workingSetMb:F0} MB");
        }
        finally
        {
            if (!ReferenceEquals(graph, source)) ((IDisposable)graph).Dispose();
        }
    }
}
