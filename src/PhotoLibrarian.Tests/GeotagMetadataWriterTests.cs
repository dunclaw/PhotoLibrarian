// Copyright (c) Duncan Lawler. All Rights Reserved.
using MetadataExtractor.Formats.Exif;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.ViewModels;
using XmpCore;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class GeotagMetadataWriterTests : IDisposable
{
    private const string DcNamespace = "http://purl.org/dc/elements/1.1/";
    private const string ExifNamespace = "http://ns.adobe.com/exif/1.0/";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"PhotoLibrarian-Geotag-{Guid.NewGuid():N}");

    public GeotagMetadataWriterTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void CoordinateDisplay_RoundsToSixPlacesWithoutChangingStoredValues()
    {
        var image = new ImageEntry
        {
            FilePath = "photo.jpg",
            FileName = "photo.jpg",
            GpsLatitude = 12.123456789,
            GpsLongitude = -98.987654321
        };

        Assert.Equal("12.123457, -98.987654", MetadataPanelViewModel.FormatGpsCoordinates([image]));
        Assert.Equal(12.123456789, image.GpsLatitude);
        Assert.Equal(-98.987654321, image.GpsLongitude);

        var other = new ImageEntry
        {
            FilePath = "other.jpg",
            FileName = "other.jpg",
            GpsLatitude = 12.123456788,
            GpsLongitude = -98.987654321
        };
        Assert.Equal("", MetadataPanelViewModel.FormatGpsCoordinates([image, other]));
    }

    [Theory]
    [InlineData(-91, 0, "latitude")]
    [InlineData(91, 0, "latitude")]
    [InlineData(0, -181, "longitude")]
    [InlineData(0, 181, "longitude")]
    [InlineData(double.NaN, 0, "latitude")]
    [InlineData(0, double.PositiveInfinity, "longitude")]
    public void GeoCoordinate_RejectsInvalidBounds(double latitude, double longitude, string parameter)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new GeoCoordinate(latitude, longitude));

        Assert.Equal(parameter, exception.ParamName);
    }

    [Theory]
    [InlineData(".jpg", true)]
    [InlineData(".tif", false)]
    public async Task EmbeddedExifWrite_PreservesImageAndBacksUpOriginal(string extension, bool jpeg)
    {
        var imagePath = Path.Combine(_directory, $"image{extension}");
        var backupDirectory = Path.Combine(_directory, "backups");
        var sidecarPath = Path.ChangeExtension(imagePath, ".xmp");
        var coordinate = new GeoCoordinate(-33.8688, 151.2093);
        await WicTestImage.CreateAsync(imagePath, 32, 24, jpeg);
        await EmbeddedMetadataWriter.WriteAsync(imagePath, title: "Keep this caption");
        await File.WriteAllTextAsync(
            sidecarPath,
            "Keep this unrelated sidecar",
            TestContext.Current.CancellationToken);

        var service = new GeotagMetadataWriterService(new OriginalBackupService(backupDirectory));
        var location = await service.WriteAsync(imagePath, coordinate);
        var gps = MetadataExtractor.ImageMetadataReader.ReadMetadata(imagePath)
            .OfType<GpsDirectory>()
            .Single()
            .GetGeoLocation();

        Assert.Equal(GeotagWriteLocation.EmbeddedExif, location);
        Assert.NotNull(gps);
        Assert.InRange(Math.Abs(gps.Value.Latitude - coordinate.Latitude), 0, 0.000001);
        Assert.InRange(Math.Abs(gps.Value.Longitude - coordinate.Longitude), 0, 0.000001);
        Assert.Equal((32u, 24u), await WicTestImage.ReadSizeAsync(imagePath));
        Assert.Equal("Keep this caption", MetadataWriterService.ReadCaptionFromSidecar(imagePath));
        Assert.Equal(
            "Keep this unrelated sidecar",
            await File.ReadAllTextAsync(sidecarPath, TestContext.Current.CancellationToken));
        Assert.True(Directory.EnumerateFiles(backupDirectory, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task UnsupportedFormat_UpdatesExistingSidecarWithoutLosingOtherXmp()
    {
        var imagePath = Path.Combine(_directory, "photo.cr3");
        var sidecarPath = Path.ChangeExtension(imagePath, ".xmp");
        var originalSidecar = CreateSidecarWithExistingMetadata();
        await File.WriteAllBytesAsync(imagePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            sidecarPath,
            XmpMetaFactory.SerializeToString(originalSidecar, new XmpCore.Options.SerializeOptions()),
            TestContext.Current.CancellationToken);

        var service = new GeotagMetadataWriterService(
            new OriginalBackupService(Path.Combine(_directory, "backups")));
        var result = await service.WriteAsync(imagePath, new GeoCoordinate(-33.8688, -151.2093));
        var updatedXmp = XmpMetaFactory.ParseFromString(
            await File.ReadAllTextAsync(sidecarPath, TestContext.Current.CancellationToken));
        var backupSidecarPath = Assert.Single(
            Directory.EnumerateFiles(_directory, "photo.xmp.*.bak", SearchOption.AllDirectories));
        var backupXmp = XmpMetaFactory.ParseFromString(
            await File.ReadAllTextAsync(backupSidecarPath, TestContext.Current.CancellationToken));
        var indexed = new MetadataReaderService().ReadMetadata(imagePath);

        Assert.Equal(GeotagWriteLocation.XmpSidecar, result);
        Assert.Equal("Keep this caption", updatedXmp.GetLocalizedText(DcNamespace, "dc:description", "", "x-default")?.Value);
        Assert.Equal(
            "Preserve tag provenance",
            updatedXmp.GetPropertyString(EmbeddedMetadataWriter.PhotoLibrarianXmpNamespace, "plib:TagMetadata"));
        Assert.Equal("Keep this caption", backupXmp.GetLocalizedText(DcNamespace, "dc:description", "", "x-default")?.Value);
        Assert.Equal(
            "Preserve tag provenance",
            backupXmp.GetPropertyString(EmbeddedMetadataWriter.PhotoLibrarianXmpNamespace, "plib:TagMetadata"));
        Assert.EndsWith("S", updatedXmp.GetPropertyString(ExifNamespace, "exif:GPSLatitude"));
        Assert.EndsWith("W", updatedXmp.GetPropertyString(ExifNamespace, "exif:GPSLongitude"));
        Assert.InRange(Math.Abs(indexed.GpsLatitude!.Value + 33.8688), 0, 0.000001);
        Assert.InRange(Math.Abs(indexed.GpsLongitude!.Value + 151.2093), 0, 0.000001);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task MalformedSidecar_IsNotOverwritten()
    {
        var imagePath = Path.Combine(_directory, "photo.cr3");
        var sidecarPath = Path.ChangeExtension(imagePath, ".xmp");
        const string malformed = "<xmp>broken";
        await File.WriteAllBytesAsync(imagePath, [1, 2, 3], TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(sidecarPath, malformed, TestContext.Current.CancellationToken);

        var service = new GeotagMetadataWriterService(
            new OriginalBackupService(Path.Combine(_directory, "backups")));

        await Assert.ThrowsAsync<XmpException>(
            () => service.WriteAsync(imagePath, new GeoCoordinate(10, 20)));

        Assert.Equal(malformed, await File.ReadAllTextAsync(sidecarPath, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task WriteBatchAsync_AppliesSameCoordinatesAndReportsPerPhotoFailures()
    {
        var firstPath = Path.Combine(_directory, "first.jpg");
        var secondPath = Path.Combine(_directory, "second.jpg");
        var missingPath = Path.Combine(_directory, "missing.jpg");
        var coordinate = new GeoCoordinate(0, -180);
        await WicTestImage.CreateAsync(firstPath, 16, 16, jpeg: true);
        await WicTestImage.CreateAsync(secondPath, 16, 16, jpeg: true);

        var service = new GeotagMetadataWriterService(
            new OriginalBackupService(Path.Combine(_directory, "backups")));
        var attempts = await service.WriteBatchAsync(
            [firstPath, secondPath, missingPath],
            coordinate);

        Assert.Equal(3, attempts.Count);
        Assert.Equal(GeotagWriteLocation.EmbeddedExif, attempts[0].Location);
        Assert.Equal(GeotagWriteLocation.EmbeddedExif, attempts[1].Location);
        Assert.Null(attempts[0].Error);
        Assert.Null(attempts[1].Error);
        Assert.NotNull(attempts[2].Error);
        foreach (var path in new[] { firstPath, secondPath })
        {
            var gps = MetadataExtractor.ImageMetadataReader.ReadMetadata(path)
                .OfType<GpsDirectory>()
                .Single()
                .GetGeoLocation();
            Assert.NotNull(gps);
            Assert.Equal(0, gps.Value.Latitude);
            Assert.Equal(-180, gps.Value.Longitude);
        }
    }

    [Fact]
    public async Task LockedImage_FallsBackToSidecarWithoutChangingOriginal()
    {
        var imagePath = Path.Combine(_directory, "locked.jpg");
        var sidecarPath = Path.ChangeExtension(imagePath, ".xmp");
        await WicTestImage.CreateAsync(imagePath, 16, 16, jpeg: true);
        var originalBytes = await File.ReadAllBytesAsync(imagePath, TestContext.Current.CancellationToken);
        var service = new GeotagMetadataWriterService(
            new OriginalBackupService(Path.Combine(_directory, "backups")));
        MetadataWriteFallbackEventArgs? fallback = null;
        void OnFallback(object? sender, MetadataWriteFallbackEventArgs args)
        {
            if (args.ImagePath == imagePath)
                fallback = args;
        }

        MetadataWriterService.SidecarFallback += OnFallback;
        try
        {
            using (File.Open(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var location = await service.WriteAsync(imagePath, new GeoCoordinate(10, 20));
                Assert.Equal(GeotagWriteLocation.XmpSidecar, location);
            }
        }
        finally
        {
            MetadataWriterService.SidecarFallback -= OnFallback;
        }

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(imagePath, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(sidecarPath));
        Assert.NotNull(fallback);
        Assert.Equal("GPS coordinates", fallback.PropertyName);
        Assert.Equal(new GeoCoordinate(10, 20), new GeoCoordinate(
            new MetadataReaderService().ReadMetadata(imagePath).GpsLatitude!.Value,
            new MetadataReaderService().ReadMetadata(imagePath).GpsLongitude!.Value));
    }

    [Fact]
    public async Task ReadOnlyImage_FallsBackToSidecarWithoutChangingOriginal()
    {
        var imagePath = Path.Combine(_directory, "read-only.jpg");
        var sidecarPath = Path.ChangeExtension(imagePath, ".xmp");
        await WicTestImage.CreateAsync(imagePath, 16, 16, jpeg: true);
        var originalBytes = await File.ReadAllBytesAsync(imagePath, TestContext.Current.CancellationToken);
        File.SetAttributes(imagePath, File.GetAttributes(imagePath) | FileAttributes.ReadOnly);
        var service = new GeotagMetadataWriterService(
            new OriginalBackupService(Path.Combine(_directory, "backups")));

        try
        {
            var location = await service.WriteAsync(imagePath, new GeoCoordinate(-10, -20));
            Assert.Equal(GeotagWriteLocation.XmpSidecar, location);
            Assert.Equal(
                originalBytes,
                await File.ReadAllBytesAsync(imagePath, TestContext.Current.CancellationToken));
            Assert.True(File.Exists(sidecarPath));
        }
        finally
        {
            foreach (var path in Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories))
                File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    private static IXmpMeta CreateSidecarWithExistingMetadata()
    {
        try
        {
            XmpMetaFactory.SchemaRegistry.RegisterNamespace(
                EmbeddedMetadataWriter.PhotoLibrarianXmpNamespace,
                "plib");
        }
        catch (XmpException)
        {
            // The namespace may already be registered by another test.
        }
        var xmp = XmpMetaFactory.Create();
        xmp.SetLocalizedText(DcNamespace, "dc:description", "", "x-default", "Keep this caption");
        xmp.SetProperty(
            EmbeddedMetadataWriter.PhotoLibrarianXmpNamespace,
            "plib:TagMetadata",
            "Preserve tag provenance");
        return xmp;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
