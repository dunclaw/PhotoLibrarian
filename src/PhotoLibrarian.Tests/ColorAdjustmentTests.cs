// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using PhotoLibrarian.ViewModels;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using XmpCore;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class ColorAdjustmentTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-ColorAdjustments-{Guid.NewGuid():N}");

    public ColorAdjustmentTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void NeutralSettingsKeepOldRecipesNeutralAndCloneIndependently()
    {
        var oldRecipe = EditParametersSerializer.ReadFromXmp(XmpMetaFactory.Create());
        Assert.True(oldRecipe.ColorAdjustments.IsDefault);
        Assert.False(oldRecipe.HasAdjustments);

        var settings = new ColorAdjustments();
        settings.MixerBands[2].HueShiftDegrees = 18;
        settings.Shadows.Amount = 0.4;
        settings.Blending = 0.7;
        settings.Validate();
        var copy = settings.Clone();
        copy.MixerBands[2].HueShiftDegrees = 30;
        copy.Shadows.Amount = 0.8;
        Assert.Equal(18, settings.MixerBands[2].HueShiftDegrees);
        Assert.Equal(0.4, settings.Shadows.Amount);
        Assert.False(settings.ContentEquals(copy));
        Assert.True(new EditParameters { ColorAdjustments = settings }.HasAdjustments);
    }

    [Fact]
    public void ColorAdjustmentsRoundTripAndClearWithoutRemovingUnrelatedXmp()
    {
        const string unrelatedNamespace = "https://photolibrarian.test/unrelated/1.0/";
        var xmp = XmpMetaFactory.Create();
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(unrelatedNamespace, "test");
        xmp.SetProperty(unrelatedNamespace, "test:Keep", "unrelated");
        var settings = new ColorAdjustments();
        settings.MixerBands[4] = new() { HueShiftDegrees = -25, Saturation = 0.3, Luminance = -0.2 };
        settings.Shadows = new() { HueDegrees = -140, Amount = 0.75 };
        settings.Midtones = new() { HueDegrees = 35, Amount = 0.2 };
        settings.Highlights = new() { HueDegrees = 80, Amount = 0.5 };
        settings.Blending = 0.6;
        settings.Balance = -0.35;

        EditParametersSerializer.WriteToXmp(xmp, new() { ColorAdjustments = settings });
        var restored = EditParametersSerializer.ReadFromXmp(xmp).ColorAdjustments;
        Assert.True(settings.ContentEquals(restored));
        Assert.True(EditParametersSerializer.HasEditProperties(xmp));

        EditParametersSerializer.ClearFromXmp(xmp);
        Assert.True(EditParametersSerializer.ReadFromXmp(xmp).ColorAdjustments.IsDefault);
        Assert.Equal("unrelated", xmp.GetPropertyString(unrelatedNamespace, "test:Keep"));
    }

    [Fact]
    public void UnsupportedOrMalformedColorRecipesAreRejected()
    {
        var xmp = XmpMetaFactory.Create();
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(EditParameters.XmpNamespace, EditParameters.XmpPrefix);
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:ColorAdjustmentsVersion", "2");
        Assert.Throws<NotSupportedException>(() => EditParametersSerializer.ReadFromXmp(xmp));
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:ColorAdjustmentsVersion", "1");
        Assert.Throws<InvalidDataException>(() => EditParametersSerializer.ReadFromXmp(xmp));
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:ColorAdjustmentsV1", "{}");
        Assert.Throws<InvalidDataException>(() => EditParametersSerializer.ReadFromXmp(xmp));

        var validJson = System.Text.Json.JsonSerializer.Serialize(new ColorAdjustments());
        var nullBandJson = validJson.Replace(
            "\"MixerBands\":[{\"HueShiftDegrees\":0,\"Saturation\":0,\"Luminance\":0},",
            "\"MixerBands\":[null,", StringComparison.Ordinal);
        Assert.NotEqual(validJson, nullBandJson);
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:ColorAdjustmentsV1", nullBandJson);
        Assert.Throws<InvalidDataException>(() => EditParametersSerializer.ReadFromXmp(xmp));

        var invalid = new ColorAdjustments();
        invalid.MixerBands[0].Saturation = double.NaN;
        Assert.Throws<InvalidDataException>(invalid.Validate);
    }

    [Fact]
    public void MonochromeMixRoundTripsAndRejectsUnsupportedOrMalformedRecipes()
    {
        var oldRecipe = EditParametersSerializer.ReadFromXmp(XmpMetaFactory.Create());
        Assert.True(oldRecipe.MonochromeMix.IsDefault);

        var mix = new MonochromeMix();
        mix.LuminanceBands[3] = 0.75;
        mix.LuminanceBands[5] = -0.4;
        var parameters = new EditParameters { Effect = PhotoEffect.BlackAndWhite, MonochromeMix = mix };
        var xmp = XmpMetaFactory.Create();
        EditParametersSerializer.WriteToXmp(xmp, parameters);

        var restored = EditParametersSerializer.ReadFromXmp(xmp);
        Assert.Equal(PhotoEffect.BlackAndWhite, restored.Effect);
        Assert.True(mix.ContentEquals(restored.MonochromeMix));
        Assert.True(EditParametersSerializer.HasEditProperties(xmp));

        EditParametersSerializer.ClearFromXmp(xmp);
        Assert.True(EditParametersSerializer.ReadFromXmp(xmp).MonochromeMix.IsDefault);

        XmpMetaFactory.SchemaRegistry.RegisterNamespace(EditParameters.XmpNamespace, EditParameters.XmpPrefix);
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:MonochromeMixVersion", "2");
        Assert.Throws<NotSupportedException>(() => EditParametersSerializer.ReadFromXmp(xmp));
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:MonochromeMixVersion", "1");
        Assert.Throws<InvalidDataException>(() => EditParametersSerializer.ReadFromXmp(xmp));
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:MonochromeMixV1", "{}");
        Assert.Throws<InvalidDataException>(() => EditParametersSerializer.ReadFromXmp(xmp));
        xmp.DeleteProperty(EditParameters.XmpNamespace, "pledit:MonochromeMixVersion");
        var validJson = System.Text.Json.JsonSerializer.Serialize(mix);
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:MonochromeMixV1", validJson);
        Assert.Throws<InvalidDataException>(() => EditParametersSerializer.ReadFromXmp(xmp));
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:MonochromeMixVersion", "1");
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:MonochromeMixV1",
            validJson[..^1] + ",\"Unexpected\":1}");
        Assert.Throws<InvalidDataException>(() => EditParametersSerializer.ReadFromXmp(xmp));

        var invalid = new MonochromeMix();
        invalid.LuminanceBands[0] = double.NaN;
        Assert.Throws<InvalidDataException>(invalid.Validate);
    }

    [Fact]
    public async Task PerceptualMixerAndGradingMatchPreviewAndKeepAlpha()
    {
        const uint width = 3;
        const uint height = 1;
        var path = Path.Combine(_directory, "preview.png");
        byte[] sourcePixels =
        [
            0, 0, 255, 255,
            0, 255, 0, 255,
            0, 0, 0, 0
        ];
        await RenderQualityFixtures.WriteImageAsync(path, width, height, sourcePixels);
        using var device = new CanvasDevice();
        using var source = await ImageEditRenderer.LoadOrientedAsync(device, path);
        var settings = new ColorAdjustments();
        settings.MixerBands[0].Saturation = -1;
        var parameters = new EditParameters { ColorAdjustments = settings };
        var rendered = await ImageEditRenderer.RenderToPixelsAsync(device, path, parameters);

        byte[] preview;
        using (var target = new CanvasRenderTarget(device, width, height, 96))
        {
            using (var session = target.CreateDrawingSession())
            {
                session.Clear(Microsoft.UI.Colors.Transparent);
                ImageEditRenderPass.Draw(session, source, parameters, new Vector2(width, height), Matrix3x2.Identity);
            }
            preview = target.GetPixelBytes();
        }

        Assert.Equal(preview, rendered.Pixels);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, rendered.Pixels[^4..]);
        var redOffset = 0;
        Assert.True(Math.Abs(rendered.Pixels[redOffset] - rendered.Pixels[redOffset + 1]) <= 18,
            $"Red output BGRA: {string.Join(",", rendered.Pixels[..4])}");
        Assert.True(Math.Abs(rendered.Pixels[redOffset + 1] - rendered.Pixels[redOffset + 2]) <= 18,
            $"Red output BGRA: {string.Join(",", rendered.Pixels[..4])}");
        Assert.InRange(Math.Abs(rendered.Pixels[4]), 0, 12);
        Assert.InRange(Math.Abs(rendered.Pixels[5] - 255), 0, 4);
        Assert.InRange(Math.Abs(rendered.Pixels[6]), 0, 4);

        settings = new ColorAdjustments();
        settings.MixerBands[0].HueShiftDegrees = 90;
        var hueShifted = await ImageEditRenderer.RenderToPixelsAsync(path,
            new EditParameters { ColorAdjustments = settings });
        Assert.NotEqual(new byte[] { 0, 0, 255 }, hueShifted.Pixels[..3]);

        settings.MixerBands[0].HueShiftDegrees = 0;
        settings.MixerBands[0].Luminance = 0.5;
        var luminanceLifted = await ImageEditRenderer.RenderToPixelsAsync(path,
            new EditParameters { ColorAdjustments = settings });
        Assert.True(luminanceLifted.Pixels[0] + luminanceLifted.Pixels[1] +
            luminanceLifted.Pixels[2] > 255);

        settings.Midtones = new() { HueDegrees = -120, Amount = 0.8 };
        var graded = await ImageEditRenderer.RenderToPixelsAsync(device, path,
            new EditParameters { ColorAdjustments = settings });
        Assert.NotEqual(0, rendered.Pixels.Zip(graded.Pixels).Count(pair => pair.First != pair.Second));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, graded.Pixels[^4..]);
    }

    [Fact]
    public async Task GradingBlendAndBalanceChangeTheTonalColorMasks()
    {
        var path = Path.Combine(_directory, "tonal-ranges.png");
        byte[] sourcePixels =
        [
            30, 30, 30, 255,
            128, 128, 128, 255,
            225, 225, 225, 255
        ];
        await RenderQualityFixtures.WriteImageAsync(path, 3, 1, sourcePixels);
        var settings = new ColorAdjustments
        {
            Shadows = new() { HueDegrees = -135, Amount = 0.9 },
            Blending = 0.1
        };
        var narrowBlend = await ImageEditRenderer.RenderToPixelsAsync(path,
            new EditParameters { ColorAdjustments = settings });
        settings.Blending = 0.9;
        var wideBlend = await ImageEditRenderer.RenderToPixelsAsync(path,
            new EditParameters { ColorAdjustments = settings });
        Assert.NotEqual(0, narrowBlend.Pixels.Zip(wideBlend.Pixels).Count(pair => pair.First != pair.Second));

        settings.Blending = 0.5;
        settings.Balance = -1;
        var shadowBiased = await ImageEditRenderer.RenderToPixelsAsync(path,
            new EditParameters { ColorAdjustments = settings });
        settings.Balance = 1;
        var highlightBiased = await ImageEditRenderer.RenderToPixelsAsync(path,
            new EditParameters { ColorAdjustments = settings });
        Assert.NotEqual(0, shadowBiased.Pixels.Zip(highlightBiased.Pixels)
            .Count(pair => pair.First != pair.Second));
    }

    [Fact]
    public async Task BlackAndWhiteMixPreservesNeutralRec709AndAdjustsOriginalHueBands()
    {
        const uint width = 3;
        const uint height = 1;
        var path = Path.Combine(_directory, "monochrome-mix.png");
        byte[] sourcePixels =
        [
            0, 0, 255, 255,
            0, 255, 0, 255,
            255, 0, 0, 255
        ];
        await RenderQualityFixtures.WriteImageAsync(path, width, height, sourcePixels);
        using var device = new CanvasDevice();
        using var source = await ImageEditRenderer.LoadOrientedAsync(device, path);

        var neutralParameters = new EditParameters { Effect = PhotoEffect.BlackAndWhite };
        var neutral = await ImageEditRenderer.RenderToPixelsAsync(device, path, neutralParameters);
        Assert.InRange(Math.Abs(neutral.Pixels[0] - 54), 0, 1);
        Assert.InRange(Math.Abs(neutral.Pixels[4] - 182), 0, 1);
        Assert.InRange(Math.Abs(neutral.Pixels[8] - 18), 0, 1);
        Assert.Equal(neutral.Pixels[0], neutral.Pixels[1]);
        Assert.Equal(neutral.Pixels[1], neutral.Pixels[2]);

        var mix = new MonochromeMix();
        mix.LuminanceBands[0] = 1;
        mix.LuminanceBands[3] = 1;
        var parameters = new EditParameters
        {
            Effect = PhotoEffect.BlackAndWhite,
            MonochromeMix = mix
        };
        var adjusted = await ImageEditRenderer.RenderToPixelsAsync(device, path, parameters);
        Assert.True(adjusted.Pixels[0] > neutral.Pixels[0] + 20,
            "The red mix band should brighten red source pixels.");
        Assert.True(adjusted.Pixels[4] > neutral.Pixels[4] + 20,
            "The green mix band should brighten green source pixels.");
        Assert.InRange(Math.Abs(adjusted.Pixels[8] - neutral.Pixels[8]), 0, 2);
        Assert.Equal(adjusted.Pixels[0], adjusted.Pixels[1]);
        Assert.Equal(adjusted.Pixels[1], adjusted.Pixels[2]);

        byte[] preview;
        using (var target = new CanvasRenderTarget(device, width, height, 96))
        {
            using (var session = target.CreateDrawingSession())
            {
                session.Clear(Microsoft.UI.Colors.Transparent);
                ImageEditRenderPass.Draw(session, source, parameters, new Vector2(width, height), Matrix3x2.Identity);
            }
            preview = target.GetPixelBytes();
        }
        Assert.Equal(preview, adjusted.Pixels);

        var gradedParameters = parameters.Clone();
        gradedParameters.ColorAdjustments.Midtones = new() { HueDegrees = 35, Amount = 0.7 };
        var toned = await ImageEditRenderer.RenderToPixelsAsync(device, path, gradedParameters);
        Assert.NotEqual(toned.Pixels[0], toned.Pixels[2]);
    }

    [Fact]
    public async Task MonochromeMixDarkensOverlappingHuesButNotNeutralPixels()
    {
        var path = Path.Combine(_directory, "monochrome-overlap.png");
        byte[] sourcePixels =
        [
            0, 0, 255, 255,
            0, 128, 255, 255,
            128, 128, 128, 255
        ];
        await RenderQualityFixtures.WriteImageAsync(path, 3, 1, sourcePixels);
        var parameters = new EditParameters { Effect = PhotoEffect.BlackAndWhite };
        var neutral = await ImageEditRenderer.RenderToPixelsAsync(path, parameters);
        parameters.MonochromeMix.LuminanceBands[0] = -1;
        var darkened = await ImageEditRenderer.RenderToPixelsAsync(path, parameters);
        Assert.True(darkened.Pixels[0] < neutral.Pixels[0] - 10);
        Assert.True(darkened.Pixels[4] < neutral.Pixels[4] - 10);
        Assert.InRange(Math.Abs(darkened.Pixels[8] - neutral.Pixels[8]), 0, 1);
    }

    [Fact]
    public async Task SkyAndFoliageMixHasVisibleIndependentBrightnessChanges()
    {
        var path = Path.Combine(_directory, "sky-and-foliage.png");
        byte[] sourcePixels = [220, 130, 60, 255, 45, 135, 50, 255];
        await RenderQualityFixtures.WriteImageAsync(path, 2, 1, sourcePixels);
        var parameters = new EditParameters { Effect = PhotoEffect.BlackAndWhite };
        var neutral = await ImageEditRenderer.RenderToPixelsAsync(path, parameters);
        parameters.MonochromeMix.LuminanceBands[5] = -1;
        var darkerSky = await ImageEditRenderer.RenderToPixelsAsync(path, parameters);
        Assert.True(neutral.Pixels[0] - darkerSky.Pixels[0] >= 20, "Blue must visibly darken blue sky pixels.");
        Assert.InRange(Math.Abs(neutral.Pixels[4] - darkerSky.Pixels[4]), 0, 3);
        parameters.MonochromeMix.LuminanceBands[5] = 0;
        parameters.MonochromeMix.LuminanceBands[3] = 1;
        var lighterFoliage = await ImageEditRenderer.RenderToPixelsAsync(path, parameters);
        Assert.True(lighterFoliage.Pixels[4] - neutral.Pixels[4] >= 20, "Green must visibly brighten foliage.");
        Assert.InRange(Math.Abs(neutral.Pixels[0] - lighterFoliage.Pixels[0]), 0, 3);
    }

    [Fact]
    public async Task MonochromeCanBeEnabledAndMixResetInEditorAfterRevert()
    {
        var path = Path.Combine(_directory, "monochrome-controls.png");
        await RenderQualityFixtures.WriteImageAsync(path, 1, 1, [220, 130, 60, 255]);
        var originalPixels = await RenderQualityFixtures.ReadPngPixelsAsync(path);
        var viewModel = new ImageEditorViewModel(new OriginalBackupService(Path.Combine(_directory, "backups")));
        await viewModel.OpenForEditAsync(new ImageEntry { FilePath = path, FileName = Path.GetFileName(path) });
        viewModel.SetMonochromeEnabled(true);
        var mix = new MonochromeMix();
        mix.LuminanceBands[5] = -0.8;
        viewModel.SetMonochromeMix(mix);
        viewModel.Exposure = 0.2;
        viewModel.ResetMonochromeMix();
        Assert.Equal(PhotoEffect.BlackAndWhite, viewModel.Effect);
        Assert.True(viewModel.GetCurrentParameters().MonochromeMix.IsDefault);
        Assert.Equal(0.2, viewModel.Exposure);
        viewModel.UndoCommand.Execute(null);
        Assert.Equal(-0.8, viewModel.GetCurrentParameters().MonochromeMix.LuminanceBands[5]);
        Assert.True(await viewModel.SaveAsync((_, _) => throw new InvalidOperationException("Do not bake B&W.")));
        await viewModel.RevertToOriginalCommand.ExecuteAsync(null);
        Assert.Equal(PhotoEffect.None, viewModel.Effect);
        viewModel.SetMonochromeEnabled(true);
        Assert.True(viewModel.CanEditMonochromeMix);
        Assert.Equal(PhotoEffect.BlackAndWhite, viewModel.Effect);
        Assert.Equal(originalPixels, await RenderQualityFixtures.ReadPngPixelsAsync(path));
    }

    [Fact]
    public async Task MixerHistoryAndExportPersistRecipeWithoutChangingSourcePixels()
    {
        var path = Path.Combine(_directory, "recipe.png");
        var pixels = Enumerable.Repeat(new byte[] { 0, 0, 255, 255 }, 64).SelectMany(value => value).ToArray();
        await RenderQualityFixtures.WriteImageAsync(path, 8, 8, pixels);
        var originalPixels = await RenderQualityFixtures.ReadPngPixelsAsync(path);
        var viewModel = new ImageEditorViewModel(
            new OriginalBackupService(Path.Combine(_directory, "backups")));
        await viewModel.OpenForEditAsync(new ImageEntry { FilePath = path, FileName = Path.GetFileName(path) });

        viewModel.BeginEdit("Color mixer");
        var settings = viewModel.GetCurrentParameters().ColorAdjustments;
        settings.MixerBands[0].Saturation = -1;
        viewModel.SetColorAdjustments(settings, "Color mixer");
        viewModel.EndEdit();
        Assert.Single(viewModel.UndoHistory);
        viewModel.UndoCommand.Execute(null);
        Assert.True(viewModel.GetCurrentParameters().ColorAdjustments.IsDefault);
        viewModel.RedoCommand.Execute(null);
        Assert.Equal(-1, viewModel.GetCurrentParameters().ColorAdjustments.MixerBands[0].Saturation);

        Assert.True(await viewModel.SaveAsync((_, _) =>
            throw new InvalidOperationException("Color recipes must remain non-destructive.")));
        Assert.Equal(-1, ImageEditService.ReadEditParameters(path).ColorAdjustments.MixerBands[0].Saturation);
        Assert.Equal(originalPixels, await RenderQualityFixtures.ReadPngPixelsAsync(path));
        Assert.False(File.Exists(path + ".xmp"));

        var unsupported = await ImageExportService.ExportAsync([path], _directory, new());
        Assert.Empty(unsupported.ExportedPaths);
        Assert.Contains("cannot render pending color adjustment", Assert.Single(unsupported.Failures).Message);
        var result = await ImageExportService.ExportAsync([path], _directory, new() { Quality = 100 },
            renderRecipe: async (sourcePath, recipe, token) =>
            {
                token.ThrowIfCancellationRequested();
                var rendered = await ImageEditRenderer.RenderToPixelsAsync(sourcePath, recipe);
                return new(rendered.Width, rendered.Height, rendered.Pixels);
            });
        Assert.Empty(result.Failures);
        await using var stream = File.OpenRead(Assert.Single(result.ExportedPaths));
        var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
        var output = (await decoder.GetPixelDataAsync()).DetachPixelData();
        Assert.InRange(Math.Abs(output[0] - output[1]), 0, 20);
        Assert.InRange(Math.Abs(output[1] - output[2]), 0, 20);
        Assert.Equal(originalPixels, await RenderQualityFixtures.ReadPngPixelsAsync(path));

        viewModel.ResetAllCommand.Execute(null);
        Assert.True(await viewModel.SaveAsync((_, _) =>
            throw new InvalidOperationException("Resetting a recipe must not bake pixels.")));
        Assert.True(ImageEditService.ReadEditParameters(path).ColorAdjustments.IsDefault);
        Assert.Equal(originalPixels, await RenderQualityFixtures.ReadPngPixelsAsync(path));
    }

    [Fact]
    public async Task BlackAndWhiteRecipeSavesWithoutBakingSourcePixels()
    {
        var path = Path.Combine(_directory, "monochrome-recipe.png");
        byte[] pixels = [0, 0, 255, 255, 0, 255, 0, 255];
        await RenderQualityFixtures.WriteImageAsync(path, 2, 1, pixels);
        var originalPixels = await RenderQualityFixtures.ReadPngPixelsAsync(path);
        var viewModel = new ImageEditorViewModel(
            new OriginalBackupService(Path.Combine(_directory, "backups")));
        await viewModel.OpenForEditAsync(new ImageEntry { FilePath = path, FileName = Path.GetFileName(path) });

        viewModel.SelectEffect(PhotoEffect.BlackAndWhite, "Black and white");
        var mix = viewModel.GetCurrentParameters().MonochromeMix;
        mix.LuminanceBands[3] = 0.6;
        viewModel.SetMonochromeMix(mix);
        Assert.True(await viewModel.SaveAsync((_, _) =>
            throw new InvalidOperationException("Black-and-white recipes must remain non-destructive.")));

        var saved = ImageEditService.ReadEditParameters(path);
        Assert.Equal(PhotoEffect.BlackAndWhite, saved.Effect);
        Assert.Equal(0.6, saved.MonochromeMix.LuminanceBands[3]);
        Assert.Equal(originalPixels, await RenderQualityFixtures.ReadPngPixelsAsync(path));

        var unsupported = await ImageExportService.ExportAsync([path], _directory, new() { Quality = 100 });
        Assert.Empty(unsupported.ExportedPaths);
        Assert.Contains("cannot render black-and-white", Assert.Single(unsupported.Failures).Message);
        var exported = await ImageExportService.ExportAsync([path], _directory, new() { Quality = 100 },
            renderRecipe: async (sourcePath, recipe, token) =>
            {
                token.ThrowIfCancellationRequested();
                var rendered = await ImageEditRenderer.RenderToPixelsAsync(sourcePath, recipe);
                return new(rendered.Width, rendered.Height, rendered.Pixels);
            });
        Assert.Empty(exported.Failures);
        await using var stream = File.OpenRead(Assert.Single(exported.ExportedPaths));
        var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
        var output = (await decoder.GetPixelDataAsync()).DetachPixelData();
        for (var index = 0; index < output.Length; index += 4)
        {
            Assert.InRange(Math.Abs(output[index] - output[index + 1]), 0, 20);
            Assert.InRange(Math.Abs(output[index + 1] - output[index + 2]), 0, 20);
        }
        Assert.Equal(originalPixels, await RenderQualityFixtures.ReadPngPixelsAsync(path));
    }
}
