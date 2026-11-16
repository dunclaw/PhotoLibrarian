// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using PhotoLibrarian.Core.Models;
using System.Numerics;

namespace PhotoLibrarian.Services;

internal static class ImageEditRenderPass
{
    internal static void DrawRawPreview(CanvasDrawingSession session, CanvasBitmap sdrSource, Matrix3x2 transform,
        bool showShadows = false, bool showHighlights = false)
    {
        DrawDiagnostic(session, sdrSource, transform, showShadows, showHighlights);
    }

    internal static void Draw(
        CanvasDrawingSession session,
        CanvasBitmap source,
        EditParameters parameters,
        Vector2 sourceSize,
        Matrix3x2 transform,
        bool showShadows = false, bool showHighlights = false)
    {
        var effect = EditEffectGraph.Build(source, sourceSize, parameters);
        try
        {
            DrawDiagnostic(session, effect, transform, showShadows, showHighlights);
        }
        finally
        {
            if (!ReferenceEquals(effect, source)) (effect as IDisposable)?.Dispose();
        }
    }

    private static void DrawDiagnostic(CanvasDrawingSession session, ICanvasImage image, Matrix3x2 transform,
        bool shadows, bool highlights)
    {
        var diagnostic = EditorClippingEffect.Create(image, shadows, highlights);
        try
        {
            session.Transform = transform;
            session.DrawImage(diagnostic);
        }
        finally
        {
            if (!ReferenceEquals(diagnostic, image)) (diagnostic as IDisposable)?.Dispose();
        }
    }
}
