// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Text.Json;
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Core.Services;

public sealed record EditRecipeComposition(
    EditParameters Parameters,
    IReadOnlyList<EditSettingGroup> SkippedGroups);

/// <summary>Copies selected edit groups and composes their continuous values at a chosen amount.</summary>
public static class EditRecipeComposer
{
    private const EditSettingGroup AllGroups = EditSettingGroup.All;

    public static EditRecipeComposition Compose(
        EditParameters current,
        EditParameters source,
        EditSettingGroup groups,
        int amountPercent,
        bool isRawTarget,
        bool sameSource)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(source);
        ValidateGroups(groups);
        if (amountPercent is < 0 or > 200)
            throw new ArgumentOutOfRangeException(nameof(amountPercent));

        var result = current.Clone();
        var skipped = new List<EditSettingGroup>();
        if (amountPercent == 0) return new(result, skipped);
        var amount = amountPercent / 100d;

        if (groups.HasFlag(EditSettingGroup.Tone))
        {
            result.Brightness = Blend(current.Brightness, source.Brightness, amount, -1, 1);
            result.Contrast = Blend(current.Contrast, source.Contrast, amount, -1, 1);
            result.Exposure = Blend(current.Exposure, source.Exposure, amount, -1, 1);
            result.Highlights = Blend(current.Highlights, source.Highlights, amount, -1, 1);
            result.Shadows = Blend(current.Shadows, source.Shadows, amount, -1, 1);
            result.BlackPoint = Blend(current.BlackPoint, source.BlackPoint, amount, 0, 1);
            result.WhitePoint = Blend(current.WhitePoint, source.WhitePoint, amount, 0, 1);
            result.Midtones = Blend(current.Midtones, source.Midtones, amount, 0, 1);
        }

        if (groups.HasFlag(EditSettingGroup.Color))
        {
            result.Saturation = Blend(current.Saturation, source.Saturation, amount, -1, 1);
            result.Temperature = Blend(current.Temperature, source.Temperature, amount, -1, 1);
            result.Tint = Blend(current.Tint, source.Tint, amount, -1, 1);
        }

        if (groups.HasFlag(EditSettingGroup.Effect) && amountPercent >= 100)
            result.Effect = source.Effect;

        if (groups.HasFlag(EditSettingGroup.Detail))
        {
            result.Clarity = Blend(current.Clarity, source.Clarity, amount, -1, 1);
            result.Sharpness = Blend(current.Sharpness, source.Sharpness, amount, 0, 2);
            result.NoiseReduction = Blend(current.NoiseReduction, source.NoiseReduction, amount, 0, 1);
            result.UnsharpMaskAmount = Blend(current.UnsharpMaskAmount, source.UnsharpMaskAmount, amount, 0, 2);
            result.UnsharpMaskRadius = Blend(current.UnsharpMaskRadius, source.UnsharpMaskRadius, amount, 0.5, 5);
            result.UnsharpMaskThreshold = Blend(current.UnsharpMaskThreshold, source.UnsharpMaskThreshold, amount, 0, 0.25);
            if (amountPercent >= 100)
                result.SharpeningMode = source.SharpeningMode;
        }

        if (groups.HasFlag(EditSettingGroup.ToneCurves))
            result.ToneCurves = BlendCurves(current.ToneCurves, source.ToneCurves, amount);

        if (groups.HasFlag(EditSettingGroup.ColorAdjustments))
            result.ColorAdjustments = BlendColorAdjustments(
                current.ColorAdjustments, source.ColorAdjustments, amount);

        if (groups.HasFlag(EditSettingGroup.Geometry))
        {
            result.RotationAngle = BlendAngle(current.RotationAngle, source.RotationAngle, amount);
            if (amountPercent >= 100)
            {
                ValidateCrop(source.Crop);
                result.Crop = source.Crop?.Clone();
            }
        }

        if (groups.HasFlag(EditSettingGroup.Retouch) && amountPercent >= 100)
        {
            if (sameSource)
                result.RetouchStrokes = source.RetouchStrokes.Select(stroke => stroke.Copy()).ToList();
            else
                skipped.Add(EditSettingGroup.Retouch);
        }

        if (groups.HasFlag(EditSettingGroup.RawDevelopment))
        {
            if (!isRawTarget)
                skipped.Add(EditSettingGroup.RawDevelopment);
            else
            {
                result.RawExposureEv = Blend(current.RawExposureEv, source.RawExposureEv, amount, -8, 8);
                result.RawWhitePoint = Blend(current.RawWhitePoint, source.RawWhitePoint, amount, 1, 16);
            }
        }

