// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class PhotoPrintLayoutTests
{
    private static readonly PhotoPrintRectangle LetterImageableArea =
        new(18, 18, 8 * PhotoPrintLayoutService.DipsPerInch, 10.5 * PhotoPrintLayoutService.DipsPerInch);

    [Theory]
    [InlineData(PhotoPrintLayout.FourBySix, false, 6, 4)]
    [InlineData(PhotoPrintLayout.FourBySix, true, 4, 6)]
    [InlineData(PhotoPrintLayout.FiveBySeven, false, 7, 5)]
    [InlineData(PhotoPrintLayout.EightByTen, true, 8, 10)]
    public void PhotoFramesUsePhysicalTemplateDimensionsWhenTheyFit(
        PhotoPrintLayout layout,
        bool portrait,
        double widthInches,
        double heightInches)
    {
        var frame = PhotoPrintLayoutService.CreatePhotoFrame(layout, LetterImageableArea, portrait);

        Assert.Equal(widthInches * PhotoPrintLayoutService.DipsPerInch, frame.Width);
        Assert.Equal(heightInches * PhotoPrintLayoutService.DipsPerInch, frame.Height);
        AssertFrameContained(frame, LetterImageableArea);
    }

    [Fact]
    public void FullPageFrameUsesOnlyThePrintableArea()
    {
        var frame = PhotoPrintLayoutService.CreatePhotoFrame(
            PhotoPrintLayout.FullPage, LetterImageableArea);

        Assert.Equal(LetterImageableArea, frame);
    }

    [Fact]
    public void FramesScaleDownToFitSmallPrintableAreasWithoutChangingAspectRatio()
    {
        var smallArea = new PhotoPrintRectangle(8, 10, 300, 240);
        var frame = PhotoPrintLayoutService.CreatePhotoFrame(PhotoPrintLayout.EightByTen, smallArea);

        AssertFrameContained(frame, smallArea);
        Assert.Equal(10d / 8d, frame.Width / frame.Height, 6);
    }

    [Fact]
    public void ContactSheetsPaginateNineItemsAndIncludeFilenameSlotsForLastPage()
    {
        Assert.Equal(3, PhotoPrintLayoutService.GetPageCount(19, PhotoPrintLayout.ContactSheet));

        var page = PhotoPrintLayoutService.CreatePageSlots(
            3,
            19,
            PhotoPrintLayout.ContactSheet,
            816,
            1056,
            LetterImageableArea);

        Assert.Equal([18], page.Select(slot => slot.PhotoIndex));
        var filenames = Enumerable.Range(0, 19).Select(index => $"photo-{index}.jpg").ToArray();
        Assert.Equal(["photo-18.jpg"], page.Select(slot => filenames[slot.PhotoIndex]));
        Assert.All(page, slot => Assert.NotNull(slot.CaptionBounds));
        Assert.All(page, slot => AssertFrameContained(slot.ImageBounds, LetterImageableArea));
    }

    [Fact]
    public void WalletLayoutPrintsFourPhotosPerPageWithinPrintableBounds()
    {
        Assert.Equal(2, PhotoPrintLayoutService.GetPageCount(5, PhotoPrintLayout.Wallet));

        var page = PhotoPrintLayoutService.CreatePageSlots(
            1,
            5,
            PhotoPrintLayout.Wallet,
            816,
            1056,
            LetterImageableArea);

        Assert.Equal(4, page.Count);
        Assert.Equal(2.5 * PhotoPrintLayoutService.DipsPerInch, page[0].ImageBounds.Width);
        Assert.Equal(3.5 * PhotoPrintLayoutService.DipsPerInch, page[0].ImageBounds.Height);
        Assert.All(page, slot => AssertFrameContained(slot.ImageBounds, LetterImageableArea));
    }

    [Fact]
    public void SelectionIsOrderedByDisplayedItemsAndMissingItemsRemainDeterministic()
    {
        var displayed = new[] { "c.jpg", "a.jpg", "b.jpg" };
        var selected = new[] { "z.jpg", "b.jpg", "c.jpg", "a.jpg" };

        var ordered = PhotoPrintLayoutService.OrderSelection(displayed, selected, path => path);

        Assert.Equal(["c.jpg", "a.jpg", "b.jpg", "z.jpg"], ordered);
    }

    [Fact]
    public async Task PreCanceledDecodeDoesNotOpenThePhotoFile()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PhotoPrintImageDecoder.DecodeAsync("missing.jpg", 100, cancellation.Token));
    }

    [Fact]
    public async Task DecoderAppliesExifOrientationBeforeReturningPrintDimensions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.jpg");
        try
        {
            await RenderQualityFixtures.WriteImageAsync(
                path,
                2,
                1,
                RenderQualityFixtures.CreatePixels(RenderQualityFixtureKind.Gradient, 2, 1),
                orientation: 6);

            var decoded = await PhotoPrintImageDecoder.DecodeAsync(path, 100);

            Assert.Equal(1u, decoded.Width);
            Assert.Equal(2u, decoded.Height);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task DecoderSurfacesMissingPhotoErrorsAndEnforcesItsMemoryLimit()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            PhotoPrintImageDecoder.DecodeAsync("missing.jpg", 100));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            PhotoPrintImageDecoder.DecodeAsync(
                "photo.jpg",
                PhotoPrintImageDecoder.MaximumPhotoDimension + 1));
    }

    [Theory]
    [InlineData(".jpg", true)]
    [InlineData(".tiff", true)]
    [InlineData(".raw", false)]
    [InlineData(".mp4", false)]
    public void DecoderOnlyAdvertisesSupportedRasterFormats(string extension, bool expected)
    {
        Assert.Equal(expected, PhotoPrintImageDecoder.IsSupported($"photo{extension}"));
    }

    private static void AssertFrameContained(PhotoPrintRectangle frame, PhotoPrintRectangle area)
    {
        Assert.True(frame.X >= area.X);
        Assert.True(frame.Y >= area.Y);
        Assert.True(frame.X + frame.Width <= area.X + area.Width);
        Assert.True(frame.Y + frame.Height <= area.Y + area.Height);
    }
}
