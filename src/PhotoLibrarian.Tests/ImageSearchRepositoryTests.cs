using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using Xunit;
using XmpCore;

namespace PhotoLibrarian.Tests;

public sealed class ImageSearchRepositoryTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"PhotoSearch-{Guid.NewGuid():N}");
    private CacheDatabase _database = null!;
    private ImageRepository _images = null!;
    private ImageSearchRepository _search = null!;
    private TagRepository _tags = null!;
    private FaceRepository _faces = null!;

    public async ValueTask InitializeAsync()
    {
        _database = new CacheDatabase(Path.Combine(_directory, "cache.db"));
        await _database.InitializeAsync();
        _images = new(_database);
        _search = new(_database);
        _tags = new(_database);
        _faces = new(_database);
    }

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Search_MatchesEveryFieldAndCombinesWordPrefixesAcrossFields()
    {
        var image = await AddImageAsync("Vienna_2026.jpg", "Sunset beside the river", "Holiday memories");
        image.CameraMake = "Canon";
        image.CameraModel = "EOS R5";
        await _images.UpsertImageAsync(image);
        await _tags.AddTagAsync(new ImageTag { ImageId = image.Id, Tag = "Travel/Austria" });
        var person = await _faces.CreatePersonAsync("Renée Smith");
        await _faces.AddFaceRegionAsync(new FaceRegion
        {
            ImageId = image.Id, PersonId = person, PersonName = "Renée Smith",
            Width = 0.2, Height = 0.2
        });
        await AddImageAsync("unrelated.jpg");

        foreach (var query in new[] { "vien", "SUNSET", "holiday", "aust", "canon", "eos",
                     "r5", "renee", "smi", "vien river austr canon renee" })
            Assert.Equal(image.Id, Assert.Single(await _search.SearchAsync(query)).Id);
        Assert.Empty(await _search.SearchAsync("vien missing"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\" : - * ( ) { } / \\")]
    [InlineData("filename:OR NOT NEAR AND")]
    [InlineData("' OR 1=1; DROP TABLE images; --")]
    public async Task Search_TreatsInputLiterallyWithoutFtsSyntaxErrors(string query)
    {
        var image = await AddImageAsync("photo.jpg");
        Assert.Empty(await _search.SearchAsync(query));
        Assert.Equal(image.Id, Assert.Single(await _images.GetAllAsync()).Id);
    }

    [Fact]
    public async Task Search_HandlesPunctuatedFilenamesAndLiteralOperatorWords()
    {
        var image = await AddImageAsync("Mom's-trip_01.jpg", "near AND far");
        Assert.Equal(image.Id, Assert.Single(await _search.SearchAsync("Mom's-trip_01.jpg")).Id);
        Assert.Equal(image.Id, Assert.Single(await _search.SearchAsync("near AND")).Id);
    }

    [Fact]
    public async Task ImageUpdatesAndDeletion_RefreshSearchWithoutStaleTerms()
    {
        var image = await AddImageAsync("before.jpg", "original", "initial");
        image.Caption = "replacement";
        image.Title = "changed";
        image.CameraMake = "Nikon";
        await _images.UpsertImageAsync(image);
        await _images.UpdatePathAsync(image.Id, Path.Combine(_directory, "after.jpg"), sidecarPath: null);
        Assert.Empty(await _search.SearchAsync("before"));
        Assert.Empty(await _search.SearchAsync("original"));
        Assert.Empty(await _search.SearchAsync("initial"));
        Assert.Equal(image.Id, Assert.Single(await _search.SearchAsync("after replacement changed nikon")).Id);

        await _images.UpdateCaptionAsync(image.Id, "edited");
        Assert.Empty(await _search.SearchAsync("replacement"));
        Assert.Equal("edited", (await _images.GetByPathAsync(Path.Combine(_directory, "after.jpg")))!.Caption);
        Assert.Single(await _search.SearchAsync("edited"));
        await _images.DeleteByPathAsync(Path.Combine(_directory, "after.jpg"));
        Assert.Empty(await _search.SearchAsync("edited"));
        Assert.Empty(await _search.SearchAsync("after"));
    }

    [Fact]
    public async Task TagChangesAndCascadeDeletion_KeepIndexCurrent()
    {
        var image = await AddImageAsync("photo.jpg");
        await _tags.AddTagAsync(new ImageTag { ImageId = image.Id, Tag = "mountains" });
        Assert.Single(await _search.SearchAsync("mount"));
        await _tags.RenameTagAsync("mountains", "beaches");
        Assert.Empty(await _search.SearchAsync("mount"));
        Assert.Single(await _search.SearchAsync("beach"));
        await _tags.RemoveTagAsync(image.Id, "beaches");
        Assert.Empty(await _search.SearchAsync("beach"));
        await _tags.AddTagAsync(new ImageTag { ImageId = image.Id, Tag = "water" });
        await _images.DeleteByPathAsync(image.FilePath);
        Assert.Empty(await _search.SearchAsync("water"));
        using var connection = _database.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM image_search";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task FaceAssignmentRenameAndRemoval_RefreshBothOldAndNewImage()
    {
        var first = await AddImageAsync("first.jpg");
        var second = await AddImageAsync("second.jpg");
        var person = await _faces.CreatePersonAsync("Alice");
        var face = await _faces.AddFaceRegionAsync(new FaceRegion
        {
            ImageId = first.Id, PersonName = "Visitor", Width = 0.2, Height = 0.2
        });
        Assert.Equal(first.Id, Assert.Single(await _search.SearchAsync("Visitor")).Id);
        await _faces.UpdatePersonAsync(face, person, "Alice");
        Assert.Empty(await _search.SearchAsync("Visitor"));
        Assert.Single(await _search.SearchAsync("Alice"));
        await _faces.RenamePersonAsync(person, "Beatrice");
        Assert.Empty(await _search.SearchAsync("Alice"));
        Assert.Single(await _search.SearchAsync("Beatrice"));

        using (var connection = _database.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE face_regions SET image_id = $image WHERE id = $face";
            command.Parameters.AddWithValue("$image", second.Id);
            command.Parameters.AddWithValue("$face", face);
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(second.Id, Assert.Single(await _search.SearchAsync("Beatrice")).Id);
        await _faces.DeleteFaceRegionAsync(face);
        Assert.Empty(await _search.SearchAsync("Beatrice"));
    }

    [Fact]
    public async Task RolledBackWrites_DoNotLeakIntoSearch()
    {
        var image = await AddImageAsync("photo.jpg", "original");
        using (var connection = _database.CreateConnection())
        using (var transaction = connection.BeginTransaction())
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE images SET caption = 'uncommitted' WHERE id = $id";
            command.Parameters.AddWithValue("$id", image.Id);
            await command.ExecuteNonQueryAsync();
            transaction.Rollback();
        }
        Assert.Empty(await _search.SearchAsync("uncommitted"));
        Assert.Single(await _search.SearchAsync("original"));
    }

    [Fact]
    public async Task Initialization_BackfillsExistingRowsAndDoesNotDuplicateOnRestart()
    {
        var image = await AddImageAsync("legacy.jpg", "historic");
        await _tags.AddTagAsync(new ImageTag { ImageId = image.Id, Tag = "archive" });
        using (var connection = _database.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TABLE image_search";
            await command.ExecuteNonQueryAsync();
        }
        _database.Dispose();
        await _database.InitializeAsync();
        Assert.Equal(image.Id, Assert.Single(await _search.SearchAsync("legacy historic archive")).Id);
        _database.Dispose();
        await _database.InitializeAsync();
        Assert.Single(await _search.SearchAsync("legacy"));
    }

    [Fact]
    public async Task Suggestions_AreBoundedWhileSubmittedSearchIsNot()
    {
        for (var i = 0; i < 12; i++)
            await AddImageAsync($"holiday_{i}.jpg");
        using (var connection = _database.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT group_concat(rowid || ':' || file_name) FROM image_search";
            var indexed = await command.ExecuteScalarAsync();
            Assert.Contains("holiday", indexed?.ToString() ?? "(null)");
            command.CommandText = "SELECT COUNT(*) FROM image_search WHERE image_search MATCH 'holiday*'";
            Assert.Equal(12L, await command.ExecuteScalarAsync());
        }
        Assert.Equal(8, (await _search.SearchAsync("holiday", 8)).Count);
        Assert.Equal(12, (await _search.SearchAsync("holiday")).Count);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _search.SearchAsync("holiday", 0));
    }

    [Fact]
    public async Task MetadataReader_IndexesCaptionFromRawSidecar()
    {
        var path = Path.Combine(_directory, "test.nef");
        await File.WriteAllBytesAsync(path, [0]);
        await MetadataWriterService.WriteCaptionAsync(path, "Sidecar holiday caption");
        var entry = new MetadataReaderService().ReadMetadata(path);
        Assert.Equal("Sidecar holiday caption", entry.Caption);
        entry.Id = await _images.UpsertImageAsync(entry);
        Assert.Equal(entry.Id, Assert.Single(await _search.SearchAsync("sidecar holiday")).Id);
    }

    [Fact]
    public async Task MetadataReader_IndexesDistinctSidecarTitleAndCaption()
    {
        var path = Path.Combine(_directory, "titled.nef");
        await File.WriteAllBytesAsync(path, [0], TestContext.Current.CancellationToken);
        var xmp = XmpMetaFactory.Create();
        const string dcNs = "http://purl.org/dc/elements/1.1/";
        xmp.SetLocalizedText(dcNs, "dc:title", "", "x-default", "Mountain summit");
        xmp.SetLocalizedText(dcNs, "dc:description", "", "x-default", "Alpine adventure");
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".xmp"),
            XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions()),
            TestContext.Current.CancellationToken);
        var entry = new MetadataReaderService().ReadMetadata(path);
        Assert.Equal("Mountain summit", entry.Title);
        Assert.Equal("Alpine adventure", entry.Caption);
        entry.Id = await _images.UpsertImageAsync(entry);
        Assert.Single(await _search.SearchAsync("summit adventure"));
    }

    [Fact]
    public async Task BackgroundIndexing_PopulatesLegacyCaptionEvenWhenFileIsUnchanged()
    {
        var path = Path.Combine(_directory, "caption.jpg");
        await WicTestImage.CreateAsync(path, 16, 12, jpeg: true);
        await MetadataWriterService.WriteCaptionAsync(path, "Embedded caption");
        using var scanner = new FolderScannerService();
        var indexer = new LibraryIndexingService(
            _database, _images, _tags, _faces, scanner,
            new MetadataReaderService(), new FaceMetadataStore());
        Assert.True(await indexer.IndexFileAsync(path));
        Assert.Single(await _search.SearchAsync("embedded caption"));

        using (var connection = _database.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE images SET caption = NULL, title = NULL, search_metadata_indexed = 0;
                """;
            await command.ExecuteNonQueryAsync();
        }
        Assert.Empty(await _search.SearchAsync("embedded"));
        Assert.True(await indexer.IndexFileAsync(path));
        Assert.Single(await _search.SearchAsync("embedded caption"));
        Assert.True((await _images.GetByPathAsync(path))!.SearchMetadataIndexed);
        Assert.False(await indexer.IndexFileAsync(path));
    }

    [Fact]
    public async Task Migration_AddsTextColumnsAndBackfillsOldSchema()
    {
        var image = await AddImageAsync("legacy.jpg");
        await _tags.AddTagAsync(new ImageTag { ImageId = image.Id, Tag = "oldtag" });
        using (var connection = _database.CreateConnection())
        {
            var triggerNames = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'trigger' AND name LIKE 'image_search_%'";
                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) triggerNames.Add(reader.GetString(0));
            }
            using var drop = connection.CreateCommand();
            foreach (var name in triggerNames)
            {
                drop.CommandText = $"DROP TRIGGER \"{name}\"";
                await drop.ExecuteNonQueryAsync();
            }
            drop.CommandText = """
                DROP VIEW image_search_documents;
                DROP TABLE image_search;
                ALTER TABLE images DROP COLUMN caption;
                ALTER TABLE images DROP COLUMN title;
                ALTER TABLE images DROP COLUMN search_metadata_indexed;
                """;
            await drop.ExecuteNonQueryAsync();
        }
        _database.Dispose();
        await _database.InitializeAsync();
        Assert.Equal(image.Id, Assert.Single(await _search.SearchAsync("legacy oldtag")).Id);
        Assert.False((await _images.GetByPathAsync(image.FilePath))!.SearchMetadataIndexed);
        await _images.UpdateCaptionAsync(image.Id, "upgraded");
        Assert.Single(await _search.SearchAsync("upgraded"));
    }

    private async Task<ImageEntry> AddImageAsync(string name, string? caption = null, string? title = null)
    {
        var image = new ImageEntry
        {
            FilePath = Path.Combine(_directory, name), FileName = name,
            Caption = caption, Title = title, DateModified = DateTime.UtcNow
        };
        image.Id = await _images.UpsertImageAsync(image);
        return image;
    }

    [Fact]
    public async Task Filter_SearchOnlyRestrictsNavigationUnionAndHeaderRefinements()
    {
        var folderMatch = await AddImageAsync("folder.jpg", "holiday");
        var personMatch = await AddImageAsync("person.jpg", "holiday");
        var excludedByHeader = await AddImageAsync("lowrating.jpg", "holiday");
        var outsideSelection = await AddImageAsync("outside.jpg", "holiday");
        var noTextMatch = await AddImageAsync("other.jpg", "work");
        folderMatch.Rating = 5;
        personMatch.Rating = 4;
        excludedByHeader.Rating = 1;
        noTextMatch.Rating = 5;
        var navigationUnion = new[] { folderMatch, personMatch, excludedByHeader, noTextMatch };
        var refinement = new ImageRefinementFilter { Rating = 4 };
        var scope = navigationUnion.Where(image => refinement.Matches(image)).ToList();

        var results = await _search.FilterAsync("holiday", scope);

        Assert.Equal(new[] { folderMatch.Id, personMatch.Id }, results.Select(image => image.Id));
        Assert.DoesNotContain(results, image => image.Id == excludedByHeader.Id || image.Id == outsideSelection.Id);
        Assert.Empty(await _search.FilterAsync("holiday", []));
    }

    [Fact]
    public async Task Filter_SuggestionsLimitAfterScopingAndPreserveGridOrderRatherThanRelevance()
    {
        for (var i = 0; i < 10; i++)
            await AddImageAsync($"outside-{i}.jpg", "holiday holiday holiday");
        var first = await AddImageAsync("z.jpg", "holiday");
        var second = await AddImageAsync("a.jpg", "holiday holiday");
        var third = await AddImageAsync("m.jpg", "holiday");
        var scope = new[] { first, second, third };

        Assert.Equal(new[] { first.Id, second.Id },
            (await _search.FilterAsync("holiday", scope, 2)).Select(image => image.Id));
        Assert.Equal(scope.Select(image => image.Id),
            (await _search.FilterAsync("holiday", scope)).Select(image => image.Id));
        Assert.Equal(new[] { third.Id, second.Id, first.Id },
            (await _search.FilterAsync("holiday", Enumerable.Reverse(scope).ToArray())).Select(image => image.Id));
    }

    [Fact]
    public async Task Filter_ChangingSearchOrScopeDoesNotNarrowAgainstPreviousSearchResults()
    {
        var holiday = await AddImageAsync("holiday.jpg");
        var work = await AddImageAsync("work.jpg");
        var scope = new[] { holiday, work };
        Assert.Equal(holiday.Id, Assert.Single(await _search.FilterAsync("holiday", scope)).Id);
        Assert.Equal(work.Id, Assert.Single(await _search.FilterAsync("work", scope)).Id);
        Assert.Empty(await _search.FilterAsync("holiday", [work]));
        Assert.Equal(holiday.Id, Assert.Single(await _search.FilterAsync("holiday", scope)).Id);
    }
}
