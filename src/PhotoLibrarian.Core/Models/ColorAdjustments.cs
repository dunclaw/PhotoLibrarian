// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Text.Json.Serialization;

namespace PhotoLibrarian.Core.Models;

/// <summary>Eight hue-local mixer bands and three OkLab color-grading wheels.</summary>
public sealed class ColorAdjustments
{
    public const int MixerBandCount = 8;
    private static readonly double[] HueCenters = [29, 57, 110, 142, 195, 264, 293, 328];

    [JsonRequired]
    public List<ColorMixerBandAdjustment> MixerBands { get; set; } = CreateDefaultBands();

    [JsonRequired]
    public ColorGradingWheel Shadows { get; set; } = new();

    [JsonRequired]
    public ColorGradingWheel Midtones { get; set; } = new();

    [JsonRequired]
    public ColorGradingWheel Highlights { get; set; } = new();

    [JsonRequired]
    public double Blending { get; set; } = 0.5;

    [JsonRequired]
    public double Balance { get; set; }

    [JsonIgnore]
    public bool IsDefault =>
        MixerBands.Count == MixerBandCount && MixerBands.All(band => band.IsDefault) &&
        Shadows.IsDefault && Midtones.IsDefault && Highlights.IsDefault &&
        Blending == 0.5 && Balance == 0;

    public static double GetHueCenter(int bandIndex)
    {
        if ((uint)bandIndex >= MixerBandCount)
            throw new ArgumentOutOfRangeException(nameof(bandIndex));
        return HueCenters[bandIndex];
    }

    public static List<ColorMixerBandAdjustment> CreateDefaultBands() =>
        Enumerable.Range(0, MixerBandCount).Select(_ => new ColorMixerBandAdjustment()).ToList();

    public ColorAdjustments Clone() => new()
    {
        MixerBands = MixerBands.Select(band => band.Clone()).ToList(),
        Shadows = Shadows.Clone(),
        Midtones = Midtones.Clone(),
        Highlights = Highlights.Clone(),
        Blending = Blending,
        Balance = Balance
    };

    public bool ContentEquals(ColorAdjustments other) =>
        MixerBands.Count == other.MixerBands.Count &&
        MixerBands.Zip(other.MixerBands).All(pair => pair.First.ContentEquals(pair.Second)) &&
        Shadows.ContentEquals(other.Shadows) &&
        Midtones.ContentEquals(other.Midtones) &&
        Highlights.ContentEquals(other.Highlights) &&
        Blending == other.Blending && Balance == other.Balance;

    public void Validate()
    {
        if (MixerBands is null || MixerBands.Count != MixerBandCount)
            throw new InvalidDataException($"Color mixer recipes must contain exactly {MixerBandCount} hue bands.");
        foreach (var band in MixerBands) band.Validate();
        (Shadows ?? throw new InvalidDataException("Shadow color grading is missing.")).Validate();
        (Midtones ?? throw new InvalidDataException("Midtone color grading is missing.")).Validate();
        (Highlights ?? throw new InvalidDataException("Highlight color grading is missing.")).Validate();
        ValidateRange(Blending, 0, 1, nameof(Blending));
        ValidateRange(Balance, -1, 1, nameof(Balance));
    }

    private static void ValidateRange(double value, double minimum, double maximum, string name)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
            throw new InvalidDataException($"Color adjustment {name} must be between {minimum} and {maximum}.");
    }
}

public sealed class ColorMixerBandAdjustment
{
    [JsonRequired] public double HueShiftDegrees { get; set; }
    [JsonRequired] public double Saturation { get; set; }
    [JsonRequired] public double Luminance { get; set; }

    [JsonIgnore]
    public bool IsDefault => HueShiftDegrees == 0 && Saturation == 0 && Luminance == 0;

    public ColorMixerBandAdjustment Clone() => new()
    {
        HueShiftDegrees = HueShiftDegrees,
        Saturation = Saturation,
        Luminance = Luminance
    };

    public bool ContentEquals(ColorMixerBandAdjustment other) =>
        HueShiftDegrees == other.HueShiftDegrees &&
        Saturation == other.Saturation &&
        Luminance == other.Luminance;

    internal void Validate()
    {
        if (!double.IsFinite(HueShiftDegrees) || HueShiftDegrees < -180 || HueShiftDegrees > 180)
            throw new InvalidDataException("Color mixer hue shift must be between -180 and 180 degrees.");
        if (!double.IsFinite(Saturation) || Saturation < -1 || Saturation > 1)
            throw new InvalidDataException("Color mixer saturation must be between -1 and 1.");
        if (!double.IsFinite(Luminance) || Luminance < -1 || Luminance > 1)
            throw new InvalidDataException("Color mixer luminance must be between -1 and 1.");
    }
}

public sealed class ColorGradingWheel
{
    [JsonRequired] public double HueDegrees { get; set; }
    [JsonRequired] public double Amount { get; set; }

    [JsonIgnore]
    public bool IsDefault => HueDegrees == 0 && Amount == 0;

    public ColorGradingWheel Clone() => new() { HueDegrees = HueDegrees, Amount = Amount };

    public bool ContentEquals(ColorGradingWheel other) =>
        HueDegrees == other.HueDegrees && Amount == other.Amount;

    internal void Validate()
    {
        if (!double.IsFinite(HueDegrees) || HueDegrees < -180 || HueDegrees > 180)
            throw new InvalidDataException("Color grading hue must be between -180 and 180 degrees.");
        if (!double.IsFinite(Amount) || Amount < 0 || Amount > 1)
            throw new InvalidDataException("Color grading amount must be between 0 and 1.");
    }
}
