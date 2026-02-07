using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using System.Collections.ObjectModel;

namespace PhotoLibrarian.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly CacheDatabase _db;
    private readonly ImageRepository _imageRepo;
    private readonly ThumbnailRepository _thumbRepo;
    private readonly FolderScannerService _scanner;
    private readonly MetadataReaderService _metadataReader;
    private readonly ThumbnailService _thumbnailService;
    private readonly LibraryIndexingService _indexingService;
    private readonly OriginalBackupService _backupService;

    public FolderNavigationViewModel FolderNav { get; }
    public ImageGridViewModel ImageGrid { get; }
    public ImageViewerViewModel ImageViewer { get; }
    public ImageEditorViewModel ImageEditor { get; }
    public MetadataPanelViewModel MetadataPanel { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    public partial bool IsIndexing { get; set; }

    [ObservableProperty]
    public partial int TotalImages { get; set; }

    public MainViewModel(
        CacheDatabase db,
        ImageRepository imageRepo,
        ThumbnailRepository thumbRepo,
        FolderScannerService scanner,
        MetadataReaderService metadataReader,
        ThumbnailService thumbnailService,
        LibraryIndexingService indexingService,
        OriginalBackupService backupService)
    {
        _db = db;
        _imageRepo = imageRepo;
        _thumbRepo = thumbRepo;
        _scanner = scanner;
        _metadataReader = metadataReader;
        _thumbnailService = thumbnailService;
        _indexingService = indexingService;
        _backupService = backupService;

        StatusText = "Ready";

        FolderNav= new FolderNavigationViewModel(db, scanner, indexingService, this);
        ImageGrid = new ImageGridViewModel(imageRepo, thumbRepo, thumbnailService, this);
        ImageViewer = new ImageViewerViewModel();
        ImageEditor = new ImageEditorViewModel(backupService);
        MetadataPanel = new MetadataPanelViewModel();
        Settings = new SettingsViewModel(db);

        _indexingService.Progress += OnIndexingProgress;
    }

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        await FolderNav.LoadWatchedFoldersAsync();
        await ImageGrid.LoadImagesAsync();
        TotalImages = await _imageRepo.GetCountAsync();
        StatusText = TotalImages > 0 ? $"{TotalImages:N0} items" : "Add folders to get started";
    }

    private void OnIndexingProgress(object? sender, IndexingProgressEventArgs e)
    {
        App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            IsIndexing = !e.IsComplete;
            StatusText = e.IsComplete
                ? $"Indexed {e.Processed:N0} new items ({e.Skipped:N0} unchanged)"
                : $"Indexing… {e.Processed:N0} processed";
        });
    }

    public async Task RefreshAfterIndexAsync()
    {
        await ImageGrid.LoadImagesAsync();
        TotalImages = await _imageRepo.GetCountAsync();
        StatusText = $"{TotalImages:N0} items";
    }
}
