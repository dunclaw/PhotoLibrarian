// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using PhotoLibrarian.Core.Models;
using System.Numerics;

namespace PhotoLibrarian.Services;

internal static class ImageEditRenderPass
{
    internal static void DrawRawPreview(CanvasDrawingSession session, CanvasBitmap sdrSource, Matrix3x2 transform)
    {
        session.Transform = transform;
        session.DrawImage(sdrSource);
    }

    internal static void Draw(
        CanvasDrawingSession session,
        CanvasBitmap source,
        EditParameters parameters,
        Vector2 sourceSize,
        Matrix3x2 transform)
    {
        var effect = EditEffectGraph.Build(source, sourceSize, parameters);
        try
        {
            session.Transform = transform;
            session.DrawImage(effect);
        }
        finally
        {
            if (!ReferenceEquals(effect, source)) (effect as IDisposable)?.Dispose();
        }
    }
}
