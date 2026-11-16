// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using PhotoLibrarian.Core.Models;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace PhotoLibrarian.Services;

/// <summary>Sampled final-render histogram, independent of view overlays, zoom and comparison.</summary>
internal sealed record EditorPreviewAnalysis(float[] Bins, bool HasShadows, bool HasHighlights)
{
    internal static EditorPreviewAnalysis Render(CanvasBitmap source, EditParameters parameters, bool raw)
    {
        var size = new Vector2((float)source.Size.Width, (float)source.Size.Height);
        var (offset, width, height) = EditEffectGraph.ComputeOutputExtent(size.X, size.Y, raw ? 0 : parameters.RotationAngle);
        // A histogram preview budget, not a limit on the image or the full-resolution diagnostic.
        var scale = Math.Min(1, 512d / Math.Max(width, height));
        var sampleWidth = Math.Max(1, (int)Math.Round(width * scale));
        var sampleHeight = Math.Max(1, (int)Math.Round(height * scale));
        using var target = new CanvasRenderTarget(source, sampleWidth, sampleHeight, 96,
            DirectXPixelFormat.R32G32B32A32Float, CanvasAlphaMode.Premultiplied);
        var image = raw ? source : EditEffectGraph.Build(source, size, parameters);
        try
        {
            using (var session = target.CreateDrawingSession())
            {
                session.Clear(Microsoft.UI.Colors.Transparent);
                session.Transform = Matrix3x2.CreateScale(
                    (float)(sampleWidth / (double)width), (float)(sampleHeight / (double)height));
                session.DrawImage(image, Vector2.Zero, new Rect(-offset.X, -offset.Y, width, height),
                    1, CanvasImageInterpolation.NearestNeighbor);
            }
            return Calculate(MemoryMarshal.Cast<byte, float>(target.GetPixelBytes()));
        }
        finally
        {
            if (!ReferenceEquals(image, source)) (image as IDisposable)?.Dispose();
        }
    }

    internal static EditorPreviewAnalysis Calculate(ReadOnlySpan<float> premultipliedRgba)
    {
        var bins = new float[ImageHistogram.BinCount];
        var shadows = false;
        var highlights = false;
        for (var i = 0; i + 3 < premultipliedRgba.Length; i += 4)
        {
            var alpha = premultipliedRgba[i + 3];
            if (alpha <= 0) continue;
            var red = Math.Clamp(premultipliedRgba[i] / alpha, 0, 1);
            var green = Math.Clamp(premultipliedRgba[i + 1] / alpha, 0, 1);
            var blue = Math.Clamp(premultipliedRgba[i + 2] / alpha, 0, 1);
            var maximum = Math.Max(red, Math.Max(green, blue));
            shadows |= maximum <= 0.5f / 255;
            highlights |= maximum >= 254.5f / 255;
            var luminance = .2126f * red + .7152f * green + .0722f * blue;
            bins[Math.Clamp((int)(luminance * bins.Length), 0, bins.Length - 1)]++;
        }
        var peak = bins.Max();
        if (peak > 0)
            for (var i = 0; i < bins.Length; i++) bins[i] /= peak;
        return new(bins, shadows, highlights);
    }
}