        result.ToneCurves.Validate();
        result.ColorAdjustments.Validate();
        SpotRetouchProcessor.Validate(result.RetouchStrokes);
        if (isRawTarget) RawToneMappingService.ValidateRecipe(result);
        return new(result, skipped);
    }

    public static EditParameters SelectGroups(EditParameters source, EditSettingGroup groups)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateGroups(groups);
        return Compose(new EditParameters(), source, groups, 100, isRawTarget: true, sameSource: true)
            .Parameters;
    }

    public static bool ContentEquals(EditParameters first, EditParameters second) =>
        JsonSerializer.Serialize(first) == JsonSerializer.Serialize(second);

    private static double Blend(double current, double source, double amount, double minimum, double maximum)
    {
        if (!double.IsFinite(current) || !double.IsFinite(source))
            throw new InvalidDataException("Edit recipe values must be finite.");
        return Math.Clamp(current + amount * (source - current), minimum, maximum);
    }

    private static ToneCurves BlendCurves(ToneCurves current, ToneCurves source, double amount)
    {
        current.Validate();
        source.Validate();
        if (amount == 1) return source.Clone();
        return new ToneCurves
        {
            Rgb = BlendCurve(current.Rgb, source.Rgb, amount),
            Red = BlendCurve(current.Red, source.Red, amount),
            Green = BlendCurve(current.Green, source.Green, amount),
            Blue = BlendCurve(current.Blue, source.Blue, amount)
        };
    }

    private static List<ToneCurvePoint> BlendCurve(
        IReadOnlyList<ToneCurvePoint> current,
        IReadOnlyList<ToneCurvePoint> source,
        double amount)
    {
        var xValues = current.Select(point => point.X)
            .Concat(source.Select(point => point.X))
            .Distinct()
            .Order()
            .ToArray();
        var points = new List<ToneCurvePoint>(xValues.Length);
        var previousY = 0d;
        foreach (var x in xValues)
        {
            var y = Math.Clamp(
                ToneCurves.Evaluate(current, x) +
                amount * (ToneCurves.Evaluate(source, x) - ToneCurves.Evaluate(current, x)),
                0,
                1);
            y = Math.Max(previousY, y);
            points.Add(new(x, y));
            previousY = y;
        }

        points[0] = new(0, 0);
        points[^1] = new(1, 1);
        return points;
    }

    private static ColorAdjustments BlendColorAdjustments(
        ColorAdjustments current,
        ColorAdjustments source,
        double amount)
    {
        current.Validate();
        source.Validate();
        if (amount == 1) return source.Clone();

        var result = new ColorAdjustments
        {
            MixerBands = current.MixerBands.Zip(source.MixerBands, (from, to) => new ColorMixerBandAdjustment
            {
                HueShiftDegrees = BlendHue(from.HueShiftDegrees, to.HueShiftDegrees, amount),
                Saturation = Blend(from.Saturation, to.Saturation, amount, -1, 1),
                Luminance = Blend(from.Luminance, to.Luminance, amount, -1, 1)
            }).ToList(),
            Shadows = BlendWheel(current.Shadows, source.Shadows, amount),
            Midtones = BlendWheel(current.Midtones, source.Midtones, amount),
            Highlights = BlendWheel(current.Highlights, source.Highlights, amount),
            Blending = Blend(current.Blending, source.Blending, amount, 0, 1),
            Balance = Blend(current.Balance, source.Balance, amount, -1, 1)
        };
        result.Validate();
        return result;
    }

    private static ColorGradingWheel BlendWheel(
        ColorGradingWheel current, ColorGradingWheel source, double amount) =>
        new()
        {
            HueDegrees = BlendHue(current.HueDegrees, source.HueDegrees, amount),
            Amount = Blend(current.Amount, source.Amount, amount, 0, 1)
        };

    private static double BlendHue(double current, double source, double amount)
    {
        var difference = ((source - current + 540) % 360) - 180;
        var result = ((current + amount * difference + 540) % 360) - 180;
        return Math.Abs(result + 180) < 1e-10 ? 180 : result;
    }

    private static double BlendAngle(double current, double source, double amount)
    {
        if (!double.IsFinite(current) || !double.IsFinite(source))
            throw new InvalidDataException("Rotation must be finite.");
        var difference = ((source - current + 540) % 360) - 180;
        var result = ((current + amount * difference + 180) % 360 + 360) % 360 - 180;
        return Math.Abs(result + 180) < 1e-10 ? 180 : result;
    }

    private static void ValidateCrop(CropRect? crop)
    {
        if (crop is null) return;
        if (!double.IsFinite(crop.Left) || !double.IsFinite(crop.Top) ||
            !double.IsFinite(crop.Width) || !double.IsFinite(crop.Height) ||
            crop.Left < 0 || crop.Top < 0 || crop.Width <= 0 || crop.Height <= 0 ||
            crop.Left + crop.Width > 1 || crop.Top + crop.Height > 1)
            throw new InvalidDataException("Crop must be a positive normalized rectangle inside the transformed photo.");
    }

    private static void ValidateGroups(EditSettingGroup groups)
    {
        if (groups == EditSettingGroup.None || (groups & ~AllGroups) != 0)
            throw new ArgumentOutOfRangeException(nameof(groups), "At least one supported edit group is required.");
    }
}
