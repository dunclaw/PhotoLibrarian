using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace PhotoLibrarian.Services;

/// <summary>
/// Manages zoom level, pan gestures, and scroll-wheel zoom for image viewer.
/// Based on OrthoClassificationViewer's ZoomPanController.
/// </summary>
internal sealed class ImageZoomPanController
{
    private readonly ScrollViewer _scrollViewer;
    private readonly Canvas _scrollContent;
    private readonly Grid _imageHost;
    private readonly Image _image;

    private float _zoomFactor = 1.0f;
    private const float MaxZoomFactor = 10f;

    private bool _isPanning;
    private Point _panStartPoint;
    private double _panStartCanvasLeft;
    private double _panStartCanvasTop;

    private int _imageWidth;
    private int _imageHeight;

    public float ZoomFactor => _zoomFactor;

    public ImageZoomPanController(ScrollViewer scrollViewer, Canvas scrollContent, Grid imageHost, Image image)
    {
        _scrollViewer = scrollViewer;
        _scrollContent = scrollContent;
        _imageHost = imageHost;
        _image = image;
    }

    /// <summary>Updates the logical image dimensions (in pixels) being displayed.</summary>
    public void SetImageSize(int width, int height)
    {
        System.Diagnostics.Debug.WriteLine($"ZoomPanController: SetImageSize({width}, {height})");
        _imageWidth = width;
        _imageHeight = height;
        UpdateImageSize();
    }

    public void ZoomIn()
    {
        var target = _zoomFactor * 1.2f;
        if (target > MaxZoomFactor) target = MaxZoomFactor;
        ZoomToCenter(target);
    }

    public void ZoomOut()
    {
        var target = _zoomFactor / 1.2f;
        var minZoom = GetMinZoomFactor();
        if (target < minZoom) target = minZoom;
        ZoomToCenter(target);
    }

    /// <summary>
    /// Fits the image to the viewport. <paramref name="contentInset"/> reserves a gap on every
    /// side — the crop overlay needs it so handles straddling the image edge aren't clipped.
    /// </summary>
    public void ApplyBestFit(double contentInset = 0)
    {
        if (_imageWidth == 0 || _imageHeight == 0)
        {
            System.Diagnostics.Debug.WriteLine("ZoomPanController: ApplyBestFit - no image size set");
            return;
        }

        var viewportWidth = _scrollViewer.ActualWidth;
        var viewportHeight = _scrollViewer.ActualHeight;
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            System.Diagnostics.Debug.WriteLine($"ZoomPanController: ApplyBestFit - invalid viewport: {viewportWidth}x{viewportHeight}");
            return;
        }

        var fitWidth = viewportWidth;
        var fitHeight = viewportHeight;
        if (contentInset > 0)
        {
            // Never inset so far that nothing is left to fit into.
            fitWidth = Math.Max(viewportWidth / 2, viewportWidth - 2 * contentInset);
            fitHeight = Math.Max(viewportHeight / 2, viewportHeight - 2 * contentInset);
        }

        var zoomX = (float)(fitWidth / _imageWidth);
        var zoomY = (float)(fitHeight / _imageHeight);
        var bestFitZoom = Math.Min(zoomX, zoomY);

        if (bestFitZoom < 0.01f) bestFitZoom = 0.01f;
        if (bestFitZoom > MaxZoomFactor) bestFitZoom = MaxZoomFactor;

        System.Diagnostics.Debug.WriteLine($"ZoomPanController: ApplyBestFit - viewport={viewportWidth}x{viewportHeight}, zoom={bestFitZoom:F3}");

