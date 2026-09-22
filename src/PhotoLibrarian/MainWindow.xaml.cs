using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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

    public async Task RefreshMetadataTreesAsync()
    {
        await FolderNavPanel.RefreshMetadataTreesAsync();
        await ImageGridPanel.RefreshPeopleAsync();
    }

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
        ViewModel.ImageViewer.PropertyChanged += OnImageViewerPropertyChanged;
        ViewModel.Settings.PropertyChanged += OnSettingsPropertyChanged;

        UpdateFaceDetectionButton();

        // Top-ribbon events
        TopRibbon.CropClicked += OnRibbonCropClicked;
        TopRibbon.StraightenClicked += OnRibbonStraightenClicked;
        TopRibbon.CloseViewerClicked += (_, _) => ViewModel.ImageViewer.CloseCommand.Execute(null);
        TopRibbon.AdjustClicked += OnRibbonAdjustClicked;
        TopRibbon.ApplyCropClicked += OnRibbonApplyCropClicked;
        TopRibbon.CancelCropClicked += OnRibbonCancelCropClicked;
        TopRibbon.CropAspectChanged += OnRibbonCropAspectChanged;
        TopRibbon.ApplyStraightenClicked += OnRibbonApplyStraightenClicked;
        TopRibbon.CancelStraightenClicked += OnRibbonCancelStraightenClicked;
        ViewerOverlay.CropApplyRequested += OnRibbonApplyCropClicked;
        ViewerOverlay.CropCancelRequested += OnRibbonCancelCropClicked;
        ViewerOverlay.StraightenApplyRequested += OnRibbonApplyStraightenClicked;
        ViewerOverlay.StraightenCancelRequested += OnRibbonCancelStraightenClicked;
        ViewerOverlay.ManualFaceTaggingExited += (_, _) => ViewModel.OnManualFaceTaggingExited();

        // Cleanup on window close
        this.Closed += OnWindowClosed;
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
                    TopRibbon.SetContextLabel(ViewModel.ImageViewer.Title ?? "");
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
        ViewModel.PropertyChanged -= OnMainViewModelPropertyChanged;
        ViewModel.PeopleReview.PropertyChanged -= OnPeopleReviewPropertyChanged;
        ViewModel.ImageViewer.PropertyChanged -= OnImageViewerPropertyChanged;
        ViewModel.Settings.PropertyChanged -= OnSettingsPropertyChanged;
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
        if (!ImageEditRenderer.IsSupported(entry.FilePath))
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

        await ViewModel.ImageEditor.OpenForEditAsync(entry);
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
            await ViewModel.PeopleReview.OpenAsync();
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
