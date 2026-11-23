// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using PhotoLibrarian.ViewModels;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class CaptureDateEditTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"PhotoLibrarian-CaptureDate-{Guid.NewGuid():N}");

    public CaptureDateEditTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task DirectSetAndOffset_UpdatePortableDatesAndCacheAndSkipUndated()
    {
        var firstPath = Path.Combine(_directory, "first.jpg");
        var secondPath = Path.Combine(_directory, "second.cr3");
        var undatedPath = Path.Combine(_directory, "undated.cr3");
        await WicTestImage.CreateAsync(firstPath, 16, 16, jpeg: true);
        await File.WriteAllBytesAsync(secondPath, [1, 2, 3]);
        await File.WriteAllBytesAsync(undatedPath, [1, 2, 3]);
        using var database = new CacheDatabase(Path.Combine(_directory, "cache.db"));
        await database.InitializeAsync();
        var repository = new ImageRepository(database);
        var first = new ImageEntry { FilePath = firstPath, FileName = "first.jpg" };
        var second = new ImageEntry { FilePath = secondPath, FileName = "second.cr3" };
        var undated = new ImageEntry { FilePath = undatedPath, FileName = "undated.cr3" };
        foreach (var entry in new[] { first, second, undated })
            entry.Id = await repository.UpsertImageAsync(entry);
        var panel = new MetadataPanelViewModel();
        panel.Initialize(imageRepo: repository);
        panel.ShowMetadata([first, second]);
        var date = new DateTime(2020, 5, 6, 9, 30, 0);

        Assert.True(await panel.SetDateTakenAsync(date));
        panel.ShowMetadata([first, second, undated]);
        Assert.True(await panel.OffsetDateTakenAsync(TimeSpan.FromHours(2)));

        foreach (var entry in new[] { first, second })
        {
            Assert.Equal(date.AddHours(2), entry.DateTaken);
            Assert.Equal(entry.DateTaken, (await repository.GetByPathAsync(entry.FilePath))!.DateTaken);
            Assert.Equal(entry.DateTaken, new MetadataReaderService().ReadMetadata(entry.FilePath).DateTaken);
        }
        Assert.Null(undated.DateTaken);
        Assert.Null((await repository.GetByPathAsync(undatedPath))!.DateTaken);
        Assert.False(File.Exists(Path.ChangeExtension(undatedPath, ".xmp")));
        Assert.True(panel.IsDateMixed);
    }

    [Fact]
    public async Task DirectSet_FailedPortableWriteDoesNotUpdateEntryOrCache()
    {
        var goodPath = Path.Combine(_directory, "good.jpg");
        var missingPath = Path.Combine(_directory, "missing.jpg");
        await WicTestImage.CreateAsync(goodPath, 16, 16, jpeg: true);
        using var database = new CacheDatabase(Path.Combine(_directory, "cache.db"));
        await database.InitializeAsync();
        var repository = new ImageRepository(database);
        var oldDate = new DateTime(2019, 1, 2);
        var good = new ImageEntry { FilePath = goodPath, FileName = "good.jpg", DateTaken = oldDate };
        var missing = new ImageEntry { FilePath = missingPath, FileName = "missing.jpg", DateTaken = oldDate };
        foreach (var entry in new[] { good, missing })
            entry.Id = await repository.UpsertImageAsync(entry);
        var panel = new MetadataPanelViewModel();
        panel.Initialize(imageRepo: repository);
        panel.ShowMetadata([good, missing]);
        var date = new DateTime(2021, 3, 4);

        await Assert.ThrowsAnyAsync<Exception>(() => panel.SetDateTakenAsync(date));

        Assert.Equal(date, good.DateTaken);
        Assert.Equal(date, new MetadataReaderService().ReadMetadata(goodPath).DateTaken);
        Assert.Equal(date, (await repository.GetByPathAsync(goodPath))!.DateTaken);
        Assert.Equal(oldDate, missing.DateTaken);
        Assert.Equal(oldDate, (await repository.GetByPathAsync(missingPath))!.DateTaken);
        Assert.True(panel.IsDateMixed);
    }

    [Fact]
    public async Task DirectWrite_IndexFailureReportsPortableSuccess()
    {
        var path = Path.Combine(_directory, "photo.cr3");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        using var database = new CacheDatabase(Path.Combine(_directory, "uninitialized.db"));
        var entry = new ImageEntry { Id = 1, FilePath = path, FileName = "photo.cr3" };
        var editor = new CaptureDateEditService(new ImageRepository(database));
        var date = new DateTime(2022, 5, 6, 7, 8, 9);

        var error = await Assert.ThrowsAsync<IOException>(() =>
            editor.WriteDirectAsync(CaptureDateEditService.PrepareSet([entry], date)));

        Assert.Contains("capture date was written", error.Message);
        Assert.NotNull(error.InnerException);
        Assert.Equal(date, entry.DateTaken);
        Assert.Equal(date, new MetadataReaderService().ReadMetadata(path).DateTaken);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OffsetOutOfRange_RejectsWholeBatchWithoutWrites(bool upperBound)
    {
        var path = Path.Combine(_directory, "photo.cr3");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var original = new DateTime(2020, 1, 2);
        var first = new ImageEntry { FilePath = path, FileName = "photo.cr3", DateTaken = original };
        var extreme = new ImageEntry
        {
            FilePath = Path.Combine(_directory, "extreme.cr3"),
            FileName = "extreme.cr3",
            DateTaken = upperBound ? DateTime.MaxValue : DateTime.MinValue
        };
        var panel = new MetadataPanelViewModel();
        panel.ShowMetadata([first, extreme]);

        Assert.False(await panel.OffsetDateTakenAsync(
            upperBound ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(-1)));
        Assert.Equal(original, first.DateTaken);
        Assert.False(File.Exists(Path.ChangeExtension(path, ".xmp")));
    }

    public void Dispose()
    {
        foreach (var name in new[] { "cache.db", "uninitialized.db" })
        {
            var path = Path.Combine(_directory, name);
            if (!File.Exists(path)) continue;
            using var database = new CacheDatabase(path);
            using var connection = database.CreateConnection();
            SqliteConnection.ClearPool(connection);
        }
        Directory.Delete(_directory, recursive: true);
    }
}
