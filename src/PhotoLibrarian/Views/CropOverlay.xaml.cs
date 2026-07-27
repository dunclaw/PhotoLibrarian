using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using System;
using Windows.Foundation;
using Windows.Graphics.Imaging;

namespace PhotoLibrarian.Views;

public enum CropAspectRatio
{
    Original,
    Free,
    Square,        // 1:1
    Landscape4x3,  // 4:3
    Landscape3x2,  // 3:2 (35mm)
    Landscape16x9, // 16:9
}

public sealed partial class CropOverlay : UserControl
{
    private const double HandleHitSize = 28;
    private const double MinCropSize = 32;

    // Fraction of the frame the crop rect covers when the tool opens.
    private const double DefaultCropFraction = 0.75;

    // How far the pointer must travel on the dimmed area before it counts as "draw a new crop
    // rect" rather than a click. Without this, a stray click — notably the one that reactivates
    // the window after alt-tabbing — collapses the crop to the minimum size at the click point.
    private const double NewDragThreshold = 8;

    // Image pixel dimensions (oriented — i.e. what the user sees)
    private uint _imagePixelWidth;
    private uint _imagePixelHeight;

    private double DisplayedWidth => LayoutRoot.ActualWidth;
    private double DisplayedHeight => LayoutRoot.ActualHeight;

    private bool HasSize => DisplayedWidth >= MinCropSize && DisplayedHeight >= MinCropSize;

    // Crop rect in DISPLAY coordinates (DIPs within LayoutRoot)
    private Rect _crop;

    // True until the crop rect has been established against a real (non-zero) layout size.
    private bool _needsReset = true;

    // Size the crop rect was last laid out against, so a resize can rescale it proportionally.
    private Size _lastSize = new(0, 0);

    private CropAspectRatio _aspect = CropAspectRatio.Free;
    public CropAspectRatio AspectRatio
    {
        get => _aspect;
        set
        {
            if (_aspect == value) return;
            _aspect = value;
            if (!_needsReset && HasSize)
            {
                ApplyAspectConstraint();
                RelayoutOverlay();
            }
        }
    }

    // Drag state
    private bool _dragging;
    private string _dragMode = "";          // handle Tag ("NW","N",...), "Move", or "New"
    private bool _dragArmed;                // false while a "New" drag is still under the threshold
    private Point _dragStart;
    private Rect _dragStartCrop;
    private uint _activePointer;

    public CropOverlay()
    {
        this.InitializeComponent();
        this.SizeChanged += OnLayoutRootSizeChanged;
    }

    /// <summary>True once the crop rect has been established against a real layout size.</summary>
    public bool IsCropEstablished => !_needsReset;

    /// <summary>
    /// Initialise the overlay with the image's pixel dimensions (oriented) and reset the crop
    /// rect. Safe to call before the control has been laid out — the reset is deferred until a
    /// real size is available.
    /// </summary>
    public void InitializeForImage(uint imagePixelWidth, uint imagePixelHeight)
    {
        _imagePixelWidth = imagePixelWidth;
        _imagePixelHeight = imagePixelHeight;
        _needsReset = true;
        _dragging = false;
        _dragMode = "";
        _dragArmed = false;
        _lastSize = new Size(0, 0);

        if (HasSize) ResetCrop();
    }

    /// <summary>
    /// Returns the current crop rect translated into IMAGE pixel coordinates (oriented), or
    /// null when the crop rect isn't established or would be degenerate.
    /// </summary>
    public BitmapBounds? GetCropBoundsInImagePixels()
    {
        if (_needsReset || !HasSize) return null;
        if (_imagePixelWidth == 0 || _imagePixelHeight == 0) return null;

        var sx = _imagePixelWidth / DisplayedWidth;
        var sy = _imagePixelHeight / DisplayedHeight;

        var x = Math.Clamp(_crop.X * sx, 0, _imagePixelWidth - 1);
        var y = Math.Clamp(_crop.Y * sy, 0, _imagePixelHeight - 1);
        var w = Math.Clamp(_crop.Width * sx, 1, _imagePixelWidth - x);
        var h = Math.Clamp(_crop.Height * sy, 1, _imagePixelHeight - y);

        return new BitmapBounds
        {
            X = (uint)Math.Round(x),
            Y = (uint)Math.Round(y),
            Width = (uint)Math.Max(1, Math.Round(w)),
            Height = (uint)Math.Max(1, Math.Round(h)),
        };
    }

