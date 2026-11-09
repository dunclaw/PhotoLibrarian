// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.ViewModels;
using PhotoLibrarian.Services;

namespace PhotoLibrarian.Views;

public sealed partial class ImageViewerOverlay
{
    private PhotoFuseViewModel? _fuse;
    private Flyout? _replacementFlyout;
    private WriteableBitmap? _fuseBitmap;
    public bool IsPhotoFusing => _fuse is { IsActive: true };
    public event EventHandler? PhotoFuseExitRequested;
    public event EventHandler? PhotoFuseDiscardRequested;

    private void OnFuseDiscardClick(object sender, RoutedEventArgs args) =>
        PhotoFuseDiscardRequested?.Invoke(this, EventArgs.Empty);

    public void SetPhotoFuseWorkspace(PhotoFuseViewModel? workspace)
    {
        if (_fuse is not null)
        {
            _fuse.PropertyChanged -= OnFusePropertyChanged;
            _fuse.DisplayChanged -= OnFuseDisplayChanged;
            _fuse.Faces.CollectionChanged -= OnFuseFacesChanged;
        }
        _replacementFlyout?.Hide();
        _fuse = workspace;
        if (workspace is not null)
        {
            _zoomPan ??= new ImageZoomPanController(ImageScrollViewer, ScrollContent, ImageHost, FullImage);
            workspace.PropertyChanged += OnFusePropertyChanged;
            workspace.DisplayChanged += OnFuseDisplayChanged;
            workspace.Faces.CollectionChanged += OnFuseFacesChanged;
            PreviousButton.Visibility = Visibility.Collapsed;
            NextButton.Visibility = Visibility.Collapsed;
            FuseStatusBar.Visibility = Visibility.Visible;
            FuseStatusBar.IsOpen = true;
            OnFuseDisplayChanged(this, EventArgs.Empty);
        }
        else
        {
            _fuseBitmap = null;
            FuseFaceOverlay.Children.Clear();
            FuseFaceOverlay.Visibility = Visibility.Collapsed;
            FuseStatusBar.Visibility = Visibility.Collapsed;
            PreviousButton.Visibility = Visibility.Visible;
            NextButton.Visibility = Visibility.Visible;
            FullImage.Source = ViewModel?.CurrentImage;
            if (ViewModel?.CurrentImage is BitmapImage bitmap)
            {
                _currentImagePixelWidth = (uint)bitmap.PixelWidth;
                _currentImagePixelHeight = (uint)bitmap.PixelHeight;
                _zoomPan?.SetImageSize(bitmap.PixelWidth, bitmap.PixelHeight);
                _zoomPan?.ApplyBestFit();
            }
        }
    }

