// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;

namespace PhotoLibrarian.Services;

/// <summary>Editor-only SDR diagnostics. Never part of the persisted edit graph.</summary>
internal static class EditorClippingEffect
{
    private static readonly Lazy<byte[]> Shader = new(() =>
    {
        using var stream = typeof(EditorClippingEffect).Assembly.GetManifestResourceStream(
            "PhotoLibrarian.Shaders.EditorClipping.cso")
            ?? throw new InvalidOperationException("The editor clipping shader is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    });

    internal static ICanvasImage Create(ICanvasImage source, bool shadows, bool highlights)
    {
        if (!shadows && !highlights) return source;
        var effect = new PixelShaderEffect(Shader.Value)
        {
            Source1 = source,
            Source1Mapping = SamplerCoordinateMapping.OneToOne
        };
        effect.Properties["ShowShadows"] = shadows ? 1f : 0f;
        effect.Properties["ShowHighlights"] = highlights ? 1f : 0f;
        return effect;
    }
}
