using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.Metadata.Profiles.Xmp;
using SixLabors.ImageSharp.PixelFormats;
using Windows.Graphics.Imaging;
using XmpCore;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class CropMetadataRemapperTests
{
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

            using (var image = new Image<Rgba32>(
                100,
                80,
                new Rgba32(100, 149, 237)))
            {
                image.Metadata.ExifProfile = new ExifProfile();
                image.Metadata.ExifProfile.SetValue(ExifTag.SubjectLocation, new ushort[] { 50, 40 });
                image.Metadata.ExifProfile.SetValue(ExifTag.SubjectArea, new ushort[] { 50, 40, 40, 20 });
                image.Metadata.ExifProfile.SetValue(ExifTag.PixelXDimension, 100U);
                image.Metadata.ExifProfile.SetValue(ExifTag.PixelYDimension, 80U);
                image.Metadata.XmpProfile = new XmpProfile(
                    await File.ReadAllBytesAsync(sidecarPath, cancellationToken));
                await image.SaveAsJpegAsync(imagePath, cancellationToken);
            }
            var ownedSidecar =
                FaceMetadataStore.GetSidecarPathForImage(imagePath);
            File.Move(sidecarPath, ownedSidecar);
            sidecarPath = ownedSidecar;

            var result = await CropService.CropImageAsync(
                imagePath,
                new BitmapBounds { X = 25, Y = 20, Width = 50, Height = 40 });

            Assert.Equal((uint)50, result.Width);
            Assert.Equal((uint)40, result.Height);
            using var cropped = await Image.LoadAsync(imagePath, cancellationToken);
            Assert.Equal(50, cropped.Width);
            Assert.Equal(40, cropped.Height);

            Assert.True(cropped.Metadata.ExifProfile!.TryGetValue(
                ExifTag.SubjectLocation,
                out var subjectLocation));
            Assert.Equal([25, 20], subjectLocation.Value);
            Assert.True(cropped.Metadata.ExifProfile.TryGetValue(ExifTag.SubjectArea, out var subjectArea));
            Assert.Equal([25, 20, 40, 20], subjectArea.Value);
            Assert.True(cropped.Metadata.ExifProfile.TryGetValue(ExifTag.PixelXDimension, out var pixelWidth));
            Assert.Equal(50U, pixelWidth.Value);
            Assert.True(cropped.Metadata.ExifProfile.TryGetValue(ExifTag.PixelYDimension, out var pixelHeight));
            Assert.Equal(40U, pixelHeight.Value);

            var embedded = XmpMetaFactory.ParseFromBuffer(
                cropped.Metadata.XmpProfile!.ToByteArray(),
                null);
            AssertRemappedXmp(embedded);
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
            using (var image = new Image<Rgba32>(
                100,
                80,
                new Rgba32(100, 149, 237)))
            {
                image.Frames.RootFrame.Metadata.ExifProfile = new ExifProfile();
                image.Frames.RootFrame.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6);
                await image.SaveAsTiffAsync(imagePath, cancellationToken);
            }

            var result = await CropService.CropImageAsync(
                imagePath,
                new BitmapBounds { X = 0, Y = 0, Width = 80, Height = 100 });

            Assert.Equal((uint)80, result.Width);
            Assert.Equal((uint)100, result.Height);
            using var cropped = await Image.LoadAsync(imagePath, cancellationToken);
            Assert.Equal(80, cropped.Width);
            Assert.Equal(100, cropped.Height);
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
