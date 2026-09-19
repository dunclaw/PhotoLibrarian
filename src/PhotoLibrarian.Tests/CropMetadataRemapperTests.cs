using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using Windows.Graphics.Imaging;
using XmpCore;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class CropMetadataRemapperTests
{
    [Theory]
    [InlineData(".jpg", true)]
    [InlineData(".png", false)]
    [InlineData(".tiff", false)]
    [InlineData(".bmp", false)]
    public async Task CropImage_SupportsAllEditableFormats(string extension, bool jpeg)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, $"image{extension}");
        try
        {
            await WicTestImage.CreateAsync(imagePath, 100, 80, jpeg);

            var result = await CropService.CropImageAsync(
                imagePath,
                new BitmapBounds { X = 25, Y = 20, Width = 50, Height = 40 });

            Assert.Equal((50u, 40u), (result.Width, result.Height));
            Assert.Equal((50u, 40u), await WicTestImage.ReadSizeAsync(imagePath));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RemapFaceRegion_ClipsPartialRegionAndDropsOutsideRegion()
    {
        var crop = new CropRectangle(25, 20, 50, 40);
        var partial = CropMetadataRemapper.RemapFaceRegion(
            new FaceRegion { X = 0.1, Y = 0.25, Width = 0.4, Height = 0.5 },
            100,
            80,
            crop);

        Assert.NotNull(partial);
        Assert.Equal(0, partial.X, 6);
        Assert.Equal(0, partial.Y, 6);
        Assert.Equal(0.5, partial.Width, 6);
        Assert.Equal(1, partial.Height, 6);

        Assert.Null(CropMetadataRemapper.RemapFaceRegion(
            new FaceRegion { X = 0, Y = 0, Width = 0.1, Height = 0.1 },
            100,
            80,
            crop));
    }

    [Fact]
    public void OrientExifLocations_RotatesCentersAndDimensionsIntoDisplayCoordinates()
    {
        Assert.Equal(
            [49, 20],
            CropMetadataRemapper.OrientSubjectLocation([20, 30], 100, 80, orientation: 6));
        Assert.Equal(
            [49, 20, 20, 10],
            CropMetadataRemapper.OrientSubjectArea([20, 30, 10, 20], 100, 80, orientation: 6));
    }

    [Fact]
    public async Task CropImage_RemapEmbeddedAndSidecarMwgRegionsAndExifLocations()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, "portrait.jpg");
        var sidecarPath = Path.ChangeExtension(imagePath, ".xmp");

        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await new FaceMetadataStore().WriteAsync(
                imagePath,
                new PhotoFaceMetadata(
                    100,
                    80,
                    [
                        new PortableFaceMetadata(
                            1,
                            0.3,
                            0.25,
                            0.4,
                            0.5,
                            "Alex",
                            true,
                            false,
                            ["Sam"]),
                        new PortableFaceMetadata(
                            2,
                            0,
                            0,
                            0.1,
                            0.1,
                            "Outside",
                            true,
                            false,
                            [])
                    ]),
                cancellationToken);

            await WicTestImage.CreateAsync(imagePath, 100, 80, true);
            var ownedSidecar =
                FaceMetadataStore.GetSidecarPathForImage(imagePath);
            File.Move(sidecarPath, ownedSidecar);
            sidecarPath = ownedSidecar;

            var result = await CropService.CropImageAsync(
                imagePath,
                new BitmapBounds { X = 25, Y = 20, Width = 50, Height = 40 });

            Assert.Equal((uint)50, result.Width);
            Assert.Equal((uint)40, result.Height);
            Assert.Equal((50u, 40u), await WicTestImage.ReadSizeAsync(imagePath));
            AssertRemappedXmp(XmpMetaFactory.ParseFromString(
                await File.ReadAllTextAsync(sidecarPath, cancellationToken)));
            var restoredReview = Assert.Single(
                new FaceMetadataStore().Read(imagePath).Faces);
            Assert.True(restoredReview.SuggestionsHidden);
            Assert.Equal(["Sam"], restoredReview.RejectedPersonNames);
            Assert.Equal(0.1, restoredReview.X, 6);
            Assert.Equal(0, restoredReview.Y, 6);
            Assert.Equal(0.8, restoredReview.Width, 6);
            Assert.Equal(1, restoredReview.Height, 6);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task CropImage_UsesDisplayCoordinatesForOrientedTiff()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, "oriented.tiff");
        var cancellationToken = TestContext.Current.CancellationToken;

        try
        {
            await WicTestImage.CreateAsync(imagePath, 100, 80);

            var result = await CropService.CropImageAsync(
                imagePath,
                new BitmapBounds { X = 0, Y = 0, Width = 80, Height = 80 });

            Assert.Equal((uint)80, result.Width);
            Assert.Equal((uint)80, result.Height);
            Assert.Equal((80u, 80u), await WicTestImage.ReadSizeAsync(imagePath));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void AssertRemappedXmp(IXmpMeta xmp)
    {
        const string listPath = "Regions/mwg-rs:RegionList";
        Assert.Equal(1, xmp.CountArrayItems(CropMetadataRemapper.MwgRs, listPath));

        var areaPath = $"{listPath}[1]/mwg-rs:Area";
        Assert.Equal(
            "0.5",
            xmp.GetStructField(CropMetadataRemapper.MwgRs, areaPath, CropMetadataRemapper.StArea, "x")?.Value);
        Assert.Equal(
            "1",
            xmp.GetStructField(CropMetadataRemapper.MwgRs, areaPath, CropMetadataRemapper.StArea, "h")?.Value);

        var dimensionsPath = "Regions/mwg-rs:AppliedToDimensions";
        Assert.Equal(
            "50",
            xmp.GetStructField(CropMetadataRemapper.MwgRs, dimensionsPath, CropMetadataRemapper.StDim, "w")?.Value);
        Assert.Equal(
            "40",
            xmp.GetStructField(CropMetadataRemapper.MwgRs, dimensionsPath, CropMetadataRemapper.StDim, "h")?.Value);
    }
}
