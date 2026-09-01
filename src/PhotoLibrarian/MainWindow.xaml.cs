using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using PhotoLibrarian.ViewModels;
using PhotoLibrarian.Views;
using System;
using System.Threading.Tasks;

namespace PhotoLibrarian;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel => App.ViewModel;

    private CropAspectRatio _pendingAspect = CropAspectRatio.Free;

    public async Task RefreshMetadataTreesAsync()
    {
        await FolderNavPanel.RefreshMetadataTreesAsync();
    }

    /// <summary>Repaints the left-panel "Flagged" node (count changes after flag edits).</summary>
    public void RefreshFlagTree()
    {
        FolderNavPanel.RefreshFlagTree();
    }

    public MainWindow()
    {
        this.InitializeComponent();

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
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(ViewModel.StatusText))
                    StatusBarText.Text = ViewModel.StatusText;
                if (e.PropertyName == nameof(ViewModel.IsIndexing))
                    UpdateBackgroundProgress();
                if (e.PropertyName == nameof(ViewModel.IsFaceDetectionRunning))
                {
                    UpdateBackgroundProgress();
                    UpdateFaceDetectionButton();
                }
                if (e.PropertyName is nameof(ViewModel.ImageViewer))
                    UpdateViewerVisibility();
                if (e.PropertyName is nameof(ViewModel.Settings))
                    UpdateSettingsVisibility();
            };

            // Track viewer open/close so the top ribbon mirrors it.
            ViewModel.ImageViewer.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(ImageViewerViewModel.IsOpen))
                    DispatcherQueue.TryEnqueue(UpdateRibbonVisibility);
                if (e.PropertyName == nameof(ImageViewerViewModel.Title))
                    DispatcherQueue.TryEnqueue(() => TopRibbon.SetContextLabel(ViewModel.ImageViewer.Title ?? ""));
            };
        }

        UpdateFaceDetectionButton();

        // Wire up settings close handler
        ViewModel.Settings.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.Settings.IsOpen))
                UpdateSettingsVisibility();
        };

        // Top-ribbon events
        TopRibbon.CropClicked += OnRibbonCropClicked;
        TopRibbon.CloseViewerClicked += (_, _) => ViewModel.ImageViewer.CloseCommand.Execute(null);
        TopRibbon.AdjustClicked += OnRibbonAdjustClicked;
        TopRibbon.ApplyCropClicked += OnRibbonApplyCropClicked;
        TopRibbon.CancelCropClicked += OnRibbonCancelCropClicked;
        TopRibbon.CropAspectChanged += OnRibbonCropAspectChanged;
        ViewerOverlay.CropApplyRequested += OnRibbonApplyCropClicked;
        ViewerOverlay.CropCancelRequested += OnRibbonCancelCropClicked;

        // Cleanup on window close
        this.Closed += OnWindowClosed;
    }

    private void UpdateBackgroundProgress()
    {
        IndexingProgress.IsActive = ViewModel.IsIndexing || ViewModel.IsFaceDetectionRunning;
    }

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
        // Cancel all background tasks to allow clean shutdown
        await ViewModel.CleanupAsync();
        ViewModel?.ImageGrid?.Cleanup();
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
    }

    private void OnRibbonCropClicked(object? sender, EventArgs e)
    {
        if (!ViewModel.ImageViewer.IsOpen) return;
        if (ViewModel.ImageViewer.IsVideo) return;
        ViewerOverlay.EnterCropMode();
        ViewerOverlay.CropOverlay.AspectRatio = _pendingAspect;
        TopRibbon.EnterCropMode();
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

        await ViewModel.ImageEditor.OpenForEditAsync(entry);
    }

    private void OnRibbonCancelCropClicked(object? sender, EventArgs e)
    {
        ViewerOverlay.ExitCropMode();
        TopRibbon.ExitCropMode();
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
        ViewModel.StatusText = "Applying crop…";
        try
        {
            // Back up the original first (no-op if a backup already exists).
            await App.ViewModel.BackupService.BackupOriginalAsync(entry.FilePath);

            var (w, h) = await CropService.CropImageAsync(entry.FilePath, bounds.Value);
            await ViewModel.RefreshAfterCropAsync(entry.FilePath, w, h);
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Crop failed: {ex.Message}";
        }
        finally
        {
            ViewerOverlay.ExitCropMode();
            TopRibbon.ExitCropMode();
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
    
    private async void OnBenchmarkClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.RunBenchmarkCommand.ExecuteAsync(null);
    }
}
