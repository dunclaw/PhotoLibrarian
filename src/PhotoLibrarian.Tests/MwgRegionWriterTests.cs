using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using System.Globalization;
using XmpCore;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class MwgRegionWriterTests
{
    [Fact]
    public async Task WriteFaceRegions_UsesInvariantNormalizedCoordinates()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"PhotoLibrarian-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, "portrait.jpg");
        var originalCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            await MwgRegionWriter.WriteFaceRegionsAsync(
                imagePath,
                [
                    new FaceRegion
                    {
                        X = 0.1,
                        Y = 0.2,
                        Width = 0.3,
                        Height = 0.4,
                        PersonName = "Alex"
                    }
                ],
                1200,
                800);

            var xmp = await File.ReadAllTextAsync(
                Path.ChangeExtension(imagePath, ".xmp"),
                TestContext.Current.CancellationToken);
            Assert.Contains("Alex", xmp);
            Assert.Contains("0.250000", xmp);
            Assert.Contains("0.400000", xmp);
            Assert.DoesNotContain("0,250000", xmp);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            var sidecarPath = Path.ChangeExtension(imagePath, ".xmp");
            if (File.Exists(sidecarPath))
            {
                File.Delete(sidecarPath);
            }

            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task FaceMetadataStore_RoundTripsPortableReviewStateAndPreservesSidecarFields()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"PhotoLibrarian-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, "portrait.cr3");
        var sidecarPath = Path.ChangeExtension(imagePath, ".xmp");
        var store = new FaceMetadataStore();
        const string xap = "http://ns.adobe.com/xap/1.0/";
        var xmp = XmpMetaFactory.Create();
        xmp.SetProperty(xap, "xmp:CreatorTool", "Original Tool");
        xmp.AppendArrayItem(
            CropMetadataRemapper.MwgRs,
            "Regions/mwg-rs:RegionList",
            new XmpCore.Options.PropertyOptions
            {
                IsArray = true,
                IsArrayOrdered = true
            },
            null,
            new XmpCore.Options.PropertyOptions { IsStruct = true });
        xmp.SetStructField(
            CropMetadataRemapper.MwgRs,
            "Regions/mwg-rs:RegionList[1]",
            CropMetadataRemapper.MwgRs,
            "Type",
            "Pet");
        await File.WriteAllTextAsync(
            sidecarPath,
            XmpMetaFactory.SerializeToString(
                xmp,
                new XmpCore.Options.SerializeOptions()),
            TestContext.Current.CancellationToken);

        try
        {
            await store.WriteAsync(
                imagePath,
                new PhotoFaceMetadata(
                    1200,
                    800,
                    [
                        new PortableFaceMetadata(
                            42,
                            0.1,
                            0.2,
                            0.3,
                            0.4,
                            "Alex",
                            true,
                            true,
                            ["Sam"])
                    ]),
                TestContext.Current.CancellationToken);

            var restored = store.Read(imagePath);
            var face = Assert.Single(restored.Faces);
            Assert.Equal("Alex", face.PersonName);
            Assert.True(face.SuggestionsHidden);
            Assert.True(face.PersonSuggestionsHidden);
            Assert.Equal(["Sam"], face.RejectedPersonNames);
            var updatedXmp = XmpMetaFactory.ParseFromString(
                await File.ReadAllTextAsync(
                    sidecarPath,
                    TestContext.Current.CancellationToken));
            Assert.Equal(
                "Original Tool",
                updatedXmp.GetPropertyString(xap, "xmp:CreatorTool"));
            Assert.Equal(
                "Pet",
                updatedXmp.GetStructField(
                    CropMetadataRemapper.MwgRs,
                    "Regions/mwg-rs:RegionList[1]",
                    CropMetadataRemapper.MwgRs,
                    "Type")?.Value);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task FaceMetadataStore_EmbedsMetadataInJpegWithoutSidecar()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"PhotoLibrarian-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, "portrait.jpg");

        try
        {
            await WicTestImage.CreateAsync(imagePath, 16, 12, true);

            var store = new FaceMetadataStore();
            await store.WriteAsync(
                imagePath,
                new PhotoFaceMetadata(
                    16,
                    12,
                    [
                        new PortableFaceMetadata(
                            1,
                            0.1,
                            0.2,
                            0.3,
                            0.4,
                            "Alex",
                            false,
                            false,
                            [])
                    ]),
                TestContext.Current.CancellationToken);

            Assert.False(File.Exists(Path.ChangeExtension(imagePath, ".xmp")));
            Assert.Equal("Alex", Assert.Single(store.Read(imagePath).Faces).PersonName);
            Assert.Equal((16u, 12u), await WicTestImage.ReadSizeAsync(imagePath));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task FaceMetadataStore_SidecarOverridesStaleEmbeddedReviewState()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"PhotoLibrarian-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, "portrait.jpg");

        try
        {
            await WicTestImage.CreateAsync(imagePath, 16, 12, true);

            var store = new FaceMetadataStore();
            var hidden = new PortableFaceMetadata(
                1,
                0.1,
                0.2,
                0.3,
                0.4,
                null,
                true,
                false,
                ["Alex"]);
            await store.WriteAsync(
                imagePath,
                new PhotoFaceMetadata(
                    16,
                    12,
                    [
                        hidden,
                        hidden with
                        {
                            FaceRegionId = 2,
                            X = 0.6,
                            SuggestionsHidden = false,
                            RejectedPersonNames = []
                        }
                    ]),
                TestContext.Current.CancellationToken);
            await store.WriteAsync(
                Path.ChangeExtension(imagePath, ".cr3"),
                new PhotoFaceMetadata(
                    16,
                    12,
                    [
                        hidden with
                        {
                            SuggestionsHidden = false,
                            RejectedPersonNames = []
                        }
                    ]),
                TestContext.Current.CancellationToken);
            File.Move(
                Path.ChangeExtension(imagePath, ".xmp"),
                $"{imagePath}.xmp");

            var restored = Assert.Single(store.Read(imagePath).Faces);
            Assert.False(restored.SuggestionsHidden);
            Assert.Empty(restored.RejectedPersonNames);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task FaceMetadataStore_DoesNotUseRawSidecarForSameStemJpeg()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"PhotoLibrarian-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var jpegPath = Path.Combine(directory, "portrait.jpg");
        var rawPath = Path.Combine(directory, "portrait.cr3");

        try
        {
            await WicTestImage.CreateAsync(jpegPath, 16, 12, true);

            var store = new FaceMetadataStore();
            await store.WriteAsync(
                rawPath,
                new PhotoFaceMetadata(
                    16,
                    12,
                    [
                        new PortableFaceMetadata(
                            1,
                            0.1,
                            0.2,
                            0.3,
                            0.4,
                            "Raw Person",
                            false,
                            false,
                            [])
                    ]),
                TestContext.Current.CancellationToken);
            await store.WriteAsync(
                jpegPath,
                new PhotoFaceMetadata(
                    16,
                    12,
                    [
                        new PortableFaceMetadata(
                            2,
                            0.4,
                            0.2,
                            0.3,
                            0.4,
                            "JPEG Person",
                            false,
                            false,
                            [])
                    ]),
                TestContext.Current.CancellationToken);

            Assert.Equal(
                "Raw Person",
                Assert.Single(store.Read(rawPath).Faces).PersonName);
            Assert.Equal(
                "JPEG Person",
                Assert.Single(store.Read(jpegPath).Faces).PersonName);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task FaceMetadataStore_BatchFailureRestoresEarlierFiles()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"PhotoLibrarian-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var firstPath = Path.Combine(directory, "first.cr3");
        var secondPath = Path.Combine(directory, "second.cr3");
        var store = new FaceMetadataStore();
        var original = new PhotoFaceMetadata(
            100,
            100,
            [
                new PortableFaceMetadata(
                    1,
                    0.1,
                    0.1,
                    0.2,
                    0.2,
                    "Original",
                    false,
                    false,
                    [])
            ]);

        try
        {
            await store.WriteAsync(
                firstPath,
                original,
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.ChangeExtension(secondPath, ".xmp"),
                "not valid XMP",
                TestContext.Current.CancellationToken);

            await Assert.ThrowsAnyAsync<Exception>(
                () => store.WriteBatchAsync(
                    [
                        (
                            firstPath,
                            original with
                            {
                                Faces =
                                [
                                    original.Faces[0] with
                                    {
                                        PersonName = "Changed"
                                    }
                                ]
                            }),
                        (secondPath, original)
                    ],
                    TestContext.Current.CancellationToken));

            Assert.Equal(
                "Original",
                Assert.Single(store.Read(firstPath).Faces).PersonName);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
