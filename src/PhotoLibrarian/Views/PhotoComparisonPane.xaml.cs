// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Services;
using Windows.System;

namespace PhotoLibrarian.Views;

public sealed partial class PhotoComparisonPane : UserControl
{
    private readonly ImageZoomPanController _controller;
    private ComparisonViewport _view = ComparisonViewport.Fit;
    private bool _ready;
    internal bool IsReady => _ready;
    internal ImageEntry Entry { get; }
    internal event Action<PhotoComparisonPane>? RemoveRequested;
    internal event Action<PhotoComparisonPane>? RetryRequested;
    internal event Action<ComparisonViewport>? ViewChanged;

    internal PhotoComparisonPane(ImageEntry entry)
    {
        InitializeComponent();
        Entry = entry;
        FileNameText.Text = entry.FileName;
        ToolTipService.SetToolTip(FileNameText, entry.FilePath);
        AutomationProperties.SetName(this, entry.FileName);
        AutomationProperties.SetName(Viewport, entry.FileName);
        AutomationProperties.SetHelpText(Viewport, ComparisonStrings.Get("ComparisonKeyboardHelp"));
        AutomationProperties.SetName(RemoveButton, ComparisonStrings.Format("ComparisonRemoveName", entry.FileName));
        AutomationProperties.SetName(RetryButton, ComparisonStrings.Format("ComparisonRetryName", entry.FileName));
        _controller = new ImageZoomPanController(Viewport, ScrollContent, ImageHost, Photo);
        Viewport.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnWheel), handledEventsToo: true);
        SetLoading();
    }

    internal void SetLoading()
    {
        StatusText.Text = ComparisonStrings.Get("ComparisonLoading");
        RetryButton.Visibility = Visibility.Collapsed;
    }

    internal void SetImage(BitmapImage image)
    {
        Photo.Source = image;
        _controller.SetImageSize(image.PixelWidth, image.PixelHeight);
        _ready = true;
        StatusText.Text = ComparisonStrings.Format("ComparisonDimensions", image.PixelWidth, image.PixelHeight);
        ApplyView(_view);
    }

    internal void SetError(string message)
    {
        StatusText.Text = ComparisonStrings.Format("ComparisonLoadFailed", message);
        RetryButton.Visibility = Visibility.Visible;
    }

    internal void Release()
    {
        _ready = false;
        Photo.Source = null;
    }

    internal void ApplyView(ComparisonViewport view)
    {
        _view = view;
        if (_ready) _controller.ApplyView(view);
    }

    private void PublishView()
    {
        if (!_ready) return;
        _view = _controller.CaptureView();
        ViewChanged?.Invoke(_view);
    }

    internal void Zoom(bool zoomIn)
    {
        if (!_ready) return;
        if (zoomIn) _controller.ZoomIn();
        else _controller.ZoomOut();
        PublishView();
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e) => ApplyView(_view);
    private void OnRemove(object sender, RoutedEventArgs e) => RemoveRequested?.Invoke(this);
    private void OnRetry(object sender, RoutedEventArgs e) => RetryRequested?.Invoke(this);
    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (!_ready) return;
        _controller.HandlePointerWheelChanged(e);
        PublishView();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Viewport.Focus(FocusState.Pointer);
        if (_ready) _controller.HandlePointerPressed(Viewport, e);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_ready) return;
        _controller.HandlePointerMoved(Viewport, e);
        if (e.Handled) PublishView();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e) =>
        _controller.HandlePointerReleased(Viewport, e);
    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e) =>
        _controller.HandlePointerCanceled(Viewport, e);

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (HandlePhotoKey(e.Key)) e.Handled = true;
    }

    internal bool HandlePhotoKey(VirtualKey key)
    {
        if (!_ready) return false;
        switch (key)
        {
            case VirtualKey.Add:
            case (VirtualKey)0xBB: Zoom(true); break;
            case VirtualKey.Subtract:
            case (VirtualKey)0xBD: Zoom(false); break;
            case VirtualKey.Home: ApplyView(ComparisonViewport.Fit); PublishView(); break;
            case VirtualKey.Number1: ApplyView(new(1, 0.5, 0.5, IsActualSize: true)); PublishView(); break;
            case VirtualKey.Left: _controller.PanBy(48, 0); PublishView(); break;
            case VirtualKey.Right: _controller.PanBy(-48, 0); PublishView(); break;
            case VirtualKey.Up: _controller.PanBy(0, 48); PublishView(); break;
            case VirtualKey.Down: _controller.PanBy(0, -48); PublishView(); break;
            default: return false;
        }
        return true;
    }
}
