// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using PhotoLibrarian.ViewModels;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class BatchAutoAdjustTests
{
    [Fact]
    public async Task SelectionGetsIndependentCorrectionsWithOneUndoAndUnchangedSourcePixels()
    {
        var token = TestContext.Current.CancellationToken;
        await using var library = await StabilizedVideoLibraryTests.LibraryFixture.CreateAsync();
        var dark = await AddPhotoAsync(library.Photos, "dark-warm.png", [48, 52, 60, 255]);
        var bright = await AddPhotoAsync(library.Photos, "bright-cool.jpg", [218, 205, 190, 255]);
        var transparent = await AddPhotoAsync(library.Photos, "transparent.png", [0, 0, 0, 0]);
        var bmp = new ImageEntry { FileName = "unsupported.bmp", FilePath = Path.Combine(library.Photos, "unsupported.bmp") };
        var bmpBytes = await WindowsImagingCodec.EncodeAsync(bmp.FilePath, [40, 40, 40, 255], 1, 1);
        await File.WriteAllBytesAsync(bmp.FilePath, bmpBytes, token);
        var corrupt = new ImageEntry { FileName = "corrupt.png", FilePath = Path.Combine(library.Photos, "corrupt.png") };
        await File.WriteAllBytesAsync(corrupt.FilePath, [1, 2, 3], token);

        var beforeDark = new EditParameters
        {
            Brightness = .3,
            RotationAngle = 90,
            Effect = PhotoEffect.Sepia,
            Clarity = .4
        };
        await ImageEditService.WritePendingRecipeAsync(dark.FilePath, beforeDark);
        var beforeDarkBytes = File.ReadAllBytes(dark.FilePath);
        var backupHash = await OriginalBackupService.ComputeFileHashAsync(dark.FilePath);
        var darkPixels = (await WindowsImagingCodec.DecodeAsync(dark.FilePath)).Pixels;
        var brightPixels = (await WindowsImagingCodec.DecodeAsync(bright.FilePath)).Pixels;
        var unadjustedRender = await ImageEditRenderer.RenderToPixelsAsync(dark.FilePath, beforeDark);

        var result = await library.Main.AutoAdjustSelectionAsync(
            [dark, bright, transparent, bmp, corrupt], cancellationToken: token);

        Assert.True(result.Applied == 2, result.SaveMessage);
        Assert.False(result.IsCancelled);
        Assert.Collection(result.Issues,
            skip => { Assert.Equal(transparent.FilePath, skip.Path); Assert.False(skip.IsFailure); },
            skip => { Assert.Equal(bmp.FilePath, skip.Path); Assert.False(skip.IsFailure); },
            failure => { Assert.Equal(corrupt.FilePath, failure.Path); Assert.True(failure.IsFailure); });
        var adjustedDark = ImageEditService.ReadEditParameters(dark.FilePath);
        var adjustedBright = ImageEditService.ReadEditParameters(bright.FilePath);
        Assert.True(adjustedDark.Exposure > 0);
        Assert.True(adjustedBright.Exposure < 0);
        Assert.True(adjustedDark.Temperature < 0);
        Assert.True(adjustedBright.Temperature > 0);
        Assert.Equal(0, adjustedDark.Brightness);
        Assert.Equal(beforeDark.Effect, adjustedDark.Effect);
        Assert.Equal(beforeDark.RotationAngle, adjustedDark.RotationAngle);
        Assert.Equal(beforeDark.Clarity, adjustedDark.Clarity);
        Assert.Equal(darkPixels, (await WindowsImagingCodec.DecodeAsync(dark.FilePath)).Pixels);
        Assert.Equal(brightPixels, (await WindowsImagingCodec.DecodeAsync(bright.FilePath)).Pixels);
        var backup = Path.Combine(library.Root, "backups", backupHash[..2], $"{backupHash}.png");
        Assert.Equal(beforeDarkBytes, File.ReadAllBytes(backup));
        Assert.Equal(bmpBytes, File.ReadAllBytes(bmp.FilePath));
        Assert.False(File.Exists(Path.ChangeExtension(bmp.FilePath, ".xmp")));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(corrupt.FilePath));
        Assert.False(File.Exists(Path.ChangeExtension(dark.FilePath, ".xmp")));
        Assert.False(File.Exists(Path.ChangeExtension(bright.FilePath, ".xmp")));
        var rendered = await ImageEditRenderer.RenderToPixelsAsync(dark.FilePath, adjustedDark);
        Assert.False(rendered.Pixels.SequenceEqual(unadjustedRender.Pixels));

        var repeat = await library.Main.AutoAdjustSelectionAsync([dark, bright], cancellationToken: token);
        Assert.Equal(0, repeat.Applied);
        Assert.Equal(2, repeat.Unchanged);

        Assert.Equal(EditHistoryStatus.Applied, (await library.Main.EditHistory.UndoAsync()).Status);
        Assert.True(EditRecipeComposer.ContentEquals(beforeDark, ImageEditService.ReadEditParameters(dark.FilePath)));
        Assert.False(ImageEditService.ReadEditParameters(bright.FilePath).HasAdjustments);
        Assert.False(library.Main.EditHistory.CanUndo);
        Assert.Equal(EditHistoryStatus.Applied, (await library.Main.EditHistory.RedoAsync()).Status);
        Assert.True(EditRecipeComposer.ContentEquals(adjustedDark, ImageEditService.ReadEditParameters(dark.FilePath)));
        Assert.True(EditRecipeComposer.ContentEquals(adjustedBright, ImageEditService.ReadEditParameters(bright.FilePath)));
    }

    [Fact]
    public async Task CancellationAfterAnalyzingAChangedPhotoDoesNotSaveItOrCreateHistory()
    {
        await using var library = await StabilizedVideoLibraryTests.LibraryFixture.CreateAsync();
        var first = await library.AddPhotoAsync("first.png");
        var second = await library.AddPhotoAsync("second.png");
        var firstBytes = File.ReadAllBytes(first.FilePath);
        var secondBytes = File.ReadAllBytes(second.FilePath);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var progress = new InlineProgress(update =>
        {
            if (update.Examined == 1) cancellation.Cancel();
        });

        var result = await library.Main.AutoAdjustSelectionAsync([first, second], progress, cancellation.Token);

        Assert.True(result.IsCancelled);
        Assert.Equal(0, result.Applied);
        Assert.Equal(firstBytes, File.ReadAllBytes(first.FilePath));
        Assert.Equal(secondBytes, File.ReadAllBytes(second.FilePath));
        Assert.False(library.Main.EditHistory.CanUndo);
    }

    [Fact]
    public async Task APhotoLockedForWritesPreventsPublishingAnyPreparedRecipe()
    {
        await using var library = await StabilizedVideoLibraryTests.LibraryFixture.CreateAsync();
        var first = await library.AddPhotoAsync("first.png");
        var second = await library.AddPhotoAsync("second.png");
        var firstBytes = File.ReadAllBytes(first.FilePath);
        var secondBytes = File.ReadAllBytes(second.FilePath);
        using var locked = File.OpenRead(second.FilePath);
        var result = await library.Main.AutoAdjustSelectionAsync(
            [first, second], cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsCancelled);
        Assert.Equal(0, result.Applied);
        Assert.Empty(result.Issues);
        Assert.Equal(2, result.NotSaved);
        Assert.Contains(second.FileName, result.SaveMessage);
        Assert.Equal(firstBytes, File.ReadAllBytes(first.FilePath));
        Assert.Equal(secondBytes, File.ReadAllBytes(second.FilePath));
        Assert.False(library.Main.EditHistory.CanUndo);
    }

    private static async Task<ImageEntry> AddPhotoAsync(string folder, string name, byte[] pixel)
    {
        var path = Path.Combine(folder, name);
        var pixels = new byte[32 * 24 * 4];
        for (var offset = 0; offset < pixels.Length; offset += pixel.Length)
            pixel.CopyTo(pixels, offset);
        await RenderQualityFixtures.WriteImageAsync(path, 32, 24, pixels, jpeg: name.EndsWith(".jpg", StringComparison.Ordinal));
        return new ImageEntry { FileName = name, FilePath = path };
    }

    private sealed class InlineProgress(Action<AutoAdjustProgress> action) : IProgress<AutoAdjustProgress>
    {
        public void Report(AutoAdjustProgress value) => action(value);
    }
}
