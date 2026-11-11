// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using System.Xml.Linq;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class ViewerQuickEditTests
{
    [Fact]
    public void AutoBrightnessAdaptsToSourceAndPreservesColorDetailAndGeometry()
    {
        var recipe = new EditParameters
        {
            Brightness = .4, Saturation = .3, Temperature = -.2, Tint = .1,
            RotationAngle = 12.5, Effect = PhotoEffect.Sepia, NoiseReduction = .2,
            Clarity = .4, Sharpness = .3, Crop = new() { Left = .1, Width = .8 }
        };
        var crop = recipe.Crop;
        byte[] dark = [16, 16, 16, 255];
        Assert.True(ViewerQuickEditService.Apply(recipe, ViewerQuickEdit.AutoBrightness, dark));
        Assert.True(recipe.Exposure > 0);
        Assert.True(recipe.Shadows > 0);
        Assert.Equal(0, recipe.Brightness);
        Assert.Equal(.3, recipe.Saturation);
        Assert.Equal(-.2, recipe.Temperature);
        Assert.Equal(.1, recipe.Tint);
        Assert.Equal(12.5, recipe.RotationAngle);
        Assert.Same(crop, recipe.Crop);
        Assert.Equal(PhotoEffect.Sepia, recipe.Effect);
        Assert.Equal(.2, recipe.NoiseReduction);
        Assert.Equal(.4, recipe.Clarity);
        Assert.Equal(.3, recipe.Sharpness);
        Assert.False(ViewerQuickEditService.Apply(recipe, ViewerQuickEdit.AutoBrightness, dark));
        Assert.True(ViewerQuickEditService.Apply(recipe, ViewerQuickEdit.AutoBrightness, [240, 240, 240, 255]));
        Assert.True(recipe.Exposure < 0);
        Assert.True(recipe.Highlights < 0);
    }

    [Fact]
    public void AutoColorReducesNeutralCastWithoutChangingToneOrEffects()
    {
        byte[] warm = [130, 140, 150, 255];
        var recipe = new EditParameters { Exposure = .2, Contrast = .1, Effect = PhotoEffect.Cyanotype };
        Assert.True(ViewerQuickEditService.Apply(recipe, ViewerQuickEdit.AutoColor, warm));
        var red = 150 * (1 + recipe.Temperature * .3);
        var green = 140 * (1 + recipe.Tint * .3);
        var blue = 130 * (1 - recipe.Temperature * .3);
        Assert.True(Math.Abs(red - blue) < 1);
        Assert.True(Math.Abs(red - green) < 1);
        Assert.InRange(recipe.Temperature, -.3, 0);
        Assert.Equal(.2, recipe.Exposure);
        Assert.Equal(.1, recipe.Contrast);
        Assert.Equal(PhotoEffect.Cyanotype, recipe.Effect);
        Assert.False(ViewerQuickEditService.Apply(recipe, ViewerQuickEdit.AutoColor, warm));
    }

    [Fact]
    public void AutoColorIgnoresTransparentClippedAndStronglyColoredPixels()
    {
        var neutral = AutoEnhanceAnalyzer.AnalyzeColor([128, 128, 128, 255]);
        var noNeutral = AutoEnhanceAnalyzer.AnalyzeColor(
            [0, 0, 255, 255, 255, 255, 255, 255, 1, 2, 3, 255, 255, 0, 0, 0]);
        Assert.Equal(0, neutral.Temperature);
        Assert.Equal(0, neutral.Tint);
        Assert.Equal(0, neutral.Saturation);
        Assert.Equal(0, noNeutral.Temperature);
        Assert.Equal(0, noNeutral.Tint);
        var greenCast = AutoEnhanceAnalyzer.AnalyzeColor([128, 145, 128, 255]);
        Assert.True(greenCast.Tint < 0);
        Assert.InRange(greenCast.Tint, -.3, .3);
        var transparent = AutoEnhanceAnalyzer.AnalyzeColor([255, 0, 0, 0]);
        Assert.False(transparent.HasAdjustments);
    }

    [Fact]
    public void RotationsComposeWithFineAngleAndPreserveRetouchCoordinates()
    {
        var recipe = new EditParameters { RotationAngle = 12.5, Exposure = .3 };
        recipe.RetouchStrokes.Add(new RetouchStroke(32, 24, 2, [new(10, 10)]));
        var strokes = recipe.RetouchStrokes;
        for (var turn = 0; turn < 4; turn++)
            Assert.True(ViewerQuickEditService.Apply(recipe, ViewerQuickEdit.RotateRight, []));
        Assert.Equal(12.5, recipe.RotationAngle);
        ViewerQuickEditService.Apply(recipe, ViewerQuickEdit.RotateLeft, []);
        Assert.Equal(-77.5, recipe.RotationAngle);
        ViewerQuickEditService.Apply(recipe, ViewerQuickEdit.RotateRight, []);
        Assert.Equal(12.5, recipe.RotationAngle);
        Assert.Equal(.3, recipe.Exposure);
        Assert.Same(strokes, recipe.RetouchStrokes);
    }

    [Fact]
    public async Task QuickEditsShareChronologicalHistoryAcrossPhotosAndSurviveRecipeReload()
    {
        await using var library = await StabilizedVideoLibraryTests.LibraryFixture.CreateAsync();
        var first = await library.AddPhotoAsync("first.png");
        var second = await library.AddPhotoAsync("second.png");
        var main = library.Main;
        await main.ImageGrid.FilterByFolderAsync(library.Photos);
        var firstBytes = File.ReadAllBytes(first.FilePath);
        var secondBytes = File.ReadAllBytes(second.FilePath);

        async Task EditAsync(ImageEntry entry, ViewerQuickEdit action)
        {
            Assert.True(await main.RunPhotoEditAsync(action.ToString(), [entry.FilePath],
                copies => ViewerQuickEditService.PrepareAsync(copies[entry.FilePath], action)), main.StatusText);
        }

        await EditAsync(first, ViewerQuickEdit.AutoBrightness);
        var tonePacket = File.ReadAllBytes(Path.ChangeExtension(first.FilePath, ".xmp"));
        await EditAsync(second, ViewerQuickEdit.AutoColor);
        await EditAsync(first, ViewerQuickEdit.RotateRight);
        Assert.Contains("RotateRight", main.EditHistory.UndoLabel);
        var recipe = ImageEditService.ReadEditParameters(first.FilePath);
        Assert.Equal(90, recipe.RotationAngle);
        var rendered = await ImageEditRenderer.RenderToPixelsAsync(first.FilePath, recipe);
        Assert.Equal(24u, rendered.Width);
        Assert.Equal(32u, rendered.Height);
        Assert.Equal(firstBytes, File.ReadAllBytes(first.FilePath));
        Assert.Equal(secondBytes, File.ReadAllBytes(second.FilePath));
        Assert.True(await main.BackupService.HasBackupAsync(first.FilePath));

        await main.EditHistory.UndoAsync();
        Assert.Equal(tonePacket, File.ReadAllBytes(Path.ChangeExtension(first.FilePath, ".xmp")));
        // Re-running the same auto action does not write or clear the pending rotation redo.
        await EditAsync(first, ViewerQuickEdit.AutoBrightness);
        Assert.True(main.EditHistory.CanRedo);
        Assert.Contains("AutoColor", main.EditHistory.UndoLabel);
        await main.EditHistory.UndoAsync();
        Assert.False(ImageEditService.ReadEditParameters(second.FilePath).HasAdjustments);
        await main.EditHistory.UndoAsync();
        Assert.False(ImageEditService.ReadEditParameters(first.FilePath).HasAdjustments);
        Assert.False(main.EditHistory.CanUndo);
        for (var step = 0; step < 3; step++)
            Assert.Equal(EditHistoryStatus.Applied, (await main.EditHistory.RedoAsync()).Status);
        Assert.Equal(90, ImageEditService.ReadEditParameters(first.FilePath).RotationAngle);
        await main.EditHistory.UndoAsync();
        await EditAsync(first, ViewerQuickEdit.RotateLeft);
        Assert.False(main.EditHistory.CanRedo);
        Assert.Equal(-90, ImageEditService.ReadEditParameters(first.FilePath).RotationAngle);
        Assert.Equal(firstBytes, File.ReadAllBytes(first.FilePath));
    }

    [Fact]
    public async Task BrokenSidecarFailsWithoutOverwritingItOrConsumingHistory()
    {
        await using var library = await StabilizedVideoLibraryTests.LibraryFixture.CreateAsync();
        var photo = await library.AddPhotoAsync("broken.png");
        var sidecar = Path.ChangeExtension(photo.FilePath, ".xmp");
        await File.WriteAllTextAsync(sidecar, "broken metadata", TestContext.Current.CancellationToken);
        var bytes = File.ReadAllBytes(photo.FilePath);
        Assert.False(await library.Main.RunPhotoEditAsync("Auto brightness", [photo.FilePath],
            copies => ViewerQuickEditService.PrepareAsync(copies[photo.FilePath], ViewerQuickEdit.AutoBrightness)));
        Assert.Contains("failed", library.Main.StatusText);
        Assert.Equal("broken metadata", File.ReadAllText(sidecar));
        Assert.Equal(bytes, File.ReadAllBytes(photo.FilePath));
        Assert.False(library.Main.EditHistory.CanUndo);
    }

    [Fact]
    public void QuickActionsAreDirectLocalizedAccessibleButtonsInViewerTools()
    {
        var assets = Path.Combine(AppContext.BaseDirectory, "TestAssets");
        var ribbon = XDocument.Load(Path.Combine(assets, "TopRibbon.xaml"));
        var resources = XDocument.Load(Path.Combine(assets, "Resources.resw"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var action in Enum.GetValues<ViewerQuickEdit>())
        {
            var id = $"ViewerQuick{action}";
            var button = Assert.Single(ribbon.Descendants(), element =>
                (string?)element.Attribute("AutomationProperties.AutomationId") == id);
            Assert.Equal("Button", button.Name.LocalName);
            Assert.Equal("OnQuickEditClick", (string?)button.Attribute("Click"));
            Assert.Equal(id, (string?)button.Attribute(xaml + "Uid"));
            Assert.Contains(button.Ancestors(), element =>
                (string?)element.Attribute(xaml + "Name") == "ViewerTools");
            Assert.DoesNotContain(button.Ancestors(), element => element.Name.LocalName.EndsWith("Flyout"));
            foreach (var suffix in new[]
            {
                ".[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name",
                ".[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip"
            })
                Assert.Contains(resources.Descendants("data"), element =>
                    (string?)element.Attribute("name") == id + suffix &&
                    !string.IsNullOrWhiteSpace(element.Element("value")?.Value));
        }
    }
}
