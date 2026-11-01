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
        Assert.Equal(2, main.TotalImages);
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