    // ----------------------------------------------------------------------
    //  Safe numeric helpers
    //
    //  Math.Clamp throws when min > max, and FrameworkElement.Width/Height and
    //  Rect both throw E_INVALIDARG on negative values. Degenerate layout sizes
    //  are routine here (the overlay is collapsed/re-shown and lives inside a
    //  zoomed ScrollViewer), so every value that reaches XAML goes through these.
    // ----------------------------------------------------------------------

    private static double SafeClamp(double value, double min, double max)
    {
        if (double.IsNaN(value)) return min;
        if (max < min) return min;
        return Math.Clamp(value, min, max);
    }

    private static double NonNegative(double value) =>
        double.IsNaN(value) || value < 0 ? 0 : value;

    private static Rect MakeRect(double left, double top, double right, double bottom)
    {
        if (right < left) (left, right) = (right, left);
        if (bottom < top) (top, bottom) = (bottom, top);
        return new Rect(left, top, NonNegative(right - left), NonNegative(bottom - top));
    }

    // ----------------------------------------------------------------------
    //  Sizing / reset
    // ----------------------------------------------------------------------

    private void OnLayoutRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!HasSize) return;

        if (_needsReset)
        {
            ResetCrop();
            return;
        }

        // Preserve the crop proportionally across zoom / window resize.
        if (_lastSize.Width > 0 && _lastSize.Height > 0)
        {
            var fx = DisplayedWidth / _lastSize.Width;
            var fy = DisplayedHeight / _lastSize.Height;
            _crop = new Rect(
                _crop.X * fx,
                _crop.Y * fy,
                NonNegative(_crop.Width * fx),
                NonNegative(_crop.Height * fy));
        }

        RelayoutOverlay();
    }

    private void ResetCrop()
    {
        // Start at a centred fraction of the frame rather than the whole image: the user opened
        // the crop tool to crop, so the handles should already be grabbable and the effect of
        // dragging them obvious.
        var w = DisplayedWidth * DefaultCropFraction;
        var h = DisplayedHeight * DefaultCropFraction;
        _crop = new Rect(
            (DisplayedWidth - w) / 2,
            (DisplayedHeight - h) / 2,
            NonNegative(w),
            NonNegative(h));
        _needsReset = false;
        ApplyAspectConstraint();
        RelayoutOverlay();
    }

    // ----------------------------------------------------------------------
    //  Aspect-ratio handling
    // ----------------------------------------------------------------------

    private double? AspectValue()
    {
        return _aspect switch
        {
            CropAspectRatio.Square => 1.0,
            CropAspectRatio.Landscape4x3 => 4.0 / 3.0,
            CropAspectRatio.Landscape3x2 => 3.0 / 2.0,
            CropAspectRatio.Landscape16x9 => 16.0 / 9.0,
            CropAspectRatio.Original => _imagePixelHeight > 0 ? _imagePixelWidth / (double)_imagePixelHeight : (double?)null,
            _ => null,
        };
    }

    /// <summary>
    /// The ratio a resize drag must respect: the chosen proportion, or — when Ctrl is held in
    /// Free mode — the ratio the crop rect had when the drag began.
    /// </summary>
    private double? ActiveAspect(PointerRoutedEventArgs e)
    {
        if (AspectValue() is double fixedAspect) return fixedAspect;

        if (!e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control)) return null;
        if (_dragStartCrop.Width <= 0 || _dragStartCrop.Height <= 0) return null;

        return _dragStartCrop.Width / _dragStartCrop.Height;
    }

    private void ApplyAspectConstraint()
    {
        if (AspectValue() is not double a || a <= 0) return;
        if (!HasSize) return;

        // Largest rect of this aspect that fits inside the current crop rect, keeping its
        // centre — switching proportion should refine the user's framing, not discard it.
        var centreX = _crop.X + _crop.Width / 2;
        var centreY = _crop.Y + _crop.Height / 2;

        double w = _crop.Width;
        double h = w / a;
        if (h > _crop.Height)
        {
            h = _crop.Height;
            w = h * a;
        }

        var x = SafeClamp(centreX - w / 2, 0, DisplayedWidth - w);
        var y = SafeClamp(centreY - h / 2, 0, DisplayedHeight - h);
        _crop = new Rect(x, y, NonNegative(w), NonNegative(h));
    }

    // ----------------------------------------------------------------------
    //  Layout
    // ----------------------------------------------------------------------

    private void RelayoutOverlay()
    {
        if (!HasSize) return;

        var w = DisplayedWidth;
        var h = DisplayedHeight;
        var minW = Math.Min(MinCropSize, w);
        var minH = Math.Min(MinCropSize, h);

        // Clamp crop into the display rect. Size is clamped before position so a crop wider
        // than the display shrinks rather than pushing the origin negative.
        var cw = SafeClamp(_crop.Width, minW, w);
        var ch = SafeClamp(_crop.Height, minH, h);
        var cx = SafeClamp(_crop.X, 0, w - cw);
        var cy = SafeClamp(_crop.Y, 0, h - ch);
        _crop = new Rect(cx, cy, NonNegative(cw), NonNegative(ch));

        // Dim masks
        var bottomGap = NonNegative(h - (cy + ch));
        MaskTop.Height = NonNegative(cy);
        MaskBottom.Height = bottomGap;
        MaskLeft.Width = NonNegative(cx);
        MaskLeft.Margin = new Thickness(0, cy, 0, bottomGap);
        MaskRight.Width = NonNegative(w - (cx + cw));
        MaskRight.Margin = new Thickness(0, cy, 0, bottomGap);

        // Crop border
        Canvas.SetLeft(CropBorder, cx);
        Canvas.SetTop(CropBorder, cy);
        CropBorder.Width = NonNegative(cw);
        CropBorder.Height = NonNegative(ch);

        // Rule-of-thirds lines
        var v1x = cx + cw / 3.0;
        var v2x = cx + 2 * cw / 3.0;
        var h1y = cy + ch / 3.0;
        var h2y = cy + 2 * ch / 3.0;
        SetLine(GridV1, v1x, cy, v1x, cy + ch);
        SetLine(GridV2, v2x, cy, v2x, cy + ch);
        SetLine(GridH1, cx, h1y, cx + cw, h1y);
        SetLine(GridH2, cx, h2y, cx + cw, h2y);

        // Handle positions (centred on the crop rect's corners / edge mid-points)
        PlaceHandle(HandleNW, cx, cy);
        PlaceHandle(HandleN, cx + cw / 2, cy);
        PlaceHandle(HandleNE, cx + cw, cy);
        PlaceHandle(HandleE, cx + cw, cy + ch / 2);
        PlaceHandle(HandleSE, cx + cw, cy + ch);
        PlaceHandle(HandleS, cx + cw / 2, cy + ch);
        PlaceHandle(HandleSW, cx, cy + ch);
        PlaceHandle(HandleW, cx, cy + ch / 2);

        _lastSize = new Size(w, h);
    }

    private static void SetLine(Line l, double x1, double y1, double x2, double y2)
    {
        l.X1 = x1; l.Y1 = y1; l.X2 = x2; l.Y2 = y2;
    }

    private static void PlaceHandle(FrameworkElement e, double cx, double cy)
    {
        Canvas.SetLeft(e, cx - HandleHitSize / 2);
        Canvas.SetTop(e, cy - HandleHitSize / 2);
    }

    // ----------------------------------------------------------------------
    //  Drag helpers
    // ----------------------------------------------------------------------

    private void BeginDrag(UIElement capture, PointerRoutedEventArgs e, string mode)
    {
        _dragging = true;
        _dragMode = mode;
        // Only a new-rect drag needs to prove itself; grabbing a handle or the rect interior
        // is already an explicit gesture.
        _dragArmed = mode != "New";
        _dragStart = e.GetCurrentPoint(LayoutRoot).Position;
        _dragStartCrop = _crop;
        _activePointer = e.Pointer.PointerId;
        capture.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void EndDrag(object sender, PointerRoutedEventArgs e)
    {
        // Clear the drag state before releasing capture: the release raises PointerCaptureLost
        // synchronously, and that handler must not mistake a completed drag for a lost one.
        _dragging = false;
        _dragMode = "";
        _dragArmed = false;
        if (sender is UIElement el) el.ReleasePointerCapture(e.Pointer);
        SetHoverCursor(sender);
        e.Handled = true;
    }

    /// <summary>
    /// Abandons an in-progress drag and restores the crop rect it started from. Used when the
    /// gesture is lost rather than completed (capture stolen, pointer cancelled), so an
    /// interrupted drag never leaves a half-formed crop rect behind.
    /// </summary>
    private void CancelDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        _dragMode = "";
        _dragArmed = false;
        ProtectedCursor = null;
        if (!_needsReset && HasSize)
        {
            _crop = _dragStartCrop;
            RelayoutOverlay();
        }
    }

    private bool IsActiveDrag(PointerRoutedEventArgs e, string expectedMode) =>
        _dragging && _dragMode == expectedMode && e.Pointer.PointerId == _activePointer;

    // ----------------------------------------------------------------------
    //  Hover cursors
    //
    //  ProtectedCursor is only settable on this control, so the hover cursor for a child
    //  handle is applied here and inherited by the child under the pointer.
    // ----------------------------------------------------------------------

    private void SetHoverCursor(object? hovered)
    {
        if (_dragging) return;

        var shape = hovered switch
        {
            FrameworkElement fe when fe.Tag is string tag && tag.Length > 0 => CursorShapeForHandle(tag),
            Border => InputSystemCursorShape.SizeAll,
            _ => (InputSystemCursorShape?)null,
        };

        ProtectedCursor = shape is null ? null : InputSystemCursor.Create(shape.Value);
    }

    private void OnHandlePointerEntered(object sender, PointerRoutedEventArgs e) => SetHoverCursor(sender);

    private void OnCropBorderPointerEntered(object sender, PointerRoutedEventArgs e) => SetHoverCursor(sender);

    private void OnPointerExitedResetCursor(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) ProtectedCursor = null;
    }

    // ----------------------------------------------------------------------
    //  Draw a new crop rect by dragging on the dimmed area
    // ----------------------------------------------------------------------

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!HasSize || _needsReset) return;
        BeginDrag(LayoutRoot, e, "New");
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Cross);
    }

    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!IsActiveDrag(e, "New"))
        {
            // Crosshair over the dimmed area advertises "drag here to draw a new crop". The
            // source check keeps it from overriding the handle/move cursors, since pointer
            // events from those children bubble up to the root as well.
            if (!_dragging && ReferenceEquals(e.OriginalSource, LayoutRoot))
                ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Cross);
            return;
        }

        var p = e.GetCurrentPoint(LayoutRoot).Position;

        if (!_dragArmed)
        {
            var travelled = Math.Abs(p.X - _dragStart.X) + Math.Abs(p.Y - _dragStart.Y);
            if (travelled < NewDragThreshold) return;
            _dragArmed = true;
        }

        var left = SafeClamp(Math.Min(_dragStart.X, p.X), 0, DisplayedWidth);
        var top = SafeClamp(Math.Min(_dragStart.Y, p.Y), 0, DisplayedHeight);
        var right = SafeClamp(Math.Max(_dragStart.X, p.X), 0, DisplayedWidth);
        var bottom = SafeClamp(Math.Max(_dragStart.Y, p.Y), 0, DisplayedHeight);

        if (AspectValue() is double a && a > 0)
        {
            var width = right - left;
            var height = width / a;
            if (top + height > DisplayedHeight)
            {
                height = DisplayedHeight - top;
                width = height * a;
                right = left + width;
            }
            bottom = top + height;
        }

        _crop = MakeRect(left, top, right, bottom);
        RelayoutOverlay();
        e.Handled = true;
    }

    private void OnRootPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        // A click (or a drag too small to be deliberate) leaves the crop rect alone. The rect
        // itself can't be measured for this because RelayoutOverlay has already clamped it up
        // to the minimum size, so the armed flag is what decides.
        if (_dragging && _dragMode == "New" && !_dragArmed)
        {
            _crop = _dragStartCrop;
            RelayoutOverlay();
        }
        EndDrag(sender, e);
    }

    private void OnRootPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        CancelDrag();
        e.Handled = true;
    }

    private void OnRootPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_dragMode == "New") CancelDrag();
    }

    // ----------------------------------------------------------------------
    //  Interior pan (move whole crop rect)
    // ----------------------------------------------------------------------

    private void OnCropBorderPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement el || !HasSize || _needsReset) return;
        BeginDrag(el, e, "Move");
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    }

    private void OnCropBorderPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!IsActiveDrag(e, "Move"))
        {
            SetHoverCursor(sender);
            return;
        }

        var p = e.GetCurrentPoint(LayoutRoot).Position;
        var dx = p.X - _dragStart.X;
        var dy = p.Y - _dragStart.Y;
        _crop = new Rect(
            SafeClamp(_dragStartCrop.X + dx, 0, DisplayedWidth - _dragStartCrop.Width),
            SafeClamp(_dragStartCrop.Y + dy, 0, DisplayedHeight - _dragStartCrop.Height),
            NonNegative(_dragStartCrop.Width),
            NonNegative(_dragStartCrop.Height));
        RelayoutOverlay();
        e.Handled = true;
    }

    private void OnCropBorderPointerReleased(object sender, PointerRoutedEventArgs e) => EndDrag(sender, e);

    private void OnCropBorderPointerCaptureLost(object sender, PointerRoutedEventArgs e) => StopDragKeepingRect();

    /// <summary>Ends a move/resize gesture that was interrupted, leaving the rect where it is.</summary>
    private void StopDragKeepingRect()
    {
        if (!_dragging) return;
        _dragging = false;
        _dragMode = "";
        _dragArmed = false;
        ProtectedCursor = null;
    }

    // ----------------------------------------------------------------------
    //  Handle drag (resize)
    // ----------------------------------------------------------------------

    private void OnHandlePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement h || !HasSize || _needsReset) return;
        var tag = h.Tag as string;
        if (string.IsNullOrEmpty(tag)) return;

        BeginDrag(h, e, tag);
        ProtectedCursor = InputSystemCursor.Create(CursorShapeForHandle(tag));
    }

    private void OnHandlePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || e.Pointer.PointerId != _activePointer)
        {
            SetHoverCursor(sender);
            return;
        }
        if (_dragMode is "" or "Move" or "New") return;
        if (!HasSize) return;

        var p = e.GetCurrentPoint(LayoutRoot).Position;
        var dx = p.X - _dragStart.X;
        var dy = p.Y - _dragStart.Y;

        double left = _dragStartCrop.X;
        double top = _dragStartCrop.Y;
        double right = _dragStartCrop.X + _dragStartCrop.Width;
        double bottom = _dragStartCrop.Y + _dragStartCrop.Height;

        var minW = Math.Min(MinCropSize, DisplayedWidth);
        var minH = Math.Min(MinCropSize, DisplayedHeight);

        if (_dragMode.Contains('W')) left = SafeClamp(left + dx, 0, right - minW);
        if (_dragMode.Contains('E')) right = SafeClamp(right + dx, left + minW, DisplayedWidth);
        if (_dragMode.Contains('N')) top = SafeClamp(top + dy, 0, bottom - minH);
        if (_dragMode.Contains('S')) bottom = SafeClamp(bottom + dy, top + minH, DisplayedHeight);

        if (ActiveAspect(e) is double a && a > 0)
        {
            bool isCorner = _dragMode.Length == 2;
            bool horizontalDrive = isCorner
                ? Math.Abs(dx) >= Math.Abs(dy)
                : _dragMode is "E" or "W";

            if (horizontalDrive)
            {
                var height = (right - left) / a;
                if (_dragMode.Contains('N')) top = bottom - height;
                else if (_dragMode.Contains('S')) bottom = top + height;
                else
                {
                    var cy = _dragStartCrop.Y + _dragStartCrop.Height / 2;
                    top = cy - height / 2;
                    bottom = cy + height / 2;
                }
            }
            else
            {
                var width = (bottom - top) * a;
                if (_dragMode.Contains('W')) left = right - width;
                else if (_dragMode.Contains('E')) right = left + width;
                else
                {
                    var cx = _dragStartCrop.X + _dragStartCrop.Width / 2;
                    left = cx - width / 2;
                    right = cx + width / 2;
                }
            }

            // Aspect rebalancing can push an edge outside the image. Shift the rect back in,
            // then shrink it if it still doesn't fit (shifting alone can overflow the far edge).
            if (left < 0) { right -= left; left = 0; }
            if (top < 0) { bottom -= top; top = 0; }
            if (right > DisplayedWidth) { left -= right - DisplayedWidth; right = DisplayedWidth; }
            if (bottom > DisplayedHeight) { top -= bottom - DisplayedHeight; bottom = DisplayedHeight; }
            left = Math.Max(0, left);
            top = Math.Max(0, top);

            // Re-fit to the aspect after clamping so the ratio is never silently violated.
            var fitW = Math.Min(right - left, DisplayedWidth - left);
            var fitH = Math.Min(bottom - top, DisplayedHeight - top);
            if (fitW / a > fitH) fitW = fitH * a; else fitH = fitW / a;
            right = left + fitW;
            bottom = top + fitH;
        }

        _crop = MakeRect(left, top, right, bottom);
        RelayoutOverlay();
        e.Handled = true;
    }

    private void OnHandlePointerReleased(object sender, PointerRoutedEventArgs e) => EndDrag(sender, e);

    private void OnHandlePointerCaptureLost(object sender, PointerRoutedEventArgs e) => StopDragKeepingRect();

    private static InputSystemCursorShape CursorShapeForHandle(string tag) => tag switch
    {
        "NW" or "SE" => InputSystemCursorShape.SizeNorthwestSoutheast,
        "NE" or "SW" => InputSystemCursorShape.SizeNortheastSouthwest,
        "N" or "S" => InputSystemCursorShape.SizeNorthSouth,
        "E" or "W" => InputSystemCursorShape.SizeWestEast,
        _ => InputSystemCursorShape.Arrow,
    };
}
