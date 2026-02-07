namespace PhotoLibrarian.Core.Models;

/// <summary>
/// Defines all image adjustment parameters. Serializable to/from XMP metadata.
/// All values use normalized ranges centered on 0 (no change) unless noted.
/// </summary>
public sealed class EditParameters
{
    // Tone adjustments (-1.0 to 1.0, 0 = no change)
    public double Brightness { get; set; }
    public double Contrast { get; set; }
    public double Exposure { get; set; }
    public double Highlights { get; set; }
    public double Shadows { get; set; }

    // Color adjustments (-1.0 to 1.0, 0 = no change)
    public double Saturation { get; set; }
    public double Temperature { get; set; }
    public double Tint { get; set; }

    // Detail
    public double Clarity { get; set; }
    public double Sharpness { get; set; }

    // Levels (0.0 to 1.0)
    public double BlackPoint { get; set; }
    public double WhitePoint { get; set; } = 1.0;
    public double Midtones { get; set; } = 0.5;

    // Crop & Rotation
    public double RotationAngle { get; set; }
    public CropRect? Crop { get; set; }

    public bool HasAdjustments =>
        Brightness != 0 || Contrast != 0 || Exposure != 0 ||
        Highlights != 0 || Shadows != 0 || Saturation != 0 ||
        Temperature != 0 || Tint != 0 || Clarity != 0 || Sharpness != 0 ||
        BlackPoint != 0 || WhitePoint != 1.0 || Midtones != 0.5 ||
        RotationAngle != 0 || Crop is not null;

    public EditParameters Clone() => new()
    {
        Brightness = Brightness,
        Contrast = Contrast,
        Exposure = Exposure,
        Highlights = Highlights,
        Shadows = Shadows,
        Saturation = Saturation,
        Temperature = Temperature,
        Tint = Tint,
        Clarity = Clarity,
        Sharpness = Sharpness,
        BlackPoint = BlackPoint,
        WhitePoint = WhitePoint,
        Midtones = Midtones,
        RotationAngle = RotationAngle,
        Crop = Crop?.Clone()
    };

    public void Reset()
    {
        Brightness = Contrast = Exposure = 0;
        Highlights = Shadows = 0;
        Saturation = Temperature = Tint = 0;
        Clarity = Sharpness = 0;
        BlackPoint = 0;
        WhitePoint = 1.0;
        Midtones = 0.5;
        RotationAngle = 0;
        Crop = null;
    }

    // XMP namespace for PhotoLibrarian edit parameters
    public const string XmpNamespace = "http://ns.photolibrarian.app/edit/1.0/";
    public const string XmpPrefix = "pledit";
}

/// <summary>
/// Normalized crop rectangle (0.0 to 1.0 relative to image dimensions).
/// </summary>
public sealed class CropRect
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; } = 1.0;
    public double Height { get; set; } = 1.0;

    public CropRect Clone() => new()
    {
        Left = Left, Top = Top, Width = Width, Height = Height
    };
}
