using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class BatchRenameTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-{Guid.NewGuid():N}");

    public BatchRenameTests() => Directory.CreateDirectory(_directory);

    private ImageEntry Photo(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, name);
        return new ImageEntry
        {
            FilePath = path, FileName = name,
            DateTaken = new DateTime(2024, 3, 9, 14, 5, 6),
            DateModified = new DateTime(2023, 2, 8, 13, 4, 5)
        };
    }

    [Fact]
    public void Preview_ExpandsTokensAndKeepsExtensionsAndSelectionOrder()
    {
        var first = Photo("first.JPG");
        var second = Photo("second.cr3");
        second.DateTaken = null;
        var result = BatchRenameService.Preview([first, second],
            "{yyyy}-{MM}-{dd}_{HH}-{mm}-{ss}_{original}_{####}");

        Assert.Equal("2024-03-09_14-05-06_first_0001.JPG", result[0].NewName);
        Assert.Equal("2023-02-08_13-04-05_second_0002.cr3", result[1].NewName);
        Assert.All(result, p => Assert.Null(p.Error));
        Assert.True(File.Exists(first.FilePath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("bad\\name")]
    [InlineData("bad:name")]
    [InlineData("CON")]
    [InlineData("LPT1.more")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("{unknown}")]
    [InlineData("{yyyy")]
    [InlineData("{##########}")]
    public void Preview_RejectsInvalidPatterns(string pattern)
    {
        Assert.NotNull(BatchRenameService.Preview([Photo("source.jpg")], pattern)[0].Error);
    }

    [Fact]
    public void Preview_DetectsDuplicateTargetsCaseInsensitively()
    {
        var result = BatchRenameService.Preview([Photo("one.jpg"), Photo("two.JPG")], "same");
        Assert.All(result, p => Assert.NotNull(p.Error));
    }

    [Fact]
    public void Preview_DetectsDuplicateRawSidecarTargets()
    {
        var result = BatchRenameService.Preview([Photo("one.cr3"), Photo("two.nef")], "same");
        Assert.All(result, p => Assert.NotNull(p.Error));
    }

    [Theory]
    [InlineData("target.jpg", false)]
    [InlineData("target.jpg.xmp", false)]
    [InlineData("target.jpg", true)]
    [InlineData("target.jpg.xmp", true)]
    public void Preview_DetectsExistingPhotoSidecarAndDirectory(string target, bool directory)
    {
        var photo = Photo("source.jpg");
        if (directory) Directory.CreateDirectory(Path.Combine(_directory, target));
        else File.WriteAllText(Path.Combine(_directory, target), "do not overwrite");
        Assert.NotNull(BatchRenameService.Preview([photo], "target")[0].Error);
    }

    [Fact]
    public void Preview_AllowsUnchangedNameAndDifferentFolders()
    {
        var photo = Photo("source.jpg");
        Assert.Null(BatchRenameService.Preview([photo], "{original}")[0].Error);
        var otherDirectory = Path.Combine(_directory, "other");
        Directory.CreateDirectory(otherDirectory);
        var other = new ImageEntry { FilePath = Path.Combine(otherDirectory, "second.jpg"), FileName = "second.jpg" };
        File.WriteAllText(other.FilePath, "second");
        Assert.All(BatchRenameService.Preview([photo, other], "same"), p => Assert.Null(p.Error));
    }

    [Fact]
    public void Preview_PreservesBracesInOriginalNames()
    {
        var result = BatchRenameService.Preview([Photo("source{test}.jpg")], "{original}_{##}");
        Assert.Null(result[0].Error);
        Assert.Equal("source{test}_01.jpg", result[0].NewName);
    }

    [Fact]
    public void Preview_RejectsSharedSourceSidecar()
    {
        var first = Photo("source.cr3");
        var second = Photo("source.nef");
        File.WriteAllText(Path.ChangeExtension(first.FilePath, ".xmp"), "shared");
        Assert.All(BatchRenameService.Preview([first, second], "Trip_{##}"),
            p => Assert.NotNull(p.Error));
    }

    [Theory]
    [InlineData(".jpg", ".jpg.xmp")]
    [InlineData(".cr3", ".xmp")]
    public async Task Rename_MovesSidecarsAndUpdatesSameDatabaseRows(string extension, string sidecarExtension)
    {
        using var database = new CacheDatabase(Path.Combine(_directory, "cache.db"));
        await database.InitializeAsync();
        var repository = new ImageRepository(database);
        var entries = new[] { Photo("first" + extension), Photo("second" + extension) };
        foreach (var entry in entries)
        {
            entry.Id = await repository.UpsertImageAsync(entry);
            File.WriteAllText(FaceMetadataStore.GetSidecarPathForImage(entry.FilePath), entry.FileName + " metadata");
        }
        var preview = BatchRenameService.Preview(entries, "Trip_{####}");
        await new BatchRenameService(repository).RenameAsync(preview);

        for (var index = 0; index < entries.Length; index++)
        {
            var path = Path.Combine(_directory, $"Trip_{index + 1:0000}{extension}");
            var sidecarPath = Path.Combine(_directory, $"Trip_{index + 1:0000}{sidecarExtension}");
            Assert.Equal(preview[index].OriginalName, File.ReadAllText(path));
            Assert.Equal(preview[index].OriginalName + " metadata", File.ReadAllText(sidecarPath));
            Assert.False(File.Exists(preview[index].SourcePath));
            Assert.False(File.Exists(preview[index].SourceSidecar));
            Assert.Null(await repository.GetByPathAsync(preview[index].SourcePath));
            var stored = await repository.GetByPathAsync(path);
            Assert.NotNull(stored);
            Assert.Equal(entries[index].Id, stored.Id);
            Assert.Equal(sidecarPath, stored.FaceSidecarPath);
            Assert.Equal(Path.GetFileName(path), stored.FileName);
        }
    }

    [Fact]
    public async Task Rename_RechecksCollisionsBeforeMovingAnyFiles()
    {
        using var database = new CacheDatabase(Path.Combine(_directory, "cache.db"));
        await database.InitializeAsync();
        var first = Photo("first.jpg");
        var second = Photo("second.jpg");
        var preview = BatchRenameService.Preview([first, second], "Trip_{##}");
        File.WriteAllText(preview[1].DestinationSidecar, "created after preview");

        await Assert.ThrowsAsync<IOException>(() => new BatchRenameService(new(database)).RenameAsync(preview));
        Assert.True(File.Exists(first.FilePath));
        Assert.True(File.Exists(second.FilePath));
        Assert.False(File.Exists(preview[0].DestinationPath));
        Assert.Equal("created after preview", File.ReadAllText(preview[1].DestinationSidecar));
    }

    [Fact]
    public async Task Rename_RestoresPhotoAndSidecarWhenDatabaseUpdateFails()
    {
        using var database = new CacheDatabase(Path.Combine(_directory, "cache.db"));
        await database.InitializeAsync();
        var repository = new ImageRepository(database);
        var photo = Photo("source.cr3");
        photo.Id = await repository.UpsertImageAsync(photo);
        var oldPath = photo.FilePath;
        var sidecar = FaceMetadataStore.GetSidecarPathForImage(oldPath);
        File.WriteAllText(sidecar, "metadata");
        var preview = BatchRenameService.Preview([photo], "renamed");
        using (var connection = database.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER fail_path_update BEFORE UPDATE OF file_path ON images
                BEGIN SELECT RAISE(ABORT, 'test failure'); END;
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await Assert.ThrowsAsync<IOException>(() => new BatchRenameService(repository).RenameAsync(preview));
        Assert.Equal(oldPath, photo.FilePath);
        Assert.True(File.Exists(oldPath));
        Assert.Equal("metadata", File.ReadAllText(sidecar));
        Assert.False(File.Exists(preview[0].DestinationPath));
        Assert.NotNull(await repository.GetByPathAsync(oldPath));
    }

    [Fact]
    public async Task Rename_RestoresPhotoWhenSidecarMoveFails()
    {
        using var database = new CacheDatabase(Path.Combine(_directory, "cache.db"));
        await database.InitializeAsync();
        var repository = new ImageRepository(database);
        var photo = Photo("source.jpg");
        photo.Id = await repository.UpsertImageAsync(photo);
        var oldPath = photo.FilePath;
        var sidecar = oldPath + ".xmp";
        File.WriteAllText(sidecar, "metadata");
        var preview = BatchRenameService.Preview([photo], "renamed");
        using (var locked = new FileStream(sidecar, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => new BatchRenameService(repository).RenameAsync(preview));
        }
        Assert.Equal(oldPath, photo.FilePath);
        Assert.True(File.Exists(oldPath));
        Assert.Equal("metadata", File.ReadAllText(sidecar));
        Assert.False(File.Exists(preview[0].DestinationPath));
        Assert.NotNull(await repository.GetByPathAsync(oldPath));
    }

    [Fact]
    public async Task Rename_ReportsCompletedCountAndStopsOnFailure()
    {
        using var database = new CacheDatabase(Path.Combine(_directory, "cache.db"));
        await database.InitializeAsync();
        var repository = new ImageRepository(database);
        var entries = new[] { Photo("first.jpg"), Photo("second.jpg"), Photo("third.jpg") };
        foreach (var entry in entries) entry.Id = await repository.UpsertImageAsync(entry);
        var preview = BatchRenameService.Preview(entries, "Trip_{##}");
        using (var connection = database.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER fail_second_path_update BEFORE UPDATE OF file_path ON images
                WHEN NEW.file_name = 'Trip_02.jpg'
                BEGIN SELECT RAISE(ABORT, 'test failure'); END;
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var error = await Assert.ThrowsAsync<IOException>(() => new BatchRenameService(repository).RenameAsync(preview));
        Assert.Contains("after 1 files", error.Message);
        Assert.True(File.Exists(preview[0].DestinationPath));
        Assert.NotNull(await repository.GetByPathAsync(preview[0].DestinationPath));
        Assert.True(File.Exists(preview[1].SourcePath));
        Assert.NotNull(await repository.GetByPathAsync(preview[1].SourcePath));
        Assert.True(File.Exists(preview[2].SourcePath));
        Assert.NotNull(await repository.GetByPathAsync(preview[2].SourcePath));
        Assert.False(File.Exists(preview[1].DestinationPath));
        Assert.False(File.Exists(preview[2].DestinationPath));
    }

    [Fact]
    public async Task SingleRename_UsesSameSidecarSafeOperation()
    {
        using var database = new CacheDatabase(Path.Combine(_directory, "cache.db"));
        await database.InitializeAsync();
        var repository = new ImageRepository(database);
        var photo = Photo("source.jpg");
        photo.Id = await repository.UpsertImageAsync(photo);
        File.WriteAllText(photo.FilePath + ".xmp", "metadata");
        var operations = new PhotoOperationsService(repository, new OriginalBackupService(Path.Combine(_directory, "backups")));

        Assert.Equal(Path.Combine(_directory, "renamed.jpg"), await operations.RenameAsync(photo, "renamed"));
        Assert.Equal("metadata", File.ReadAllText(photo.FilePath + ".xmp"));
        Assert.Equal(photo.Id, (await repository.GetByPathAsync(photo.FilePath))!.Id);
        Assert.Null(await operations.RenameAsync(photo, "..\\escape.jpg"));
        Assert.Null(await operations.RenameAsync(photo, ""));
        Assert.Null(await operations.RenameAsync(photo, " "));
    }

    [Fact]
    public async Task SingleRename_KeepsExplicitExtensionAndUsesDestinationSidecarConvention()
    {
        using var database = new CacheDatabase(Path.Combine(_directory, "cache.db"));
        await database.InitializeAsync();
        var repository = new ImageRepository(database);
        var photo = Photo("source.tiff");
        var sourceSidecar = FaceMetadataStore.GetSidecarPathForImage(photo.FilePath);
        File.WriteAllText(sourceSidecar, "metadata");
        var operations = new PhotoOperationsService(repository, new OriginalBackupService(Path.Combine(_directory, "backups")));

        Assert.Equal(Path.Combine(_directory, "renamed.png"), await operations.RenameAsync(photo, "renamed.png"));
        Assert.Equal("metadata", File.ReadAllText(FaceMetadataStore.GetSidecarPathForImage(photo.FilePath)));
        Assert.False(File.Exists(sourceSidecar));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }
}
