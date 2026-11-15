// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using PhotoLibrarian.Core.Models;
using System.Numerics;
using Windows.Foundation;

namespace PhotoLibrarian.Services;

internal static class EditorComparisonRenderer
{
    internal static Vector2 GetViewportSize(Vector2 canvasSize, EditorComparisonMode mode) =>
        mode == EditorComparisonMode.SideBySide
            ? new Vector2(canvasSize.X / 2, canvasSize.Y) : canvasSize;

    internal static void Draw(CanvasDrawingSession session, CanvasBitmap before, CanvasBitmap after,
        EditParameters parameters, EditorComparisonMode mode, EditorViewport viewport,
        Vector2 canvasSize, float splitFraction, bool showShadows = false, bool showHighlights = false)
    {
        var afterSize = new Vector2((float)after.Size.Width, (float)after.Size.Height);
        var beforeSize = new Vector2((float)before.Size.Width, (float)before.Size.Height);
        var (offset, width, height) = EditEffectGraph.ComputeOutputExtent(
            afterSize.X, afterSize.Y, parameters.RotationAngle);
        var outputSize = new Vector2(width, height);
        var contentSize = mode switch
        {
            EditorComparisonMode.After => outputSize,
            EditorComparisonMode.Before => beforeSize,
            _ => Vector2.Max(beforeSize, outputSize)
        };
        viewport.SetCanvasSize(GetViewportSize(canvasSize, mode));
        viewport.SetContentSize(contentSize, resetView: false);
        var origin = viewport.ContentOrigin;
        var scale = viewport.Scale;
        var beforeTransform = Matrix3x2.CreateScale(scale) *
            Matrix3x2.CreateTranslation(origin + (contentSize - beforeSize) * scale / 2);
        var afterTransform = Matrix3x2.CreateTranslation(offset) *
            Matrix3x2.CreateScale(scale) *
            Matrix3x2.CreateTranslation(origin + (contentSize - outputSize) * scale / 2);

        var boundary = mode == EditorComparisonMode.SideBySide
            ? canvasSize.X / 2 : canvasSize.X * Math.Clamp(splitFraction, 0, 1);
        var beforeClip = new Rect(0, 0,
            mode == EditorComparisonMode.Before ? canvasSize.X : boundary, canvasSize.Y);
        var afterClip = new Rect(boundary, 0, canvasSize.X - boundary, canvasSize.Y);

        try
        {
            if (mode != EditorComparisonMode.After)
            {
                session.Transform = Matrix3x2.Identity;
                using var layer = session.CreateLayer(1, beforeClip);
                ImageEditRenderPass.DrawRawPreview(session, before, beforeTransform);
            }
            if (mode != EditorComparisonMode.Before)
            {
                session.Transform = Matrix3x2.Identity;
                using var layer = session.CreateLayer(1,
                    mode == EditorComparisonMode.After ? new Rect(0, 0, canvasSize.X, canvasSize.Y) : afterClip);
                if (mode == EditorComparisonMode.SideBySide)
                    afterTransform *= Matrix3x2.CreateTranslation(canvasSize.X / 2, 0);
                ImageEditRenderPass.Draw(session, after, parameters, afterSize, afterTransform,
                    showShadows, showHighlights);
            }
        }
        finally { session.Transform = Matrix3x2.Identity; }
    }
}
