// Copyright (c) Duncan Lawler. All Rights Reserved.

using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.ML.Services;
using PhotoLibrarian.ViewModels;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class WatchedFolderRemovalTests
{
    [Fact]
    public async Task BackgroundTagRefresh_DoesNotRebuildFolderGridOrLoseSelection()
    {
        await using var library = await Library.CreateAsync();
        await library.WatchAsync(library.Photos);
        var image = await library.AddVideoAsync(library.Photos);
        await library.Main.ImageGrid.FilterByFolderAsync(library.Photos);
        var selected = Assert.Single(library.Main.ImageGrid.Images);
        library.Main.ImageGrid.UpdateSelection([selected], selected);
        var resultChanges = 0;
        library.Main.ImageGrid.ResultsChanged += (_, _) => resultChanges++;

        await library.Tags.AddTagAsync(new ImageTag { ImageId = image.Id, Tag = "Travel" });
        await library.Main.RefreshTagsTreeAsync();
        await library.Main.RefreshTagsTreeAsync();

        Assert.Equal(0, resultChanges);
        Assert.Same(selected, Assert.Single(library.Main.ImageGrid.Images));
        Assert.Same(selected, library.Main.ImageGrid.SelectedImage);
        Assert.Same(selected, Assert.Single(library.Main.ImageGrid.SelectedImages));
    }

    [Fact]
    public async Task BackgroundTagRefresh_UpdatesTagFilteredResultsAndPreservesSelection()
    {
        await using var library = await Library.CreateAsync();
        await library.WatchAsync(library.Photos);
        var first = await library.AddVideoAsync(library.Photos);
        var second = await library.AddVideoAsync(library.Photos, "other.mp4");
        await library.Tags.AddTagAsync(new ImageTag { ImageId = first.Id, Tag = "Travel" });
        await library.Main.ImageGrid.FilterByMultipleCriteriaAsync(
            null, false, null, null, false, null, true, null);
        var selected = Assert.Single(library.Main.ImageGrid.Images);
        library.Main.ImageGrid.UpdateSelection([selected], selected);

        await library.Tags.AddTagAsync(new ImageTag { ImageId = second.Id, Tag = "Travel" });
        await library.Main.RefreshTagsTreeAsync();

        Assert.Equal(2, library.Main.ImageGrid.Images.Count);
        Assert.Equal(first.Id, library.Main.ImageGrid.SelectedImage?.Entry.Id);
        Assert.Equal(first.Id, Assert.Single(library.Main.ImageGrid.SelectedImages).Entry.Id);
    }

    [Fact]
    public async Task RemovingCheckedRoot_RefreshesGridSelectionViewerAndCountsWithoutChangingFiles()
    {
        await using var library = await Library.CreateAsync();
        var folder = await library.WatchAsync(library.Photos);
        var image = await library.AddVideoAsync(library.Photos);
        await library.Tags.AddTagAsync(new ImageTag { ImageId = image.Id, Tag = "Travel" });
        var sidecar = Path.ChangeExtension(image.FilePath, ".xmp");
        await File.WriteAllTextAsync(sidecar, "portable metadata");
        var originalBytes = await File.ReadAllBytesAsync(image.FilePath);
        var originalTimestamp = File.GetLastWriteTimeUtc(image.FilePath);
        var sidecarTimestamp = File.GetLastWriteTimeUtc(sidecar);
        await library.Main.ImageGrid.FilterByFolderAsync(folder.Path);
        Assert.Single(library.Main.ImageGrid.Images);
        library.Main.ImageGrid.UpdateSelection(
            library.Main.ImageGrid.Images.ToList(), library.Main.ImageGrid.Images[0]);
        library.Main.ImageViewer.OpenImage(image, library.Main.ImageGrid.GetViewerImages());
        var refreshes = 0;
        library.Main.ImageGrid.ResultsChanged += (_, _) => refreshes++;

        await library.Main.FolderNav.RemoveWatchedFolderAsync(folder);

        Assert.Empty(library.Main.FolderNav.RootFolders);
        Assert.Empty(library.Main.ImageGrid.Images);
        Assert.Empty(library.Main.ImageGrid.GetViewerImages());
        Assert.Empty(library.Main.ImageGrid.SelectedImages);
        Assert.Null(library.Main.ImageGrid.SelectedImage);
        Assert.False(library.Main.ImageViewer.IsOpen);
        Assert.Equal(0, library.Main.TotalImages);
        Assert.Equal(0, library.Main.FlagNav.Count);
        Assert.Equal(0, library.Main.NotTaggedNav.Count);
        Assert.Empty(library.Main.TagNav.RootTags.Single().Children);
        Assert.True(refreshes > 0);
        Assert.Equal(image.Id, (await library.Images.GetByPathAsync(image.FilePath))!.Id);
        Assert.Single(await library.Tags.GetTagsAsync(image.Id));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(image.FilePath));
        Assert.Equal("portable metadata", await File.ReadAllTextAsync(sidecar));
        Assert.Equal(originalTimestamp, File.GetLastWriteTimeUtc(image.FilePath));
        Assert.Equal(sidecarTimestamp, File.GetLastWriteTimeUtc(sidecar));

        // A stale filter, a late index write, and an unindexed file must not reintroduce the root.
        await library.AddVideoAsync(library.Photos, "late.mp4");
        await File.WriteAllTextAsync(Path.Combine(library.Photos, "unindexed.mp4"), "unindexed");
        await library.Main.RefreshAfterIndexAsync();
        Assert.Empty(library.Main.ImageGrid.Images);
        Assert.Equal(0, library.Main.TotalImages);
        Assert.Empty(await library.Main.ImageGrid.GetSearchSuggestionsAsync("holiday", default));

        await library.WatchAsync(library.Photos);
        await library.Main.ImageGrid.FilterByFolderAsync(library.Photos);
        Assert.Contains(library.Main.ImageGrid.Images, item => item.Entry.Id == image.Id);
        Assert.Equal(2, await library.Images.GetCountAsync(watchedOnly: true));
        Assert.Single(await library.Tags.GetTagsAsync(image.Id));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task OverlappingRoots_PreserveCoverageAccordingToRemainingRoot(
        bool removeParent, bool parentRecursive)
    {
        await using var library = await Library.CreateAsync();
        var childPath = Path.Combine(library.Photos, "nested");
        var parent = await library.WatchAsync(library.Photos, parentRecursive);
        var child = await library.WatchAsync(childPath);
        var parentImage = await library.AddVideoAsync(library.Photos);
        var childImage = await library.AddVideoAsync(childPath);
        await library.Main.ImageGrid.FilterByFolderAsync(library.Photos);

        await library.Main.FolderNav.RemoveWatchedFolderAsync(removeParent ? parent : child);

        var visible = await library.Images.GetAllAsync(watchedOnly: true);
        Assert.Equal(removeParent ? 1 : parentRecursive ? 2 : 1, visible.Count);
        Assert.Equal(!removeParent, visible.Any(image => image.Id == parentImage.Id));
        Assert.Equal(removeParent || parentRecursive, visible.Any(image => image.Id == childImage.Id));
        Assert.Equal(visible.Count, library.Main.TotalImages);
        Assert.Equal(visible.Count, library.Main.ImageGrid.Images.Count);
        Assert.Equal(2, await library.Images.GetCountAsync());
    }

    [Fact]
    public async Task RemovalDuringSearch_PreservesSearchAndRefinementOnRemainingRoot()
    {
        await using var library = await Library.CreateAsync();
        var removed = await library.WatchAsync(library.Photos);
        var remainingPath = Path.Combine(library.Root, "photos-sibling_%");
        await library.WatchAsync(remainingPath);
        await library.AddVideoAsync(library.Photos);
        var remaining = await library.AddVideoAsync(remainingPath);
        await library.Main.ImageGrid.FilterByMultipleCriteriaAsync(
            [library.Photos, remainingPath], false, null, null, false, null, false, null);
        var refinement = new ImageRefinementFilter { Rating = 4, MediaKind = MediaKindFilter.Videos };
        await library.Main.ImageGrid.ApplyRefinementAsync(refinement);
        await library.Main.ImageGrid.ApplySearchAsync("holiday");
        Assert.Equal(2, library.Main.ImageGrid.Images.Count);
        library.Main.ImageViewer.OpenImage(remaining, library.Main.ImageGrid.GetViewerImages());

        await library.Main.FolderNav.RemoveWatchedFolderAsync(removed);

        Assert.Equal(remaining.Id, Assert.Single(library.Main.ImageGrid.Images).Entry.Id);
        Assert.Same(refinement, library.Main.ImageGrid.Refinement);
        Assert.True(library.Main.ImageViewer.IsOpen);
        Assert.Equal(remaining.FilePath, library.Main.ImageViewer.CurrentEntry!.FilePath);
        Assert.Equal(remaining.Id, Assert.Single(await library.Main.ImageGrid
            .GetSearchSuggestionsAsync("holiday", default)).Id);
        await library.Main.ImageGrid.ApplySearchAsync("nomatch");
        Assert.Empty(library.Main.ImageGrid.Images);
        await library.Main.ImageGrid.ApplySearchAsync("");
        Assert.Equal(remaining.Id, Assert.Single(library.Main.ImageGrid.Images).Entry.Id);
    }

    [Fact]
    public async Task OfflineRoot_RemovalAndReaddingDoNotRequireDiskOrDiscardCachedMetadata()
    {
        await using var library = await Library.CreateAsync();
        var root = await library.WatchAsync(library.Photos);
        var image = await library.AddVideoAsync(library.Photos);
        var unavailable = Path.Combine(library.Root, "offline");
        Directory.Move(library.Photos, unavailable);
        await library.Main.ImageGrid.FilterByFolderAsync(library.Photos);
        Assert.Single(library.Main.ImageGrid.Images);

        await library.Main.FolderNav.RemoveWatchedFolderAsync(root);
        Assert.Empty(library.Main.ImageGrid.Images);
        await library.WatchAsync(library.Photos, createDirectory: false);
        await library.Main.ImageGrid.FilterByFolderAsync(library.Photos);

        Assert.Equal(image.Id, Assert.Single(library.Main.ImageGrid.Images).Entry.Id);
        Assert.True((await library.Images.GetByPathAsync(image.FilePath))!.IsFlagged);
        Assert.True(File.Exists(Path.Combine(unavailable, image.FileName)));
    }

    [Fact]
    public async Task OverlappingRoots_CountTagsPeopleAndFlagsOnlyOnceAndRetainCachedAssignments()
    {
        await using var library = await Library.CreateAsync();
        await library.WatchAsync(library.Root);
        var child = await library.WatchAsync(library.Photos);
        var image = await library.AddVideoAsync(library.Photos);
        await library.Tags.AddTagAsync(new ImageTag { ImageId = image.Id, Tag = "Travel" });
        await library.AddPersonAsync(image.Id);
        await library.Main.ImageGrid.FilterByFolderAsync(library.Photos);
        await library.Main.RefreshAfterIndexAsync();
        Assert.Equal(1, library.Main.TotalImages);
        Assert.Equal(1, library.Main.PeopleNav.RootNodes.Single().Count);
        Assert.Equal(1, library.Main.FlagNav.Count);

        await library.Main.FolderNav.RemoveWatchedFolderAsync(child);
        Assert.Equal(1, library.Main.TotalImages);
        Assert.Equal(1, library.Main.PeopleNav.RootNodes.Single().Count);
        Assert.Equal(1, library.Main.TagNav.RootTags.Single().Count);
        Assert.Equal(1, library.Main.FlagNav.Count);
        await library.Main.FolderNav.RemoveWatchedFolderAsync(library.Main.FolderNav.RootFolders.Single());
        Assert.Equal(0, library.Main.PeopleNav.RootNodes.Single().Count);
        Assert.Equal(0, library.Main.FlagNav.Count);
        Assert.True((await library.Images.GetByPathAsync(image.FilePath))!.IsFlagged);
        await library.WatchAsync(library.Photos);
        await library.Main.RefreshAfterIndexAsync();
        Assert.Equal(1, library.Main.PeopleNav.RootNodes.Single().Count);
        Assert.Equal(1, library.Main.FlagNav.Count);
        Assert.Equal(1, library.Main.TagNav.RootTags.Single().Count);
    }

    [Theory]
    [InlineData(@"C:\photos\a.jpg", @"C:\photos\", true, true)]
    [InlineData(@"C:\PHOTOS\a.jpg", @"c:\photos", false, true)]
    [InlineData(@"C:\photos-other\a.jpg", @"C:\photos", true, false)]
    [InlineData(@"C:\photos\nested\a.jpg", @"C:\photos", false, false)]
    [InlineData(@"C:\photos_%\a.jpg", @"C:\photos_%", false, true)]
    [InlineData(@"C:\a.jpg", @"C:\", false, true)]
    [InlineData(@"\\server\share\nested\a.jpg", @"\\server\share\", true, true)]
    public void Membership_UsesPathBoundariesNotSqlWildcards(
        string file, string root, bool recursive, bool expected) =>
        Assert.Equal(expected, WatchedFolderScope.ContainsFile(file, root, recursive));

    private sealed class Library : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-watch-{Guid.NewGuid():N}");
        public string Photos => Path.Combine(Root, "photos");
        private readonly CacheDatabase _database;
        public ImageRepository Images { get; }
        public TagRepository Tags { get; }
        public MainViewModel Main { get; }

        private Library()
        {
            _database = new CacheDatabase(Path.Combine(Root, "cache.db"));
            Images = new ImageRepository(_database);
            Tags = new TagRepository(_database);
            var faces = new FaceRepository(_database);
            var scanner = new FolderScannerService();
            var reader = new MetadataReaderService();
            var metadata = new FaceMetadataStore();
            var indexer = new LibraryIndexingService(_database, Images, Tags, faces, scanner, reader, metadata);
            var sessions = new OnnxSessionManager(Path.Combine(Root, "models"));
            var models = new AutoTagModelManager(sessions);
            var tagger = new AutoTaggingService(sessions, models);
            var activity = new UserActivityGate();
            var pipeline = new RecognitionPipeline(faces, Tags, null!, null!, null!, models, tagger, activity, null!);
            Main = new MainViewModel(_database, Images, Tags, faces, scanner, reader, indexer,
                new OriginalBackupService(Path.Combine(Root, "backups")), pipeline,
                new FaceReviewService(faces, Images, new FaceClusteringService(),
                    new FaceRecognitionService(), metadata), sessions,
                new AutoTaggingSettingsStore(Path.Combine(Root, "settings.json")), models,
                new AutoTagBenchmarkProcessor(models, tagger),
                new SelectedPhotoAutoTagReviewProcessor(models, tagger), activity);
        }

        public static async Task<Library> CreateAsync()
        {
            var library = new Library();
            await library._database.InitializeAsync();
            await library.Main.PauseFaceDetectionAsync();
            return library;
        }

        public async Task<FolderNode> WatchAsync(string path, bool recursive = true, bool createDirectory = true)
        {
            if (createDirectory)
                Directory.CreateDirectory(path);
            using var connection = _database.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO watched_folders(path, include_sub) VALUES ($path, $sub)";
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$sub", recursive);
            await command.ExecuteNonQueryAsync();
            await Main.FolderNav.LoadWatchedFoldersAsync();
            return Main.FolderNav.RootFolders.Single(folder => folder.Path == path);
        }

        public async Task<ImageEntry> AddVideoAsync(string directory, string name = "holiday.mp4")
        {
            var path = Path.Combine(directory, name);
            await File.WriteAllTextAsync(path, "original video");
            var image = new ImageEntry
            {
                FilePath = path, FileName = name, MediaType = MediaType.Video,
                DateModified = File.GetLastWriteTimeUtc(path), DateTaken = DateTime.UtcNow,
                IsFlagged = true, Rating = 5
            };
            image.Id = await Images.UpsertImageAsync(image);
            return image;
        }

        public async Task AddPersonAsync(long imageId)
        {
            using var connection = _database.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO persons (id, name) VALUES (1, 'Person');
                INSERT INTO face_regions (image_id, x, y, width, height, person_id, confidence)
                VALUES ($image, 0.1, 0.1, 0.2, 0.2, 1, 1.0);
                """;
            command.Parameters.AddWithValue("$image", imageId);
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Main.ImageViewer.CloseCommand.Execute(null);
            await Main.CleanupAsync();
            _database.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }
}
