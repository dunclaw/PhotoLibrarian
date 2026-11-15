// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Text.Json.Serialization;

namespace PhotoLibrarian.Core.Models;

/// <summary>Eight hue-local lightness adjustments used when converting an image to monochrome.</summary>
public sealed class MonochromeMix
{
    public const int BandCount = ColorAdjustments.MixerBandCount;

    [JsonRequired]
    public List<double> LuminanceBands { get; set; } = CreateDefaultBands();

    [JsonIgnore]
    public bool IsDefault => LuminanceBands is { Count: BandCount } &&
        LuminanceBands.All(value => value == 0);

    public static List<double> CreateDefaultBands() => Enumerable.Repeat(0d, BandCount).ToList();

    public MonochromeMix Clone()
    {
        Validate();
        return new() { LuminanceBands = [.. LuminanceBands] };
    }

    public bool ContentEquals(MonochromeMix other) =>
        LuminanceBands is not null && other.LuminanceBands is not null &&
        LuminanceBands.SequenceEqual(other.LuminanceBands);

    public void Validate()
    {
        if (LuminanceBands is null || LuminanceBands.Count != BandCount)
            throw new InvalidDataException($"Monochrome mix recipes must contain exactly {BandCount} hue bands.");
        if (LuminanceBands.Any(value => !double.IsFinite(value) || value < -1 || value > 1))
            throw new InvalidDataException("Monochrome mix values must be finite and between -1 and 1.");
    }
}
