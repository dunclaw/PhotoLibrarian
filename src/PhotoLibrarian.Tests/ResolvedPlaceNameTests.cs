// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.ViewModels;
using Microsoft.Data.Sqlite;
using System.ComponentModel;
using XmpCore;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class ResolvedPlaceNameTests
{
    [Fact]
    public async Task ReadsPlaceNameFromXmpSidecarAndPersistsItInTheLocalImageCache()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"PhotoLibrarian-place-name-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, "photo.raw");

        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await File.WriteAllBytesAsync(imagePath, [1, 2, 3], cancellationToken);
            var xmp = XmpMetaFactory.Create();
            const string iptcCoreNamespace = "http://iptc.org/std/Iptc4xmpCore/1.0/xmlns/";
            xmp.SetProperty(iptcCoreNamespace, "City", "Seattle");
            xmp.SetProperty(iptcCoreNamespace, "State", "Washington");
            xmp.SetProperty(iptcCoreNamespace, "CountryName", "United States");
            await File.WriteAllTextAsync(
                Path.ChangeExtension(imagePath, ".xmp"),
                XmpMetaFactory.SerializeToString(
                    xmp,
                    new XmpCore.Options.SerializeOptions()),
                cancellationToken);

            var image = new MetadataReaderService().ReadMetadata(imagePath);
            Assert.Equal("Seattle, Washington, United States", image.PlaceName);

            using var database = new CacheDatabase(Path.Combine(directory, "cache.db"));
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            await images.UpsertImageAsync(image);

            var cachedImage = await images.GetByPathAsync(imagePath);
            Assert.Equal(image.PlaceName, cachedImage?.PlaceName);

            var viewModel = new MetadataPanelViewModel();
            viewModel.ShowMetadata(cachedImage!);
            Assert.Equal(image.PlaceName, viewModel.PlaceName);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DoesNotInventAPlaceNameFromCoordinatesAlone()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"PhotoLibrarian-place-name-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, "photo.raw");

        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await File.WriteAllBytesAsync(imagePath, [1, 2, 3], cancellationToken);
            var xmp = XmpMetaFactory.Create();
            xmp.SetProperty("http://ns.adobe.com/exif/1.0/", "GPSLatitude", "47,36N");
            xmp.SetProperty("http://ns.adobe.com/exif/1.0/", "GPSLongitude", "122,20W");
            await File.WriteAllTextAsync(
                Path.ChangeExtension(imagePath, ".xmp"),
                XmpMetaFactory.SerializeToString(
                    xmp,
                    new XmpCore.Options.SerializeOptions()),
                cancellationToken);

            var image = new MetadataReaderService().ReadMetadata(imagePath);

            Assert.NotNull(image.GpsLatitude);
            Assert.NotNull(image.GpsLongitude);
            Assert.Empty(image.PlaceName);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadsPlaceNameFromPhotoForPreviouslyIndexedGeotaggedImage()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"PhotoLibrarian-place-name-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, "photo.raw");

        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await File.WriteAllBytesAsync(imagePath, [1, 2, 3], cancellationToken);
            var xmp = XmpMetaFactory.Create();
            const string iptcCoreNamespace = "http://iptc.org/std/Iptc4xmpCore/1.0/xmlns/";
            xmp.SetProperty(iptcCoreNamespace, "City", "Seattle");
            xmp.SetProperty(iptcCoreNamespace, "State", "Washington");
            xmp.SetProperty(iptcCoreNamespace, "CountryName", "United States");
            await File.WriteAllTextAsync(
                Path.ChangeExtension(imagePath, ".xmp"),
                XmpMetaFactory.SerializeToString(
                    xmp,
                    new XmpCore.Options.SerializeOptions()),
                cancellationToken);

            using var database = new CacheDatabase(Path.Combine(directory, "cache.db"));
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            var tags = new TagRepository(database);
            var faces = new FaceRepository(database);
            var oldImage = new ImageEntry
            {
                FilePath = imagePath,
                FileName = Path.GetFileName(imagePath),
                FileSize = 3,
                DateModified = File.GetLastWriteTimeUtc(imagePath),
                GpsLatitude = 47.6,
                GpsLongitude = -122.3
            };
            oldImage.Id = await images.UpsertImageAsync(oldImage);

            var viewModel = new MetadataPanelViewModel();
            viewModel.Initialize(images, tags, faces, main: null);
            var placeNameLoaded = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MetadataPanelViewModel.PlaceName) &&
                    viewModel.PlaceName == "Seattle, Washington, United States")
                {
                    placeNameLoaded.TrySetResult(true);
                }
            };
            viewModel.ShowMetadata(oldImage);

            await placeNameLoaded.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                cancellationToken);

            Assert.Equal("Seattle, Washington, United States", viewModel.PlaceName);
            Assert.Equal(
                viewModel.PlaceName,
                (await images.GetByPathAsync(imagePath))?.PlaceName);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AddsPlaceNameColumnToAnExistingCache()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"PhotoLibrarian-place-name-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "cache.db");

        try
        {
            using (var database = new CacheDatabase(databasePath))
                await database.InitializeAsync();

            await using (var connection = new SqliteConnection(
                             new SqliteConnectionStringBuilder
                             {
                                 DataSource = databasePath,
                                 Mode = SqliteOpenMode.ReadWrite
                             }.ToString()))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                using var command = connection.CreateCommand();
                command.CommandText = "ALTER TABLE images DROP COLUMN place_name";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            using var migratedDatabase = new CacheDatabase(databasePath);
            await migratedDatabase.InitializeAsync();
            var images = new ImageRepository(migratedDatabase);
            var image = new ImageEntry
            {
                FilePath = Path.Combine(directory, "photo.jpg"),
                FileName = "photo.jpg",
                PlaceName = "Seattle, Washington, United States",
                DateModified = DateTime.UtcNow
            };
            await images.UpsertImageAsync(image);

            Assert.Equal(
                image.PlaceName,
                (await images.GetByPathAsync(image.FilePath))?.PlaceName);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
