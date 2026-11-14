// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PhotoLibrarian.Services;
using System.Numerics;
using Windows.Graphics.DirectX;

namespace PhotoLibrarian.Views;

public sealed partial class ImageEditorView
{
    private CanvasBitmap? _beforeBitmap;
    private byte[]? _beforePixels;
    private int _beforeWidth;
    private int _beforeHeight;
    private int _loadedSourceVersion = -1;
    private bool _syncingComparison;

    private EditorComparisonMode ComparisonMode => ViewModel?.ComparisonMode ?? EditorComparisonMode.After;

    private Vector2 PreviewSize => EditorComparisonRenderer.GetViewportSize(
        new Vector2((float)EditCanvas.ActualWidth, (float)EditCanvas.ActualHeight), ComparisonMode);

    internal bool TryDrawComparisonPreview(CanvasDrawingSession session, Vector2 canvasSize)
    {
        var before = _beforeBitmap;
        var after = _retouchedBitmap ?? _sourceBitmap;
        var vm = ViewModel;
        if (before is null || after is null || vm is null || vm.IsRawPreview ||
            _loadedSourceVersion != vm.SourceVersion) return false;
        EditorComparisonRenderer.Draw(session, before, after, vm.GetCurrentParameters(),
            ComparisonMode, _viewport, canvasSize, (float)SplitPositionSlider.Value / 100);
        return true;
    }

    private void RegisterComparisonControls()
    {
        foreach (var item in new[] { AfterViewItem, BeforeViewItem, SideBySideViewItem, SplitViewItem })
            item.RegisterPropertyChangedCallback(ToggleMenuFlyoutItem.IsCheckedProperty, OnComparisonItemChanged);
    }

    private void OnComparisonItemChanged(DependencyObject sender, DependencyProperty property)
    {
        if (_syncingComparison) return;
        if (sender is ToggleMenuFlyoutItem { Tag: string tag } item &&
            Enum.TryParse<EditorComparisonMode>(tag, out var mode) && ComparisonButton.IsEnabled)
        {
            CancelRetouchGesture();
            ViewModel?.ToggleComparison(item.IsChecked ? mode : EditorComparisonMode.After);
        }
        UpdateHistoryControls();
    }

    private void OnSplitPositionChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (EditCanvas is null || SplitDivider is null) return;
        UpdateComparisonDivider();
        EditCanvas.Invalidate();
    }

    private void UpdateComparisonControls()
    {
        var available = ViewModel is { IsOpen: true, IsRawPreview: false, IsBusy: false } &&
            _sourceBitmap is not null && _beforeBitmap is not null;
        ComparisonButton.IsEnabled = available;
        ToolTipService.SetToolTip(ComparisonButton, ViewModel?.IsRawPreview == true
            ? "Before/after comparison is not available for RAW development."
            : "B: before recipes; N: side by side; M: split. Press again for After. Earlier baked edits are not undone.");
        _syncingComparison = true;
        try
        {
            AfterViewItem.IsChecked = ComparisonMode == EditorComparisonMode.After;
            BeforeViewItem.IsChecked = ComparisonMode == EditorComparisonMode.Before;
            SideBySideViewItem.IsChecked = ComparisonMode == EditorComparisonMode.SideBySide;
            SplitViewItem.IsChecked = ComparisonMode == EditorComparisonMode.Split;
        }
        finally { _syncingComparison = false; }
        var comparing = ComparisonMode != EditorComparisonMode.After;
        ComparisonLabel.Visibility = comparing ? Visibility.Visible : Visibility.Collapsed;
        ComparisonLabelText.Text = ComparisonMode == EditorComparisonMode.Before
            ? "Before recipes (opening source)" : "Before recipes (left) | After (right)";
        SplitPositionSlider.Visibility = ComparisonMode == EditorComparisonMode.Split
            ? Visibility.Visible : Visibility.Collapsed;
        SplitDivider.Visibility = ComparisonMode is EditorComparisonMode.Split or EditorComparisonMode.SideBySide
            ? Visibility.Visible : Visibility.Collapsed;
        // Painting is only meaningful in the single After view, where source coordinates are unambiguous.
        if (comparing) RetouchToggle.IsChecked = false;
        RetouchToggle.IsEnabled &= !comparing;
        RetouchRadius.IsEnabled = RetouchToggle.IsEnabled;
        UpdateComparisonDivider();
        EditCanvas.Invalidate();
    }

    private void UpdateComparisonDivider()
    {
        var fraction = ComparisonMode == EditorComparisonMode.SideBySide ? .5 : SplitPositionSlider.Value / 100;
        SplitDivider.Translation = new Vector3((float)(EditCanvas.ActualWidth * fraction), 0, 0);
    }

    private void ReleaseSourceBitmap()
    {
        if (!ReferenceEquals(_sourceBitmap, _beforeBitmap)) _sourceBitmap?.Dispose();
        _sourceBitmap = null;
        _sourcePixels = null;
        ComparisonButton.IsEnabled = false;
    }

    private void KeepComparisonSource(CanvasBitmap bitmap, byte[] pixels)
    {
        if (_beforePixels is null)
        {
            _beforePixels = pixels;
            _beforeWidth = (int)bitmap.SizeInPixels.Width;
            _beforeHeight = (int)bitmap.SizeInPixels.Height;
            _beforeBitmap = bitmap;
        }
        else if (_beforeBitmap is null)
        {
            _beforeBitmap = CanvasBitmap.CreateFromBytes(EditCanvas, _beforePixels, _beforeWidth, _beforeHeight,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
        }
    }

    private void ReleaseComparisonDevice()
    {
        ReleaseSourceBitmap();
        _beforeBitmap?.Dispose();
        _beforeBitmap = null;
    }

    private void ClearComparisonSource()
    {
        if (ReferenceEquals(_sourceBitmap, _beforeBitmap)) _sourceBitmap = null;
        _beforeBitmap?.Dispose();
        _beforeBitmap = null;
        _beforePixels = null;
        _beforeWidth = _beforeHeight = 0;
    }
}
