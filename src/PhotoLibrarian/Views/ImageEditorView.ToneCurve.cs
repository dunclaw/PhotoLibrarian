// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Services;
using System.Numerics;

namespace PhotoLibrarian.Views;

public sealed partial class ImageEditorView
{
    private int _selectedCurvePoint;
    private uint? _curvePointer;
    private bool _curvePointMoved;

    private static List<ToneCurvePoint> CurveFor(ToneCurves curves, int channel) => channel switch
    {
        1 => curves.Red,
        2 => curves.Green,
        3 => curves.Blue,
        _ => curves.Rgb
    };

    private void OnCurveChannelChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedCurvePoint = 0;
        SyncCurveControls();
    }

    private void SyncCurveControls()
    {
        if (ViewModel is null || CurveCanvas is null) return;
        var points = CurveFor(ViewModel.GetCurrentParameters().ToneCurves, CurveChannel.SelectedIndex);
        _selectedCurvePoint = Math.Clamp(_selectedCurvePoint, 0, points.Count - 1);
        CurveCanvas.Invalidate();
    }

    private void MoveCurvePoint(double x, double y)
    {
        if (ViewModel is null) return;
        var curves = ViewModel.GetCurrentParameters().ToneCurves;
        var points = CurveFor(curves, CurveChannel.SelectedIndex);
        var i = _selectedCurvePoint;
        if (i < 0 || i >= points.Count) return;
        var minX = i == 0 ? 0 : Math.BitIncrement(points[i - 1].X);
        var maxX = i == points.Count - 1 ? 1 : Math.BitDecrement(points[i + 1].X);
        x = i == 0 ? 0 : i == points.Count - 1 ? 1 : Math.Clamp(x, minX, maxX);
        y = Math.Clamp(y, i == 0 ? 0 : points[i - 1].Y, i == points.Count - 1 ? 1 : points[i + 1].Y);
        points[i] = new(x, y);
        ViewModel.SetToneCurves(curves);
    }

    private void OnCurveDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (ViewModel is null || sender.Size.Width <= 16 || sender.Size.Height <= 16) return;
        var width = (float)sender.Size.Width - 16;
        var height = (float)sender.Size.Height - 16;
        var foreground = GetResourceColor("TextFillColorSecondaryBrush", Microsoft.UI.Colors.Gray);
        var accent = GetResourceColor("AccentFillColorDefaultBrush", Microsoft.UI.Colors.CornflowerBlue);
        if (GetPreviewAnalysis() is { } analysis)
        {
            var histogram = analysis.Bins;
            for (var i = 0; i < histogram.Length; i++)
            {
                var x = 8 + (i + 0.5f) * width / histogram.Length;
                args.DrawingSession.DrawLine(x, 8 + height, x, 8 + height * (1 - histogram[i]), foreground);
            }
            DrawClippingIndicators(args.DrawingSession, width, height, analysis, 8, 8);
        }
        var points = CurveFor(ViewModel.GetCurrentParameters().ToneCurves, CurveChannel.SelectedIndex);
        Vector2 Position(ToneCurvePoint point) => new(8 + (float)point.X * width, 8 + (float)(1 - point.Y) * height);
        var previous = Position(points[0]);
        var samples = Math.Max(1, (int)Math.Ceiling(width));
        for (var i = 1; i <= samples; i++)
        {
            var x = i / (double)samples;
            var current = Position(new(x, ToneCurves.Evaluate(points, x)));
            args.DrawingSession.DrawLine(previous, current, accent, 2);
            previous = current;
        }
        for (var i = 0; i < points.Count; i++)
            args.DrawingSession.FillCircle(Position(points[i]), i == _selectedCurvePoint ? 5 : 3, accent);
    }

    private void OnCurvePressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel is null || ViewModel.IsBusy || _curvePointer is not null ||
            CurveCanvas.ActualWidth <= 16 || CurveCanvas.ActualHeight <= 16 ||
            !e.GetCurrentPoint(CurveCanvas).Properties.IsLeftButtonPressed) return;
        CurveCanvas.Focus(FocusState.Pointer);
        var position = e.GetCurrentPoint(CurveCanvas).Position;
        var x = Math.Clamp((position.X - 8) / (CurveCanvas.ActualWidth - 16), 0, 1);
        var y = Math.Clamp(1 - (position.Y - 8) / (CurveCanvas.ActualHeight - 16), 0, 1);
        var curves = ViewModel.GetCurrentParameters().ToneCurves;
        var points = CurveFor(curves, CurveChannel.SelectedIndex);
        var closest = -1;
        var distance = 10d * 10d;
        for (var i = 0; i < points.Count; i++)
        {
            var dx = (points[i].X - x) * (CurveCanvas.ActualWidth - 16);
            var dy = (points[i].Y - y) * (CurveCanvas.ActualHeight - 16);
            if (dx * dx + dy * dy < distance) { closest = i; distance = dx * dx + dy * dy; }
        }
        ViewModel.BeginEdit("Tone curve");
        if (closest < 0)
        {
            var index = points.FindIndex(point => point.X >= x);
            if (index <= 0 || points[index].X == x) { ViewModel.EndEdit(); return; }
            y = Math.Clamp(y, points[index - 1].Y, points[index].Y);
            points.Insert(index, new(x, y));
            _selectedCurvePoint = index;
            ViewModel.SetToneCurves(curves);
        }
        else _selectedCurvePoint = closest;
        _curvePointMoved = false;
        _curvePointer = e.Pointer.PointerId;
        if (!CurveCanvas.CapturePointer(e.Pointer))
        {
            _curvePointer = null;
            ViewModel.EndEdit();
        }
        SyncCurveControls();
        e.Handled = true;
    }

    private void OnCurveMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_curvePointer != e.Pointer.PointerId) return;
        _curvePointMoved = true;
        var position = e.GetCurrentPoint(CurveCanvas).Position;
        MoveCurvePoint((position.X - 8) / (CurveCanvas.ActualWidth - 16),
            1 - (position.Y - 8) / (CurveCanvas.ActualHeight - 16));
        SyncCurveControls();
        e.Handled = true;
    }

    private void OnCurveReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_curvePointer != e.Pointer.PointerId) return;
        if (_curvePointMoved && ViewModel is not null)
        {
            var position = e.GetCurrentPoint(CurveCanvas).Position;
            var curves = ViewModel.GetCurrentParameters().ToneCurves;
            var points = CurveFor(curves, CurveChannel.SelectedIndex);
            if (TryMergeCurvePoint(points, _selectedCurvePoint,
                new((position.X - 8) / (CurveCanvas.ActualWidth - 16),
                    1 - (position.Y - 8) / (CurveCanvas.ActualHeight - 16)),
                CurveCanvas.ActualWidth - 16, CurveCanvas.ActualHeight - 16))
            {
                _selectedCurvePoint = Math.Min(_selectedCurvePoint, points.Count - 1);
                ViewModel.SetToneCurves(curves);
            }
        }
        _curvePointer = null;
        CurveCanvas.ReleasePointerCapture(e.Pointer);
        ViewModel?.EndEdit();
        e.Handled = true;
    }

    private void OnCurveCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_curvePointer != e.Pointer.PointerId) return;
        _curvePointer = null;
        CurveCanvas.ReleasePointerCapture(e.Pointer);
        ViewModel?.EndEdit();
    }

    private void OnCurveCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_curvePointer != e.Pointer.PointerId) return;
        _curvePointer = null;
        ViewModel?.EndEdit();
    }

    internal static bool TryMergeCurvePoint(List<ToneCurvePoint> points, int index,
        ToneCurvePoint position, double width, double height)
    {
        if (index <= 0 || index >= points.Count - 1 || width <= 0 || height <= 0) return false;
        for (var candidate = 0; candidate < points.Count; candidate++)
        {
            if (candidate == index) continue;
            var dx = (points[candidate].X - position.X) * width;
            var dy = (points[candidate].Y - position.Y) * height;
            if (dx * dx + dy * dy > 12 * 12) continue;
            points.RemoveAt(index);
            return true;
        }
        return false;
    }

    private void RemoveCurvePoint()
    {
        if (ViewModel is null) return;
        var curves = ViewModel.GetCurrentParameters().ToneCurves;
        var points = CurveFor(curves, CurveChannel.SelectedIndex);
        if (_selectedCurvePoint <= 0 || _selectedCurvePoint >= points.Count - 1) return;
        points.RemoveAt(_selectedCurvePoint);
        _selectedCurvePoint--;
        ViewModel.SetToneCurves(curves);
        SyncCurveControls();
    }

    private void AddCurvePoint()
    {
        if (ViewModel is null) return;
        var curves = ViewModel.GetCurrentParameters().ToneCurves;
        var points = CurveFor(curves, CurveChannel.SelectedIndex);
        var index = Math.Min(_selectedCurvePoint, points.Count - 2);
        var left = points[index];
        var right = points[index + 1];
        var x = (left.X + right.X) / 2;
        if (x <= left.X || x >= right.X)
        {
            SetStatus("There is no representable input value between these points.");
            return;
        }
        points.Insert(index + 1, new(x, (left.Y + right.Y) / 2));
        _selectedCurvePoint = index + 1;
        ViewModel.SetToneCurves(curves);
        SyncCurveControls();
    }

    private void OnCurveKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ViewModel is null || ViewModel.IsBusy || _curvePointer is not null) return;
        var points = CurveFor(ViewModel.GetCurrentParameters().ToneCurves, CurveChannel.SelectedIndex);
        var selected = points[Math.Clamp(_selectedCurvePoint, 0, points.Count - 1)];
        switch (e.Key)
        {
            case Windows.System.VirtualKey.PageUp:
                _selectedCurvePoint = Math.Max(0, _selectedCurvePoint - 1);
                break;
            case Windows.System.VirtualKey.PageDown:
                _selectedCurvePoint = Math.Min(points.Count - 1, _selectedCurvePoint + 1);
                break;
            case Windows.System.VirtualKey.Left: MoveCurvePoint(selected.X - .01, selected.Y); break;
            case Windows.System.VirtualKey.Right: MoveCurvePoint(selected.X + .01, selected.Y); break;
            case Windows.System.VirtualKey.Up: MoveCurvePoint(selected.X, selected.Y + .01); break;
            case Windows.System.VirtualKey.Down: MoveCurvePoint(selected.X, selected.Y - .01); break;
            case Windows.System.VirtualKey.Delete: RemoveCurvePoint(); break;
            case Windows.System.VirtualKey.Insert: AddCurvePoint(); break;
            default: return;
        }
        SyncCurveControls();
        e.Handled = true;
    }

    private void OnCurveReset(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var curves = ViewModel.GetCurrentParameters().ToneCurves;
        var points = CurveFor(curves, CurveChannel.SelectedIndex);
        points.Clear();
        points.AddRange(ToneCurves.Identity());
        _selectedCurvePoint = 0;
        ViewModel.SetToneCurves(curves);
        SyncCurveControls();
    }
}
