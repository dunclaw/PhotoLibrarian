// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using PhotoLibrarian.ViewModels;
using System.Numerics;
using System.Xml.Linq;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using XmpCore;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class PhotoEffectTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-effects-{Guid.NewGuid():N}");

    public PhotoEffectTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task GridRecipeThumbnailsSurviveRestartAndFollowUndoWithoutChangingSource()
    {
        var path = Path.Combine(_directory, "grid.png");
        await RenderQualityFixtures.WriteImageAsync(path, 9, 7,
            RenderQualityFixtures.CreatePixels(RenderQualityFixtureKind.Gradient, 9, 7));
        var original = await File.ReadAllBytesAsync(path);
        var service = new ViewerEffectService(new OriginalBackupService(Path.Combine(_directory, "backups")));
        await service.SelectAsync(path, PhotoEffect.BlackAndWhite);
        var first = await DecodeGridThumbnailAsync(path);
        for (var i = 0; i < first.Length; i += 4)
        {
            Assert.Equal(first[i], first[i + 1]);
            Assert.Equal(first[i], first[i + 2]);
        }
        // A fresh read has no dependency on a viewer/session or in-memory thumbnail cache.
        Assert.Equal(first, await DecodeGridThumbnailAsync(path));
        Assert.Equal((await ImageEditRenderer.RenderToPixelsAsync(path,
            ImageEditService.ReadEditParameters(path))).Pixels, first);
        await service.SelectAsync(path, PhotoEffect.Sepia);
        Assert.NotEqual(first, await DecodeGridThumbnailAsync(path));
        await service.MoveHistoryAsync(path, undo: true);
        Assert.Equal(first, await DecodeGridThumbnailAsync(path));
        await service.MoveHistoryAsync(path, undo: false);
        Assert.NotEqual(first, await DecodeGridThumbnailAsync(path));
        await service.SelectAsync(path, PhotoEffect.None);
        var neutral = await DecodeGridThumbnailAsync(path);
        Assert.Contains(Enumerable.Range(0, neutral.Length / 4), i =>
            neutral[i * 4] != neutral[i * 4 + 1] || neutral[i * 4] != neutral[i * 4 + 2]);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    private static async Task<byte[]> DecodeGridThumbnailAsync(string path)
    {
        var bytes = await RecipeThumbnailService.GetThumbnailStreamAsync(path, 180);
        Assert.NotNull(bytes);
        using var stream = new MemoryStream(bytes);
        var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
        Assert.InRange(decoder.PixelWidth, 1u, 180u);
        Assert.InRange(decoder.PixelHeight, 1u, 180u);
        return (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform(), ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage)).DetachPixelData();
    }

    [Fact]
    public async Task RecipeStampInvalidatesReuseAndStaleBatchBytesAreRejectedBeforeDecode()
    {
        var path = Path.Combine(_directory, "stamp.png");
        await RenderQualityFixtures.WriteImageAsync(path, 9, 7,
            RenderQualityFixtures.CreatePixels(RenderQualityFixtureKind.Gradient, 9, 7));
        var entry = new ImageEntry { FilePath = path, FileName = Path.GetFileName(path) };
        var before = new ImageThumbnailViewModel(entry);
        Assert.True(new ImageThumbnailViewModel(entry).CanReuseThumbnail(before));
        var edits = new ImageEditService(new OriginalBackupService(Path.Combine(_directory, "backups")));
        await edits.SaveEditParametersAsync(path, new() { Effect = PhotoEffect.BlackAndWhite });
        var after = new ImageThumbnailViewModel(entry);
        Assert.False(after.CanReuseThumbnail(before));
        var batchVersion = after.ThumbnailVersion;
        after.InvalidateThumbnail();
        await after.LoadThumbnailFromStreamAsync([0], batchVersion);
        Assert.Null(after.Thumbnail);
        Assert.True(after.IsLoading);
    }

    [Fact]
    public async Task GridThumbnailPreservesAlphaAndRejectsBrokenRecipes()
    {
        var path = Path.Combine(_directory, "alpha.png");
        await RenderQualityFixtures.WriteImageAsync(path, 3, 2,
            [0, 0, 0, 0, 32, 16, 8, 64, 40, 20, 10, 128,
             12, 24, 48, 128, 20, 40, 80, 128, 40, 80, 160, 255]);
        await new ImageEditService(new OriginalBackupService(Path.Combine(_directory, "backups")))
            .SaveEditParametersAsync(path, new() { Effect = PhotoEffect.Cyanotype });
        var grid = await DecodeGridThumbnailAsync(path);
        Assert.Equal((await ImageEditRenderer.RenderToPixelsAsync(path,
            ImageEditService.ReadEditParameters(path))).Pixels, grid);
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".xmp"), "invalid XMP");
        await Assert.ThrowsAnyAsync<Exception>(() => RecipeThumbnailService.GetThumbnailStreamAsync(path, 180));
    }

    [Fact]
    public void GalleryUsesSelectableLocalizedAccessibleItemsAndLiveThumbnailBindings()
    {
        var assets = Path.Combine(AppContext.BaseDirectory, "TestAssets");
        var editor = XDocument.Load(Path.Combine(assets, "ImageEditorView.xaml"));
        var resources = XDocument.Load(Path.Combine(assets, "Resources.resw"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var viewer = XDocument.Load(Path.Combine(assets, "ImageViewerOverlay.xaml"));
        Assert.DoesNotContain(editor.Descendants(), element =>
            (string?)element.Attribute(xaml + "Name") == "EffectsGallery");
        var gallery = Assert.Single(viewer.Descendants().Where(
            element => (string?)element.Attribute(xaml + "Name") == "EffectsGallery"));
        var flyout = Assert.Single(gallery.Ancestors().Where(element => element.Name.LocalName == "Flyout"));
        Assert.Equal("EffectsFlyout", (string?)flyout.Attribute(xaml + "Name"));
        Assert.DoesNotContain(flyout.Descendants(), element => element.Name.LocalName == "Button");
        Assert.DoesNotContain(gallery.Ancestors(), element =>
            (string?)element.Attribute(xaml + "Name") == "AdjustmentsPanel");
        var ribbon = XDocument.Load(Path.Combine(assets, "TopRibbon.xaml"));
        Assert.Contains(ribbon.Descendants(), element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "RibbonUndo" &&
            (string?)element.Attribute("Click") == "OnUndoClick");
        Assert.Contains(ribbon.Descendants(), element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "RibbonRedo" &&
            (string?)element.Attribute("Click") == "OnRedoClick");
        var effectsButton = Assert.Single(ribbon.Descendants().Where(element =>
            (string?)element.Attribute(xaml + "Name") == "EffectsButton"));
        Assert.Equal("AdjustButton", (string?)effectsButton.ElementsBeforeSelf().Last().Attribute(xaml + "Name"));
        Assert.Equal("OnEffectsClick", (string?)effectsButton.Attribute("Click"));
        Assert.Equal("RibbonEffects", (string?)effectsButton.Attribute("AutomationProperties.AutomationId"));
        var code = File.ReadAllText(Path.Combine(assets, "MainWindow.xaml.cs"));
        Assert.Contains("ViewModel.EditHistory.UndoAsync", code);
        Assert.Contains("ViewModel.EditHistory.RedoAsync", code);
        Assert.Contains("HasNativeHistoryOrDialog(focus), out var undo", code);
        Assert.Contains("RichEditBox or ContentDialog", code);
        var effectsHandler = code[code.IndexOf("private async void OnRibbonEffectsClicked", StringComparison.Ordinal)..];
        effectsHandler = effectsHandler[..effectsHandler.IndexOf("private void OnEditHistoryChanged", StringComparison.Ordinal)];
        Assert.Contains("ViewerOverlay.ShowEffectsAsync", effectsHandler);
        Assert.DoesNotContain("EditorOverlay", effectsHandler);
        Assert.DoesNotContain("OpenRibbonEditorAsync", effectsHandler);
        Assert.DoesNotContain("ShowAdjustments", effectsHandler);
        var window = XDocument.Load(Path.Combine(assets, "MainWindow.xaml"));
        var overlay = Assert.Single(window.Descendants().Where(element =>
            (string?)element.Attribute(xaml + "Name") == "EditorOverlay"));
        Assert.Equal("1", (string?)overlay.Attribute("Grid.Row"));
        Assert.Null(overlay.Attribute("Grid.RowSpan"));
        Assert.Equal("Single", (string?)gallery.Attribute("SelectionMode"));
        Assert.Equal("EditorEffectsGallery", (string?)gallery.Attribute("AutomationProperties.AutomationId"));
        Assert.Equal("OnEffectSelectionChanged", (string?)gallery.Attribute("SelectionChanged"));
        Assert.Contains(gallery.Descendants(), element =>
            (string?)element.Attribute("Source") == "{x:Bind Thumbnail, Mode=OneWay}");
        Assert.Contains(gallery.Descendants(), element =>
            (string?)element.Attribute("Property") == "AutomationProperties.Name");
        Assert.Contains(gallery.Descendants(), element =>
            (string?)element.Attribute("Property") == "AutomationProperties.AutomationId");
        foreach (var effect in Enum.GetValues<PhotoEffect>())
            Assert.Contains(resources.Descendants("data"), element =>
                (string?)element.Attribute("name") == $"EditorEffect{effect}" &&
                !string.IsNullOrWhiteSpace(element.Element("value")?.Value));
        Assert.Contains(resources.Descendants("data"), element =>
            (string?)element.Attribute("name") == "EditorEffectsRawUnavailable" &&
            element.Element("value")!.Value.Contains("RAW"));
    }

    [Fact]
    public void ReopeningUncroppedEffectRecipeDoesNotThrowFirstChanceNullReferences()
    {
        var references = new List<Exception>();
        void OnException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
        {
            if (args.Exception is NullReferenceException) references.Add(args.Exception);
        }
        AppDomain.CurrentDomain.FirstChanceException += OnException;
        try
        {
            var xmp = XmpMetaFactory.Create();
            EditParametersSerializer.WriteToXmp(xmp, new() { Effect = PhotoEffect.Sepia });
            var packet = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
            for (var i = 0; i < 3; i++)
            {
                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(packet));
                var recipe = EditParametersSerializer.ReadFromXmp(XmpMetaFactory.Parse(stream));
                Assert.Equal(PhotoEffect.Sepia, recipe.Effect);
                Assert.Null(recipe.Crop);
            }
            Assert.Empty(references);
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= OnException; }
    }

    [Fact]
    public async Task ViewerEffectsUndoRedoArePerPhotoAndPreserveOtherTweaksAndPixels()
    {
        var path = Path.Combine(_directory, "history.png");
        var other = Path.Combine(_directory, "other.png");
        var pixels = RenderQualityFixtures.CreatePixels(RenderQualityFixtureKind.Gradient, 7, 5);
        await RenderQualityFixtures.WriteImageAsync(path, 7, 5, pixels);
        await RenderQualityFixtures.WriteImageAsync(other, 7, 5, pixels);
        var original = await File.ReadAllBytesAsync(path);
        var backups = new OriginalBackupService(Path.Combine(_directory, "backups"));
        var edits = new ImageEditService(backups);
        await edits.SaveEditParametersAsync(path, new() { Effect = PhotoEffect.WarmTint, Exposure = .1 });
        var service = new ViewerEffectService(backups);
        Assert.False(service.CanUndo(path));
        await service.SelectAsync(path, PhotoEffect.Sepia);
        await service.SelectAsync(path, PhotoEffect.Cyanotype);
        await service.SelectAsync(other, PhotoEffect.YellowFilter);
        var undo = await service.MoveHistoryAsync(path, undo: true);
        Assert.Equal(PhotoEffect.Sepia, ImageEditService.ReadEditParameters(path).Effect);
        Assert.Equal(PhotoEffect.YellowFilter, ImageEditService.ReadEditParameters(other).Effect);
        using var device = new CanvasDevice();
        Assert.Equal((await ImageEditRenderer.RenderToPixelsAsync(device, path,
            ImageEditService.ReadEditParameters(path))).Pixels, undo.Pixels);
        // A no-op selection does not remove redo.
        await service.SelectAsync(path, PhotoEffect.Sepia);
        Assert.True(service.CanRedo(path));
        await service.MoveHistoryAsync(path, undo: true);
        Assert.Equal(PhotoEffect.WarmTint, ImageEditService.ReadEditParameters(path).Effect);
        Assert.False(service.CanUndo(path));
        await service.MoveHistoryAsync(path, undo: false);
        await service.MoveHistoryAsync(path, undo: false);
        Assert.Equal(PhotoEffect.Cyanotype, ImageEditService.ReadEditParameters(path).Effect);
        Assert.False(service.CanRedo(path));
        await service.MoveHistoryAsync(path, undo: true);
        // Undo changes only Effect, not newly adjusted tone fields.
        var recipe = ImageEditService.ReadEditParameters(path);
        recipe.Exposure = .4;
        await edits.SaveEditParametersAsync(path, recipe);
        await service.MoveHistoryAsync(path, undo: true);
        Assert.Equal(.4, ImageEditService.ReadEditParameters(path).Exposure);
        await service.SelectAsync(path, PhotoEffect.None);
        Assert.False(service.CanRedo(path));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task FailedUndoKeepsHistoryAndUnreadableMetadataForRetry()
    {
        var path = Path.Combine(_directory, "retry.png");
        await RenderQualityFixtures.WriteImageAsync(path, 7, 5,
            RenderQualityFixtures.CreatePixels(RenderQualityFixtureKind.Gradient, 7, 5));
        var service = new ViewerEffectService(new OriginalBackupService(Path.Combine(_directory, "backups")));
        await service.SelectAsync(path, PhotoEffect.Sepia);
        var sidecar = Path.ChangeExtension(path, ".xmp");
        var good = await File.ReadAllTextAsync(sidecar);
        await File.WriteAllTextAsync(sidecar, "unreadable");
        await Assert.ThrowsAnyAsync<Exception>(() => service.MoveHistoryAsync(path, undo: true));
        Assert.True(service.CanUndo(path));
        Assert.False(service.CanRedo(path));
        Assert.Equal("unreadable", await File.ReadAllTextAsync(sidecar));
        await File.WriteAllTextAsync(sidecar, good);
        await service.MoveHistoryAsync(path, undo: true);
        Assert.Equal(PhotoEffect.None, ImageEditService.ReadEditParameters(path).Effect);
    }

    [Fact]
    public async Task ViewerSelectionPreservesPhotoBytesAndReplacesPortableRecipeInPlace()
    {
        var path = Path.Combine(_directory, "viewer.png");
        await RenderQualityFixtures.WriteImageAsync(path, 19, 13,
            RenderQualityFixtures.CreatePixels(RenderQualityFixtureKind.Gradient, 19, 13, alpha: true));
        var original = await File.ReadAllBytesAsync(path);
        var backups = new OriginalBackupService(Path.Combine(_directory, "backups"));
        await new ImageEditService(backups).SaveEditParametersAsync(path,
            new() { Temperature = -.1 });
        var service = new ViewerEffectService(backups);
        foreach (var effect in new[] { PhotoEffect.Sepia, PhotoEffect.Cyanotype, PhotoEffect.None })
        {
            var rendered = await service.SelectAsync(path, effect);
            var recipe = ImageEditService.ReadEditParameters(path);
            Assert.Equal(effect, recipe.Effect);
            Assert.Equal(-.1, recipe.Temperature);
            using var device = new CanvasDevice();
            var expected = await ImageEditRenderer.RenderToPixelsAsync(device, path, recipe);
            Assert.Equal(expected.Pixels, rendered.Pixels);
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            Assert.Equal(original, await File.ReadAllBytesAsync((await backups.GetBackupPathAsync(path))!));
        }
        Assert.Equal(PhotoEffect.None, ImageEditService.ReadEditParameters(path).Effect);
    }

    [Fact]
    public async Task ViewerInvalidPhotoDoesNotPersistAnUnrenderableEffect()
    {
        var path = Path.Combine(_directory, "broken.png");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var service = new ViewerEffectService(new OriginalBackupService(Path.Combine(_directory, "backups")));
        await Assert.ThrowsAnyAsync<Exception>(() => service.SelectAsync(path, PhotoEffect.Sepia));
        Assert.False(File.Exists(Path.ChangeExtension(path, ".xmp")));
        var raw = Path.Combine(_directory, "original.dng");
        await File.WriteAllBytesAsync(raw, [1, 2, 3]);
        await Assert.ThrowsAsync<NotSupportedException>(() => service.SelectAsync(raw, PhotoEffect.Sepia));
        Assert.False(File.Exists(Path.ChangeExtension(raw, ".xmp")));
    }

    [Fact]
    public async Task ViewerUnreadableSidecarIsNotReplacedByAnEffectRecipe()
    {
        var path = Path.Combine(_directory, "sidecar.png");
        await RenderQualityFixtures.WriteImageAsync(path, 3, 2,
            RenderQualityFixtures.CreatePixels(RenderQualityFixtureKind.Gradient, 3, 2));
        var sidecar = Path.ChangeExtension(path, ".xmp");
        await File.WriteAllTextAsync(sidecar, "unreadable existing metadata");
        var service = new ViewerEffectService(new OriginalBackupService(Path.Combine(_directory, "backups")));
        await Assert.ThrowsAnyAsync<Exception>(() => service.SelectAsync(path, PhotoEffect.Sepia));
        Assert.Equal("unreadable existing metadata", await File.ReadAllTextAsync(sidecar));
    }

    [Fact]
    public void EveryPresetClonesResetsAndRoundTripsInPortableXmp()
    {
        foreach (var effect in Enum.GetValues<PhotoEffect>())
        {
            var parameters = new EditParameters { Effect = effect };
            Assert.Equal(effect != PhotoEffect.None, parameters.HasAdjustments);
            var clone = parameters.Clone();
            parameters.Reset();
            Assert.Equal(PhotoEffect.None, parameters.Effect);
            Assert.False(parameters.HasAdjustments);
            Assert.Equal(effect, clone.Effect);

            var xmp = XmpMetaFactory.Create();
            EditParametersSerializer.WriteToXmp(xmp, clone);
            var packet = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(packet));
            Assert.Equal(effect, EditParametersSerializer.ReadFromXmp(XmpMetaFactory.Parse(stream)).Effect);
            xmp.SetProperty("http://ns.adobe.com/xap/1.0/", "xmp:Label", "Keep");
            EditParametersSerializer.ClearFromXmp(xmp);
            Assert.False(xmp.DoesPropertyExist(EditParameters.XmpNamespace, "Effect"));
            Assert.False(EditParametersSerializer.ReadFromXmp(xmp).HasAdjustments);
            Assert.Equal("Keep", xmp.GetPropertyString("http://ns.adobe.com/xap/1.0/", "xmp:Label"));
        }
    }

    [Fact]
    public void LegacyRecipeIsNeutralAndUnknownEffectFailsExplicitly()
    {
        var xmp = XmpMetaFactory.Create();
        Assert.Equal(PhotoEffect.None, EditParametersSerializer.ReadFromXmp(xmp).Effect);
        EditParametersSerializer.WriteToXmp(xmp, new EditParameters { Exposure = .2 });
        xmp.DeleteProperty(EditParameters.XmpNamespace, "Effect");
        var legacy = EditParametersSerializer.ReadFromXmp(xmp);
        Assert.Equal(.2, legacy.Exposure);
        Assert.Equal(PhotoEffect.None, legacy.Effect);
        xmp.SetProperty(EditParameters.XmpNamespace, "Effect", "FutureEffectV2");
        Assert.Throws<NotSupportedException>(() => EditParametersSerializer.ReadFromXmp(xmp));
    }

    [Fact]
    public void PresetsUseEncodedColorOnceAndPreservePremultipliedAlpha()
    {
        // Transparent, half-opacity, and opaque copies of the same straight RGB color.
        byte[] pixels = [0, 0, 0, 0, 20, 50, 80, 128, 40, 100, 160, 255];
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var source = CanvasBitmap.CreateFromBytes(device, pixels, 3, 1,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
        Assert.Same(source, PhotoEffectTransform.Create(source, PhotoEffect.None));
        foreach (var effect in Enum.GetValues<PhotoEffect>())
        {
            using var target = new CanvasRenderTarget(device, 3, 1, 96);
            using (var session = target.CreateDrawingSession())
            {
                session.Clear(Microsoft.UI.Colors.Transparent);
                ImageEditRenderPass.Draw(session, source, new EditParameters { Effect = effect },
                    new Vector2(3, 1), Matrix3x2.Identity);
            }
            var output = target.GetPixelBytes();
            Assert.Equal(new byte[] { 0, 0, 0, 0 }, output[..4]);
            Assert.Equal(128, output[7]);
            Assert.Equal(255, output[11]);
            for (var channel = 0; channel < 3; channel++)
                Assert.InRange(Math.Abs(output[4 + channel] * 2 - output[8 + channel]), 0, 2);

            var expected = ExpectedColor(effect, new Vector3(160, 100, 40));
            Assert.InRange(Math.Abs(output[10] - expected.X), 0, 1);
            Assert.InRange(Math.Abs(output[9] - expected.Y), 0, 1);
            Assert.InRange(Math.Abs(output[8] - expected.Z), 0, 1);
        }
    }

    private static Vector3 ExpectedColor(PhotoEffect effect, Vector3 rgb)
    {
        if (effect == PhotoEffect.None) return rgb;
        if (effect == PhotoEffect.WarmTint) return rgb * new Vector3(1, .96f, .85f);
        if (effect == PhotoEffect.CoolTint) return rgb * new Vector3(.88f, .96f, 1);
        var luminance = effect switch
        {
            PhotoEffect.RedFilter => rgb.X * .65f + rgb.Y * .30f + rgb.Z * .05f,
            PhotoEffect.OrangeFilter => rgb.X * .50f + rgb.Y * .40f + rgb.Z * .10f,
            PhotoEffect.YellowFilter => rgb.X * .40f + rgb.Y * .50f + rgb.Z * .10f,
            _ => rgb.X * .2126f + rgb.Y * .7152f + rgb.Z * .0722f
        };
        return effect switch
        {
            PhotoEffect.Sepia => luminance * new Vector3(1, .82f, .60f),
            PhotoEffect.Cyanotype => luminance * new Vector3(.45f, .78f, 1),
            _ => new Vector3(luminance)
        };
    }

    [Fact]
    public async Task SelectionNoneResetUndoRedoOpenAndSaveKeepOneConsistentState()
    {
        var path = Path.Combine(_directory, "recipe.jpg");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        var backups = new OriginalBackupService(Path.Combine(_directory, "backups"));
        var edits = new ImageEditService(backups);
        await edits.SaveEditParametersAsync(path, new EditParameters { Effect = PhotoEffect.Sepia, Exposure = .2 });
        var entry = new ImageEntry { FilePath = path, FileName = "recipe.jpg" };
        var vm = new ImageEditorViewModel(backups);
        await vm.OpenForEditAsync(entry);
        Assert.Equal(PhotoEffect.Sepia, vm.Effect);
        Assert.False(vm.HasChanges);
        vm.SelectEffect(PhotoEffect.Cyanotype, "Cyanotype");
        vm.SelectEffect(PhotoEffect.Cyanotype, "Cyanotype");
        Assert.Single(vm.UndoHistory);
        vm.UndoSteps(1);
        Assert.Equal(PhotoEffect.Sepia, vm.Effect);
        Assert.False(vm.HasChanges);
        vm.RedoSteps(1);
        Assert.Equal(PhotoEffect.Cyanotype, vm.Effect);
        vm.SelectEffect(PhotoEffect.None, "None");
        Assert.Equal(.2, vm.Exposure);
        Assert.Equal(PhotoEffect.None, vm.GetCurrentParameters().Effect);
        vm.UndoSteps(1);
        vm.ResetAllCommand.Execute(null);
        Assert.Equal(PhotoEffect.None, vm.Effect);
        Assert.False(vm.GetCurrentParameters().HasAdjustments);
        vm.UndoSteps(1);
        Assert.Equal(PhotoEffect.Cyanotype, vm.Effect);
        Assert.True(await vm.SaveAsync((_, parameters) =>
        {
            Assert.Equal(PhotoEffect.Cyanotype, parameters.Effect);
            Assert.Equal(.2, parameters.Exposure);
            return Task.FromResult((3u, 2u));
        }));
        Assert.Equal(PhotoEffect.None, vm.Effect);
        Assert.False(vm.HasChanges);
        Assert.Empty(vm.UndoHistory);
        Assert.False(ImageEditService.ReadEditParameters(path).HasAdjustments);
        vm.CloseCommand.Execute(null);
        await vm.OpenForEditAsync(entry);
        Assert.Equal(PhotoEffect.None, vm.Effect);
    }

    [Fact]
    public async Task RawGallerySelectionIsGatedAndDormantEffectSurvivesRecipeReset()
    {
        var path = Path.Combine(_directory, "original.dng");
        byte[] original = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(path, original);
        await ImageEditService.SaveRawPreviewRecipeAsync(path,
            new EditParameters { Effect = PhotoEffect.Sepia, RawExposureEv = 1 });
        var vm = new ImageEditorViewModel(new OriginalBackupService(Path.Combine(_directory, "backups")));
        await vm.OpenForEditAsync(new ImageEntry { FilePath = path, FileName = "original.dng" });
        vm.SelectEffect(PhotoEffect.Cyanotype, "Cyanotype");
        Assert.Equal(PhotoEffect.Sepia, vm.Effect);
        Assert.Empty(vm.UndoHistory);
        vm.ResetAllCommand.Execute(null);
        Assert.Equal(PhotoEffect.Sepia, vm.Effect);
        Assert.Equal(0, vm.RawExposureEv);
        await vm.KeepRawPreviewRecipeAsync();
        Assert.Equal(PhotoEffect.Sepia, ImageEditService.ReadEditParameters(path).Effect);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task RawSdrPixelsIgnoreDormantRasterEffects()
    {
        float[] linear = [0, 0, 0, .18f, .32f, .06f, 1, 2, 4, 4, 4, 4];
        var neutral = await RawToneMappingService.RenderAsync(linear, 2, 2, new());
        var dormant = await RawToneMappingService.RenderAsync(linear, 2, 2,
            new() { Effect = PhotoEffect.Cyanotype });
        Assert.Equal(neutral.ToPreviewBgra8(), dormant.ToPreviewBgra8());
    }

    [Fact]
    public async Task ThumbnailsUseCurrentImageAndRecipeWithoutStackingSelectedEffect()
    {
        var first = Path.Combine(_directory, "first.png");
        var second = Path.Combine(_directory, "second.png");
        await RenderQualityFixtures.WriteImageAsync(first, 9, 7,
            RenderQualityFixtures.CreatePixels(RenderQualityFixtureKind.Gradient, 9, 7));
        await RenderQualityFixtures.WriteImageAsync(second, 9, 7,
            RenderQualityFixtures.CreatePixels(RenderQualityFixtureKind.Edge, 9, 7));
        var session = new EffectThumbnailSession();
        var parameters = new EditParameters { Effect = PhotoEffect.Cyanotype, Exposure = .2 };
        var thumbnails = (await session.RequestAsync(first, parameters))!;
        Assert.Equal(Enum.GetValues<PhotoEffect>().Length, thumbnails.Count);
        Assert.Equal(PhotoEffect.Cyanotype, parameters.Effect);
        foreach (var thumbnail in thumbnails)
        {
            var recipe = parameters.Clone();
            recipe.Effect = thumbnail.Effect;
            var expected = await ImageEditRenderer.RenderToPixelsAsync(first, recipe);
            Assert.Equal(expected.Pixels, thumbnail.Pixels);
        }
        var next = (await session.RequestAsync(second, parameters))!;
        Assert.NotEqual(thumbnails[0].Pixels, next[0].Pixels);
        parameters.Exposure = -.4;
        var adjusted = (await session.RequestAsync(second, parameters))!;
        Assert.NotEqual(next[0].Pixels, adjusted[0].Pixels);
    }

    [Fact]
    public async Task LargeOrientedAndRotatedThumbnailsStayBounded()
    {
        var path = Path.Combine(_directory, "oriented.jpg");
        await RenderQualityFixtures.WriteImageAsync(path, 901, 301,
            RenderQualityFixtures.CreatePixels(RenderQualityFixtureKind.Gradient, 901, 301),
            dpi: 300, orientation: 6);
        var session = new EffectThumbnailSession();
        var thumbnails = (await session.RequestAsync(path, new()))!;
        Assert.All(thumbnails, thumbnail =>
        {
            Assert.True(thumbnail.Height > thumbnail.Width);
            Assert.InRange(thumbnail.Width, 1, EffectThumbnailSession.MaximumDimension);
            Assert.InRange(thumbnail.Height, 1, EffectThumbnailSession.MaximumDimension);
            Assert.Equal(thumbnail.Width * thumbnail.Height * 4, thumbnail.Pixels.Length);
        });
        var rotated = (await session.RequestAsync(path, new() { RotationAngle = 37 }))!;
        Assert.All(rotated, thumbnail =>
        {
            Assert.InRange(thumbnail.Width, 1, EffectThumbnailSession.MaximumDimension);
            Assert.InRange(thumbnail.Height, 1, EffectThumbnailSession.MaximumDimension);
            Assert.Equal(new byte[] { 255, 255, 255, 255 }, thumbnail.Pixels[..4]);
        });
        var grid = await EffectThumbnailSession.RenderRecipeThumbnailAsync(path,
            new() { RotationAngle = 37, Effect = PhotoEffect.BlackAndWhite }, 180, CancellationToken.None);
        Assert.InRange(grid.Width, 1, 180);
        Assert.InRange(grid.Height, 1, 180);
        Assert.Equal(grid.Width * grid.Height * 4, grid.Pixels.Length);
    }

    [Fact]
    public async Task LatestRequestRejectsNonCooperativeStaleWorkAndSnapshotsParameters()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paths = new List<string>();
        var active = 0;
        var session = new EffectThumbnailSession(async (path, parameters, token) =>
        {
            Assert.Equal(1, Interlocked.Increment(ref active));
            paths.Add(path);
            if (path == "old")
            {
                Assert.Equal(.2, parameters.Exposure);
                started.SetResult();
                await release.Task; // Simulates a codec that ignores cancellation.
            }
            Interlocked.Decrement(ref active);
            return new[] { new EffectThumbnail(parameters.Effect, 1, 1, [0, 0, 0, 255]) };
        });
        var original = new EditParameters { Exposure = .2 };
        var old = session.RequestAsync("old", original);
        original.Exposure = .8;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var superseded = session.RequestAsync("superseded", new());
        var latest = session.RequestAsync("latest", new() { Effect = PhotoEffect.Sepia });
        release.SetResult();
        Assert.Null(await old);
        Assert.Null(await superseded);
        Assert.Equal(PhotoEffect.Sepia, Assert.Single((await latest)!).Effect);
        Assert.Equal(new[] { "old", "latest" }, paths);
    }

    [Fact]
    public async Task ClosingInvalidatesRunningThumbnailsAndCurrentErrorsAreNotHidden()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new EffectThumbnailSession(async (_, _, _) =>
        {
            started.SetResult();
            await release.Task;
            return Array.Empty<EffectThumbnail>();
        });
        var pending = session.RequestAsync("photo", new());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        session.Invalidate();
        release.SetResult();
        Assert.Null(await pending);
        var failed = new EffectThumbnailSession((_, _, _) =>
            throw new IOException("Cannot decode photo"));
        await Assert.ThrowsAsync<IOException>(() => failed.RequestAsync("photo", new()));
    }
}
