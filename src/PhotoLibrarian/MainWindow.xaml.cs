// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using PhotoLibrarian.ViewModels;
using PhotoLibrarian.Views;
using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace PhotoLibrarian;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel => App.ViewModel;

    private CropAspectRatio _pendingAspect = CropAspectRatio.Free;
    private bool _isClosing;
    private SlideshowWindow? _slideshow;
    private Control? _slideshowReturnFocus;
    private double _slideshowIntervalSeconds = 5;

    private void OnSlideshowInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (CanStartSlideshow())
        {
            args.Handled = true;
            StartSlideshow(null);
        }
    }

    private bool CanStartSlideshow() =>
        !(_isClosing || ViewModel.Settings.IsOpen || ViewModel.PeopleReview.IsOpen ||
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
        _slideshow = new SlideshowWindow(sequence, _slideshowIntervalSeconds);
        _slideshow.Closed += OnSlideshowClosed;
        _slideshow.Activate();
    }

    private void OnSlideshowClosed(object sender, WindowEventArgs args)
    {
        if (_slideshow is null) return;
        _slideshowIntervalSeconds = _slideshow.ViewModel.IntervalSeconds;
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

    public void BeginManualFaceTagging() =>
        ViewerOverlay.EnterManualFaceTagging();

    public void CancelManualFaceTagging() =>
        ViewerOverlay.ExitManualFaceTagging();

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
        TopRibbon.MakeCopyClicked += OnRibbonMakeCopyClicked;
        TopRibbon.StraightenClicked += OnRibbonStraightenClicked;
        TopRibbon.SlideshowClicked += (_, _) => StartSlideshow(null);
        TopRibbon.StabilizeVideoClicked += async (_, _) => await ViewerOverlay.StabilizeVideoAsync();
        ImageGridPanel.SlideshowRequested += (_, selection) => StartSlideshow(selection);
        ImageGridPanel.PeopleReviewRequested += OnPeopleReviewClick;
        ImageGridPanel.DuplicateReviewRequested += OnDuplicateReviewClick;
        TopRibbon.AdjustClicked += OnRibbonAdjustClicked;
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
        ViewerOverlay.ManualFaceTaggingExited += (_, _) => ViewModel.OnManualFaceTaggingExited();

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
    }

    private void OnMetadataSidecarFallback(object? sender, MetadataWriteFallbackEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosing) return;
            MetadataFallbackInfoBar.Message =
                $"The latest {e.PropertyName} edit could not be written into \"{e.ImagePath}\". " +
                $"It was saved to \"{e.SidecarPath}\" instead. Keep this sidecar with the image. " +
                $"Reason: {e.Error.Message}";
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
        if (_isClosing || e.PropertyName != nameof(ImageEditorViewModel.IsOpen)) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosing) return;
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
        if (ViewModel.ImageEditor.IsOpen && !ViewModel.Settings.IsOpen && !ViewModel.PeopleReview.IsOpen)
        {
            var editorFocus = Content?.XamlRoot is { } editorRoot
                ? FocusManager.GetFocusedElement(editorRoot) as DependencyObject
                : null;
            await EditorOverlay.HandleEditorKeyAsync(e, editorFocus);
            return;
        }
        if (!ViewModel.ImageViewer.IsOpen
            || ViewModel.Settings.IsOpen
            || ViewModel.PeopleReview.IsOpen
            || EditorOverlay.Visibility == Visibility.Visible
            || ViewerOverlay.HasFocusWithin())
            return;

        var focused = Content?.XamlRoot is { } root
            ? FocusManager.GetFocusedElement(root) as DependencyObject
            : null;
        if (focused is TextBox or PasswordBox or AutoSuggestBox or ComboBox or Slider
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

    private static bool IsWithin(DependencyObject? element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor)) return true;
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
        FolderNavPanel.SaveState();
        _slideshow?.Close();
        ViewModel.PropertyChanged -= OnMainViewModelPropertyChanged;
        ViewModel.PeopleReview.PropertyChanged -= OnPeopleReviewPropertyChanged;
        ViewModel.DuplicateReview.PropertyChanged -= OnDuplicateReviewPropertyChanged;
        ViewModel.DuplicateReview.Close();
        ViewModel.ImageViewer.PropertyChanged -= OnImageViewerPropertyChanged;
        ViewModel.ImageEditor.PropertyChanged -= OnImageEditorPropertyChanged;
        ViewModel.Settings.PropertyChanged -= OnSettingsPropertyChanged;
        MetadataWriterService.SidecarFallback -= OnMetadataSidecarFallback;
        ViewModel.ImageGrid.Cleanup();
        await ViewModel.CleanupAsync();
    }

    private void UpdateViewerVisibility()
    {
        ViewerOverlay.Visibility = ViewModel.ImageViewer.IsOpen
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateRibbonVisibility();
    }

    private void UpdateRibbonVisibility()
    {
        TopRibbon.Visibility = ViewModel.ImageViewer.IsOpen
            ? Visibility.Visible : Visibility.Collapsed;
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
    }

    private async void OnRibbonAdjustClicked(object? sender, EventArgs e)
    {
        var entry = ViewModel.ImageViewer.CurrentEntry;
        if (entry is null || ViewModel.ImageViewer.IsVideo) return;
        if (!ImageEditRenderer.IsSupported(entry.FilePath) && !RawToneMappingService.IsRaw(entry.FilePath))
        {
            ViewModel.StatusText = $"Editing not supported for {System.IO.Path.GetExtension(entry.FilePath)}";
            return;
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
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Unable to open editor: {ex.Message}";
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
    }

    private async void OnRedEyeSelectionCompleted(object? sender, RedEyeBounds bounds)
    {
        var entry = ViewModel.ImageViewer.CurrentEntry;
        if (entry is null) return;

        TopRibbon.IsEnabled = false;
        try
        {
            await ViewModel.BackupService.BackupOriginalAsync(entry.FilePath);
            ViewModel.StatusText = "Removing red eye…";
            var changed = await ImageEditRenderer.RemoveRedEyeAsync(entry.FilePath, bounds);
            await ViewModel.RefreshAfterPixelEditAsync(
                entry.FilePath,
                ViewerOverlay.CurrentImagePixelWidth,
                ViewerOverlay.CurrentImagePixelHeight,
                changed == 0 ? "No red eye found in selection" : "Removed red eye from");
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
        }
    }

    private void OnRibbonCancelCropClicked(object? sender, EventArgs e)
    {
        ViewerOverlay.ExitCropMode();
        TopRibbon.ExitCropMode();
    }

    private void OnRibbonCancelStraightenClicked(object? sender, EventArgs e)
    {
        ViewerOverlay.ExitStraightenMode();
        TopRibbon.ExitStraightenMode();
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
            await App.ViewModel.BackupService.BackupOriginalAsync(entry.FilePath);

            var result = await CropService.CropImageAsync(entry.FilePath, bounds.Value);
            await ViewModel.RefreshAfterCropAsync(entry.FilePath, result);
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
            await ViewModel.BackupService.BackupOriginalAsync(entry.FilePath);
            var (width, height) =
                await ImageEditRenderer.RenderStraightenedAsync(entry.FilePath, angle);
            await ViewModel.RefreshAfterStraightenAsync(entry.FilePath, width, height);
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