    private void OnFusePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_fuse is null) return;
        FuseStatusBar.Message = _fuse.Status;
        FuseFaceOverlay.IsHitTestVisible = !_fuse.IsBusy;
        if (_fuse.IsBusy) _replacementFlyout?.Hide();
    }

    private void OnFuseDisplayChanged(object? sender, EventArgs args)
    {
        if (_fuse?.DisplayPixels is not { } pixels) return;
        bool first = _fuseBitmap is null;
        _fuseBitmap ??= new WriteableBitmap(_fuse.Width, _fuse.Height);
        using (var stream = _fuseBitmap.PixelBuffer.AsStream()) stream.Write(pixels);
        _fuseBitmap.Invalidate();
        FullImage.Source = _fuseBitmap;
        if (first)
        {
            _currentImagePixelWidth = (uint)_fuse.Width;
            _currentImagePixelHeight = (uint)_fuse.Height;
            _zoomPan?.SetImageSize(_fuse.Width, _fuse.Height);
            _zoomPan?.ApplyBestFit();
            SetZoomControlsEnabled(true);
        }
        DrawFuseFaces();
    }

    private void OnFuseFacesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args) =>
        DrawFuseFaces();

    private void OnFuseOverlaySizeChanged(object sender, SizeChangedEventArgs args) => DrawFuseFaces();

    private void DrawFuseFaces()
    {
        FuseFaceOverlay.Children.Clear();
        if (_fuse is null) return;
        FuseFaceOverlay.Visibility = IsManualFaceTagging ? Visibility.Collapsed : Visibility.Visible;
        foreach (var face in _fuse.Faces)
        {
            var button = new Button
            {
                Tag = face,
                Style = (Style)RootGrid.Resources["FuseFaceButtonStyle"],
                Width = face.Width * FuseFaceOverlay.ActualWidth,
                Height = face.Height * FuseFaceOverlay.ActualHeight
            };
            AutomationProperties.SetAutomationId(button, $"FuseFace_{face.Id}");
            AutomationProperties.SetName(button, PhotoFuseStrings.Format("FuseFaceAccessible",
                face.PersonName ?? PhotoFuseStrings.Get("FuseUnnamedFace")));
            ToolTipService.SetToolTip(button, PhotoFuseStrings.Get("FuseChooseFace"));
            Canvas.SetLeft(button, face.X * FuseFaceOverlay.ActualWidth);
            Canvas.SetTop(button, face.Y * FuseFaceOverlay.ActualHeight);
            button.Click += OnFuseFaceClick;
            FuseFaceOverlay.Children.Add(button);
        }
    }

    private void OnFuseFaceClick(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: FaceRegion face } anchor || _fuse is null || _fuse.IsBusy) return;
        StopFocusGuard();
        var workspace = _fuse;
        var candidates = workspace.GetReplacements(face);
        var items = candidates.Select(candidate => new PersonTagDisplayItem(candidate.Image, candidate.Face)).ToList();
        var panel = new StackPanel { Width = 320, Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = PhotoFuseStrings.Get(candidates.Count == 0 ? "FuseNoOptions" : "FuseOptionsGuidance"),
            TextWrapping = TextWrapping.Wrap
        });
        var original = new Button { Content = PhotoFuseStrings.Get("FuseOriginalButton"),
            HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(original, "FuseOriginal");
        original.Click += async (_, _) =>
        {
            _replacementFlyout?.Hide();
            await workspace.SelectAsync(face, null);
        };
        panel.Children.Add(original);
        var list = new ListView
        {
            ItemsSource = items,
            ItemTemplate = (DataTemplate)RootGrid.Resources["FuseReplacementTemplate"],
            MaxHeight = 360,
            SelectionMode = ListViewSelectionMode.Single
        };
        AutomationProperties.SetAutomationId(list, "FuseReplacements");
        AutomationProperties.SetName(list, PhotoFuseStrings.Get("FuseReplacementList"));
        list.ContainerContentChanging += OnFuseReplacementRealized;
        list.SelectionChanged += async (_, _) =>
        {
            if (list.SelectedItem is not PersonTagDisplayItem item || workspace.IsBusy || !workspace.IsActive) return;
            int index = items.IndexOf(item);
            _replacementFlyout?.Hide();
            await workspace.SelectAsync(face, candidates[index]);
        };
        panel.Children.Add(list);
        _replacementFlyout = new Flyout { Content = panel };
        _replacementFlyout.ShowAt(anchor);
    }

    private async void OnFuseReplacementRealized(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not PersonTagDisplayItem item) return;
        AutomationProperties.SetAutomationId(args.ItemContainer, $"FuseReplacement_{item.Image.Id}_{item.FaceId}");
        AutomationProperties.SetName(args.ItemContainer, item.Image.FileName);
        try { await item.LoadThumbnailAsync(); }
        catch (Exception ex)
        {
            if (_fuse is not null) _fuse.Status = PhotoFuseStrings.Format("FuseThumbnailFailed", ex.Message);
            Core.Diagnostics.DebugLog.WriteLine($"Photo Fuse thumbnail: {ex}");
        }
    }
}
