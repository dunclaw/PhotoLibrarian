// Copyright (c) Duncan Lawler. All Rights Reserved.
using Moq;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.ML.Services;
using PhotoLibrarian.ViewModels;
using PhotoLibrarian.Views;
using System.Xml.Linq;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class ImageEditorNavigationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-navigation-{Guid.NewGuid():N}");
    private readonly OriginalBackupService _backups;

    public ImageEditorNavigationTests()
    {
        Directory.CreateDirectory(_directory);
        _backups = new OriginalBackupService(Path.Combine(_directory, "backups"));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private ImageEntry Entry(string name, MediaType mediaType = MediaType.Image)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return new ImageEntry { FilePath = path, FileName = name, MediaType = mediaType };
    }

    private async Task<ImageEditorViewModel> OpenAsync(ImageEntry entry, params ImageEntry[] images)
    {
        var vm = new ImageEditorViewModel(_backups);
        vm.UpdateLibraryImages(images);
        await vm.OpenForEditAsync(entry);
        return vm;
    }

    private static Task<PendingEditDecision> UnexpectedPrompt() =>
        throw new InvalidOperationException("A clean photo must not prompt.");

    private static Task<(uint Width, uint Height)> UnexpectedRenderer(string path, EditParameters parameters) =>
        throw new InvalidOperationException("This operation must not overwrite pixels.");

    private static Task<PendingEditDecision> Discard() => Task.FromResult(PendingEditDecision.Discard);
    private static Task<PendingEditDecision> Save() => Task.FromResult(PendingEditDecision.Save);
    private static Task<PendingEditDecision> Cancel() => Task.FromResult(PendingEditDecision.Cancel);

    [Fact]
    public async Task MainViewModel_SynchronizesViewerGridMetadataAndSelectionRestoreOnlyAfterCommittedMoves()
    {
        using var db = new CacheDatabase(Path.Combine(_directory, "library.db"));
        using var sessions = new OnnxSessionManager(Path.Combine(_directory, "models"));
        var images = new ImageRepository(db);
        var tags = new TagRepository(db);
        var faces = new FaceRepository(db);
        var scanner = new FolderScannerService();
        var reader = new MetadataReaderService();
        var indexer = new LibraryIndexingService(db, images, tags, faces, scanner, reader, null!);
        var pipeline = new RecognitionPipeline(null!, null!, null!, null!, null!, null!, null!, null!, null!);
        var models = new AutoTagModelManager(sessions);
        var tagger = new AutoTaggingService(sessions, models);
        var main = new MainViewModel(db, images, tags, faces, scanner, reader, indexer, _backups,
            pipeline, new FaceReviewService(faces, images, null!, null!), Mock.Of<IDisposable>(),
            new AutoTaggingSettingsStore(Path.Combine(_directory, "auto-tagging.json")),
            models, new AutoTagBenchmarkProcessor(null!, null!),
            new SelectedPhotoAutoTagReviewProcessor(models, tagger),
            new UserActivityGate());
        try
        {
            var first = Entry("first.jpg");
            var second = Entry("second.jpg");
            var firstItem = new ImageThumbnailViewModel(first);
            var secondItem = new ImageThumbnailViewModel(second);
            main.ImageGrid.Images.Add(firstItem);
            main.ImageGrid.Images.Add(secondItem);
            main.ImageGrid.GroupedImages.Add(new PhotoGroup { Header = "Photos", Items = [firstItem, secondItem] });
            var selectionRestores = 0;
            main.ImageGrid.SelectionRestoreRequested += (_, _) => selectionRestores++;
            var editor = main.ImageEditor;
            editor.UpdateLibraryImages(main.ImageGrid.GetViewerImages());
            await editor.OpenForEditAsync(first);
            Assert.Same(first, main.ImageViewer.CurrentEntry);
            Assert.Same(firstItem, main.ImageGrid.SelectedImage);
            Assert.Equal(first.FilePath, main.MetadataPanel.FilePath);
            var restoresAfterOpen = selectionRestores;
            editor.Exposure = 0.3;

            Assert.False(await editor.NavigateAsync(1, Cancel, UnexpectedRenderer));
            Assert.Same(first, main.ImageViewer.CurrentEntry);
            Assert.Same(firstItem, main.ImageGrid.SelectedImage);
            Assert.Equal(restoresAfterOpen, selectionRestores);

            // Supply the changed grid order without starting the app's UI dispatcher.
            main.ImageGrid.GroupedImages.Clear();
            main.ImageGrid.GroupedImages.Add(new PhotoGroup { Header = "Filtered", Items = [secondItem] });
            editor.UpdateLibraryImages(main.ImageGrid.GetViewerImages());
            Assert.Same(first, editor.CurrentEntry);
            Assert.Same(first, main.ImageViewer.CurrentEntry);
            Assert.Equal(first.FilePath, main.MetadataPanel.FilePath);

            Assert.True(await editor.NavigateAsync(1, Discard, UnexpectedRenderer));
            Assert.Same(second, main.ImageViewer.CurrentEntry);
            Assert.Same(secondItem, main.ImageGrid.SelectedImage);
            Assert.Equal(new[] { secondItem }, main.ImageGrid.SelectedImages);
            Assert.Equal(second.FilePath, main.MetadataPanel.FilePath);
            Assert.True(selectionRestores > restoresAfterOpen);
            Assert.True(await editor.TryCloseAsync(UnexpectedPrompt, UnexpectedRenderer));
            Assert.Same(second, main.ImageViewer.CurrentEntry);
            Assert.Same(secondItem, main.ImageGrid.SelectedImage);
        }
        finally { await main.CleanupAsync(); }
    }

    [Fact]
    public async Task Navigation_UsesDisplayedOrderSkipsUnsupportedEntriesAndWraps()
    {
        var first = Entry("first.jpg");
        var second = Entry("second.png");
        var third = Entry("third.dng");
        var video = Entry("video.mp4", MediaType.Video);
        var unsupported = Entry("unsupported.heic");
        var grid = new ImageGridViewModel(null!, null!, null!, null!, null!, null!, null!);
        grid.GroupedImages.Add(new PhotoGroup
        {
            Header = "A",
            Items = [new(third), new(video), new(first)]
        });
        grid.GroupedImages.Add(new PhotoGroup
        {
            Header = "B",
            Items = [new(unsupported), new(second)]
        });
        var vm = await OpenAsync(third, grid.GetViewerImages().ToArray());
        var selections = new List<ImageEntry?>();
        vm.CurrentEntryChanged += selections.Add;

        Assert.True(await vm.NavigateAsync(1, UnexpectedPrompt, UnexpectedRenderer));
        Assert.Same(first, vm.CurrentEntry);
        Assert.True(await vm.NavigateAsync(1, UnexpectedPrompt, UnexpectedRenderer));
        Assert.Same(second, vm.CurrentEntry);
        Assert.True(await vm.NavigateAsync(1, UnexpectedPrompt, UnexpectedRenderer));
        Assert.Same(third, vm.CurrentEntry);
        Assert.True(await vm.NavigateAsync(-1, UnexpectedPrompt, UnexpectedRenderer));
        Assert.Same(second, vm.CurrentEntry);
        Assert.Equal(new[] { first, second, third, second }, selections);
    }

    [Fact]
    public async Task Cancel_PreservesCurrentPhotoParametersHistoryAndSelection()
    {
        var first = Entry("first.jpg");
        var second = Entry("second.jpg");
        var vm = await OpenAsync(first, first, second);
        vm.Exposure = 0.3;
        var selections = new List<ImageEntry?>();
        vm.CurrentEntryChanged += selections.Add;

        Assert.False(await vm.NavigateAsync(1, Cancel, UnexpectedRenderer));
        Assert.False(await vm.TryCloseAsync(Cancel, UnexpectedRenderer));
        Assert.Same(first, vm.CurrentEntry);
        Assert.Equal(0.3, vm.Exposure);
        Assert.True(vm.HasChanges);
        Assert.Equal(new[] { "Exposure" }, vm.UndoHistory);
        Assert.Empty(selections);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Discard_LoadsDestinationRecipeAndIsolatesHistoryWithoutWritingEitherPhoto()
    {
        var first = Entry("first.jpg");
        var second = Entry("second.jpg");
        await new ImageEditService(_backups).SaveEditParametersAsync(second.FilePath,
            new EditParameters { Contrast = 0.4 });
        var sidecar = File.ReadAllBytes(Path.ChangeExtension(second.FilePath, ".xmp"));
        var vm = await OpenAsync(first, first, second);
        vm.BeginEdit("Exposure drag");
        vm.Exposure = 0.3;

        Assert.True(await vm.NavigateAsync(1, Discard, UnexpectedRenderer));
        Assert.Equal(0, vm.Exposure);
        Assert.Equal(0.4, vm.Contrast);
        Assert.False(vm.HasChanges);
        Assert.Empty(vm.UndoHistory);
        Assert.Empty(vm.RedoHistory);
        Assert.Equal(sidecar, File.ReadAllBytes(Path.ChangeExtension(second.FilePath, ".xmp")));
        Assert.False(File.Exists(Path.ChangeExtension(first.FilePath, ".xmp")));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(first.FilePath));
        Assert.True(await vm.NavigateAsync(-1, UnexpectedPrompt, UnexpectedRenderer));
        Assert.Equal(0, vm.Contrast);
        Assert.Empty(vm.UndoHistory);
    }

    [Fact]
    public async Task Save_BacksUpRasterClearsPendingRecipeAndNotifiesBeforeSelectionMoves()
    {
        var first = Entry("first.jpg");
        var second = Entry("second.jpg");
        await new ImageEditService(_backups).SaveEditParametersAsync(first.FilePath,
            new EditParameters { Exposure = 0.1 });
        var backupPath = await _backups.GetBackupPathAsync(first.FilePath);
        var vm = await OpenAsync(first, first, second);
        vm.Exposure = 0.3;
        var events = new List<string>();
        vm.EditsApplied += (_, args) => events.Add($"saved:{args.FilePath}");
        vm.CurrentEntryChanged += entry => events.Add($"selected:{entry?.FilePath}");

        Assert.True(await vm.NavigateAsync(1, Save, (path, parameters) =>
        {
            Assert.Equal(first.FilePath, path);
            Assert.Equal(0.3, parameters.Exposure);
            Assert.False(vm.CanNavigate);
            File.WriteAllBytes(path, [5, 6, 7, 8]);
            return Task.FromResult((10u, 20u));
        }));

        Assert.Equal(new[] { $"saved:{first.FilePath}", $"selected:{second.FilePath}" }, events);
        Assert.False(ImageEditService.ReadEditParameters(first.FilePath).HasAdjustments);
        Assert.NotNull(backupPath);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(backupPath));
        Assert.Empty(vm.UndoHistory);
        Assert.False(vm.HasChanges);
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, File.ReadAllBytes(first.FilePath));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(second.FilePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSave_LeavesCurrentPhotoAndEditsOpen(bool close)
    {
        var first = Entry("first.jpg");
        var second = Entry("second.jpg");
        var vm = await OpenAsync(first, first, second);
        vm.Exposure = 0.3;
        Task<(uint Width, uint Height)> Fail(string path, EditParameters parameters) =>
            throw new IOException("Cannot save.");

        await Assert.ThrowsAsync<IOException>(() => close
            ? vm.TryCloseAsync(Save, Fail)
            : vm.NavigateAsync(1, Save, Fail));

        Assert.Same(first, vm.CurrentEntry);
        Assert.True(vm.IsOpen);
        Assert.True(vm.HasChanges);
        Assert.Equal(0.3, vm.Exposure);
        Assert.Equal(new[] { "Exposure" }, vm.UndoHistory);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task SaveAndClose_WritesCurrentRasterThenCloses()
    {
        var first = Entry("first.jpg");
        var vm = await OpenAsync(first, first);
        vm.Contrast = 0.4;
        Assert.True(await vm.TryCloseAsync(Save, (path, parameters) =>
        {
            Assert.Equal(first.FilePath, path);
            Assert.Equal(0.4, parameters.Contrast);
            File.WriteAllBytes(path, [5, 6]);
            return Task.FromResult((10u, 20u));
        }));
        Assert.False(vm.IsOpen);
        Assert.Null(vm.CurrentEntry);
        Assert.Empty(vm.UndoHistory);
        Assert.Equal(new byte[] { 5, 6 }, File.ReadAllBytes(first.FilePath));
    }

    [Fact]
    public async Task SaveReturningFalse_DoesNotMoveOrClose()
    {
        var first = Entry("first.jpg");
        var second = Entry("second.jpg");
        await new ImageEditService(_backups).SaveEditParametersAsync(first.FilePath,
            new EditParameters { Exposure = 0.2 });
        var vm = await OpenAsync(first, first, second);
        vm.Exposure = 0; // Changed from its recipe baseline, but no pixels to apply.

        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.NavigateAsync(1, Save, UnexpectedRenderer));
        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.TryCloseAsync(Save, UnexpectedRenderer));
        Assert.Same(first, vm.CurrentEntry);
        Assert.True(vm.HasChanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RawSave_KeepsPortableRecipeNeverCallsRasterRenderer(bool close)
    {
        var raw = Entry("first.dng");
        var second = Entry("second.jpg");
        await ImageEditService.SaveRawPreviewRecipeAsync(raw.FilePath,
            new EditParameters { Contrast = 0.4, Crop = new CropRect { Left = 0.1, Width = 0.8, Height = 1 } },
            TestContext.Current.CancellationToken);
        var vm = await OpenAsync(raw, raw, second);
        vm.RawExposureEv = -1.5;

        Assert.True(close
            ? await vm.TryCloseAsync(Save, UnexpectedRenderer)
            : await vm.NavigateAsync(1, Save, UnexpectedRenderer));

        var recipe = ImageEditService.ReadEditParameters(raw.FilePath);
        Assert.Equal(-1.5, recipe.RawExposureEv);
        Assert.Equal(0.4, recipe.Contrast);
        Assert.Equal(0.1, recipe.Crop!.Left);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(raw.FilePath));
        Assert.Empty(vm.UndoHistory);
        Assert.False(vm.HasChanges);
        Assert.Equal(close ? null : second, vm.CurrentEntry);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RawRecipeFailure_PreservesCurrentPhotoAndHistory(bool close)
    {
        var raw = Entry("first.dng");
        var second = Entry("second.jpg");
        var vm = await OpenAsync(raw, raw, second);
        vm.RawExposureEv = -1.5;
        File.WriteAllText(Path.ChangeExtension(raw.FilePath, ".xmp"), "unreadable XMP");

        await Assert.ThrowsAnyAsync<Exception>(() => close
            ? vm.TryCloseAsync(Save, UnexpectedRenderer)
            : vm.NavigateAsync(1, Save, UnexpectedRenderer));

        Assert.Same(raw, vm.CurrentEntry);
        Assert.Equal(-1.5, vm.RawExposureEv);
        Assert.True(vm.HasChanges);
        Assert.Single(vm.UndoHistory);
        Assert.Equal("unreadable XMP", File.ReadAllText(Path.ChangeExtension(raw.FilePath, ".xmp")));
    }

    [Fact]
    public async Task PendingPrompt_UsesLatestResultsAndBlocksConcurrentNavigationAndClose()
    {
        var first = Entry("first.jpg");
        var second = Entry("second.jpg");
        var third = Entry("third.jpg");
        var vm = await OpenAsync(first, first, second, third);
        vm.Exposure = 0.3;
        var prompt = new TaskCompletionSource<PendingEditDecision>();
        var navigation = vm.NavigateAsync(1, () => prompt.Task, UnexpectedRenderer);

        Assert.True(vm.IsBusy);
        Assert.False(vm.CanNavigate);
        Assert.False(vm.CanUndo);
        Assert.False(await vm.NavigateAsync(-1, Discard, UnexpectedRenderer));
        Assert.False(await vm.TryCloseAsync(Discard, UnexpectedRenderer));
        vm.CloseCommand.Execute(null);
        vm.ResetAllCommand.Execute(null);
        Assert.Equal(0.3, vm.Exposure);
        vm.UpdateLibraryImages([third, first]);
        prompt.SetResult(PendingEditDecision.Discard);

        Assert.True(await navigation);
        Assert.Same(third, vm.CurrentEntry);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task FilterRemovingTargetDuringSave_DoesNotCommitStaleSelection()
    {
        var first = Entry("first.jpg");
        var second = Entry("second.jpg");
        var third = Entry("third.jpg");
        var vm = await OpenAsync(first, first, second, third);
        vm.Exposure = 0.3;
        var render = new TaskCompletionSource<(uint Width, uint Height)>();
        var started = new TaskCompletionSource();
        var navigation = vm.NavigateAsync(1, Save, (path, parameters) =>
        {
            started.SetResult();
            return render.Task;
        });
        await started.Task;
        vm.UpdateLibraryImages([first, third]);
        render.SetResult((10, 20));

        Assert.False(await navigation);
        Assert.Same(first, vm.CurrentEntry);
        Assert.False(vm.HasChanges); // The requested save succeeded; only the stale move was cancelled.
        Assert.True(await vm.NavigateAsync(1, UnexpectedPrompt, UnexpectedRenderer));
        Assert.Same(third, vm.CurrentEntry);
    }

    [Fact]
    public async Task EmptyAndSingleResults_DisableNavigationWithoutDiscardingEdits()
    {
        var first = Entry("first.jpg");
        var vm = await OpenAsync(first, first);
        vm.Exposure = 0.3;
        Assert.False(vm.CanNavigate);
        vm.UpdateLibraryImages([]);
        Assert.False(await vm.NavigateAsync(1, UnexpectedPrompt, UnexpectedRenderer));
        Assert.True(vm.IsOpen);
        Assert.True(vm.HasChanges);
        Assert.True(await vm.TryCloseAsync(Discard, UnexpectedRenderer));
        Assert.Null(vm.CurrentEntry);
        Assert.False(vm.CanNavigate);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public async Task ExcludedCurrentPhoto_IsKeptUntilExplicitMoveToFilteredBoundary(int direction)
    {
        var first = Entry("first.jpg");
        var second = Entry("second.jpg");
        var third = Entry("third.jpg");
        var vm = await OpenAsync(first, first, second, third);
        vm.Exposure = 0.3;
        var results = new List<ImageEntry> { second, third };
        vm.UpdateLibraryImages(results);
        results.Clear();
        Assert.Same(first, vm.CurrentEntry);

        Assert.True(await vm.NavigateAsync(direction, Discard, UnexpectedRenderer));
        Assert.Same(direction == 1 ? second : third, vm.CurrentEntry);
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".dng")]
    public async Task MissingDestination_DoesNotDiscardOrSaveCurrentEdits(string extension)
    {
        var first = Entry("first.jpg");
        var second = Entry("second" + extension);
        var vm = await OpenAsync(first, first, second);
        vm.Exposure = 0.3;
        File.Delete(second.FilePath);

        await Assert.ThrowsAnyAsync<IOException>(() => vm.NavigateAsync(1, Save, UnexpectedRenderer));

        Assert.Same(first, vm.CurrentEntry);
        Assert.Equal(0.3, vm.Exposure);
        Assert.True(vm.HasChanges);
        Assert.Single(vm.UndoHistory);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void NavigationButtons_HaveAccessibleNamesAndAutomationIds()
    {
        var xaml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "ImageEditorView.xaml"));
        foreach (var id in new[] { "EditorPrevious", "EditorNext" })
        {
            var button = Assert.Single(xaml.Descendants(), element =>
                (string?)element.Attribute("AutomationProperties.AutomationId") == id);
            Assert.Equal("Button", button.Name.LocalName);
            Assert.False(string.IsNullOrEmpty((string?)button.Attribute("AutomationProperties.Name")));
            Assert.NotNull(button.Attribute("Click"));
        }
    }

    [Theory]
    [InlineData(Windows.System.VirtualKey.Left, Windows.System.VirtualKeyModifiers.None, false, -1)]
    [InlineData(Windows.System.VirtualKey.Right, Windows.System.VirtualKeyModifiers.None, false, 1)]
    [InlineData(Windows.System.VirtualKey.Left, Windows.System.VirtualKeyModifiers.None, true, 0)]
    [InlineData(Windows.System.VirtualKey.Right, Windows.System.VirtualKeyModifiers.None, true, 0)]
    [InlineData(Windows.System.VirtualKey.Right, Windows.System.VirtualKeyModifiers.Control, false, 0)]
    [InlineData(Windows.System.VirtualKey.Left, Windows.System.VirtualKeyModifiers.Shift, false, 0)]
    [InlineData(Windows.System.VirtualKey.Right, Windows.System.VirtualKeyModifiers.Menu, false, 0)]
    [InlineData(Windows.System.VirtualKey.Left, Windows.System.VirtualKeyModifiers.Windows, false, 0)]
    [InlineData(Windows.System.VirtualKey.Up, Windows.System.VirtualKeyModifiers.None, false, 0)]
    [InlineData(Windows.System.VirtualKey.Down, Windows.System.VirtualKeyModifiers.None, false, 0)]
    public void ArrowNavigation_MatchesViewerDirectionsButPreservesInputsAndModifiedKeys(
        Windows.System.VirtualKey key, Windows.System.VirtualKeyModifiers modifiers, bool isEditingControl,
        int expectedDirection)
    {
        Assert.Equal(expectedDirection, ImageEditorView.GetArrowNavigationDirection(key, modifiers, isEditingControl));
    }
}
