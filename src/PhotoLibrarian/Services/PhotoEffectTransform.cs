// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using PhotoLibrarian.Core.Models;
using System.Numerics;

namespace PhotoLibrarian.Services;

/// <summary>
/// Independently designed display-referred color effects. The raster input is already
/// sRGB encoded; these creative matrices do not introduce another color transfer.
/// </summary>
internal static class PhotoEffectTransform
{
    internal static ICanvasImage Create(ICanvasImage source, PhotoEffect effect)
    {
        if (effect == PhotoEffect.None) return source;
        return new ColorMatrixEffect
        {
            Source = source,
            ColorMatrix = GetMatrix(effect),
            AlphaMode = CanvasAlphaMode.Straight,
            ClampOutput = true
        };
    }

    internal static Matrix5x4 GetMatrix(PhotoEffect effect)
    {
        if (!Enum.IsDefined(effect)) throw new ArgumentOutOfRangeException(nameof(effect));
        if (effect == PhotoEffect.None)
            return new Matrix5x4 { M11 = 1, M22 = 1, M33 = 1, M44 = 1 };
        if (effect is PhotoEffect.WarmTint or PhotoEffect.CoolTint)
        {
            var warm = effect == PhotoEffect.WarmTint;
            return new Matrix5x4
            {
                M11 = warm ? 1f : .88f, M22 = .96f, M33 = warm ? .85f : 1f, M44 = 1
            };
        }

        var weights = effect switch
        {
            PhotoEffect.RedFilter => new Vector3(.65f, .30f, .05f),
            PhotoEffect.OrangeFilter => new Vector3(.50f, .40f, .10f),
            PhotoEffect.YellowFilter => new Vector3(.40f, .50f, .10f),
            _ => new Vector3(.2126f, .7152f, .0722f)
        };
        var tone = effect switch
        {
            PhotoEffect.Sepia => new Vector3(1f, .82f, .60f),
            PhotoEffect.Cyanotype => new Vector3(.45f, .78f, 1f),
            _ => Vector3.One
        };
        return new Matrix5x4
        {
            M11 = weights.X * tone.X, M12 = weights.X * tone.Y, M13 = weights.X * tone.Z,
            M21 = weights.Y * tone.X, M22 = weights.Y * tone.Y, M23 = weights.Y * tone.Z,
            M31 = weights.Z * tone.X, M32 = weights.Z * tone.Y, M33 = weights.Z * tone.Z,
            M44 = 1
        };
    }
}
