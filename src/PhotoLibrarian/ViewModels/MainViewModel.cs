// Copyright (c) Duncan Lawler. All Rights Reserved.
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Diagnostics;
using PhotoLibrarian.ML.Services;
using PhotoLibrarian.Services;
using System.Collections.ObjectModel;

namespace PhotoLibrarian.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly CacheDatabase _db;
    private readonly ImageRepository _imageRepo;
    private readonly TagRepository _tagRepo;
    private readonly FaceRepository _faceRepo;
    private readonly FolderScannerService _scanner;
    private readonly MetadataReaderService _metadataReader;
    private readonly LibraryIndexingService _indexingService;
    private readonly WatchedFolderChangeService _watchedChanges;
    private CancellationTokenSource? _hierarchyCleanupCancellation;
    private bool _libraryFoldersChanging;
    private readonly OriginalBackupService _backupService;
    private readonly IDisposable _faceResources;
    private readonly RecognitionPipeline _recognitionPipeline;
    private readonly FaceReviewService _faceReviewService;
    private readonly UserActivityGate _activityGate;
    private CancellationTokenSource? _indexingCts;
    private CancellationTokenSource? _recognitionCts;
    private Task? _recognitionTask;
    private readonly object _recognitionLock = new();
    private bool _faceDetectionEnabled = true;
    private bool _autoTaggingEnabled;
    private bool _recognitionRescanRequested;
    private bool _duplicateScanActive;
    private bool _autoTagReviewActive;
    private bool _photoFuseRecognitionPaused;
    private int _lastAutoTagTreeRefresh;
    private volatile bool _isShuttingDown;

    public FolderNavigationViewModel FolderNav { get; }
    public DateNavigationViewModel DateNav { get; }
    public PeopleNavigationViewModel PeopleNav { get; }
    public TagNavigationViewModel TagNav { get; }
    public FlagNavigationViewModel FlagNav { get; }
    public ColorLabelNavigationViewModel ColorLabelNav { get; }
    public NotTaggedNavigationViewModel NotTaggedNav { get; }
    public ImageGridViewModel ImageGrid { get; }
    public ImageViewerViewModel ImageViewer { get; }
    public ImageEditorViewModel ImageEditor { get; }
    public MetadataPanelViewModel MetadataPanel { get; }
    public PeopleReviewViewModel PeopleReview { get; }
    public DuplicateReviewViewModel DuplicateReview { get; }
    public AutoTagReviewViewModel AutoTagReview { get; }
    public SettingsViewModel Settings { get; }
    public Services.PhotoOperationsService PhotoOps { get; }
    public PhotoImportService PhotoImport { get; }
    public OriginalBackupService BackupService => _backupService;
    public LibrarySearchViewModel Search { get; }
    public PhotoFuseViewModel PhotoFuse { get; }

    public async Task PauseRecognitionForPhotoFuseAsync()
    {
        _photoFuseRecognitionPaused = true;
        await StopRecognitionAsync();
    }

    public void ResumeRecognitionAfterPhotoFuse()
    {
        _photoFuseRecognitionPaused = false;
        StartRecognitionProcessing();
    }

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    public partial bool IsIndexing { get; set; }

    [ObservableProperty]
    public partial bool IsFaceDetectionRunning { get; set; }

    [ObservableProperty]
    public partial bool IsAutoTaggingRunning { get; set; }

    /// <summary>
    /// One status surface for the unified recognition job: true whenever the
    /// single background pipeline is working, whatever stage it is in.
    /// </summary>
    [ObservableProperty]
    public partial bool IsRecognitionRunning { get; set; }

    [ObservableProperty]
    public partial RecognitionStage ActiveRecognitionStage { get; set; } =
        RecognitionStage.None;

    [ObservableProperty]
    public partial int TotalImages { get; set; }

    /// <summary>True while the viewer is in manual face-tagging (draw-a-box) mode, so the
    /// Browse panel's "Add people tags" button can reflect and toggle it.</summary>
    [ObservableProperty]
    public partial bool IsManualFaceTaggingActive { get; set; }

    public MainViewModel(
        CacheDatabase db,
        ImageRepository imageRepo,
        TagRepository tagRepo,
        FaceRepository faceRepo,
        FolderScannerService scanner,
        MetadataReaderService metadataReader,
        LibraryIndexingService indexingService,
        OriginalBackupService backupService,
        RecognitionPipeline recognitionPipeline,
        FaceReviewService faceReviewService,
        IDisposable faceResources,
        AutoTaggingSettingsStore autoTaggingSettingsStore,
        AutoTagModelManager autoTagModelManager,
        AutoTagBenchmarkProcessor autoTagBenchmarkProcessor,
        SelectedPhotoAutoTagReviewProcessor selectedPhotoAutoTagReviewProcessor,
        UserActivityGate activityGate,
        Services.GridLayoutSettingsStore? gridLayoutSettingsStore = null,
        FaceLibraryProcessor? photoFuseFaces = null)
    {
        _db = db;
        _imageRepo = imageRepo;
        _tagRepo = tagRepo;
        _faceRepo = faceRepo;
        _scanner = scanner;
        _metadataReader = metadataReader;
        _indexingService = indexingService;
        _watchedChanges = new WatchedFolderChangeService(scanner, indexingService, imageRepo);
        _watchedChanges.LibraryChanged += OnWatchedLibraryChanged;
        _watchedChanges.SyncFailed += OnWatchedSyncFailed;
        _scanner.WatcherError += OnWatcherError;
        _backupService = backupService;
        _recognitionPipeline = recognitionPipeline;
        _faceReviewService = faceReviewService;
        _faceResources = faceResources;
        _activityGate = activityGate;
        PhotoFuse = new PhotoFuseViewModel(
            (image, token) => _imageRepo.GetNearbyBurstAsync(image, TimeSpan.FromMinutes(2), token),
            (image, token) => photoFuseFaces is null
                ? throw new InvalidOperationException("Local face scanning is unavailable.")
                : photoFuseFaces.ScanPhotoAsync(image, token),
            image => _faceRepo.GetFacesForImageAsync(image.Id));
        PhotoImport = new PhotoImportService(
            metadataReader,
            indexingService,
            imageRepo);

        StatusText = "Ready";

        FolderNav = new FolderNavigationViewModel(db, scanner, indexingService, this);
        DateNav = new DateNavigationViewModel(imageRepo);
        PeopleNav = new PeopleNavigationViewModel(faceRepo);
        TagNav = new TagNavigationViewModel(tagRepo);
        FlagNav = new FlagNavigationViewModel(imageRepo);
        ColorLabelNav = new ColorLabelNavigationViewModel(imageRepo);
        NotTaggedNav = new NotTaggedNavigationViewModel(tagRepo);
        var searchRepo = new ImageSearchRepository(db);
        ImageGrid = new ImageGridViewModel(
            imageRepo,
            tagRepo,
            faceRepo,
            searchRepo,
            scanner,
            metadataReader,
            this,
            gridLayoutSettingsStore ?? new PhotoLibrarian.Services.GridLayoutSettingsStore());
        ImageViewer = new ImageViewerViewModel();
        ImageViewer.PersistEditAsync = (label, path, prepare) =>
            RunPhotoEditAsync(label, [path], copies => prepare(copies[Path.GetFullPath(path)]));
        ImageViewer.EffectRecipeChanged += ImageGrid.RefreshSingleImageAsync;
        Search = new LibrarySearchViewModel(ImageGrid, ImageViewer);
        ImageEditor = new ImageEditorViewModel(backupService);
        ImageEditor.OperationFailed += (_, message) => StatusText = message;
        ImageEditor.PersistEditAsync = (label, path, identity, prepare) =>
            RunPhotoEditAsync(label, [path], copies => prepare(copies[Path.GetFullPath(path)]), identity);
        MetadataPanel = new MetadataPanelViewModel();
        MetadataPanel.Initialize(imageRepo, tagRepo, faceRepo, this, backupService);
        PeopleReview = new PeopleReviewViewModel(faceReviewService);
        Settings = new SettingsViewModel(
            db,
            autoTaggingSettingsStore,
            autoTagModelManager,
            autoTagBenchmarkProcessor,
            new LibraryIndexMaintenanceService(
                db,
                imageRepo,
                indexingService));
        AutoTagReview = new AutoTagReviewViewModel(
            selectedPhotoAutoTagReviewProcessor,
            tagRepo,
            () => Settings.CurrentAutoTaggingSettings);
        AutoTagReview.SetRecognitionCallbacks(
            PauseRecognitionForAutoTagReviewAsync,
            ResumeRecognitionAfterAutoTagReview);
        AutoTagReview.SetAfterApplyCallback(async () =>
        {
            await RefreshTagsTreeAsync();
            await MetadataPanel.ReloadTagsAsync();
        });
        AutoTagReview.StatusUpdated += status => StatusText = status;
        _autoTaggingEnabled =
            Settings.CurrentAutoTaggingSettings.CanRun;
        PhotoOps = new Services.PhotoOperationsService(imageRepo, backupService);
        DuplicateReview = new DuplicateReviewViewModel(
            new DuplicateDetectionService(imageRepo, watchedOnly: true),
            PhotoOps);
        DuplicateReview.BeforeScanAsync = PauseRecognitionForDuplicateScanAsync;
        DuplicateReview.AfterScan = ResumeRecognitionAfterDuplicateScan;

        _indexingService.Progress += OnIndexingProgress;
        _recognitionPipeline.Progress += OnRecognitionProgress;
        Settings.AutoTaggingSettingsChanged += OnAutoTaggingSettingsChanged;
        ImageViewer.CurrentEntryChanged += OnViewerEntryChanged;
        ImageGrid.ResultsChanged += OnGridResultsChanged;
        ImageEditor.EditsApplied += OnEditsApplied;
        ImageEditor.Reverted += OnEditsReverted;
        ImageEditor.CurrentEntryChanged += OnEditorEntryChanged;
    }

    private async void OnEditsReverted(object? sender, string filePath)
    {
        await RefreshFileAsync(filePath);
        StatusText = $"Reverted {System.IO.Path.GetFileName(filePath)} to the original";
    }

    /// <summary>Repaints the grid thumbnail and the open viewer after a file changed on disk.</summary>
    public async Task RefreshFileAsync(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        try
        {
            await ImageGrid.RefreshSingleImageAsync(filePath);
            await ImageViewer.ReloadCurrentImageAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EDIT] RefreshFileAsync failed: {ex.Message}");
        }
    }

    private async void OnEditsApplied(object? sender, EditsAppliedEventArgs e)
    {
        await RefreshAfterPixelEditAsync(e.FilePath, e.PixelWidth, e.PixelHeight, "Saved edits to");
    }

    /// <summary>
    /// Keeps the metadata panel pointed at whatever the viewer is showing, so ratings, tags and
    /// the flag act on the image on screen. When the viewer closes, falls back to the grid
    /// selection.
    /// </summary>
    private void OnViewerEntryChanged(ImageEntry? entry)
    {
        if (ImageEditor.IsOpen) entry = ImageEditor.CurrentEntry;
        if (entry is not null)
        {
            ImageGrid.SelectViewerEntry(entry);
            MetadataPanel.ShowMetadata(entry);
        }
        else
            ImageGrid.RefreshMetadataFromSelection();
    }

    private void OnGridResultsChanged(object? sender, EventArgs e)
    {
        ImageEditor.UpdateLibraryImages(ImageGrid.GetViewerImages());
        if (ImageViewer.IsOpen && !ImageEditor.IsOpen)
            ImageViewer.UpdateLibraryImages(ImageGrid.GetViewerImages());
    }

    private void OnEditorEntryChanged(ImageEntry? entry)
    {
        if (entry is not null)
            ImageViewer.OpenImage(entry, ImageGrid.GetViewerImages());
        else
        {
            if (ImageViewer.IsOpen)
                ImageViewer.UpdateLibraryImages(ImageGrid.GetViewerImages());
            ImageGrid.RefreshMetadataFromSelection();
        }
    }

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        await FolderNav.LoadWatchedFoldersAsync();
        await FlagNav.LoadAsync();
        await ColorLabelNav.LoadAsync();
        await NotTaggedNav.LoadAsync();
        
        // Don't load images on startup - wait for user to select a folder
        // This prevents showing all 535 indexed images in random order
        TotalImages = await _imageRepo.GetCountAsync(watchedOnly: true);
        StatusText = TotalImages > 0 ? "Select a folder to view photos" : "Add folders to get started";

        // Start background indexing to populate metadata (tags, dates)
        StartBackgroundIndexing();
        StartBackgroundFaceDetection();
        StartBackgroundAutoTagging();
    }

    public async Task EnsureImportDestinationWatchedAsync(string destinationPath)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        var probePath = Path.Combine(fullPath, "_");
        if (FolderNav.RootFolders.Any(folder =>
                WatchedFolderScope.ContainsFile(
                    probePath,
                    folder.Path,
                    folder.IncludeSubfolders)))
            return;

        await new WatchedFolderRepository(_db).AddAsync(fullPath);
        await FolderNav.LoadWatchedFoldersAsync();
    }

    public async Task RefreshAfterPhotoImportAsync(IReadOnlyCollection<string> importedPaths)
    {
        ImageGrid.RevealImportedPhotos(importedPaths);
        await RefreshAfterIndexAsync();
    }

    public void SyncWatchedFolders()
    {
        try
        {
            _scanner.SyncWatchedFolders(
                FolderNav.RootFolders.Select(folder => (folder.Path, folder.IncludeSubfolders)));
        }
        catch (Exception ex)
        {
            DebugLog.WriteLine($"Failed to watch library folders: {ex}");
            var detail = ex is AggregateException aggregate
                ? string.Join("; ", aggregate.InnerExceptions.Select(error => error.Message))
                : ex.Message;
            StatusText = $"Folder monitoring failed: {detail}";
        }
    }

    private void OnWatcherError(object? sender, Exception error)
    {
        DebugLog.WriteLine($"Folder watcher error; rescanning watched folders: {error}");
        if (_isShuttingDown) return;
        App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            if (_isShuttingDown) return;
            StatusText = $"Folder watcher lost changes; rescanning: {error.Message}";
            StartBackgroundIndexing();
        });
    }

    private void OnWatchedSyncFailed(object? sender, Exception error)
    {
        if (_isShuttingDown) return;
        App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
            StatusText = $"Library update failed: {error.Message}");
    }

    private void OnWatchedLibraryChanged(
        object? sender,
        WatchedFolderChangeService.WatchedLibraryChangedEventArgs e)
    {
        if (_isShuttingDown) return;
        App.MainWindow?.DispatcherQueue.TryEnqueue(async () =>
        {
            if (_isShuttingDown) return;
            try
            {
                DebugLog.WriteLine(e.RequiresGridReload
                    ? "Library: refreshing after watched-folder membership change"
                    : $"Library: refreshing {e.ChangedImagePaths.Count} watched item(s) in place");
                if (ImageViewer.CurrentEntry is { } current &&
                    !System.IO.File.Exists(current.FilePath))
                    ImageViewer.CloseCommand.Execute(null);

                if (e.RequiresGridReload)
                {
                    await RefreshAfterIndexAsync();
                }
                else
                {
                    await ImageGrid.RefreshWatchedItemsAsync(e.ChangedImagePaths);
                    await RefreshNavigationAfterIndexAsync();
                }
            }
            catch (Exception ex)
            {
                DebugLog.WriteLine($"Failed to refresh after filesystem change: {ex}");
                StatusText = $"Library refresh failed: {ex.Message}";
            }
        });
    }

    public Task EnsureFaceMetadataPersistedAsync() =>
        _indexingService.ExportPendingFaceMetadataAsync();

    public void StartManualFaceTagging(ImageEntry entry)
    {
        if (entry.MediaType != MediaType.Image)
        {
            StatusText = "People tags can only be added to photos.";
            return;
        }

        if (ImageViewer.CurrentEntry?.Id != entry.Id)
        {
            ImageViewer.OpenImage(
                entry,
                ImageGrid.GetViewerImages());
        }

        if (App.MainWindow is MainWindow window)
        {
            IsManualFaceTaggingActive = true;
            window.BeginManualFaceTagging();
        }
    }

    /// <summary>Cancels manual face-tagging mode, e.g. when the user clicks the toggled
    /// "Add people tags" button again while it is active.</summary>
    public void CancelManualFaceTagging()
    {
        if (App.MainWindow is MainWindow window)
        {
            window.CancelManualFaceTagging();
        }
        IsManualFaceTaggingActive = false;
    }

    /// <summary>Called when manual face-tagging mode ends from inside the viewer (Save,
    /// Cancel button, or Escape) so the panel's toggle button state stays in sync.</summary>
    public void OnManualFaceTaggingExited()
    {
        IsManualFaceTaggingActive = false;
    }

    /// <summary>Forwarded from the Browse panel when the user hovers/unhovers a people-tags
    /// row, so the full viewer and/or grid thumbnail can highlight that face's location.</summary>
    public void SetHoveredFace(ImageEntry? entry, FaceRegion? region)
    {
        if (App.MainWindow is MainWindow window)
        {
            window.SetHoveredFace(entry, region);
        }
    }

    public async Task AddManualFaceAsync(
        ImageEntry image,
        FaceRegion region,
        long? existingPersonId,
        string personName,
        CancellationToken cancellationToken = default)
    {
        await _faceReviewService.AddManualFaceAsync(
            image,
            region,
            existingPersonId,
            personName,
            cancellationToken);
        await MetadataPanel.ReloadPeopleTagsAsync();
        if (PhotoFuse.IsActive)
            await PhotoFuse.RefreshFacesAsync();
        if (App.MainWindow is MainWindow window)
            await window.RefreshPeopleFiltersAsync();
        StatusText = $"Added people tag for {personName.Trim()}.";
    }

    public async Task RemoveFaceTagAsync(long faceRegionId, CancellationToken cancellationToken = default)
    {
        await _faceReviewService.UnassignFacesAsync([faceRegionId], cancellationToken);
        await MetadataPanel.ReloadPeopleTagsAsync();
        if (App.MainWindow is MainWindow window)
            await window.RefreshPeopleFiltersAsync();
        StatusText = "Removed people tag.";
    }
    
    [RelayCommand]
    public async Task RunBenchmarkAsync()
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("=== STARTING WINUI BENCHMARK ===");
            StatusText = "Running benchmark...";
            
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Task.Run(async () =>
            {
                try
                {
                    await Services.BenchmarkService.RunWICBenchmark();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Benchmark error: {ex.Message}");
                }
            });
            sw.Stop();
            
            StatusText = $"Benchmark complete in {sw.ElapsedMilliseconds}ms - check debug output";
            System.Diagnostics.Debug.WriteLine($"=== BENCHMARK COMPLETE: {sw.ElapsedMilliseconds}ms ===");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Benchmark failed: {ex.Message}");
            StatusText = $"Benchmark failed: {ex.Message}";
        }
    }

    public void PauseBackgroundIndexing()
    {
        DebugLog.WriteLine("MainViewModel: Pausing background indexing");
        _indexingCts?.Cancel();
        _indexingCts = null;
    }

    public void PauseRecognitionProcessing()
    {
        DebugLog.WriteLine("MainViewModel: Pausing background recognition");
        lock (_recognitionLock)
        {
            _recognitionRescanRequested = false;
            _recognitionCts?.Cancel();
        }
    }

    /// <summary>
    /// Starts the one background recognition job. If it is already running the
    /// request is remembered and the job restarts when the current pass ends,
    /// so face and tag work never run as competing jobs.
    /// </summary>
    private void StartRecognitionProcessing()
    {
        if (_libraryFoldersChanging)
            return;
        if (_isShuttingDown ||
            Settings.IsHierarchyTagCleanupRunning ||
            _duplicateScanActive ||
            _autoTagReviewActive ||
            _photoFuseRecognitionPaused ||
            TotalImages == 0)
        {
            return;
        }

        if (!_faceDetectionEnabled && !_autoTaggingEnabled)
        {
            return;
        }

        CancellationToken cancellationToken;
        lock (_recognitionLock)
        {
            if (_recognitionTask is { IsCompleted: false })
            {
                _recognitionRescanRequested = true;
                return;
            }

            _recognitionCts?.Dispose();
            _recognitionCts = new CancellationTokenSource();
            cancellationToken = _recognitionCts.Token;
            _recognitionRescanRequested = false;

            var request = new RecognitionRequest(
                _faceDetectionEnabled,
                _autoTaggingEnabled ? Settings.CurrentAutoTaggingSettings : null);

            IsRecognitionRunning = true;
            _recognitionTask = Task.Run(
                () => RunRecognitionAsync(request, cancellationToken),
                cancellationToken);
        }
    }

    private async Task RunRecognitionAsync(
        RecognitionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var watchedFolders = await new WatchedFolderRepository(_db).GetAllAsync();
            await _recognitionPipeline.ProcessLibraryAsync(
                request with { WatchedFolders = watchedFolders },
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
            {
                if (_isShuttingDown) return;

                StatusText = $"Background recognition failed: {exception.Message}";
                Settings.SetAutoTaggingProgress(
                    $"Failed: {exception.Message}",
                    false);
            });
        }
        finally
        {
            var shouldRestart = false;
            lock (_recognitionLock)
            {
                _recognitionTask = null;
                shouldRestart =
                    _recognitionRescanRequested && !cancellationToken.IsCancellationRequested;
                _recognitionRescanRequested = false;
            }

            if (!_isShuttingDown)
            {
                App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                {
                    if (_isShuttingDown) return;

                    SetRecognitionStage(RecognitionStage.None, false);
                    if (shouldRestart)
                    {
                        StartRecognitionProcessing();
                    }
                });
            }
        }
    }

    public void StartBackgroundIndexing()
    {
        if (FolderNav.RootFolders.Count == 0) return;

        _indexingCts?.Cancel();
        _indexingCts = new CancellationTokenSource();
        var ct = _indexingCts.Token;
        var folders = FolderNav.RootFolders.ToList();

        _ = Task.Run(async () =>
        {
            // Wait a bit before starting to let UI settle
            await Task.Delay(2000, ct);
            
            DebugLog.WriteLine($"StartBackgroundIndexing: Starting scan of {folders.Count} folders");
            var libraryChanged = false;

            foreach (var folder in folders)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    DebugLog.WriteLine($"  Indexing folder: {folder.Path}");
                    libraryChanged |= await _indexingService.IndexFolderAsync(
                        folder.Path,
                        folder.IncludeSubfolders,
                        ct);
                    DebugLog.WriteLine($"  Completed folder: {folder.Path}");
                }
                catch (OperationCanceledException)
                {
                    DebugLog.WriteLine($"  Indexing canceled");
                    break;
                }
                catch (Exception ex)
                {
                    DebugLog.WriteLine($"  ERROR indexing {folder.Path}: {ex.Message}");
                    // Continue with next folder on error
                }
            }
            
            DebugLog.WriteLine($"StartBackgroundIndexing: All folders complete");
            if (libraryChanged && !ct.IsCancellationRequested)
            {
                App.MainWindow?.DispatcherQueue.TryEnqueue(async () =>
                {
                    if (!_isShuttingDown)
                        await RefreshAfterIndexAsync();
                });
            }
        }, ct);
    }

    [RelayCommand]
    private void ToggleFaceDetection()
    {
        if (IsFaceDetectionRunning)
        {
            StopBackgroundFaceDetection();
            return;
        }

        _faceDetectionEnabled = true;
        StartBackgroundFaceDetection();
    }

    public void StartBackgroundFaceDetection()
    {
        if (_isShuttingDown || _duplicateScanActive || !_faceDetectionEnabled || TotalImages == 0)
        {
            return;
        }

        StartRecognitionProcessing();
    }

    private void StopBackgroundFaceDetection()
    {
        _faceDetectionEnabled = false;
        StatusText = "Stopping face detection…";
        RestartRecognitionAfterStageChange();
    }

    public async Task<bool> PauseFaceDetectionAsync()
    {
        var shouldResume = _faceDetectionEnabled;
        _faceDetectionEnabled = false;
        await StopRecognitionAsync();
        return shouldResume;
    }

    private async Task PauseRecognitionForDuplicateScanAsync()
    {
        _duplicateScanActive = true;
        await StopRecognitionAsync();
    }

    private void ResumeRecognitionAfterDuplicateScan()
    {
        _duplicateScanActive = false;
        StartRecognitionProcessing();
    }

    public void ResumeFaceDetection(bool shouldResume)
    {
        if (!shouldResume)
        {
            return;
        }

        _faceDetectionEnabled = true;
        StartBackgroundFaceDetection();
    }

    /// <summary>
    /// Cancels the single recognition job and waits for it to unwind, so both
    /// stages are stopped before the caller touches the library.
    /// </summary>
    private async Task StopRecognitionAsync()
    {
        Task? running;
        lock (_recognitionLock)
        {
            _recognitionRescanRequested = false;
            _recognitionCts?.Cancel();
            running = _recognitionTask;
        }

        if (running is not null)
        {
            try
            {
                await running;
            }
            catch (OperationCanceledException)
            {
            }
        }

        SetRecognitionStage(RecognitionStage.None, false);
    }

    public async Task PauseRecognitionForFolderChangeAsync()
    {
        _libraryFoldersChanging = true;
        await StopRecognitionAsync();
    }

    public void ResumeRecognitionAfterFolderChange()
    {
        _libraryFoldersChanging = false;
        StartRecognitionProcessing();
    }

    private async Task PauseRecognitionForAutoTagReviewAsync()
    {
        _autoTagReviewActive = true;
        await StopRecognitionAsync();
    }

    private void ResumeRecognitionAfterAutoTagReview()
    {
        _autoTagReviewActive = false;
        if (!_isShuttingDown && (_faceDetectionEnabled || _autoTaggingEnabled))
        {
            StartRecognitionProcessing();
        }
    }

    /// <summary>
    /// Applies an enable/disable change to the running job by restarting it
    /// with the new stage set.
    /// </summary>
    private async void RestartRecognitionAfterStageChange()
    {
        await StopRecognitionAsync();
        if (!_isShuttingDown && (_faceDetectionEnabled || _autoTaggingEnabled))
        {
            StartRecognitionProcessing();
        }
    }

    public void NotifyUserActivity()
    {
        _activityGate.NotifyUserActivity();
    }

    public void StartBackgroundAutoTagging()
    {
        if (_isShuttingDown || !_autoTaggingEnabled || TotalImages == 0)
        {
            return;
        }

        StartRecognitionProcessing();
    }

    private async void OnAutoTaggingSettingsChanged(
        object? sender,
        AutoTaggingSettingsChangedEventArgs e)
    {
        _autoTaggingEnabled = false;
        await StopRecognitionAsync();

        _autoTaggingEnabled = e.Settings.CanRun;
        if (_autoTaggingEnabled || _faceDetectionEnabled)
        {
            StartRecognitionProcessing();
        }

        if (!_autoTaggingEnabled)
        {
            Settings.SetAutoTaggingProgress(
                "Automatic tagging is off",
                false);
        }
    }

    public async Task CleanupHierarchicalTagsAsync()
    {
        if (Settings.IsHierarchyTagCleanupRunning ||
            Settings.IsAutoTagCleanupRunning ||
            Settings.IsIndexMaintenanceRunning ||
            IsIndexing)
        {
            Settings.HierarchyTagCleanupStatus =
                LibraryIndexMaintenanceStrings.Get("HierarchyCleanupBusy");
            return;
        }
        Settings.IsHierarchyTagCleanupRunning = true;
        Settings.HierarchyTagCleanupProgress = 0;
        Settings.HierarchyTagCleanupStatus =
            LibraryIndexMaintenanceStrings.Get("HierarchyCleanupStarting");
        using var cancellation = new CancellationTokenSource();
        _hierarchyCleanupCancellation = cancellation;
        // Cleanup reindexes each photo it writes; the watcher would otherwise reindex it again and
        // hold the file exclusively while cleanup is writing the next one.
        var watcherSuspension = _watchedChanges.SuspendProcessing();
        try
        {
            await StopRecognitionAsync();
            var backups = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PhotoLibrarian", "TagCleanupBackups");
            var cleanup = new HierarchicalTagCleanupService(_imageRepo, _tagRepo, _indexingService, backups);
            var progress = new Progress<HierarchicalTagCleanupProgress>(ReportHierarchyCleanupProgress);
            var result = await Task.Run(() => cleanup.CleanupAsync(progress, cancellation.Token));
            await _watchedChanges.DiscardReindexedAsync(result.ReindexedPaths ?? []);
            await RefreshAfterIndexAsync();
            await MetadataPanel.ReloadTagsAsync();
            var status = LibraryIndexMaintenanceStrings.Format(
                result.Cancelled ? "HierarchyCleanupCancelled" : "HierarchyCleanupCompleted",
                result.RemovedTags, result.UpdatedPhotos,
                result.ScannedPhotos, result.BackupDirectory, result.ReconciledPhotos);
            if (result.Failures.Count > 0)
            {
                status += Environment.NewLine + LibraryIndexMaintenanceStrings.Format(
                    "HierarchyCleanupFailures", result.Failures.Count);
                status += Environment.NewLine + string.Join(Environment.NewLine, result.Failures.Take(20));
                foreach (var failure in result.Failures)
                    DebugLog.WriteLine($"Hierarchy tag cleanup issue: {failure}");
            }
            Settings.HierarchyTagCleanupStatus = status;
            StatusText = status;
        }
        catch (Exception exception)
        {
            DebugLog.WriteLine($"Hierarchy tag cleanup failed: {exception}");
            Settings.HierarchyTagCleanupStatus =
                LibraryIndexMaintenanceStrings.Format(
                    "HierarchyCleanupFailed", HierarchicalTagCleanupService.DescribeFailure(exception));
            StatusText = Settings.HierarchyTagCleanupStatus;
        }
        finally
        {
            _hierarchyCleanupCancellation = null;
            watcherSuspension.Dispose();
            Settings.IsHierarchyTagCleanupRunning = false;
            StartRecognitionProcessing();
        }
    }

    public void CancelHierarchicalTagCleanup()
    {
        if (_hierarchyCleanupCancellation is not { } cancellation) return;
        cancellation.Cancel();
        Settings.HierarchyTagCleanupStatus =
            LibraryIndexMaintenanceStrings.Get("HierarchyCleanupCancelling");
    }

    private void ReportHierarchyCleanupProgress(HierarchicalTagCleanupProgress update)
    {
        if (_hierarchyCleanupCancellation is not { IsCancellationRequested: false }) return;
        Settings.HierarchyTagCleanupProgress = update.Total > 0
            ? 100d * update.Processed / update.Total
            : 0;
        Settings.HierarchyTagCleanupStatus = LibraryIndexMaintenanceStrings.Format(
            "HierarchyCleanupProgress", update.Processed, update.Total,
            update.UpdatedPhotos, update.ReconciledPhotos, update.FailedPhotos,
            Path.GetFileName(update.CurrentPath));
        StatusText = Settings.HierarchyTagCleanupStatus;
    }

    public async Task RemoveAllAutomaticTagsAsync(
        CancellationToken cancellationToken = default)
    {
        Settings.IsAutoTagCleanupRunning = true;
        Settings.AutoTaggingStatus =
            "Stopping automatic tagging before removing generated tags…";

        try
        {
            Settings.IsAutoTaggingEnabled = false;
            _autoTaggingEnabled = false;
            await StopRecognitionAsync();

            var affectedImages = await _tagRepo.GetImagesWithAutoTagMetadataAsync(
                cancellationToken);
            var result = await _tagRepo.RemoveAllAutoTagsAsync(
                cancellationToken);
            var metadataFailures = 0;
            string? firstMetadataFailure = null;
            foreach (var image in affectedImages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!System.IO.File.Exists(image.FilePath))
                    {
                        throw new FileNotFoundException(
                            "The photo is no longer available.",
                            image.FilePath);
                    }

                    var remainingTags = await _tagRepo.GetTagsAsync(image.Id);
                    await TagWriterService.WriteTagsToSidecarAsync(
                        image.FilePath,
                        remainingTags.Select(tag => tag.Tag),
                        remainingTags,
                        autoTagScanVersion: null);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    metadataFailures++;
                    firstMetadataFailure ??=
                        $"{image.FilePath}: {exception.Message}";
                    DebugLog.WriteLine(
                        $"Failed to remove automatic tag metadata from {image.FilePath}: {exception}");
                }
            }

            await RefreshTagsTreeAsync();
            await MetadataPanel.ReloadTagsAsync();

            var status =
                $"Removed {result.TagCount:N0} automatic tag assignments from {result.PhotoCount:N0} photos. Automatic tagging is off.";
            if (metadataFailures > 0)
            {
                status +=
                    $" Metadata was not updated for {metadataFailures:N0} photo(s); first failure: {firstMetadataFailure}";
            }
            Settings.AutoTaggingStatus = status;
            StatusText = status;
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception)
        {
            var status =
                $"Automatic tags could not be removed: {exception.Message}";
            Settings.AutoTaggingStatus = status;
            StatusText = status;
        }
        finally
        {
            Settings.IsAutoTagCleanupRunning = false;
        }
    }

    private void SetRecognitionStage(RecognitionStage stage, bool isRunning)
    {
        ActiveRecognitionStage = stage;
        IsRecognitionRunning = isRunning;
        IsFaceDetectionRunning =
            isRunning && stage == RecognitionStage.FaceDetection;
        IsAutoTaggingRunning =
            isRunning && stage == RecognitionStage.AutoTagging;
    }

    /// <summary>
    /// Single status surface for the unified job: reports the active stage and
    /// the combined progress of the one background pass.
    /// </summary>
    private void OnRecognitionProgress(
        object? sender,
        RecognitionProgressEventArgs e)
    {
        if (_isShuttingDown) return;

        App.MainWindow?.DispatcherQueue.TryEnqueue(async () =>
        {
            if (_isShuttingDown) return;

            SetRecognitionStage(e.Stage, e.IsRunning);

            if (e.IsPreparing)
            {
                StatusText = "Preparing recognition models…";
                if (_autoTaggingEnabled)
                {
                    Settings.SetAutoTaggingProgress(
                        "Preparing recognition models…",
                        true);
                }

                return;
            }

            if (e.IsCanceled)
            {
                var pausedStatus = $"Recognition paused after {e.Processed:N0} photos";
                StatusText = pausedStatus;
                Settings.SetAutoTaggingProgress(
                    pausedStatus,
                    false,
                    e.Processed,
                    e.Total);
                return;
            }

            if (e.IsComplete)
            {
                var completeStatus = e.Total == 0
                    ? "Faces and tags are up to date"
                    : $"Recognition complete: {e.FacesFound:N0} faces and {e.TagsAdded:N0} tags across {e.Processed:N0} photos";
                StatusText = e.Failed == 0
                    ? completeStatus
                    : $"{completeStatus} ({e.Failed:N0} photos failed)";
                Settings.SetAutoTaggingProgress(
                    _autoTaggingEnabled ? completeStatus : "Automatic tagging is off",
                    false,
                    e.Processed,
                    e.Total);
                await RefreshTagsTreeAsync();
                _lastAutoTagTreeRefresh = 0;
                return;
            }

            if (!string.IsNullOrEmpty(e.Error))
            {
                StatusText = $"Skipped {e.CurrentFile}: {e.Error}";
            }
            else
            {
                var stageLabel = e.Stage switch
                {
                    RecognitionStage.FaceDetection => "Finding faces",
                    RecognitionStage.AutoTagging => "Tagging photos",
                    _ => "Processing photos"
                };
                StatusText =
                    $"{stageLabel}… {e.Processed:N0}/{e.Total:N0} photos, {e.FacesFound:N0} faces, {e.TagsAdded:N0} tags";
            }

            if (_autoTaggingEnabled)
            {
                Settings.SetAutoTaggingProgress(
                    $"Recognizing photos… {e.Processed:N0}/{e.Total:N0} ({e.TagsAdded:N0} tag suggestions)",
                    true,
                    e.Processed,
                    e.Total);
            }

            if (e.TagsAdded > 0 &&
                (e.Processed == 1 || e.Processed - _lastAutoTagTreeRefresh >= 25))
            {
                _lastAutoTagTreeRefresh = e.Processed;
                await RefreshTagsTreeAsync();
            }
        });
    }

    private void OnIndexingProgress(object? sender, IndexingProgressEventArgs e)
    {
        if (_isShuttingDown) return;

        App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            if (_isShuttingDown) return;

            IsIndexing = !e.IsComplete;
            StatusText = e.IsComplete
                ? $"Indexed {e.Processed:N0} new items ({e.Skipped:N0} unchanged)"
                : $"Indexing… {e.Processed:N0} processed";
        });
    }

    /// <summary>
    /// Sets or clears the flag on a set of images. Single entry point used by the grid, the
    /// viewer, the context menu and the metadata panel: writes the file metadata, updates the
    /// cache DB, then refreshes the "Flagged" node count and any dependent UI.
    /// </summary>
    public async Task SetFlagAsync(IReadOnlyList<ImageEntry> entries, bool flagged)
    {
        if (entries is null || entries.Count == 0) return;
        await RunPhotoEditAsync(flagged ? "flag photos" : "unflag photos",
            entries.Select(entry => entry.FilePath).ToArray(), async copies =>
        {
            foreach (var path in copies.Values)
                await MetadataWriterService.WriteFlagAsync(path, flagged);
        });
    }

    /// <summary>
    /// Reloads the flagged count and repaints the left-panel "Flagged" node.
    /// </summary>
    public async Task RefreshFlagNavAsync()
    {
        await FlagNav.LoadAsync();
        await ColorLabelNav.LoadAsync();
        App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            if (App.MainWindow is MainWindow window)
                window.RefreshFlagTree();
        });
    }

    /// <summary>
    /// Refreshes the tag navigation tree (counts + new tags) — call after tag edits.
    /// </summary>
    public async Task RefreshTagsTreeAsync()
    {
        if (ImageGrid.HasTagDependentFilter)
            await ImageGrid.LoadImagesAsync();

        if (App.MainWindow is MainWindow window)
        {
            await window.RefreshTagsTreeAsync();
        }
        else
        {
            await TagNav.LoadTagsAsync();
            await NotTaggedNav.LoadAsync();
        }
    }

    /// <summary>
    /// Refreshes the date navigation tree and re-applies grid grouping/sorting — call after
    /// capture-date edits so the left-panel date filter and the grid's order/grouping reflect
    /// the new dates without a full reload.
    /// </summary>
    public async Task RefreshAfterDateChangeAsync()
    {
        await DateNav.LoadDatesAsync();
        if (App.MainWindow is MainWindow window)
        {
            await window.RefreshMetadataTreesAsync();
            await ImageGrid.RefreshGroupingAsync();
        }
    }

    /// <summary>
    /// Applies a tag to a set of images identified by their file paths. Used by the drag-to-tag
    /// drop target on the tags tree. Writes to DB and to the image file (or sidecar for RAW),
    /// then refreshes the tag tree and the metadata panel.
    /// </summary>
    public async Task ApplyTagToImagePathsAsync(string tag, IReadOnlyList<string> filePaths)
    {
        if (string.IsNullOrWhiteSpace(tag) || filePaths == null || filePaths.Count == 0) return;
        var trimmed = tag.Trim();

        await RunPhotoEditAsync($"add tag '{trimmed}'", filePaths.ToArray(), async copies =>
        {
            foreach (var (path, copy) in copies)
            {
                var entry = await _imageRepo.GetByPathAsync(path);
                if (entry is null || entry.Id <= 0)
                    throw new IOException($"The photo is no longer indexed: {path}");
                var allTags = await _tagRepo.GetTagsAsync(entry.Id);
                allTags.RemoveAll(item => string.Equals(item.Tag, trimmed, StringComparison.OrdinalIgnoreCase));
                allTags.Add(new ImageTag
                {
                    ImageId = entry.Id,
                    Tag = trimmed,
                    Source = TagSource.Manual,
                    Confidence = 1.0f
                });
                await TagWriterService.WriteTagsToSidecarAsync(copy,
                    allTags.Select(t => t.Tag).Distinct(),
                    allTags,
                    entry.AutoTagScanVersion,
                    sourceIdentity: entry.SourceIdentity);
            }
        });
    }

    /// <summary>
    /// Called after the image file at <paramref name="filePath"/> has been cropped on disk.
    /// Updates DB dimensions, refreshes the grid thumbnail and the open viewer.
    /// </summary>
    public async Task RefreshAfterCropAsync(string filePath, CropResult result)
    {
        var entry = await _imageRepo.GetByPathAsync(filePath);
        if (entry is not null && entry.Id > 0)
        {
            await _faceRepo.RemapFaceRegionsAfterCropAsync(
                entry.Id,
                result.SourceWidth,
                result.SourceHeight,
                result.Bounds);
        }

        await RefreshAfterPixelEditAsync(
            filePath,
            result.Width,
            result.Height,
            "Cropped",
            invalidateFaceScan: false);
    }

    public Task RefreshAfterStraightenAsync(
        string filePath,
        uint newPixelWidth,
        uint newPixelHeight) =>
        RefreshAfterPixelEditAsync(filePath, newPixelWidth, newPixelHeight, "Straightened");

    /// <summary>
    /// Called after the pixels of <paramref name="filePath"/> have been rewritten (crop, baked
    /// adjustments). Updates DB dimensions, refreshes the grid thumbnail and the open viewer.
    /// Both paths write display-oriented pixels and reset the EXIF orientation tag to 1.
    /// </summary>
    public async Task RefreshAfterPixelEditAsync(
        string filePath,
        uint newPixelWidth,
        uint newPixelHeight,
        string verb,
        bool invalidateFaceScan = true)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        try
        {
            var entry = await _imageRepo.GetByPathAsync(filePath);
            if (entry != null && entry.Id > 0)
            {
                var fileInfo = new System.IO.FileInfo(filePath);
                var size = fileInfo.Length;
                await _imageRepo.UpdateDimensionsAsync(
                    entry.Id,
                    (int)newPixelWidth,
                    (int)newPixelHeight,
                    size,
                    fileInfo.LastWriteTimeUtc,
                    invalidateFaceScan);
                entry.Width = (int)newPixelWidth;
                entry.Height = (int)newPixelHeight;
                entry.Orientation = 1;
                entry.FileSize = size;
                entry.DateModified = fileInfo.LastWriteTimeUtc;
            }
            await ImageGrid.RefreshSingleImageAsync(filePath);
            await ImageViewer.ReloadCurrentImageAsync();
            StartBackgroundFaceDetection();
            StatusText = $"{verb} {System.IO.Path.GetFileName(filePath)} ({newPixelWidth}×{newPixelHeight})";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EDIT] RefreshAfterPixelEditAsync failed: {ex.Message}");
            StatusText = $"Refresh failed: {ex.Message}";
        }
    }

    public async Task IndexCroppedCopyAsync(string path)
    {
        await _indexingService.IndexFileAsync(path);
        if (await _imageRepo.GetByPathAsync(path) is null)
            throw new InvalidOperationException($"The cropped copy at '{path}' could not be added to the library.");
        await RefreshAfterIndexAsync();
        StatusText = $"Original is read-only; saved cropped copy as {Path.GetFileName(path)}";
    }

    public async Task IndexRawDerivativeAsync(string path)
    {
        await _indexingService.IndexFileAsync(path);
        if (await _imageRepo.GetByPathAsync(path) is null)
            throw new InvalidOperationException($"The derivative at '{path}' could not be added to the library.");
        await RefreshAfterIndexAsync();
    }

    public async Task IndexPanoramaAsync(string path)
    {
        await _indexingService.IndexFileAsync(path);
        var entry = await _imageRepo.GetByPathAsync(path)
            ?? throw new InvalidOperationException(
                $"The panorama at '{path}' could not be added to the library.");
        ImageGrid.RevealExportedImage(entry.FilePath);
        await RefreshAfterIndexAsync();
        ImageViewer.OpenImage(entry, ImageGrid.GetViewerImages());
        StatusText = PanoramaStrings.Format("PanoramaCreatedStatus", entry.FileName);
    }

    public Task OpenStabilizedVideoAsync(string path) => OpenVideoCopyAsync(path, edited: false);

    public Task OpenEditedVideoAsync(string path) => OpenVideoCopyAsync(path, edited: true);

    private async Task OpenVideoCopyAsync(string path, bool edited)
    {
        await _indexingService.IndexFileAsync(path);
        var copy = await _imageRepo.GetByPathAsync(path)
            ?? throw new InvalidOperationException($"The copy at '{path}' could not be added to the library.");
        ImageGrid.RevealExportedVideo(copy.FilePath);
        await RefreshAfterIndexAsync();
        copy = ImageGrid.Images.FirstOrDefault(image =>
            string.Equals(image.Entry.FilePath, path, StringComparison.OrdinalIgnoreCase))?.Entry
            ?? throw new InvalidOperationException($"The copy at '{path}' could not be shown in the grid.");
        ImageViewer.OpenExportedVideo(copy);
        StatusText = edited ? Services.VideoEditStrings.Format("VideoEditOpened", path) : $"Opened stabilized copy: {path}";
    }

    public async Task OpenTrimmedVideoAsync(string path)
    {
        await _indexingService.IndexFileAsync(path);
        var copy = await _imageRepo.GetByPathAsync(path)
            ?? throw new InvalidOperationException($"The trimmed copy at '{path}' could not be added to the library.");
        ImageGrid.RevealExportedVideo(copy.FilePath);
        await RefreshAfterIndexAsync();
        copy = ImageGrid.Images.FirstOrDefault(image =>
            string.Equals(image.Entry.FilePath, path, StringComparison.OrdinalIgnoreCase))?.Entry
            ?? throw new InvalidOperationException($"The trimmed copy at '{path}' could not be shown in the grid.");
        ImageViewer.OpenExportedVideo(copy);
        StatusText = $"Opened trimmed copy: {path}";
    }

    public async Task RevealPhotoFuseCopyAsync(string path)
    {
        await _indexingService.IndexFileAsync(path);
        var copy = await _imageRepo.GetByPathAsync(path)
            ?? throw new InvalidOperationException($"The composite at '{path}' could not be added to the library.");
        if (copy.MediaType != MediaType.Image)
            throw new InvalidOperationException("The saved Photo Fuse copy is not a still image.");
        // The existing transient export reveal is media-neutral despite its historical name.
        ImageGrid.RevealExportedVideo(copy.FilePath);
        await RefreshAfterIndexAsync();
        copy = ImageGrid.Images.FirstOrDefault(image =>
            string.Equals(image.Entry.FilePath, path, StringComparison.OrdinalIgnoreCase))?.Entry
            ?? throw new InvalidOperationException($"The composite at '{path}' could not be shown in the grid.");
        ImageGrid.SelectViewerEntry(copy);
    }

    public async Task MakeCopyForEditingAsync(ImageEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.MediaType != MediaType.Image)
            throw new NotSupportedException("Only photos can be copied for editing.");

        var copyPath = await Services.PhotoOperationsService.MakeCopyForEditingAsync(entry.FilePath);
        var folderPath = System.IO.Path.GetDirectoryName(copyPath)
            ?? throw new InvalidOperationException("The copied photo has no parent folder.");
        await _indexingService.IndexFolderAsync(folderPath, includeSubfolders: false);

        var copyEntry = await _imageRepo.GetByPathAsync(copyPath)
            ?? throw new InvalidOperationException(
                $"The copy was created at '{copyPath}' but could not be added to the library.");

        await RefreshAfterIndexAsync();

        var viewerEntries = ImageGrid.GetViewerImages();
        var visibleCopy = viewerEntries.FirstOrDefault(image => image.Id == copyEntry.Id);
        if (visibleCopy is not null)
        {
            copyEntry = visibleCopy;
        }
        else
        {
            viewerEntries.Add(copyEntry);
        }
        ImageViewer.OpenImage(copyEntry, viewerEntries);
        StatusText = $"Created copy {copyEntry.FileName}; edits will not change the original";
    }

    public async Task RefreshAfterIndexAsync()
    {
        await ImageGrid.LoadImagesAsync();
        TotalImages = await _imageRepo.GetCountAsync(watchedOnly: true);
        StatusText = $"{TotalImages:N0} items";
        await RefreshNavigationAfterIndexAsync();
        StartBackgroundFaceDetection();
        StartBackgroundAutoTagging();
    }

    private async Task RefreshNavigationAfterIndexAsync()
    {
        await DateNav.LoadDatesAsync();
        await PeopleNav.LoadPeopleAsync();
        await TagNav.LoadTagsAsync();
        await FlagNav.LoadAsync();
        await ColorLabelNav.LoadAsync();
        await NotTaggedNav.LoadAsync();

        // Update UI trees on main thread
        App.MainWindow?.DispatcherQueue.TryEnqueue(async () =>
        {
            if (App.MainWindow is MainWindow window)
            {
                await window.RefreshMetadataTreesAsync();
            }
        });
    }

    public async Task CleanupAsync()
    {
        await EditHistory.WaitForIdleAsync();
        Search.Cleanup();
        DebugLog.WriteLine("MainViewModel: Cleanup - canceling background tasks");
        _isShuttingDown = true;
        await AutoTagReview.ShutdownAsync();
        await MetadataPanel.StopVideoMetadataReadsAsync();
        _watchedChanges.Dispose();
        _scanner.WatcherError -= OnWatcherError;
        _indexingCts?.Cancel();
        _indexingCts?.Dispose();
        _faceDetectionEnabled = false;
        _autoTaggingEnabled = false;
        await StopRecognitionAsync();
        lock (_recognitionLock)
        {
            _recognitionCts?.Dispose();
            _recognitionCts = null;
        }

        _recognitionPipeline.Progress -= OnRecognitionProgress;
        Settings.AutoTaggingSettingsChanged -= OnAutoTaggingSettingsChanged;
        _faceResources.Dispose();
        EditHistory.Clear();
        _scanner.Dispose();
        DebugLog.WriteLine("MainViewModel: Cleanup complete");
        Core.Diagnostics.DebugLog.Flush(TimeSpan.FromSeconds(1));
    }
}
