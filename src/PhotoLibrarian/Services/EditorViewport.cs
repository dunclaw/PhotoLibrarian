using System.Numerics;

namespace PhotoLibrarian.Services;

/// <summary>
/// Zoom and pan state for the editor's Win2D preview. All values are in DIPs.
/// The viewport is either in "fit" mode (the image is scaled to fit and follows resizes)
/// or at an explicit scale with a pan offset of the image centre from the canvas centre.
/// </summary>
public sealed class EditorViewport
{
    public const float MaxScale = 8f;
    public const float ZoomStep = 1.25f;

    private float _scale = 1f;
    private Vector2 _pan;

    public Vector2 CanvasSize { get; private set; }
    public Vector2 ContentSize { get; private set; }
    public bool IsFit { get; private set; } = true;

    public float FitScale =>
        ContentSize.X <= 0 || ContentSize.Y <= 0 || CanvasSize.X <= 0 || CanvasSize.Y <= 0
            ? 1f
            : MathF.Min(CanvasSize.X / ContentSize.X, CanvasSize.Y / ContentSize.Y);

    public float Scale => IsFit ? FitScale : _scale;

    /// <summary>Scale at which one image pixel maps to one physical screen pixel.</summary>
    public float ActualSizeScale { get; set; } = 1f;

    /// <summary>Smallest allowed scale: fit, or actual size when the image is smaller than the canvas.</summary>
    public float MinScale => MathF.Min(FitScale, ActualSizeScale);

    /// <summary>Top-left of the scaled content in canvas coordinates.</summary>
    public Vector2 ContentOrigin
    {
        get
        {
            var pan = IsFit ? Vector2.Zero : _pan;
            return (CanvasSize - ContentSize * Scale) / 2f + pan;
        }
    }

    public void SetCanvasSize(Vector2 size)
    {
        CanvasSize = size;
        ClampPan();
    }

    /// <summary>Sets the content size. A new image always starts in fit mode.</summary>
    public void SetContentSize(Vector2 size, bool resetView)
    {
        ContentSize = size;
        if (resetView) Fit();
        else ClampPan();
    }

    public void Fit()
    {
        IsFit = true;
        _pan = Vector2.Zero;
    }

    public void ZoomIn(Vector2? anchor = null) => ZoomTo(Scale * ZoomStep, anchor);

    public void ZoomOut(Vector2? anchor = null) => ZoomTo(Scale / ZoomStep, anchor);

    /// <summary>
    /// Zooms to <paramref name="scale"/> keeping the content point under
    /// <paramref name="anchor"/> (default: canvas centre) stationary.
    /// </summary>
    public void ZoomTo(float scale, Vector2? anchor = null)
    {
        if (ContentSize.X <= 0 || ContentSize.Y <= 0) return;

        var target = Math.Clamp(scale, MinScale, MaxScale);
        var point = anchor ?? CanvasSize / 2f;
        var contentPoint = (point - ContentOrigin) / Scale;

        if (MathF.Abs(target - FitScale) < 0.0001f)
        {
            Fit();
            return;
        }

        _scale = target;
        IsFit = false;
        var newOrigin = point - contentPoint * target;
        _pan = newOrigin - (CanvasSize - ContentSize * target) / 2f;
        ClampPan();
    }

    public void PanBy(Vector2 delta)
    {
        if (IsFit) return;
        _pan += delta;
        ClampPan();
    }

    private void ClampPan()
    {
        if (IsFit) return;
        var scaled = ContentSize * _scale;
        var limitX = MathF.Max(0, (scaled.X - CanvasSize.X) / 2f);
        var limitY = MathF.Max(0, (scaled.Y - CanvasSize.Y) / 2f);
        _pan = new Vector2(
            Math.Clamp(_pan.X, -limitX, limitX),
            Math.Clamp(_pan.Y, -limitY, limitY));
    }
}
