using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using PhotoLibrarian.Core.Models;
using System.Numerics;

namespace PhotoLibrarian.Services;

/// <summary>
/// Builds the Win2D GPU effect pipeline from <see cref="EditParameters"/>.
///
/// The same graph drives the live preview in the editor and the full-resolution
/// render that bakes the adjustments into the file on save, so what the user sees
/// is what gets written to disk.
///
/// Effect chain: Source → Exposure → Brightness/Contrast → Saturation →
/// Temperature/Tint → Highlights/Shadows → Levels → Sharpness → Rotation → Output
/// </summary>
public static class EditEffectGraph
{
    /// <summary>
    /// Builds the effect graph for <paramref name="source"/>. <paramref name="sourceSize"/> is the
    /// size of the source image in DIPs and is used as the rotation pivot.
    /// Returns <paramref name="source"/> unchanged when there is nothing to apply.
    /// </summary>
    public static ICanvasImage Build(ICanvasImage source, Vector2 sourceSize, EditParameters p)
    {
        ICanvasImage current = source;

        // 1. Exposure (simulated via brightness/gamma)
        if (p.Exposure != 0)
        {
            current = new ExposureEffect
            {
                Source = current,
                Exposure = Math.Clamp((float)p.Exposure * 2.0f, -2f, 2f) // Scale to useful range
            };
        }

        // 2. Brightness & Contrast
        //    BrightnessEffect describes a transfer curve through (0,0) → BlackPoint → WhitePoint →
        //    (1,1); both control points must stay inside the unit square. Pulling the white point
        //    left brightens, pulling its output down darkens.
        if (p.Brightness != 0)
        {
            var b = Math.Clamp((float)p.Brightness, -1f, 1f) * 0.5f;
            current = new BrightnessEffect
            {
                Source = current,
                WhitePoint = b >= 0 ? new Vector2(1f - b, 1f) : new Vector2(1f, 1f + b)
            };
        }

        if (p.Contrast != 0)
        {
            current = new ContrastEffect
            {
                Source = current,
                Contrast = Math.Clamp((float)p.Contrast, -1f, 1f)
            };
        }

        // 3. Saturation
        if (p.Saturation != 0)
        {
            current = new SaturationEffect
            {
                Source = current,
                Saturation = Math.Clamp(1.0f + (float)p.Saturation, 0f, 2f) // 0=grayscale, 1=normal, 2=double
            };
        }

        // 4. Temperature & Tint (simulated via color matrix)
        if (p.Temperature != 0 || p.Tint != 0)
        {
            var temp = (float)p.Temperature * 0.3f;
            var tint = (float)p.Tint * 0.3f;
            current = new ColorMatrixEffect
            {
                Source = current,
                ColorMatrix = new Matrix5x4
                {
                    M11 = 1 + temp, M12 = 0, M13 = 0, M14 = 0,
                    M21 = 0, M22 = 1 + tint, M23 = 0, M24 = 0,
                    M31 = 0, M32 = 0, M33 = 1 - temp, M34 = 0,
                    M41 = 0, M42 = 0, M43 = 0, M44 = 1,
                    M51 = 0, M52 = 0, M53 = 0, M54 = 0
                }
            };
        }

        // 5. Highlights, Shadows & Clarity (via gamma curves)
        if (p.Highlights != 0 || p.Shadows != 0 || p.Clarity != 0)
        {
            current = new HighlightsAndShadowsEffect
            {
                Source = current,
                Highlights = Math.Clamp((float)p.Highlights, -1f, 1f),
                Shadows = Math.Clamp((float)p.Shadows, -1f, 1f),
                Clarity = Math.Clamp((float)p.Clarity, -1f, 1f)
            };
        }

        // 6. Levels (black point, white point, midtones via transfer table)
        if (p.BlackPoint != 0 || p.WhitePoint != 1.0 || p.Midtones != 0.5)
        {
            var gamma = p.Midtones > 0 ? Math.Log(0.5) / Math.Log(p.Midtones) : 1.0;
            var bp = (float)p.BlackPoint;
            var wp = (float)p.WhitePoint;
            var g = (float)gamma;

            // Generate transfer table
            var table = new float[256];
            var span = Math.Max(wp - bp, 1e-4f);
            for (int i = 0; i < 256; i++)
            {
                float v = i / 255f;
                v = Math.Clamp((v - bp) / span, 0, 1);
                v = MathF.Pow(v, 1f / g);
                table[i] = v;
            }

            current = new TableTransferEffect
            {
                Source = current,
                RedTable = table,
                GreenTable = table,
                BlueTable = table
            };
        }

        // 7. Sharpness (via unsharp mask)
        if (p.Sharpness > 0)
        {
            current = new SharpenEffect
            {
                Source = current,
                Amount = Math.Clamp((float)p.Sharpness * 5.0f, 0f, 10f), // 0-5 range
                Threshold = 0.05f
            };
        }

        // 8. Rotation
        if (p.RotationAngle != 0)
        {
            current = new Transform2DEffect
            {
                Source = current,
                TransformMatrix = Matrix3x2.CreateRotation(
                    (float)(p.RotationAngle * Math.PI / 180),
                    new Vector2(sourceSize.X / 2, sourceSize.Y / 2))
            };
        }

        return current;
    }

    /// <summary>
    /// Computes the axis-aligned extent of the effect output for a source of
    /// <paramref name="sourceWidth"/> × <paramref name="sourceHeight"/> pixels.
    ///
    /// Only rotation changes the extent — every other effect in the graph is a per-pixel or
    /// small-kernel operation that keeps the source bounds. The returned offset is the
    /// translation that must be applied when drawing so the rotated content starts at (0,0).
    /// </summary>
    public static (Vector2 Offset, uint Width, uint Height) ComputeOutputExtent(
        double sourceWidth, double sourceHeight, double rotationDegrees)
    {
        if (rotationDegrees == 0)
        {
            return (Vector2.Zero,
                (uint)Math.Max(1, Math.Round(sourceWidth)),
                (uint)Math.Max(1, Math.Round(sourceHeight)));
        }

        var radians = rotationDegrees * Math.PI / 180;
        var cos = Math.Abs(Math.Cos(radians));
        var sin = Math.Abs(Math.Sin(radians));

        var rotatedWidth = sourceWidth * cos + sourceHeight * sin;
        var rotatedHeight = sourceWidth * sin + sourceHeight * cos;

        // Rotation happens about the source centre, so the rotated bounding box stays centred there.
        var offsetX = (rotatedWidth - sourceWidth) / 2;
        var offsetY = (rotatedHeight - sourceHeight) / 2;

        return (new Vector2((float)offsetX, (float)offsetY),
            (uint)Math.Max(1, Math.Round(rotatedWidth)),
            (uint)Math.Max(1, Math.Round(rotatedHeight)));
    }
}
