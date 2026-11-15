// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoLibrarian.Services;

namespace PhotoLibrarian.Views;

public sealed partial class ImageEditorView
{
    private EditorPreviewAnalysis? _previewAnalysis;
    private bool _analysisFailed;
    private bool ClippingFormatSupported => ViewModel is { IsOpen: true } vm &&
        (vm.IsRawPreview || ImageEditRenderer.IsSupported(vm.ImagePath));
    private bool ShowClippedShadows => ClippingFormatSupported && ClippedShadowsItem.IsChecked;
    private bool ShowClippedHighlights => ClippingFormatSupported && ClippedHighlightsItem.IsChecked;

    private void RegisterClippingControls()
    {
        ClippedShadowsItem.RegisterPropertyChangedCallback(
            ToggleMenuFlyoutItem.IsCheckedProperty, OnClippingChanged);
        ClippedHighlightsItem.RegisterPropertyChangedCallback(
            ToggleMenuFlyoutItem.IsCheckedProperty, OnClippingChanged);
    }

    private void OnClippingChanged(DependencyObject sender, DependencyProperty property)
    {
        EditCanvas.Invalidate();
        HistogramCanvas.Invalidate();
        RawHistogramCanvas.Invalidate();
        CurveCanvas.Invalidate();
    }

    private void OnClippingOpening(object? sender, object args)
    {
        var analysis = GetPreviewAnalysis();
        ClippingSummaryItem.Text = analysis is null ? "Sampled output unavailable" :
            $"Sampled output: shadows {(analysis.HasShadows ? "clipped" : "not clipped")}; " +
            $"highlights {(analysis.HasHighlights ? "clipped" : "not clipped")}";
    }

    private void InvalidatePreviewAnalysis()
    {
        _previewAnalysis = null;
        _analysisFailed = false;
    }

    internal EditorPreviewAnalysis? GetPreviewAnalysis()
    {
        if (!ClippingFormatSupported || _analysisFailed || _sourceBitmap is null || ViewModel is null ||
            _loadedSourceVersion != ViewModel.SourceVersion || _previewRequest is not null) return null;
        if (_previewAnalysis is not null) return _previewAnalysis;
        try
        {
            _previewAnalysis = EditorPreviewAnalysis.Render(_retouchedBitmap ?? _sourceBitmap,
                ViewModel.GetCurrentParameters(), ViewModel.IsRawPreview);
            return _previewAnalysis;
        }
        catch (Exception exception)
        {
            _analysisFailed = true;
            System.Diagnostics.Debug.WriteLine($"[EDITOR HISTOGRAM] {exception}");
            ShowRetouchError($"Histogram unavailable: {exception.Message}");
            return null;
        }
    }

    private void DrawClippingIndicators(CanvasDrawingSession session, float width, float height,
        EditorPreviewAnalysis analysis, float left = 0, float top = 0)
    {
        // Edge bars remain distinguishable by position, even without color perception.
        if (ShowClippedShadows && analysis.HasShadows)
            session.FillRectangle(left, top, 4, height, Microsoft.UI.Colors.Blue);
        if (ShowClippedHighlights && analysis.HasHighlights)
            session.FillRectangle(left + width - 4, top, 4, height, Microsoft.UI.Colors.Red);
    }
}
