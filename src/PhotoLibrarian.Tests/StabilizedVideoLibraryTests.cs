// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.ML.Services;
using PhotoLibrarian.ViewModels;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class StabilizedVideoLibraryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PanoramaIsIndexedOpenedAndSelectedInTheGrid(bool filtered)
    {
        await using var library = await LibraryFixture.CreateAsync();
        var main = library.Main;
        var original = await library.AddVideoAsync("original.mp4");
        await main.ImageGrid.FilterByFolderAsync(library.Photos);
        if (filtered)
            await main.ImageGrid.ApplySearchAsync("onlyoriginal");
        var path = Path.Combine(library.Photos, "original - Panorama.jpg");
        await WicTestImage.CreateAsync(path, 120, 120, jpeg: true);

        await main.IndexPanoramaAsync(path);

        var indexed = await library.Images.GetByPathAsync(path);
        Assert.NotNull(indexed);
        Assert.True(indexed.Id > 0);
        Assert.True(main.ImageViewer.IsOpen);
        Assert.False(main.ImageViewer.IsVideo);
        Assert.Equal(indexed.Id, main.ImageViewer.CurrentEntry!.Id);
        Assert.Equal(path, main.ImageGrid.SelectedImage!.Entry.FilePath);
        Assert.Equal(path, main.MetadataPanel.FilePath);
        Assert.Contains(main.ImageGrid.DisplayedEntries, entry => entry.FilePath == path);
        await main.RefreshAfterIndexAsync();
        Assert.Equal(path, main.ImageViewer.CurrentEntry!.FilePath);
        main.ImageViewer.CloseCommand.Execute(null);
        Assert.Equal(path, Assert.Single(main.ImageGrid.SelectedImages).Entry.FilePath);
        Assert.Equal("original", File.ReadAllText(original.FilePath));
        if (filtered)
        {
            await main.ImageGrid.ApplySearchAsync("no-matching-file");
            Assert.Empty(main.ImageGrid.Images);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyIsIndexedRevealedAndSelectedAfterViewerCloses(bool outsideFolder)
    {
        await using var library = await LibraryFixture.CreateAsync();
        var main = library.Main;
        var original = await library.AddVideoAsync("original.mp4");
        await main.ImageGrid.FilterByFolderAsync(library.Photos);
        main.ImageViewer.OpenImage(original, main.ImageGrid.GetViewerImages());
        var outputFolder = outsideFolder ? Path.Combine(library.Root, "elsewhere") : library.Photos;
        Directory.CreateDirectory(outputFolder);
        var copyPath = Path.Combine(outputFolder, "original-stabilized.mp4");
        await File.WriteAllTextAsync(copyPath, "encoded copy");
        await MetadataWriterService.WriteCaptionAsync(copyPath, "Portable caption");
        if (outsideFolder)
            await main.ImageGrid.ApplySearchAsync("onlyoriginal");
        var revealRequests = 0;
        main.ImageGrid.SelectionRestoreRequested += (_, _) => revealRequests++;

        await main.OpenStabilizedVideoAsync(copyPath);

        var indexed = await library.Images.GetByPathAsync(copyPath);
        Assert.NotNull(indexed);
        Assert.True(indexed.Id > 0);
        Assert.Equal(indexed.Id, main.ImageViewer.CurrentEntry!.Id);
        Assert.Equal("Portable caption", main.ImageViewer.CurrentEntry.Caption);
        Assert.Equal(copyPath, main.ImageViewer.VideoPath);
        Assert.Equal(copyPath, main.ImageGrid.SelectedImage!.Entry.FilePath);
        Assert.Equal(outsideFolder ? 1 : 2, main.TotalImages); // Library count excludes the temporary outside-root reveal.
        Assert.True(revealRequests > 0);

        // A watched-folder refresh and a sort must retain the copy even outside the current search.
        await main.RefreshAfterIndexAsync();
        await main.ImageGrid.SortByNameCommand.ExecuteAsync(null);
        Assert.Equal(copyPath, main.ImageViewer.CurrentEntry!.FilePath);
        main.ImageViewer.CloseCommand.Execute(null);
        Assert.Equal(copyPath, Assert.Single(main.ImageGrid.SelectedImages).Entry.FilePath);
        Assert.Equal(copyPath, main.ImageGrid.SelectedImage!.Entry.FilePath);
        Assert.Contains(main.ImageGrid.DisplayedEntries, entry => entry.FilePath == copyPath);
        Assert.Equal("original", File.ReadAllText(original.FilePath));

        main.ImageViewer.OpenImage(main.ImageGrid.SelectedImage.Entry, main.ImageGrid.GetViewerImages());
        await main.ImageViewer.NextImageCommand.ExecuteAsync(null);
        Assert.Equal(original.FilePath, main.ImageViewer.CurrentEntry!.FilePath);
        await main.ImageViewer.PreviousImageCommand.ExecuteAsync(null);
        Assert.Equal(copyPath, main.ImageViewer.CurrentEntry!.FilePath);
        main.ImageViewer.CloseCommand.Execute(null);

        await main.ImageGrid.ApplySearchAsync("no-matching-file");
        Assert.Empty(main.ImageGrid.Images);
        Assert.Null(main.ImageGrid.SelectedImage);
    }

    [Fact]
    public async Task SupersededGroupingDuringReloadDoesNotCloseViewer()
    {
        await using var library = await LibraryFixture.CreateAsync();
        var main = library.Main;
        var original = await library.AddVideoAsync("original.mp4");
        await main.ImageGrid.FilterByFolderAsync(library.Photos);
        main.ImageViewer.OpenImage(original, main.ImageGrid.GetViewerImages());
        var publishedCounts = new List<int>();
        main.ImageGrid.ResultsChanged += (_, _) => publishedCounts.Add(main.ImageGrid.Images.Count);

        var context = new DeferredContinuationContext();
        var previousContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            main.ImageGrid.SortDescending = !main.ImageGrid.SortDescending;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
        var continuation = await context.Continuation.Task.WaitAsync(
            TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        var resumedDuringReload = false;
        main.ImageGrid.Images.CollectionChanged += (_, _) =>
        {
            if (resumedDuringReload || main.ImageGrid.Images.Count != 0) return;
            // Finish the old grouping while the new load has cleared, but not repopulated, the grid.
            resumedDuringReload = true;
            continuation.Callback(continuation.State);
        };
        await main.RefreshAfterIndexAsync();

        Assert.True(resumedDuringReload);
        Assert.Equal(1, Assert.Single(publishedCounts));
        Assert.Equal(original.FilePath, main.ImageViewer.CurrentEntry?.FilePath);
    }

    [Fact]
    public async Task MetadataShutdownDrainsReadsForRapidVideoSelections()
    {
        await using var library = await LibraryFixture.CreateAsync();
        var main = library.Main;
        var original = await library.AddVideoAsync("original.mp4");
        var copy = await library.AddVideoAsync("original-stabilized.mp4");

        for (int iteration = 0; iteration < 50; iteration++)
        {
            main.MetadataPanel.ShowMetadata(original);
            main.MetadataPanel.ShowMetadata(copy);
        }

        await main.MetadataPanel.StopVideoMetadataReadsAsync();

        using var originalHandle = new FileStream(
            original.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var copyHandle = new FileStream(
            copy.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task IndexFailureLeavesViewerAndSelectionOnOriginal()
    {
        await using var library = await LibraryFixture.CreateAsync();
        var original = await library.AddVideoAsync("original.mp4");
        var main = library.Main;
        await main.ImageGrid.FilterByFolderAsync(library.Photos);
        main.ImageViewer.OpenImage(original, main.ImageGrid.GetViewerImages());

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            main.OpenStabilizedVideoAsync(Path.Combine(library.Root, "missing.mp4")));

        Assert.Equal(original.FilePath, main.ImageViewer.CurrentEntry!.FilePath);
        Assert.Equal(original.FilePath, main.ImageGrid.SelectedImage!.Entry.FilePath);
        Assert.Equal("original", File.ReadAllText(original.FilePath));
        Assert.Single(main.ImageGrid.Images);
    }

    private sealed class DeferredContinuationContext : SynchronizationContext
    {
        public TaskCompletionSource<(SendOrPostCallback Callback, object? State)> Continuation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Post(SendOrPostCallback callback, object? state) =>
            Continuation.SetResult((callback, state));
    }

    private sealed class LibraryFixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-Export-{Guid.NewGuid():N}");
        public string Photos => Path.Combine(Root, "photos");
        private readonly CacheDatabase _database;
        private readonly LibraryIndexingService _indexer;
        public ImageRepository Images { get; }
        public MainViewModel Main { get; }

        private LibraryFixture()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Photos);
            _database = new CacheDatabase(Path.Combine(Root, "cache.db"));
            Images = new ImageRepository(_database);
            var tags = new TagRepository(_database);
            var faces = new FaceRepository(_database);
            var scanner = new FolderScannerService();
            var reader = new MetadataReaderService();
            var metadataStore = new FaceMetadataStore();
            _indexer = new LibraryIndexingService(_database, Images, tags, faces, scanner, reader, metadataStore);
            var sessions = new OnnxSessionManager(Path.Combine(Root, "models"));
            var models = new AutoTagModelManager(sessions);
            var tagger = new AutoTaggingService(sessions, models);
            var activity = new UserActivityGate();
            var pipeline = new RecognitionPipeline(faces, tags, null!, null!, null!, models, tagger, activity, null!);
            var review = new FaceReviewService(faces, Images, new FaceClusteringService(),
                new FaceRecognitionService(), metadataStore);
            Main = new MainViewModel(_database, Images, tags, faces, scanner, reader, _indexer,
                new OriginalBackupService(Path.Combine(Root, "backups")), pipeline, review, sessions,
                new AutoTaggingSettingsStore(Path.Combine(Root, "settings.json")), models,
                new AutoTagBenchmarkProcessor(models, tagger),
                new SelectedPhotoAutoTagReviewProcessor(models, tagger),
                activity);
        }

        public static async Task<LibraryFixture> CreateAsync()
        {
            var result = new LibraryFixture();
            await result._database.InitializeAsync();
            using (var connection = result._database.CreateConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO watched_folders(path) VALUES ($path)";
                command.Parameters.AddWithValue("$path", result.Photos);
                await command.ExecuteNonQueryAsync();
            }
            await result.Main.FolderNav.LoadWatchedFoldersAsync();
            await result.Main.PauseFaceDetectionAsync();
            return result;
        }

        public async Task<ImageEntry> AddVideoAsync(string name)
        {
            var path = Path.Combine(Photos, name);
            await File.WriteAllTextAsync(path, "original");
            await MetadataWriterService.WriteCaptionAsync(path, "onlyoriginal");
            await _indexer.IndexFileAsync(path);
            return (await Images.GetByPathAsync(path))!;
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