        ApplyZoom(bestFitZoom);
        CenterContent();
    }

    public void HandleSizeChanged(Size previousSize)
    {
        if (_imageWidth == 0 || _imageHeight == 0)
        {
            UpdateImageSize();
            return;
        }

        var oldW = previousSize.Width;
        var oldH = previousSize.Height;
        var currentLeft = GetCanvasLeft();
        var currentTop = GetCanvasTop();

        UpdateImageSize();

        if (oldW > 0 && oldH > 0)
        {
            // Shift position to keep the same image center visible
            var newW = _scrollViewer.ActualWidth;
            var newH = _scrollViewer.ActualHeight;
            PositionContent(currentLeft + (newW - oldW) / 2, currentTop + (newH - oldH) / 2);
        }
        else
        {
            CenterContent();
        }

        UpdateMinZoomFactor();
    }

    public void HandlePointerWheelChanged(PointerRoutedEventArgs e)
    {
        var sv = _scrollViewer;
        var delta = e.GetCurrentPoint(sv).Properties.MouseWheelDelta;
        
        System.Diagnostics.Debug.WriteLine($"ZoomPanController: HandlePointerWheelChanged - delta={delta}");
        
        if (delta == 0)
            return;

        if (_imageWidth == 0 || _imageHeight == 0)
        {
            System.Diagnostics.Debug.WriteLine("ZoomPanController: No image loaded");
            e.Handled = true;
            return;
        }

        var zoomingIn = delta > 0;
        var step = zoomingIn ? 1.2f : (1f / 1.2f);
        var newZoom = _zoomFactor * step;

        var minZoom = GetMinZoomFactor();
        if (newZoom < minZoom) newZoom = minZoom;
        if (newZoom > MaxZoomFactor) newZoom = MaxZoomFactor;

        System.Diagnostics.Debug.WriteLine($"ZoomPanController: Zoom delta={delta}, current={_zoomFactor:F3}, new={newZoom:F3}, min={minZoom:F3}, zoomingIn={zoomingIn}");

        if (Math.Abs(newZoom - _zoomFactor) < 0.0001f)
        {
            e.Handled = true;
            return;
        }

        var viewportWidth = sv.ActualWidth;
        var viewportHeight = sv.ActualHeight;
        var cursorInViewport = e.GetCurrentPoint(sv).Position;

        var currentCanvasLeft = GetCanvasLeft();
        var currentCanvasTop = GetCanvasTop();

        // Compute image coords under cursor
        var imageX = (cursorInViewport.X - currentCanvasLeft) / _zoomFactor;
        var imageY = (cursorInViewport.Y - currentCanvasTop) / _zoomFactor;

        var newContentWidth = _imageWidth * newZoom;
        var newContentHeight = _imageHeight * newZoom;

        ApplyZoom(newZoom);

        // Positioning strategy:
        // - Zoom IN: track cursor position
        // - Zoom OUT while image is larger than viewport: track cursor
        // - Zoom OUT once all edges visible: progressively blend toward centered
        double newCanvasLeft, newCanvasTop;

        // Use small epsilon for floating point comparison
        const double epsilon = 0.5; // Half pixel tolerance
        var fitsEntirely = (newContentWidth <= viewportWidth + epsilon) && (newContentHeight <= viewportHeight + epsilon);
        
        System.Diagnostics.Debug.WriteLine($"ZoomPanController: fitsEntirely={fitsEntirely}, zoomingIn={zoomingIn}, content={newContentWidth:F0}x{newContentHeight:F0}, viewport={viewportWidth:F0}x{viewportHeight:F0}");

        if (!fitsEntirely)
        {
            // Image larger than viewport: always track cursor position
            newCanvasLeft = cursorInViewport.X - imageX * newZoom;
            newCanvasTop = cursorInViewport.Y - imageY * newZoom;
            System.Diagnostics.Debug.WriteLine($"ZoomPanController: Tracking cursor");
        }
        else if (zoomingIn)
        {
            newCanvasLeft = cursorInViewport.X - imageX * newZoom;
            newCanvasTop = cursorInViewport.Y - imageY * newZoom;
            System.Diagnostics.Debug.WriteLine($"ZoomPanController: Zooming in while fits, tracking cursor");
        }
        else
        {
            // Zooming out with image fitting in viewport: blend toward center
            var bestFitZoom = Math.Min(
                (float)(viewportWidth / _imageWidth),
                (float)(viewportHeight / _imageHeight));
            var blendRange = bestFitZoom - minZoom;
            var t = blendRange > 0.0001
                ? Math.Clamp((bestFitZoom - newZoom) / blendRange, 0, 1)
                : 1.0;

            var trackLeft = cursorInViewport.X - imageX * newZoom;
            var trackTop = cursorInViewport.Y - imageY * newZoom;
            var centerLeft = (viewportWidth - newContentWidth) / 2;
            var centerTop = (viewportHeight - newContentHeight) / 2;

            newCanvasLeft = trackLeft + (centerLeft - trackLeft) * t;
            newCanvasTop = trackTop + (centerTop - trackTop) * t;
            
            System.Diagnostics.Debug.WriteLine($"ZoomPanController: Blending to center, t={t:F3}, blend from ({trackLeft:F0},{trackTop:F0}) to ({centerLeft:F0},{centerTop:F0}) = ({newCanvasLeft:F0},{newCanvasTop:F0})");
        }

        PositionContent(newCanvasLeft, newCanvasTop);
        e.Handled = true;
    }

    public void HandlePointerPressed(ScrollViewer sv, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(sv);
        if (!pt.Properties.IsLeftButtonPressed && !pt.IsInContact)
            return;

        _isPanning = true;
        _panStartPoint = pt.Position;
        _panStartCanvasLeft = GetCanvasLeft();
        _panStartCanvasTop = GetCanvasTop();

        sv.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    public void HandlePointerMoved(ScrollViewer sv, PointerRoutedEventArgs e)
    {
        if (!_isPanning)
            return;

        var pt = e.GetCurrentPoint(sv);
        var dx = pt.Position.X - _panStartPoint.X;
        var dy = pt.Position.Y - _panStartPoint.Y;

        var newLeft = _panStartCanvasLeft + dx;
        var newTop = _panStartCanvasTop + dy;
        
        // Apply clamping with pan mode
        PositionContent(newLeft, newTop, clampWhenFits: true);
        e.Handled = true;
    }

    public void HandlePointerReleased(ScrollViewer sv, PointerRoutedEventArgs e)
    {
        sv.ReleasePointerCapture(e.Pointer);
        _isPanning = false;
    }

    public void HandlePointerCanceled(ScrollViewer sv, PointerRoutedEventArgs e)
    {
        sv.ReleasePointerCapture(e.Pointer);
        _isPanning = false;
    }

    private float GetMinZoomFactor()
    {
        if (_imageWidth == 0 || _imageHeight == 0)
            return 0.01f;

        var viewportWidth = _scrollViewer.ActualWidth;
        var viewportHeight = _scrollViewer.ActualHeight;
        if (viewportWidth <= 0 || viewportHeight <= 0)
            return 0.01f;

        var zoomX = (float)(viewportWidth / _imageWidth);
        var zoomY = (float)(viewportHeight / _imageHeight);
        var bestFitZoom = Math.Min(zoomX, zoomY);
        
        // Don't allow zooming out past fit - stop when image is centered and fully visible
        return Math.Max(0.01f, bestFitZoom);
    }

    private void ZoomToCenter(float targetZoom)
    {
        var viewportWidth = _scrollViewer.ActualWidth;
        var viewportHeight = _scrollViewer.ActualHeight;

        var currentCanvasLeft = GetCanvasLeft();
        var currentCanvasTop = GetCanvasTop();

        var centerImageX = (viewportWidth / 2 - currentCanvasLeft) / _zoomFactor;
        var centerImageY = (viewportHeight / 2 - currentCanvasTop) / _zoomFactor;

        ApplyZoom(targetZoom);

        var newCanvasLeft = viewportWidth / 2 - centerImageX * targetZoom;
        var newCanvasTop = viewportHeight / 2 - centerImageY * targetZoom;
        PositionContent(newCanvasLeft, newCanvasTop);
    }

    private void ApplyZoom(float zoom)
    {
        _zoomFactor = zoom;
        UpdateImageSize();
    }

    private void UpdateImageSize()
    {
        if (_imageWidth == 0 || _imageHeight == 0)
            return;

        var scaledWidth = _imageWidth * _zoomFactor;
        var scaledHeight = _imageHeight * _zoomFactor;
        
        System.Diagnostics.Debug.WriteLine($"ZoomPanController: UpdateImageSize - image={_imageWidth}x{_imageHeight}, zoom={_zoomFactor:F3}, scaled={scaledWidth:F0}x{scaledHeight:F0}");
        
        // Size the host grid to the scaled dimensions
        _imageHost.Width = scaledWidth;
        _imageHost.Height = scaledHeight;
        
        // The Image element will stretch to fill the grid (with Stretch="Uniform" or "Fill")
        // Don't set explicit Width/Height on the Image - let it fill the Grid naturally

        var viewportWidth = _scrollViewer.ActualWidth;
        var viewportHeight = _scrollViewer.ActualHeight;
        if (viewportWidth > 0) _scrollContent.Width = Math.Max(viewportWidth, scaledWidth);
        if (viewportHeight > 0) _scrollContent.Height = Math.Max(viewportHeight, scaledHeight);
    }

    private void PositionContent(double canvasLeft, double canvasTop, bool clampWhenFits = false)
    {
        // Clamp position to prevent image from being dragged too far off screen
        var viewportWidth = _scrollViewer.ActualWidth;
        var viewportHeight = _scrollViewer.ActualHeight;
        var contentWidth = _imageWidth * _zoomFactor;
        var contentHeight = _imageHeight * _zoomFactor;

        var originalLeft = canvasLeft;
        var originalTop = canvasTop;

        // Use small epsilon for floating point comparison
        const double epsilon = 0.5;
        bool fitsHorizontally = contentWidth <= viewportWidth + epsilon;
        bool fitsVertically = contentHeight <= viewportHeight + epsilon;

        // If clampWhenFits is true (e.g., during panning), center images that fit
        if (clampWhenFits)
        {
            if (fitsHorizontally)
            {
                canvasLeft = (viewportWidth - contentWidth) / 2;
            }
            if (fitsVertically)
            {
                canvasTop = (viewportHeight - contentHeight) / 2;
            }
        }

        // Only clamp if image is larger than viewport in that dimension
        if (!fitsHorizontally)
        {
            // Image wider than viewport: allow edge to go halfway off
            var halfViewport = viewportWidth / 2;
            var minLeft = -(contentWidth - halfViewport); // Right edge can go to middle of viewport
            var maxLeft = halfViewport;                    // Left edge can go to middle of viewport
            canvasLeft = Math.Clamp(canvasLeft, minLeft, maxLeft);
        }

        if (!fitsVertically)
        {
            // Image taller than viewport: allow edge to go halfway off
            var halfViewport = viewportHeight / 2;
            var minTop = -(contentHeight - halfViewport); // Bottom edge can go to middle of viewport
            var maxTop = halfViewport;                     // Top edge can go to middle of viewport
            canvasTop = Math.Clamp(canvasTop, minTop, maxTop);
        }

        if (canvasLeft != originalLeft || canvasTop != originalTop)
        {
            System.Diagnostics.Debug.WriteLine($"ZoomPanController: PositionContent clamped from ({originalLeft:F0}, {originalTop:F0}) to ({canvasLeft:F0}, {canvasTop:F0})");
        }

        Canvas.SetLeft(_imageHost, canvasLeft);
        Canvas.SetTop(_imageHost, canvasTop);
    }

    private void CenterContent()
    {
        var viewportWidth = _scrollViewer.ActualWidth;
        var viewportHeight = _scrollViewer.ActualHeight;
        var contentWidth = _imageWidth * _zoomFactor;
        var contentHeight = _imageHeight * _zoomFactor;

        var left = Math.Max(0, (viewportWidth - contentWidth) / 2);
        var top = Math.Max(0, (viewportHeight - contentHeight) / 2);
        PositionContent(left, top);
    }

    private void UpdateMinZoomFactor()
    {
        var minZoom = GetMinZoomFactor();
        if (_zoomFactor < minZoom)
            ZoomToCenter(minZoom);
    }

    private double GetCanvasLeft()
    {
        var v = Canvas.GetLeft(_imageHost);
        return double.IsNaN(v) ? 0 : v;
    }

    private double GetCanvasTop()
    {
        var v = Canvas.GetTop(_imageHost);
        return double.IsNaN(v) ? 0 : v;
    }
}
