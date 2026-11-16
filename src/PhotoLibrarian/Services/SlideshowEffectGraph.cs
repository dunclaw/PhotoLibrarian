// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using PhotoLibrarian.Core.Models;
using System.Numerics;

namespace PhotoLibrarian.Services;

/// <summary>Builds display-only Win2D effects for slideshow themes.</summary>
public static class SlideshowEffectGraph
{
    public static ICanvasImage Build(ICanvasImage source, SlideshowTheme theme) =>
        theme switch
        {
            SlideshowTheme.PanAndZoom or SlideshowTheme.Fade => source,
            SlideshowTheme.Contemporary => new SaturationEffect
            {
                Source = new ContrastEffect { Source = source, Contrast = 0.08f },
                Saturation = 1.08f
            },
            SlideshowTheme.Cinematic => new ExposureEffect
            {
                Source = new ContrastEffect
                {
                    Source = new SaturationEffect { Source = source, Saturation = 0.78f },
                    Contrast = 0.12f
                },
                Exposure = -0.12f
            },
            SlideshowTheme.BlackAndWhite => new SaturationEffect { Source = source, Saturation = 0f },
            SlideshowTheme.Sepia => new ColorMatrixEffect
            {
                Source = source,
                ColorMatrix = new Matrix5x4
                {
                    M11 = 0.393f, M21 = 0.769f, M31 = 0.189f,
                    M12 = 0.349f, M22 = 0.686f, M32 = 0.168f,
                    M13 = 0.272f, M23 = 0.534f, M33 = 0.131f,
                    M44 = 1f
                }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, "Unknown slideshow theme.")
        };
}
