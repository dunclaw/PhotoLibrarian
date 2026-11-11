// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Input;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using PhotoLibrarian.ViewModels;
using PhotoLibrarian.Views;
using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.Graphics.Printing;

namespace PhotoLibrarian;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel => App.ViewModel;

    private CropAspectRatio _pendingAspect = CropAspectRatio.Free;
    private bool _isClosing;
    private Control? _editHistoryFocus;
    private SlideshowWindow? _slideshow;
    private Control? _slideshowReturnFocus;
    private double _slideshowIntervalSeconds = 5;
    private Core.Models.SlideshowTheme _slideshowTheme = Core.Models.SlideshowTheme.Contemporary;
    private PhotoPrintSession? _printSession;
    private bool _photoFuseOpen;
    private bool _photoFuseFinishing;

    private async void OnPhotoFuseClick(object? sender, EventArgs args)
    {
        if (_photoFuseFinishing) return;
        if (ViewModel.PhotoFuse.IsActive)
        {
            await FinishPhotoFuseAsync();
            return;
        }
        if (_photoFuseOpen || ViewModel.ImageViewer.IsApplyingEffect || !CanPrintPhotos() ||
            ViewModel.ImageViewer.CurrentEntry is not { MediaType: Core.Models.MediaType.Image } basis) return;
        _photoFuseOpen = true;
        TopRibbon.PhotoFuseButtonEnabled(false);
        try
        {
            await ViewModel.PauseRecognitionForPhotoFuseAsync();
            if (_isClosing || !_photoFuseOpen) return;
            ViewModel.ImageViewer.IsPhotoFuseActive = true;
            ViewModel.ImageViewer.IsInformationPaneVisible = true;
            ViewModel.MetadataPanel.IncludeUnnamedFaces = true;
            ViewerOverlay.SetPhotoFuseWorkspace(ViewModel.PhotoFuse);
            TopRibbon.SetPhotoFuseMode(true);
            var preparing = ViewModel.PhotoFuse.StartAsync(basis);
            TopRibbon.PhotoFuseButtonEnabled(true);
            await preparing;
            if (ViewModel.PhotoFuse.IsActive)
                await ViewModel.MetadataPanel.ReloadPeopleTagsAsync();
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = PhotoFuseStrings.Format("FuseStartFailed", ex.Message);
            await FinishPhotoFuseAsync();
        }
        finally { TopRibbon.PhotoFuseButtonEnabled(true); }
    }

    private async Task FinishPhotoFuseAsync(bool discard = false)
    {
        if (_photoFuseFinishing) return;
        _photoFuseFinishing = true;
        string? saved = null;
        try
        {
            if (discard) await ViewModel.PhotoFuse.DiscardAsync();
            else saved = await ViewModel.PhotoFuse.FinishAsync();
            ViewModel.ImageViewer.IsPhotoFuseActive = false;
            ViewerOverlay.SetPhotoFuseWorkspace(null);
            TopRibbon.SetPhotoFuseMode(false);
            ViewModel.MetadataPanel.IncludeUnnamedFaces = false;
            _photoFuseOpen = false;
            ViewModel.ResumeRecognitionAfterPhotoFuse();
            if (saved is not null)
            {
                await ViewModel.RevealPhotoFuseCopyAsync(saved);
                var copy = ViewModel.ImageGrid.GetViewerImages().FirstOrDefault(image =>
                    string.Equals(image.FilePath, saved, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("The saved Photo Fuse copy could not be opened in the viewer.");
                ViewModel.ImageViewer.OpenImage(copy, ViewModel.ImageGrid.GetViewerImages());
                ViewModel.StatusText = PhotoFuseStrings.Format("FuseSaved", saved);
            }
            else ViewModel.StatusText = PhotoFuseStrings.Get("FuseClosed");
            await ViewModel.MetadataPanel.ReloadPeopleTagsAsync();
        }
        catch (Exception ex)
        {
            // A failed save stays in the workspace with the accepted choices intact.
            ViewModel.PhotoFuse.Status = saved is null
                ? PhotoFuseStrings.Format("FuseSaveFailed", ex.Message)
                : PhotoFuseStrings.Format("FuseIndexFailed", saved, ex.Message);
            ViewModel.StatusText = ViewModel.PhotoFuse.Status;
        }
        finally { _photoFuseFinishing = false; }
    }

    private async void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (!_photoFuseOpen) return;
        args.Cancel = true;
        if (_photoFuseFinishing) return;
        await FinishPhotoFuseAsync();
        if (!_photoFuseOpen) Close();
    }
    private RemovableVolumeMonitor? _removableVolumeMonitor;
    private readonly Queue<string> _detectedDeviceRoots = new();
    private string? _pendingDetectedDeviceRoot;

    private async void OnPrintPhotosInvoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!CanPrintPhotos()) return;
        args.Handled = true;
        await PrintSelectedPhotosAsync();
    }

    private bool CanPrintPhotos() =>
        !(_isClosing || _photoFuseOpen || ViewModel.Settings.IsOpen || ViewModel.PeopleReview.IsOpen ||
          ViewModel.DuplicateReview.IsOpen || ViewModel.ImageEditor.IsOpen ||
          ViewerOverlay.IsCropping || ViewerOverlay.IsStraightening ||
          ViewerOverlay.IsRedEyeRemoving || ViewerOverlay.IsManualFaceTagging);

    private async Task PrintSelectedPhotosAsync()
    {
        if (_printSession is not null)
        {
            ViewModel.StatusText = PhotoPrintStrings.Get("PrintAlreadyOpen");
            return;
        }

        var photos = GetPhotosForPrinting();
        if (photos.Count == 0)
        {
            await ShowPrintErrorAsync(PhotoPrintStrings.Get("PrintSelectionRequired"));
            return;
        }

        var unsupported = photos
            .Where(photo => photo.MediaType != Core.Models.MediaType.Image ||
                !PhotoPrintImageDecoder.IsSupported(photo.FilePath))
            .ToList();
        if (unsupported.Count > 0)
        {
            var rawSelected = unsupported.Any(photo => RawToneMappingService.IsRaw(photo.FilePath));
            var details = rawSelected
                ? PhotoPrintStrings.Get("PrintRawUnsupported")
                : PhotoPrintStrings.Get("PrintUnsupportedFormats");
            var filenames = string.Join(Environment.NewLine, unsupported.Select(photo => photo.FileName));
            await ShowPrintErrorAsync($"{details}{Environment.NewLine}{filenames}");
            return;
        }

        if (!PrintManager.IsSupported())
        {
            await ShowPrintErrorAsync(PhotoPrintStrings.Get("PrintNotSupported"));
            return;
        }

        try
        {
            var options = await new PhotoPrintOptionsDialog(this, photos).ShowAsync(CancellationToken.None);
            if (options is null) return;

            var session = new PhotoPrintSession(this, photos, options.Value.Layout, options.Value.Placement);
            session.Error += OnPrintError;
            session.Completed += OnPrintCompleted;
            _printSession = session;
            var shown = await session.ShowPrintUiAsync();
            if (!shown)
            {
                session.Dispose();
                _printSession = null;
                await ShowPrintErrorAsync(PhotoPrintStrings.Get("PrintUiUnavailable"));
            }
        }
        catch (Exception exception)
        {
            _printSession?.Dispose();
            _printSession = null;
            await ShowPrintErrorAsync(PhotoPrintStrings.Format("PrintFailed", exception.Message));
        }
    }

    private IReadOnlyList<Core.Models.ImageEntry> GetPhotosForPrinting()
    {
        var grid = ViewModel.ImageGrid;
        IReadOnlyList<Core.Models.ImageEntry> selected;
        if (grid.SelectedImages.Count > 0)
        {
            selected = grid.SelectedImages.Select(image => image.Entry).ToList();
        }
        else if (ViewModel.ImageViewer.IsOpen && ViewModel.ImageViewer.CurrentEntry is { } current)
        {
            selected = [current];
        }
        else if (grid.SelectedImage is { } selectedImage)
        {
            selected = [selectedImage.Entry];
        }
        else
        {
            selected = [];
        }

        return PhotoPrintLayoutService.OrderSelection(
            grid.DisplayedEntries,
            selected,
            photo => photo.FilePath);
    }

    private async Task ShowPrintErrorAsync(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = AppRoot.XamlRoot,
            Title = PhotoPrintStrings.Get("PrintErrorTitle"),
            Content = message,
            CloseButtonText = PhotoPrintStrings.Get("PrintClose")
        };
        await dialog.ShowAsync();
    }

    private void OnPrintError(object? sender, string message)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_isClosing) return;
            await ShowPrintErrorAsync(PhotoPrintStrings.Format("PrintFailed", message));
        });
    }

    private void OnPrintCompleted(object? sender, PrintTaskCompletion completion)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (sender is PhotoPrintSession session)
            {
                session.Error -= OnPrintError;
                session.Completed -= OnPrintCompleted;
                session.Dispose();
                if (ReferenceEquals(_printSession, session))
                    _printSession = null;
            }

            ViewModel.StatusText = completion == PrintTaskCompletion.Submitted
                ? PhotoPrintStrings.Get("PrintSubmitted")
                : PhotoPrintStrings.Get("PrintCanceled");
        });
    }

    private void OnSlideshowInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (CanStartSlideshow())
        {
            args.Handled = true;
            StartSlideshow(null);
        }
    }

    private bool CanStartSlideshow() =>
        !(_isClosing || _photoFuseOpen || ViewModel.Settings.IsOpen || ViewModel.PeopleReview.IsOpen ||
          ViewModel.DuplicateReview.IsOpen ||
          ViewModel.ImageEditor.IsOpen || ViewerOverlay.IsCropping ||
          ViewerOverlay.IsStraightening || ViewerOverlay.IsRedEyeRemoving ||
          ViewerOverlay.IsManualFaceTagging);

    /// <summary>Opens the slideshow. In the viewer it plays the viewer's photos from the photo on
    /// screen; in the grid it uses <paramref name="gridSelection"/> (or the grid's selection).</summary>
    private void StartSlideshow(IReadOnlyList<Core.Models.ImageEntry>? gridSelection)
    {
        if (!CanStartSlideshow()) return;
        if (_slideshow is not null)
        {
            _slideshow.Activate();
            return;
        }

        Core.Models.SlideshowSequence sequence;
        var viewer = ViewModel.ImageViewer;
        var grid = ViewModel.ImageGrid;
        var displayed = grid.DisplayedEntries;
        if (viewer.IsOpen && viewer.CurrentEntry is { } current)
        {
            var source = displayed.Any(entry =>
                string.Equals(entry.FilePath, current.FilePath, StringComparison.OrdinalIgnoreCase))
                ? displayed
                : viewer.Images;
            sequence = new Core.Models.SlideshowSequence(source, [], current.FilePath);
        }
        else
        {
            IEnumerable<Core.Models.ImageEntry> selection = gridSelection
                ?? (grid.SelectedImages.Count > 0
                    ? grid.SelectedImages.Select(image => image.Entry)
                    : grid.SelectedImage is { } selected ? [selected.Entry] : []);
            sequence = new Core.Models.SlideshowSequence(displayed, selection);
        }
        if (sequence.Count == 0)
        {
            ViewModel.StatusText = "No photos available for a slideshow in the current selection or filtered set.";
            return;
        }

        ViewerOverlay.PauseVideo();
        _slideshowReturnFocus = FocusManager.GetFocusedElement(AppRoot.XamlRoot) as Control;
        _slideshow = new SlideshowWindow(sequence, _slideshowIntervalSeconds, _slideshowTheme);
        _slideshow.Closed += OnSlideshowClosed;
        _slideshow.Activate();
    }

    private void OnSlideshowClosed(object sender, WindowEventArgs args)
    {
        if (_slideshow is null) return;
        _slideshowIntervalSeconds = _slideshow.ViewModel.IntervalSeconds;
        _slideshowTheme = _slideshow.ViewModel.Theme;
        TopRibbon.SetSlideshowTheme(_slideshowTheme);
        _slideshow.Closed -= OnSlideshowClosed;
        _slideshow = null;
        if (!_isClosing)
        {
            Activate();
            _slideshowReturnFocus?.Focus(FocusState.Programmatic);
        }
        _slideshowReturnFocus = null;
    }

    public async Task RefreshMetadataTreesAsync()
    {
        await FolderNavPanel.RefreshMetadataTreesAsync();
        await ImageGridPanel.RefreshPeopleAsync();
    }

    private async void LibrarySearchBox_TextChanged(
        AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            await ViewModel.Search.UpdateSuggestionsAsync(sender.Text);
    }

    private async void LibrarySearchBox_QuerySubmitted(
        AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) =>
        await ViewModel.Search.SubmitAsync(args.QueryText);

    public async Task RefreshPeopleFiltersAsync()
    {
        await FolderNavPanel.RefreshPeopleTreeAsync();
        await ImageGridPanel.RefreshPeopleAsync();
    }

    public Task RefreshTagsTreeAsync() =>
        FolderNavPanel.RefreshTagsTreeAsync();

    public void BeginManualFaceTagging()
    {
        ViewerOverlay.EnterManualFaceTagging();
        UpdateHistoryControls();
    }

    public void CancelManualFaceTagging()
    {
        ViewerOverlay.ExitManualFaceTagging();
        UpdateHistoryControls();
    }

    /// <summary>Forwards a hovered people-tag row to whichever surface is showing that image
    /// (the full viewer if it's open on that entry, and/or the grid thumbnail).</summary>
    public void SetHoveredFace(Core.Models.ImageEntry? entry, Core.Models.FaceRegion? region)
    {
        ViewerOverlay.SetFaceHighlight(
            entry != null && ViewModel.ImageViewer.CurrentEntry?.Id == entry.Id ? region : null);

        var thumbnail = entry is null
            ? null
            : ViewModel.ImageGrid.Images.FirstOrDefault(image => image.Entry.Id == entry.Id);
        ImageGridPanel.SetFaceHighlight(thumbnail, thumbnail is null ? null : region);
    }

    /// <summary>Repaints the left-panel "Flagged" node (count changes after flag edits).</summary>
    public void RefreshFlagTree()
    {
        FolderNavPanel.RefreshFlagTree();
    }

    public MainWindow()
    {
        this.InitializeComponent();
        AppRoot.AddHandler(
            UIElement.PointerMovedEvent,
            new PointerEventHandler(OnUserPointerActivity),
            true);
        AppRoot.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(OnUserPointerActivity),
            true);
        AppRoot.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(OnUserPointerPressed),
            true);
        AppRoot.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnUserPointerActivity),
            true);
        AppRoot.AddHandler(
            UIElement.KeyDownEvent,
            new KeyEventHandler(OnUserKeyActivity),
            true);

        var appWindow = this.AppWindow;
        appWindow.Closing += OnAppWindowClosing;
        appWindow.Resize(new Windows.Graphics.SizeInt32(1600, 900));
        appWindow.Title = "PhotoLibrarian";

        // Set the window icon (title bar + taskbar)
        try
        {
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "PhotoLibrarian.ico");
            if (System.IO.File.Exists(iconPath))
            {
                appWindow.SetIcon(iconPath);
            }
        }
        catch { /* Icon is cosmetic — never block startup */ }

        // Bind status bar to ViewModel
        ViewModel.PropertyChanged += OnMainViewModelPropertyChanged;
        ViewModel.PeopleReview.PropertyChanged += OnPeopleReviewPropertyChanged;
        ViewModel.DuplicateReview.PropertyChanged += OnDuplicateReviewPropertyChanged;
        ViewModel.ImageViewer.PropertyChanged += OnImageViewerPropertyChanged;
        ViewModel.ImageEditor.PropertyChanged += OnImageEditorPropertyChanged;
        ViewModel.Settings.PropertyChanged += OnSettingsPropertyChanged;
        MetadataWriterService.SidecarFallback += OnMetadataSidecarFallback;

        UpdateFaceDetectionButton();

        // Top-ribbon events
        TopRibbon.CropClicked += OnRibbonCropClicked;
        TopRibbon.PhotoFuseClicked += OnPhotoFuseClick;
        ViewerOverlay.PhotoFuseExitRequested += OnPhotoFuseClick;
        ViewerOverlay.PhotoFuseDiscardRequested += async (_, _) => await FinishPhotoFuseAsync(discard: true);
        TopRibbon.EditVideoClicked += OnEditVideo;
        TopRibbon.MakeCopyClicked += OnRibbonMakeCopyClicked;
        TopRibbon.StraightenClicked += OnRibbonStraightenClicked;
        TopRibbon.SlideshowClicked += (_, _) => StartSlideshow(null);
        TopRibbon.SlideshowThemeChanged += (_, theme) => _slideshowTheme = theme;
        TopRibbon.StabilizeVideoClicked += async (_, _) => await ViewerOverlay.StabilizeVideoAsync();
        TopRibbon.TrimVideoClicked += async (_, _) => await ViewerOverlay.TrimVideoAsync();
        ImageGridPanel.SlideshowRequested += (_, selection) => StartSlideshow(selection);
        ViewerOverlay.PhotoContextMenuOpening += ImageGridPanel.PopulateViewerContextMenu;
        ImageGridPanel.PeopleReviewRequested += OnPeopleReviewClick;
        ImageGridPanel.DuplicateReviewRequested += OnDuplicateReviewClick;
        ImageGridPanel.ImportRequested += OnImportRequested;
        TopRibbon.AdjustClicked += OnRibbonAdjustClicked;
        TopRibbon.EffectsClicked += OnRibbonEffectsClicked;
        TopRibbon.QuickEditClicked += OnRibbonQuickEditClicked;
        TopRibbon.UndoClicked += OnRibbonUndoClicked;
        TopRibbon.RedoClicked += OnRibbonRedoClicked;
        ViewModel.EditHistory.Changed += OnEditHistoryChanged;
        UpdateHistoryControls();
        TopRibbon.RetouchClicked += OnRibbonRetouchClicked;
        TopRibbon.RedEyeClicked += OnRibbonRedEyeClicked;
        TopRibbon.ApplyCropClicked += OnRibbonApplyCropClicked;
        TopRibbon.CancelCropClicked += OnRibbonCancelCropClicked;
        TopRibbon.CropAspectChanged += OnRibbonCropAspectChanged;
        TopRibbon.ApplyStraightenClicked += OnRibbonApplyStraightenClicked;
        TopRibbon.CancelStraightenClicked += OnRibbonCancelStraightenClicked;
        ViewerOverlay.CropApplyRequested += OnRibbonApplyCropClicked;
        ViewerOverlay.CropCancelRequested += OnRibbonCancelCropClicked;
        ViewerOverlay.StraightenApplyRequested += OnRibbonApplyStraightenClicked;
        ViewerOverlay.StraightenCancelRequested += OnRibbonCancelStraightenClicked;
        ViewerOverlay.RedEyeSelectionCompleted += OnRedEyeSelectionCompleted;
        ViewerOverlay.ManualFaceTaggingExited += (_, _) =>
        {
            ViewModel.OnManualFaceTaggingExited();
            UpdateHistoryControls();
        };

        // The grid sits underneath the full viewer; if it takes focus (double-click or a
        // selection sync), hand focus back so arrow keys navigate photos.
        ImageGridPanel.GotFocus += (_, _) =>
        {
            if (ViewModel.ImageViewer.IsOpen)
                DispatcherQueue.TryEnqueue(() => ViewerOverlay.FocusViewer());
        };
        AppRoot.PreviewKeyDown += OnAppRootPreviewKeyDown;

        // Cleanup on window close
        this.Closed += OnWindowClosed;
        _removableVolumeMonitor = new RemovableVolumeMonitor();
        _removableVolumeMonitor.DeviceInserted += OnRemovableDeviceInserted;
        _removableVolumeMonitor.ScanFailed += OnRemovableDeviceScanFailed;
        _removableVolumeMonitor.Start();
    }

    private void OnRemovableDeviceInserted(object? sender, string sourceRoot)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosing) return;
            _detectedDeviceRoots.Enqueue(sourceRoot);
            ShowNextDetectedDevice();
        });
    }

    private void OnRemovableDeviceScanFailed(object? sender, Exception exception)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isClosing)
                ViewModel.StatusText = PhotoImportStrings.Format(
                    "PhotoImportDeviceScanFailed", exception.Message);
        });
    }

    private void ShowNextDetectedDevice()
    {
        if (_isClosing || _pendingDetectedDeviceRoot is not null ||
            _detectedDeviceRoots.Count == 0)
            return;

        _pendingDetectedDeviceRoot = _detectedDeviceRoots.Dequeue();
        ImportDeviceInfoBar.Message = PhotoImportStrings.Format(
            "PhotoImportDeviceInfoMessage",
            _pendingDetectedDeviceRoot);
        ImportDeviceInfoBar.IsOpen = true;
    }

    private async void OnImportDetectedDeviceClick(object sender, RoutedEventArgs e)
    {
        var sourceRoot = _pendingDetectedDeviceRoot;
        _pendingDetectedDeviceRoot = null;
        ImportDeviceInfoBar.IsOpen = false;
        if (sourceRoot is not null)
            await ShowPhotoImportDialogAsync(sourceRoot);
        ShowNextDetectedDevice();
    }

    private async void OnImportRequested(object sender, RoutedEventArgs e) =>
        await ShowPhotoImportDialogAsync();

    private async Task ShowPhotoImportDialogAsync(string? sourceRoot = null)
    {
        var destinationRoot = ViewModel.FolderNav.RootFolders.FirstOrDefault()?.Path;
        var dialog = new PhotoImportDialog(ViewModel, sourceRoot, destinationRoot)
        {
            XamlRoot = AppRoot.XamlRoot
        };
        await dialog.ShowAsync();

        if (dialog.Outcome is not { } result ||
            !dialog.DeleteSourceRequested ||
            result.SuccessfulItems.Count == 0)
            return;

        var confirmation = new ContentDialog
        {
            XamlRoot = AppRoot.XamlRoot,
            Title = PhotoImportStrings.Get("PhotoImportDeleteConfirmTitle"),
            Content = PhotoImportStrings.Format(
                "PhotoImportDeleteConfirmContent",
                result.SuccessfulItems.Count),
            PrimaryButtonText = PhotoImportStrings.Get("PhotoImportDeleteConfirmButton"),
            CloseButtonText = PhotoImportStrings.Get("PhotoImportCancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            ViewModel.StatusText = PhotoImportStrings.Get("PhotoImportDeleteCanceled");
            return;
        }

        var progress = new Progress<PhotoImportProgress>(update =>
        {
            ViewModel.StatusText = PhotoImportStrings.Format(
                "PhotoImportDeleteProgress",
                update.CurrentFileName,
                update.FilesCompleted + 1,
                update.FilesTotal);
        });
        var deletionResults = await Task.Run(
            () => ViewModel.PhotoImport.DeleteVerifiedSourcesAsync(
                result.SuccessfulItems,
                progress));
        var deletedCount = deletionResults.Count(item => item.Deleted);
        var summary = PhotoImportStrings.Format(
            "PhotoImportDeleteSummary",
            deletedCount,
            deletionResults.Count);
        var errors = deletionResults
            .Where(item => !item.Deleted || item.Error is not null)
            .Select(item => PhotoImportStrings.Format(
                "PhotoImportDeleteResult",
                Path.GetFileName(item.SourcePath),
                item.Error ?? PhotoImportStrings.Get("PhotoImportUnverifiedSource")))
            .ToList();
        ViewModel.StatusText = summary;

        var resultDialog = new ContentDialog
        {
            XamlRoot = AppRoot.XamlRoot,
            Title = PhotoImportStrings.Get("PhotoImportDeleteResultTitle"),
            Content = errors.Count == 0
                ? summary
                : $"{summary}{Environment.NewLine}{Environment.NewLine}{string.Join(Environment.NewLine, errors)}",
            CloseButtonText = PhotoImportStrings.Get("PhotoImportClose")
        };
        await resultDialog.ShowAsync();
    }

    private void OnMetadataSidecarFallback(object? sender, MetadataWriteFallbackEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosing) return;
            MetadataFallbackInfoBar.Message =
                $"The latest {e.PropertyName} edit could not be written into \"{e.ImagePath}\". " +
                $"It was saved to \"{e.SidecarPath}\" instead. Keep this sidecar with the image. " +
                $"Reason: {e.Reason}";
            MetadataFallbackInfoBar.IsOpen = true;
        });
    }

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isClosing) return;

        if (e.PropertyName == nameof(ViewModel.StatusText))
            StatusBarText.Text = ViewModel.StatusText;
        if (e.PropertyName == nameof(ViewModel.IsIndexing))
            UpdateBackgroundProgress();
        if (e.PropertyName == nameof(ViewModel.IsFaceDetectionRunning))
        {
            UpdateBackgroundProgress();
            UpdateFaceDetectionButton();
        }
        if (e.PropertyName == nameof(ViewModel.IsAutoTaggingRunning))
            UpdateBackgroundProgress();
        if (e.PropertyName is nameof(ViewModel.ImageViewer))
            UpdateViewerVisibility();
        if (e.PropertyName is nameof(ViewModel.Settings))
            UpdateSettingsVisibility();
    }

    private void OnPeopleReviewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_isClosing && e.PropertyName == nameof(ViewModel.PeopleReview.IsOpen))
            UpdatePeopleReviewVisibility();
    }

    private void OnDuplicateReviewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_isClosing && e.PropertyName == nameof(ViewModel.DuplicateReview.IsOpen))
            UpdateDuplicateReviewVisibility();
    }

    private void OnImageEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isClosing) return;
        UpdateHistoryControls();
        if (e.PropertyName == nameof(ImageEditorViewModel.Title))
            TopRibbon.SetContextLabel(ViewModel.ImageEditor.Title);
        if (e.PropertyName != nameof(ImageEditorViewModel.IsOpen)) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosing) return;
            UpdateRibbonVisibility();
            if (ViewModel.ImageEditor.IsOpen)
            {
                ViewerOverlay.StopFocusGuard();
                EditorOverlay.FocusEditor();
            }
            else if (ViewModel.ImageViewer.IsOpen)
                ViewerOverlay.FocusViewer();
        });
    }

    private void OnImageViewerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isClosing) return;
        if (e.PropertyName is nameof(ImageViewerViewModel.IsApplyingEffect) or nameof(ImageViewerViewModel.CurrentEntry))
            UpdateHistoryControls();

        if (e.PropertyName == nameof(ImageViewerViewModel.ShouldShowInformationPane))
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_isClosing) UpdateInformationPaneVisibility();
            });

        if (e.PropertyName == nameof(ImageViewerViewModel.IsOpen))
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_isClosing) UpdateRibbonVisibility();
            });
        if (e.PropertyName == nameof(ImageViewerViewModel.Title))
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_isClosing)
                {
                    TopRibbon.SetContextLabel(ViewModel.ImageViewer.Title ?? "");
                    TopRibbon.SetVideoMode(ViewModel.ImageViewer.IsVideo);
                }
            });
        if (e.PropertyName == nameof(ImageViewerViewModel.IsVideo))
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_isClosing)
                    TopRibbon.SetVideoMode(ViewModel.ImageViewer.IsVideo);
            });
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_isClosing && e.PropertyName == nameof(ViewModel.Settings.IsOpen))
            UpdateSettingsVisibility();
    }

    private void UpdateBackgroundProgress()
    {
        IndexingProgress.IsActive =
            ViewModel.IsIndexing ||
            ViewModel.IsFaceDetectionRunning ||
            ViewModel.IsAutoTaggingRunning;
    }

    private void OnUserPointerActivity(
        object sender,
        PointerRoutedEventArgs e) =>
        ViewModel.NotifyUserActivity();

    private void OnUserPointerPressed(
        object sender,
        PointerRoutedEventArgs e) =>
        ViewerOverlay.StopFocusGuard();

    /// <summary>
    /// While the full viewer is open, route its shortcuts to it even if keyboard focus is
    /// still on the (hidden) grid, so navigation works immediately after opening.
    /// </summary>
    private async void OnAppRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ViewModel.EditHistory.IsBusy)
        {
            e.Handled = true;
            return;
        }
        // Deliberate keyboard navigation must end the short opening-time focus recovery.
        if (e.Key == Windows.System.VirtualKey.Tab)
            ViewerOverlay.StopFocusGuard();
        if (ViewerOverlay.IsPhotoContextMenuOpen) return;
        var focus = Content?.XamlRoot is { } historyRoot
            ? FocusManager.GetFocusedElement(historyRoot) as DependencyObject : null;
        if (!e.Handled && TryGetEditorHistoryShortcut(e.Key, GetCurrentModifiers(),
                HasNativeHistoryOrDialog(focus), out var undo) && CanUseEditHistory())
        {
            e.Handled = true;
            await MoveEditHistoryAsync(undo);
            return;
        }
        if (ViewModel.ImageEditor.IsOpen && !ViewModel.Settings.IsOpen && !ViewModel.PeopleReview.IsOpen)
        {
            var editorFocus = GetFocusedElement();
            await EditorOverlay.HandleEditorKeyAsync(e, editorFocus);
            return;
        }

        var focused = GetFocusedElement();
        if (await HandlePhotoLibraryShortcutAsync(e, focused)) return;

        if (!ViewModel.ImageViewer.IsOpen
            || ViewModel.Settings.IsOpen
            || ViewModel.PeopleReview.IsOpen
            || EditorOverlay.Visibility == Visibility.Visible
            || ViewerOverlay.HasFocusWithin())
            return;

        if (IsInputOrDialogFocused(focused)
            || IsWithin(focused, FolderNavPanel)
            || IsWithin(focused, MetadataDetailPanel)
            || IsWithin(focused, TopRibbon))
            return;

        if (ViewerOverlay.HandleViewerKey(e.Key))
        {
            e.Handled = true;
            ViewerOverlay.FocusViewer();
        }
    }

    internal static bool TryGetEditorHistoryShortcut(
        Windows.System.VirtualKey key,
        Windows.System.VirtualKeyModifiers modifiers,
        bool isInputFocused,
        out bool isUndo)
    {
        isUndo = false;
        if (isInputFocused)
            return false;
        if (key == Windows.System.VirtualKey.Z &&
            modifiers == (Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift))
            return true;
        if (modifiers != Windows.System.VirtualKeyModifiers.Control) return false;

        if (key == Windows.System.VirtualKey.Z)
        {
            isUndo = true;
            return true;
        }

        return key == Windows.System.VirtualKey.Y;
    }

    private async Task<bool> HandlePhotoLibraryShortcutAsync(
        KeyRoutedEventArgs e,
        DependencyObject? focused)
    {
        if (e.Handled || !CanRunPhotoLibraryShortcut() || IsInputOrDialogFocused(focused))
            return false;

        var modifiers = GetCurrentModifiers();
        if (ViewModel.ImageViewer.IsOpen &&
            e.Key is Windows.System.VirtualKey.Number0 or Windows.System.VirtualKey.NumberPad0 &&
            modifiers == Windows.System.VirtualKeyModifiers.Control &&
            ViewerOverlay.HandleViewerKey(e.Key))
        {
            e.Handled = true;
            return true;
        }

        var grid = ViewModel.ImageGrid;
        bool hasSelection = grid.SelectedImages.Count > 0 || grid.SelectedImage is not null;
        if (TryGetRatingShortcut(e.Key, modifiers, out var rating))
        {
            if (!hasSelection) return false;
            e.Handled = true;
            await ViewModel.MetadataPanel.SetRatingFromShortcutAsync(rating);
            return true;
        }

        if (modifiers == Windows.System.VirtualKeyModifiers.Control)
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.R when hasSelection:
                    e.Handled = true;
                    await ImageGridPanel.RotateSelectedAsync(clockwise: true);
                    return true;
                case Windows.System.VirtualKey.L when hasSelection:
                    e.Handled = true;
                    await ImageGridPanel.RotateSelectedAsync(clockwise: false);
                    return true;
                case Windows.System.VirtualKey.T when hasSelection:
                    e.Handled = true;
                    if (ViewModel.ImageViewer.IsOpen &&
                        !ViewModel.ImageViewer.IsInformationPaneVisible)
                    {
                        ViewModel.ImageViewer.IsInformationPaneVisible = true;
                        DispatcherQueue.TryEnqueue(() => MetadataDetailPanel.FocusNewTagInput());
                    }
                    else
                    {
                        MetadataDetailPanel.FocusNewTagInput();
                    }
                    return true;
            }
        }
        else if (modifiers == Windows.System.VirtualKeyModifiers.None)
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.F2 when hasSelection:
                    e.Handled = true;
                    await ImageGridPanel.RenameSelectedAsync();
                    return true;
                case Windows.System.VirtualKey.Delete when hasSelection:
                    e.Handled = true;
                    await ImageGridPanel.DeleteSelectedAsync();
                    return true;
            }
        }

        return false;
    }

    private bool CanRunPhotoLibraryShortcut() =>
        !_isClosing && !_photoFuseOpen && _slideshow is null &&
        !ViewModel.Settings.IsOpen && !ViewModel.PeopleReview.IsOpen &&
        !ViewModel.DuplicateReview.IsOpen && !ViewModel.ImageEditor.IsOpen &&
        !ViewerOverlay.IsCropping && !ViewerOverlay.IsStraightening &&
        !ViewerOverlay.IsRedEyeRemoving && !ViewerOverlay.IsManualFaceTagging;

    internal static bool TryGetRatingShortcut(
        Windows.System.VirtualKey key,
        Windows.System.VirtualKeyModifiers modifiers,
        out int rating)
    {
        rating = key switch
        {
            Windows.System.VirtualKey.Number0 or Windows.System.VirtualKey.NumberPad0 => 0,
            Windows.System.VirtualKey.Number1 or Windows.System.VirtualKey.NumberPad1 => 1,
            Windows.System.VirtualKey.Number2 or Windows.System.VirtualKey.NumberPad2 => 2,
            Windows.System.VirtualKey.Number3 or Windows.System.VirtualKey.NumberPad3 => 3,
            Windows.System.VirtualKey.Number4 or Windows.System.VirtualKey.NumberPad4 => 4,
            Windows.System.VirtualKey.Number5 or Windows.System.VirtualKey.NumberPad5 => 5,
            _ => -1
        };

        return rating >= 0 &&
            (modifiers == Windows.System.VirtualKeyModifiers.None ||
             (modifiers == Windows.System.VirtualKeyModifiers.Control && rating > 0));
    }

    private DependencyObject? GetFocusedElement() =>
        Content?.XamlRoot is { } root
            ? FocusManager.GetFocusedElement(root) as DependencyObject
            : null;

    private static bool IsInputOrDialogFocused(DependencyObject? focused)
    {
        for (var current = focused; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is TextBox or PasswordBox or AutoSuggestBox or ComboBox or
                NumberBox or RichEditBox or ContentDialog)
                return true;
        }
        return false;
    }

    private static Windows.System.VirtualKeyModifiers GetCurrentModifiers()
    {
        var modifiers = Windows.System.VirtualKeyModifiers.None;
        if (IsKeyDown(Windows.System.VirtualKey.Control))
            modifiers |= Windows.System.VirtualKeyModifiers.Control;
        if (IsKeyDown(Windows.System.VirtualKey.Shift))
            modifiers |= Windows.System.VirtualKeyModifiers.Shift;
        if (IsKeyDown(Windows.System.VirtualKey.Menu))
            modifiers |= Windows.System.VirtualKeyModifiers.Menu;
        if (IsKeyDown(Windows.System.VirtualKey.LeftWindows) ||
            IsKeyDown(Windows.System.VirtualKey.RightWindows))
            modifiers |= Windows.System.VirtualKeyModifiers.Windows;
        return modifiers;
    }

    private static bool IsKeyDown(Windows.System.VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) &
         Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

    private static bool IsWithin(DependencyObject? element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor)) return true;
        }
        return false;
    }

    internal static bool HasNativeHistoryOrDialog(DependencyObject? element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is TextBox or PasswordBox or AutoSuggestBox or RichEditBox or ContentDialog)
                return true;
        }
        return false;
    }

    private void OnUserKeyActivity(
        object sender,
        KeyRoutedEventArgs e) =>
        ViewModel.NotifyUserActivity();

    private void UpdateFaceDetectionButton()
    {
        var isRunning = ViewModel.IsFaceDetectionRunning;
        FaceDetectionIcon.Glyph = isRunning ? "\uE769" : "\uE768";
        var label = isRunning ? "Stop face detection" : "Start face detection";
        AutomationProperties.SetName(FaceDetectionButton, label);
        ToolTipService.SetToolTip(FaceDetectionButton, label);
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _isClosing = true;
        _removableVolumeMonitor?.Dispose();
        _removableVolumeMonitor = null;
        FolderNavPanel.SaveState();
        _slideshow?.Close();
        _printSession?.Dispose();
        _printSession = null;
        ViewModel.PropertyChanged -= OnMainViewModelPropertyChanged;
        ViewModel.PeopleReview.PropertyChanged -= OnPeopleReviewPropertyChanged;
        ViewModel.DuplicateReview.PropertyChanged -= OnDuplicateReviewPropertyChanged;
        ViewModel.DuplicateReview.Close();
        ViewModel.ImageViewer.PropertyChanged -= OnImageViewerPropertyChanged;
        ViewModel.ImageEditor.PropertyChanged -= OnImageEditorPropertyChanged;
        ViewModel.EditHistory.Changed -= OnEditHistoryChanged;
        ViewModel.Settings.PropertyChanged -= OnSettingsPropertyChanged;
        MetadataWriterService.SidecarFallback -= OnMetadataSidecarFallback;
        ViewModel.ImageGrid.Cleanup();
        await ViewModel.CleanupAsync();
    }

    private GridLength _informationPaneWidth = new(300);
    private double _informationPaneMinWidth = 200;

    private void UpdateInformationPaneVisibility()
    {
        bool visible = ViewModel.ImageViewer.ShouldShowInformationPane;
        if (visible == (InformationPane.Visibility == Visibility.Visible)) return;

        if (!visible)
        {
            var focused = Content?.XamlRoot is { } root
                ? FocusManager.GetFocusedElement(root) as DependencyObject : null;
            if (IsWithin(focused, InformationPane) || IsWithin(focused, InformationPaneSplitter))
                ViewerOverlay.FocusViewer();
            _informationPaneWidth = InformationPaneColumn.Width;
            _informationPaneMinWidth = InformationPaneColumn.MinWidth;
            InformationPaneColumn.MinWidth = 0;
            InformationPaneColumn.Width = new GridLength(0);
        }
        else
        {
            InformationPaneColumn.Width = _informationPaneWidth;
            InformationPaneColumn.MinWidth = _informationPaneMinWidth;
        }
        InformationPane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        InformationPaneSplitter.Visibility = InformationPane.Visibility;
    }

    private void UpdateViewerVisibility()
    {
        ViewerOverlay.Visibility = ViewModel.ImageViewer.IsOpen
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateInformationPaneVisibility();
        UpdateRibbonVisibility();
    }

    private void UpdateRibbonVisibility()
    {
        TopRibbon.Visibility = Visibility.Visible;
        TopRibbon.SetPhotoContext(ViewModel.ImageViewer.IsOpen, ViewModel.ImageEditor.IsOpen);
        TopRibbon.SetContextLabel(ViewModel.ImageEditor.IsOpen ? ViewModel.ImageEditor.Title
            : ViewModel.ImageViewer.IsOpen ? ViewModel.ImageViewer.Title : "");
        if (!ViewModel.ImageViewer.IsOpen && ViewerOverlay.IsCropping)
        {
            ViewerOverlay.ExitCropMode();
            TopRibbon.ExitCropMode();
        }
        if (!ViewModel.ImageViewer.IsOpen && ViewerOverlay.IsStraightening)
        {
            ViewerOverlay.ExitStraightenMode();
            TopRibbon.ExitStraightenMode();
        }
        if (!ViewModel.ImageViewer.IsOpen && ViewerOverlay.IsRedEyeRemoving)
        {
            ViewerOverlay.ExitRedEyeMode();
            TopRibbon.ExitRedEyeMode();
        }
        UpdateHistoryControls();
    }

    private void OnRibbonCropClicked(object? sender, EventArgs e)
    {
        if (!ViewModel.ImageViewer.IsOpen) return;
        if (ViewModel.ImageViewer.IsVideo) return;
        if (ViewerOverlay.IsStraightening)
        {
            ViewerOverlay.ExitStraightenMode();
            TopRibbon.ExitStraightenMode();
        }
        ViewerOverlay.EnterCropMode();
        ViewerOverlay.CropOverlay.AspectRatio = _pendingAspect;
        TopRibbon.EnterCropMode();
        UpdateHistoryControls();
    }

    private async void OnRibbonMakeCopyClicked(object? sender, EventArgs e)
    {
        var entry = ViewModel.ImageViewer.CurrentEntry;
        if (entry is null || entry.MediaType != Core.Models.MediaType.Image) return;

        TopRibbon.IsEnabled = false;
        ViewModel.StatusText = "Creating a copy…";
        try
        {
            await ViewModel.MakeCopyForEditingAsync(entry);
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Make a copy failed: {ex.Message}";
        }
        finally
        {
            TopRibbon.IsEnabled = true;
        }
    }

    private void OnRibbonStraightenClicked(object? sender, EventArgs e)
    {
        var entry = ViewModel.ImageViewer.CurrentEntry;
        if (entry is null || ViewModel.ImageViewer.IsVideo) return;
        if (ViewerOverlay.CurrentImagePixelWidth == 0 || ViewerOverlay.CurrentImagePixelHeight == 0)
            return;
        if (!ImageEditRenderer.IsSupported(entry.FilePath))
        {
            ViewModel.StatusText =
                $"Editing not supported for {System.IO.Path.GetExtension(entry.FilePath)}";
            return;
        }

        if (ViewerOverlay.IsCropping)
        {
            ViewerOverlay.ExitCropMode();
            TopRibbon.ExitCropMode();
        }

        ViewerOverlay.EnterStraightenMode();
        TopRibbon.EnterStraightenMode();
        UpdateHistoryControls();
    }

    private async void OnRibbonAdjustClicked(object? sender, EventArgs e)
        => await OpenRasterEditorAsync(retouch: false);

    private async void OnRibbonRetouchClicked(object? sender, EventArgs e)
        => await OpenRasterEditorAsync(retouch: true);

    private async Task OpenRasterEditorAsync(bool retouch)
    {
        await OpenRibbonEditorAsync(retouch);
    }

    private async void OnRibbonEffectsClicked(object? sender, EventArgs e)
    {
        await ViewerOverlay.ShowEffectsAsync(TopRibbon.EffectsAnchor);
    }

    private async void OnRibbonQuickEditClicked(object? sender, ViewerQuickEdit action)
    {
        var entry = ViewModel.ImageViewer.CurrentEntry;
        if (!CanUseEditHistory() || ViewModel.ImageEditor.IsOpen ||
            entry?.MediaType != Core.Models.MediaType.Image ||
            !ImageEditRenderer.IsSupported(entry.FilePath)) return;
        var label = EditorEffectStrings.Get($"ViewerQuick{action}Label");
        ViewModel.StatusText = $"{label}...";
        var applied = await ViewModel.RunPhotoEditAsync(label, [entry.FilePath],
            copies => ViewerQuickEditService.PrepareAsync(copies[System.IO.Path.GetFullPath(entry.FilePath)], action));
        if (applied) ViewModel.StatusText = $"{label} - {entry.FileName}";
    }

    private async void OnRibbonUndoClicked(object? sender, EventArgs e) =>
        await MoveEditHistoryAsync(undo: true);

    private async void OnRibbonRedoClicked(object? sender, EventArgs e) =>
        await MoveEditHistoryAsync(undo: false);

    private bool CanUseEditHistory() =>
        !_isClosing && !_photoFuseOpen && !ViewModel.Settings.IsOpen &&
        !ViewModel.PeopleReview.IsOpen && !ViewModel.DuplicateReview.IsOpen &&
        !ViewerOverlay.IsCropping && !ViewerOverlay.IsStraightening &&
        !ViewerOverlay.IsRedEyeRemoving && !ViewerOverlay.IsManualFaceTagging &&
        !ViewModel.ImageEditor.IsBusy && !ViewModel.EditHistory.IsBusy;

    private void OnEditHistoryChanged(object? sender, EventArgs e)
    {
        var enabled = !ViewModel.EditHistory.IsBusy;
        if (!enabled && Content?.XamlRoot is { } root)
        {
            var focused = FocusManager.GetFocusedElement(root) as Control;
            _editHistoryFocus = !HasNativeHistoryOrDialog(focused) && !IsWithin(focused, TopRibbon)
                ? focused : null;
        }
        ImageGridPanel.IsEnabled = enabled;
        MetadataDetailPanel.IsEnabled = enabled;
        FolderNavPanel.IsEnabled = enabled;
        ViewerOverlay.IsEnabled = enabled;
        LibrarySearchBox.IsEnabled = enabled;
        EditorOverlay.IsEnabled = enabled;
        UpdateHistoryControls();
        if (enabled && _editHistoryFocus is { } previous)
        {
            _editHistoryFocus = null;
            if (previous.IsEnabled && previous.Visibility == Visibility.Visible)
                previous.Focus(FocusState.Keyboard);
        }
    }

    private void UpdateHistoryControls()
    {
        var editor = ViewModel.ImageEditor;
        var preview = editor.IsOpen && (editor.UndoHistory.Count > 0 || editor.RedoHistory.Count > 0);
        var available = CanUseEditHistory();
        TopRibbon.SetQuickEditAvailability(available && !editor.IsOpen &&
            ViewModel.ImageViewer.CurrentEntry is { MediaType: Core.Models.MediaType.Image } entry &&
            ImageEditRenderer.IsSupported(entry.FilePath));
        TopRibbon.SetHistoryAvailability(available && (preview ? editor.CanUndo : ViewModel.EditHistory.CanUndo),
            available && (preview ? editor.CanRedo : ViewModel.EditHistory.CanRedo), ViewModel.EditHistory.IsBusy);
        var undoLabel = ViewModel.EditHistory.UndoLabel;
        var redoLabel = ViewModel.EditHistory.RedoLabel;
        if (preview)
        {
            undoLabel = editor.UndoHistory.Count > 0 ? $"Undo {editor.UndoHistory[0]}" : "Undo";
            redoLabel = editor.RedoHistory.Count > 0 ? $"Redo {editor.RedoHistory[0]}" : "Redo";
        }
        TopRibbon.SetHistoryLabels(undoLabel, redoLabel);
    }

    private async Task MoveEditHistoryAsync(bool undo)
    {
        if (!CanUseEditHistory()) return;
        try
        {
            var editor = ViewModel.ImageEditor;
            if (editor.IsOpen)
            {
                editor.EndEdit();
                if (editor.UndoHistory.Count > 0 || editor.RedoHistory.Count > 0)
                {
                    if (undo && editor.CanUndo) editor.UndoSteps(1);
                    else if (!undo && editor.CanRedo) editor.RedoSteps(1);
                    return;
                }
            }
            var result = undo ? await ViewModel.EditHistory.UndoAsync() : await ViewModel.EditHistory.RedoAsync();
            if (result.Message is not null) ViewModel.StatusText = result.Message;
            if (result.Status == EditHistoryStatus.Applied && editor.IsOpen && editor.CurrentEntry is { } entry)
                await editor.OpenForEditAsync(entry);
        }
        catch (Exception exception)
        {
            ViewModel.StatusText = $"History action failed: {exception.Message}";
        }
        finally { UpdateHistoryControls(); }
    }

    private async Task<bool> OpenRibbonEditorAsync(bool retouch)
    {
        if (_photoFuseOpen) return false;
        if (ViewModel.ImageEditor.IsOpen) return !ViewModel.ImageEditor.IsBusy;
        var entry = ViewModel.ImageViewer.CurrentEntry;
        if (entry is null || ViewModel.ImageViewer.IsVideo) return false;
        if (retouch && !ImageEditRenderer.IsSupported(entry.FilePath))
        {
            ViewModel.StatusText = RawToneMappingService.IsRaw(entry.FilePath)
                ? "Export a JPEG or PNG derivative before retouching a RAW photo."
                : $"Retouch not supported for {System.IO.Path.GetExtension(entry.FilePath)}";
            return false;
        }
        if (!ImageEditRenderer.IsSupported(entry.FilePath) && !RawToneMappingService.IsRaw(entry.FilePath))
        {
            ViewModel.StatusText = $"Editing not supported for {System.IO.Path.GetExtension(entry.FilePath)}";
            return false;
        }

        if (ViewerOverlay.IsCropping)
        {
            ViewerOverlay.ExitCropMode();
            TopRibbon.ExitCropMode();
        }
        if (ViewerOverlay.IsStraightening)
        {
            ViewerOverlay.ExitStraightenMode();
            TopRibbon.ExitStraightenMode();
        }
        if (ViewerOverlay.IsRedEyeRemoving)
        {
            ViewerOverlay.ExitRedEyeMode();
            TopRibbon.ExitRedEyeMode();
        }

        try
        {
            ViewModel.ImageEditor.UpdateLibraryImages(ViewModel.ImageGrid.GetViewerImages());
            await ViewModel.ImageEditor.OpenForEditAsync(entry);
            EditorOverlay.SetRetouchWorkspace(retouch);
            return true;
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Unable to open editor: {ex.Message}";
            return false;
        }
    }

    private void OnRibbonRedEyeClicked(object? sender, EventArgs e)
    {
        var entry = ViewModel.ImageViewer.CurrentEntry;
        if (entry is null || ViewModel.ImageViewer.IsVideo) return;
        if (!ImageEditRenderer.IsSupported(entry.FilePath))
        {
            ViewModel.StatusText =
                $"Editing not supported for {System.IO.Path.GetExtension(entry.FilePath)}";
            return;
        }

        if (ViewerOverlay.IsCropping) OnRibbonCancelCropClicked(sender, e);
        if (ViewerOverlay.IsStraightening) OnRibbonCancelStraightenClicked(sender, e);
        ViewerOverlay.EnterRedEyeMode();
        TopRibbon.EnterRedEyeMode();
        UpdateHistoryControls();
    }

    private async void OnRedEyeSelectionCompleted(object? sender, RedEyeBounds bounds)
    {
        var entry = ViewModel.ImageViewer.CurrentEntry;
        if (entry is null) return;

        TopRibbon.IsEnabled = false;
        try
        {
            ViewModel.StatusText = "Removing red eye…";
            var changed = 0;
            var applied = await ViewModel.RunPhotoEditAsync("red-eye removal", [entry.FilePath], async copies =>
                changed = await ImageEditRenderer.RemoveRedEyeAsync(copies[Path.GetFullPath(entry.FilePath)], bounds));
            if (applied && changed == 0) ViewModel.StatusText = "No red eye found in selection";
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Red-eye removal failed: {ex.Message}";
        }
        finally
        {
            ViewerOverlay.ExitRedEyeMode();
            TopRibbon.ExitRedEyeMode();
            TopRibbon.IsEnabled = true;
            UpdateHistoryControls();
        }
    }

    private void OnRibbonCancelCropClicked(object? sender, EventArgs e)
    {
        ViewerOverlay.ExitCropMode();
        TopRibbon.ExitCropMode();
        UpdateHistoryControls();
    }

    private void OnRibbonCancelStraightenClicked(object? sender, EventArgs e)
    {
        ViewerOverlay.ExitStraightenMode();
        TopRibbon.ExitStraightenMode();
        UpdateHistoryControls();
    }

    private void OnRibbonCropAspectChanged(object? sender, CropAspectRatio ratio)
    {
        _pendingAspect = ratio;
        if (ViewerOverlay.IsCropping)
            ViewerOverlay.CropOverlay.AspectRatio = ratio;
    }

    private async void OnRibbonApplyCropClicked(object? sender, EventArgs e)
    {
        if (!ViewerOverlay.IsCropping) return;
        var bounds = ViewerOverlay.CropOverlay.GetCropBoundsInImagePixels();
        var entry = ViewModel.ImageViewer.CurrentEntry;
        if (bounds is null || entry is null) return;

        TopRibbon.IsEnabled = false;
        var resumeFaceDetection = false;
        try
        {
            resumeFaceDetection = await ViewModel.PauseFaceDetectionAsync();
            ViewModel.StatusText = "Applying crop…";

            // Back up the original first (no-op if a backup already exists).
            // A read-only original is left untouched; the crop is written to a new file instead.
            var sidecar = FaceMetadataStore.GetSidecarPathForImage(entry.FilePath);
            var readOnlyOriginal = new System.IO.FileInfo(entry.FilePath).IsReadOnly ||
                (File.Exists(sidecar) && new FileInfo(sidecar).IsReadOnly);
            if (readOnlyOriginal)
            {
                var result = await CropService.CropImageAsync(entry.FilePath, bounds.Value);
                if (result.CopyPath is not null)
                {
                    await ViewModel.IndexCroppedCopyAsync(result.CopyPath);
                    ViewModel.StatusText = "Created a cropped copy. Undo does not delete exported copies.";
                }
            }
            else
                await ViewModel.RunPhotoEditAsync("crop", [entry.FilePath], async copies =>
                    _ = await CropService.CropImageAsync(copies[Path.GetFullPath(entry.FilePath)], bounds.Value));
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Crop failed: {ex.Message}";
        }
        finally
        {
            ViewModel.ResumeFaceDetection(resumeFaceDetection);
            ViewerOverlay.ExitCropMode();
            TopRibbon.ExitCropMode();
            TopRibbon.IsEnabled = true;
            UpdateHistoryControls();
        }
    }

    private async void OnRibbonApplyStraightenClicked(object? sender, EventArgs e)
    {
        if (!ViewerOverlay.IsStraightening) return;

        var entry = ViewModel.ImageViewer.CurrentEntry;
        if (entry is null)
        {
            OnRibbonCancelStraightenClicked(sender, e);
            return;
        }

        var angle = ViewerOverlay.StraightenAngle;
        if (Math.Abs(angle) < 0.05)
        {
            ViewModel.StatusText = "No straighten adjustment to apply";
            OnRibbonCancelStraightenClicked(sender, e);
            return;
        }

        TopRibbon.IsEnabled = false;
        ViewModel.StatusText = "Applying straighten…";
        try
        {
            await ViewModel.RunPhotoEditAsync("straighten", [entry.FilePath], async copies =>
                _ = await ImageEditRenderer.RenderStraightenedAsync(copies[Path.GetFullPath(entry.FilePath)], angle));
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Straighten failed: {ex.Message}";
        }
        finally
        {
            ViewerOverlay.ExitStraightenMode();
            TopRibbon.ExitStraightenMode();
            TopRibbon.IsEnabled = true;
            UpdateHistoryControls();
        }
    }

    private void UpdateSettingsVisibility()
    {
        SettingsOverlay.Visibility = ViewModel.Settings.IsOpen
            ? Visibility.Visible : Visibility.Collapsed;
        
        if (ViewModel.Settings.IsOpen)
        {
            SettingsPanel.Loaded += (s, e) => ViewModel.Settings.OpenCommand.Execute(null);
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        ViewModel.Settings.OpenCommand.Execute(null);
    }

    private async void OnPeopleReviewClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.PeopleReview.OpenForImagesAsync(ViewModel.ImageGrid.DisplayedEntries);
        }
        catch (Exception ex)
        {
            ViewModel.PeopleReview.Close();
            var dialog = new ContentDialog
            {
                Title = "People review couldn't be opened",
                Content = ex.Message,
                CloseButtonText = "Close",
                XamlRoot = MainLayout.XamlRoot
            };
            await dialog.ShowAsync();
        }
    }

    private async void OnDuplicateReviewClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.DuplicateReview.OpenForImagesAsync(ViewModel.ImageGrid.DisplayedEntries);
    }

    private void UpdateDuplicateReviewVisibility()
    {
        DuplicateReviewOverlay.Visibility = ViewModel.DuplicateReview.IsOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdatePeopleReviewVisibility()
    {
        PeopleReviewOverlay.Visibility = ViewModel.PeopleReview.IsOpen
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!ViewModel.PeopleReview.IsOpen)
            _ = RefreshPeopleFiltersAsync();
    }
    
    private async void OnBenchmarkClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.RunBenchmarkCommand.ExecuteAsync(null);
    }
}
