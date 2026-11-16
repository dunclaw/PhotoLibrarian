// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class EditRecipeComposerTests
{
    [Theory]
    [InlineData(0, 0.1)]
    [InlineData(50, 0.3)]
    [InlineData(100, 0.5)]
    [InlineData(200, 0.9)]
    public void AmountInterpolatesFromCurrentToExactPresetThenExtrapolates(int amount, double expected)
    {
        var current = new EditParameters { Exposure = 0.1, Brightness = 0.7 };
        var preset = new EditParameters { Exposure = 0.5, Brightness = -0.5 };

        var result = EditRecipeComposer.Compose(
            current, preset, EditSettingGroup.Tone, amount, isRawTarget: false, sameSource: true);

        Assert.Equal(expected, result.Parameters.Exposure, 12);
        Assert.Equal(
            Math.Clamp(0.7 + amount / 100d * (-0.5 - 0.7), -1, 1),
            result.Parameters.Brightness,
            12);
    }

    [Fact]
    public void AmountZeroIsAnExactNoOpAndUnselectedGroupsArePreserved()
    {
        var current = new EditParameters
        {
            Exposure = 0.4,
            Effect = PhotoEffect.Sepia,
            Crop = new CropRect { Left = 0.1, Top = 0.2, Width = 0.5, Height = 0.6 }
        };
        var preset = new EditParameters { Exposure = -0.2, Effect = PhotoEffect.Cyanotype };

        var result = EditRecipeComposer.Compose(
            current, preset, EditSettingGroup.All, 0, isRawTarget: false, sameSource: false);

        Assert.True(EditRecipeComposer.ContentEquals(current, result.Parameters));
        Assert.Empty(result.SkippedGroups);
    }

    [Fact]
    public void DiscreteSettingsWaitUntilOneHundredPercentWhileRotationInterpolates()
    {
        var current = new EditParameters
        {
            Effect = PhotoEffect.Sepia,
            SharpeningMode = SharpeningMode.Legacy,
            RotationAngle = 5,
            Crop = new CropRect { Left = 0.1, Top = 0.1, Width = 0.8, Height = 0.8 }
        };
        var preset = new EditParameters
        {
            Effect = PhotoEffect.Cyanotype,
            SharpeningMode = SharpeningMode.UnsharpMask,
            RotationAngle = -12,
            Crop = new CropRect { Left = 0.25, Top = 0.2, Width = 0.5, Height = 0.6 }
        };

        var beforeHundred = EditRecipeComposer.Compose(
            current, preset,
            EditSettingGroup.Effect | EditSettingGroup.Detail | EditSettingGroup.Geometry,
            99, isRawTarget: false, sameSource: true);
        var atHundred = EditRecipeComposer.Compose(
            current, preset,
            EditSettingGroup.Effect | EditSettingGroup.Detail | EditSettingGroup.Geometry,
            100, isRawTarget: false, sameSource: true);

        Assert.Equal(PhotoEffect.Sepia, beforeHundred.Parameters.Effect);
        Assert.Equal(SharpeningMode.Legacy, beforeHundred.Parameters.SharpeningMode);
        Assert.Equal(-11.83, beforeHundred.Parameters.RotationAngle, 10);
        Assert.Equal(0.1, beforeHundred.Parameters.Crop!.Left);
        Assert.Equal(PhotoEffect.Cyanotype, atHundred.Parameters.Effect);
        Assert.Equal(SharpeningMode.UnsharpMask, atHundred.Parameters.SharpeningMode);
        Assert.Equal(-12, atHundred.Parameters.RotationAngle);
        Assert.Equal(0.25, atHundred.Parameters.Crop!.Left);
        Assert.Equal(0.5, atHundred.Parameters.Crop.Width);
    }

    [Fact]
    public void RetouchIsSkippedForDifferentSourceAndCopiedForMatchingSource()
    {
        var stroke = new RetouchStroke(32, 24, 4, [new RetouchPoint(10, 10)]);
        var current = new EditParameters();
        var preset = new EditParameters { RetouchStrokes = [stroke] };

        var differentSource = EditRecipeComposer.Compose(
            current, preset, EditSettingGroup.Retouch, 100, isRawTarget: false, sameSource: false);
        var sameSource = EditRecipeComposer.Compose(
            current, preset, EditSettingGroup.Retouch, 100, isRawTarget: false, sameSource: true);

        Assert.Empty(differentSource.Parameters.RetouchStrokes);
        Assert.Equal([EditSettingGroup.Retouch], differentSource.SkippedGroups);
        Assert.True(RetouchStroke.SequenceEqual(preset.RetouchStrokes, sameSource.Parameters.RetouchStrokes));
        Assert.NotSame(stroke.Points, sameSource.Parameters.RetouchStrokes[0].Points);
    }

    [Fact]
    public void RawGroupIsSkippedForRasterAndComposedForRawTargets()
    {
        var current = new EditParameters { RawExposureEv = -2, RawWhitePoint = 8 };
        var preset = new EditParameters { RawExposureEv = 2, RawWhitePoint = 4 };

        var raster = EditRecipeComposer.Compose(
            current, preset, EditSettingGroup.RawDevelopment, 100, isRawTarget: false, sameSource: true);
        var raw = EditRecipeComposer.Compose(
            current, preset, EditSettingGroup.RawDevelopment, 50, isRawTarget: true, sameSource: true);

        Assert.Equal(-2, raster.Parameters.RawExposureEv);
        Assert.Equal([EditSettingGroup.RawDevelopment], raster.SkippedGroups);
        Assert.Equal(0, raw.Parameters.RawExposureEv);
        Assert.Equal(6, raw.Parameters.RawWhitePoint);
    }

    [Fact]
    public void CurvesAndColorAdjustmentsRemainValidAtTwoHundredPercent()
    {
        var current = new EditParameters
        {
            ToneCurves = new() { Rgb = [new(0, 0), new(0.5, 0.8), new(1, 1)] },
            ColorAdjustments = new()
            {
                Shadows = new() { HueDegrees = 170, Amount = 0.5 }
            }
        };
        var preset = new EditParameters
        {
            ToneCurves = new() { Rgb = [new(0, 0), new(0.5, 0.2), new(1, 1)] },
            ColorAdjustments = new()
            {
                Shadows = new() { HueDegrees = -170, Amount = 0.75 }
            }
        };

        var result = EditRecipeComposer.Compose(
            current, preset,
            EditSettingGroup.ToneCurves | EditSettingGroup.ColorAdjustments,
            200, isRawTarget: false, sameSource: true).Parameters;

        result.ToneCurves.Validate();
        result.ColorAdjustments.Validate();
        Assert.Equal(-150, result.ColorAdjustments.Shadows.HueDegrees);
        Assert.Equal(1, result.ColorAdjustments.Shadows.Amount);
    }
}
