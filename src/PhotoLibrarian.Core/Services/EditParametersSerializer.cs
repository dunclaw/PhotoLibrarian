// Copyright (c) Duncan Lawler. All Rights Reserved.
using XmpCore;
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Core.Services;

/// <summary>
/// Serializes/deserializes EditParameters to/from XMP metadata,
/// enabling portable, non-destructive edit storage inside images.
/// </summary>
public static class EditParametersSerializer
{
    private const string Ns = EditParameters.XmpNamespace;
    private const string Pre = EditParameters.XmpPrefix;

    public static void WriteToXmp(IXmpMeta xmp, EditParameters p)
    {
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(Ns, Pre);

        SetDouble(xmp, "Brightness", p.Brightness);
        SetDouble(xmp, "Contrast", p.Contrast);
        SetDouble(xmp, "Exposure", p.Exposure);
        SetDouble(xmp, "RawExposureEv", p.RawExposureEv);
        SetDouble(xmp, "RawWhitePoint", p.RawWhitePoint);
        xmp.SetProperty(Ns, $"{Pre}:RawToneMapping", RawToneMappingService.RecipeVersion);
        SetDouble(xmp, "Highlights", p.Highlights);
        SetDouble(xmp, "Shadows", p.Shadows);
        SetDouble(xmp, "Saturation", p.Saturation);
        SetDouble(xmp, "Temperature", p.Temperature);
        SetDouble(xmp, "Tint", p.Tint);
        SetDouble(xmp, "Clarity", p.Clarity);
        SetDouble(xmp, "Sharpness", p.Sharpness);
        SetDouble(xmp, "NoiseReduction", p.NoiseReduction);
        xmp.SetProperty(Ns, $"{Pre}:SharpeningMode", p.SharpeningMode.ToString());
        SetDouble(xmp, "UnsharpMaskAmount", p.UnsharpMaskAmount);
        SetDouble(xmp, "UnsharpMaskRadius", p.UnsharpMaskRadius);
        SetDouble(xmp, "UnsharpMaskThreshold", p.UnsharpMaskThreshold);
        SetDouble(xmp, "BlackPoint", p.BlackPoint);
        SetDouble(xmp, "WhitePoint", p.WhitePoint);
        SetDouble(xmp, "Midtones", p.Midtones);
        SetDouble(xmp, "RotationAngle", p.RotationAngle);

        if (p.Crop is not null)
        {
            SetDouble(xmp, "CropLeft", p.Crop.Left);
            SetDouble(xmp, "CropTop", p.Crop.Top);
            SetDouble(xmp, "CropWidth", p.Crop.Width);
            SetDouble(xmp, "CropHeight", p.Crop.Height);
        }
        else
        {
            DeleteProp(xmp, "CropLeft");
            DeleteProp(xmp, "CropTop");
            DeleteProp(xmp, "CropWidth");
            DeleteProp(xmp, "CropHeight");
        }
    }

    /// <summary>
    /// Removes every PhotoLibrarian edit property from the XMP packet. Used once the parameters
    /// have been baked into the pixels, so re-opening the image doesn't apply them a second time.
    /// Properties owned by other applications are left untouched.
    /// </summary>
    public static void ClearFromXmp(IXmpMeta xmp, bool includeRawParameters = true)
    {
        foreach (var prop in new[]
        {
            "Brightness", "Contrast", "Exposure", "Highlights", "Shadows",
            "Saturation", "Temperature", "Tint", "Clarity", "Sharpness", "NoiseReduction",
            "SharpeningMode", "UnsharpMaskAmount", "UnsharpMaskRadius", "UnsharpMaskThreshold",
            "BlackPoint", "WhitePoint", "Midtones", "RotationAngle",
            "CropLeft", "CropTop", "CropWidth", "CropHeight"
        })
        {
            DeleteProp(xmp, prop);
        }
        if (includeRawParameters)
        {
            DeleteProp(xmp, "RawExposureEv");
            DeleteProp(xmp, "RawWhitePoint");
            DeleteProp(xmp, "RawToneMapping");
        }
    }

    public static EditParameters ReadFromXmp(IXmpMeta xmp)
    {
        var p = new EditParameters
        {
            Brightness = GetDouble(xmp, "Brightness"),
            Contrast = GetDouble(xmp, "Contrast"),
            Exposure = GetDouble(xmp, "Exposure"),
            RawExposureEv = GetRawDouble(xmp, "RawExposureEv", 0),
            RawWhitePoint = GetRawDouble(xmp, "RawWhitePoint", 4.0),
            Highlights = GetDouble(xmp, "Highlights"),
            Shadows = GetDouble(xmp, "Shadows"),
            Saturation = GetDouble(xmp, "Saturation"),
            Temperature = GetDouble(xmp, "Temperature"),
            Tint = GetDouble(xmp, "Tint"),
            Clarity = GetDouble(xmp, "Clarity"),
            Sharpness = GetDouble(xmp, "Sharpness"),
            NoiseReduction = GetDouble(xmp, "NoiseReduction"),
            SharpeningMode = GetSharpeningMode(xmp),
            UnsharpMaskAmount = GetDouble(xmp, "UnsharpMaskAmount"),
            UnsharpMaskRadius = GetDouble(xmp, "UnsharpMaskRadius", 1.0),
            UnsharpMaskThreshold = GetDouble(xmp, "UnsharpMaskThreshold", 0.02),
            BlackPoint = GetDouble(xmp, "BlackPoint"),
            WhitePoint = GetDouble(xmp, "WhitePoint", 1.0),
            Midtones = GetDouble(xmp, "Midtones", 0.5),
            RotationAngle = GetDouble(xmp, "RotationAngle")
        };

        var cropLeft = GetDoubleOrNull(xmp, "CropLeft");
        if (cropLeft.HasValue)
        {
            p.Crop = new CropRect
            {
                Left = cropLeft.Value,
                Top = GetDouble(xmp, "CropTop"),
                Width = GetDouble(xmp, "CropWidth", 1.0),
                Height = GetDouble(xmp, "CropHeight", 1.0)
            };
        }

        return p;
    }

    private static double GetRawDouble(IXmpMeta xmp, string prop, double defaultValue) =>
        xmp.DoesPropertyExist(Ns, $"{Pre}:{prop}")
            ? xmp.GetPropertyDouble(Ns, $"{Pre}:{prop}")
            : defaultValue;

    private static void SetDouble(IXmpMeta xmp, string prop, double value)
    {
        xmp.SetPropertyDouble(Ns, $"{Pre}:{prop}", value);
    }

    private static double GetDouble(IXmpMeta xmp, string prop, double defaultValue = 0.0)
    {
        try { return xmp.GetPropertyDouble(Ns, $"{Pre}:{prop}"); }
        catch { return defaultValue; }
    }

    private static double? GetDoubleOrNull(IXmpMeta xmp, string prop)
    {
        try { return xmp.GetPropertyDouble(Ns, $"{Pre}:{prop}"); }
        catch { return null; }
    }

    private static SharpeningMode GetSharpeningMode(IXmpMeta xmp)
    {
        try
        {
            var value = xmp.GetPropertyString(Ns, $"{Pre}:SharpeningMode");
            return Enum.TryParse<SharpeningMode>(value, out var mode) && Enum.IsDefined(mode)
                ? mode
                : SharpeningMode.Legacy;
        }
        catch
        {
            return SharpeningMode.Legacy;
        }
    }

    private static void DeleteProp(IXmpMeta xmp, string prop)
    {
        try { xmp.DeleteProperty(Ns, $"{Pre}:{prop}"); }
        catch { /* Not found */ }
    }
}
