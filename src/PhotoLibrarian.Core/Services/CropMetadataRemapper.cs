using PhotoLibrarian.Core.Models;
using System.Globalization;
using XmpCore;
using XmpCore.Options;

namespace PhotoLibrarian.Core.Services;

public readonly record struct CropRectangle(uint X, uint Y, uint Width, uint Height);

public readonly record struct NormalizedRegion(double X, double Y, double Width, double Height);

public static class CropMetadataRemapper
{
    public const string MwgRs = "http://www.metadataworkinggroup.com/schemas/regions/";
    public const string StArea = "http://ns.adobe.com/xmp/sType/Area#";
    public const string StDim = "http://ns.adobe.com/xmp/sType/Dimensions#";

    public static FaceRegion? RemapFaceRegion(
        FaceRegion face,
        uint sourceWidth,
        uint sourceHeight,
        CropRectangle crop)
    {
        var remapped = RemapNormalizedRegion(
            new NormalizedRegion(face.X, face.Y, face.Width, face.Height),
            sourceWidth,
            sourceHeight,
            crop);
        if (remapped is null)
        {
            return null;
        }

        return new FaceRegion
        {
            Id = face.Id,
            ImageId = face.ImageId,
            X = remapped.Value.X,
            Y = remapped.Value.Y,
            Width = remapped.Value.Width,
            Height = remapped.Value.Height,
            PersonName = face.PersonName,
            PersonId = face.PersonId,
            Embedding = face.Embedding,
            Confidence = face.Confidence
        };
    }

    public static NormalizedRegion? RemapNormalizedRegion(
        NormalizedRegion region,
        uint sourceWidth,
        uint sourceHeight,
        CropRectangle crop)
    {
        ValidateDimensions(sourceWidth, sourceHeight, crop);

        var left = region.X * sourceWidth;
        var top = region.Y * sourceHeight;
        var right = left + (region.Width * sourceWidth);
        var bottom = top + (region.Height * sourceHeight);

        var clippedLeft = Math.Max(left, crop.X);
        var clippedTop = Math.Max(top, crop.Y);
        var clippedRight = Math.Min(right, crop.X + crop.Width);
        var clippedBottom = Math.Min(bottom, crop.Y + crop.Height);
        if (clippedRight <= clippedLeft || clippedBottom <= clippedTop)
        {
            return null;
        }

        return new NormalizedRegion(
            Math.Clamp((clippedLeft - crop.X) / crop.Width, 0, 1),
            Math.Clamp((clippedTop - crop.Y) / crop.Height, 0, 1),
            Math.Clamp((clippedRight - clippedLeft) / crop.Width, 0, 1),
            Math.Clamp((clippedBottom - clippedTop) / crop.Height, 0, 1));
    }

    public static ushort[]? RemapSubjectLocation(
        ushort[] value,
        uint sourceWidth,
        uint sourceHeight,
        CropRectangle crop)
    {
        ValidateDimensions(sourceWidth, sourceHeight, crop);
        if (value.Length != 2 || !Contains(crop, value[0], value[1]))
        {
            return null;
        }

        return [ToUShort(value[0] - crop.X), ToUShort(value[1] - crop.Y)];
    }

    public static ushort[]? OrientSubjectLocation(
        ushort[] value,
        uint sourceWidth,
        uint sourceHeight,
        ushort orientation)
    {
        if (value.Length != 2 || sourceWidth == 0 || sourceHeight == 0)
        {
            return null;
        }

        var (x, y) = OrientPoint(value[0], value[1], sourceWidth, sourceHeight, orientation);
        return [ToUShort(x), ToUShort(y)];
    }

    public static ushort[]? RemapSubjectArea(
        ushort[] value,
        uint sourceWidth,
        uint sourceHeight,
        CropRectangle crop)
    {
        ValidateDimensions(sourceWidth, sourceHeight, crop);
        if (value.Length == 2)
        {
            return RemapSubjectLocation(value, sourceWidth, sourceHeight, crop);
        }

        if (value.Length is not (3 or 4) || value[2] == 0 || (value.Length == 4 && value[3] == 0))
        {
            return null;
        }

        var width = value[2];
        var height = value.Length == 3 ? value[2] : value[3];
        var left = value[0] - (width / 2d);
        var top = value[1] - (height / 2d);
        var right = left + width;
        var bottom = top + height;

        var clippedLeft = Math.Max(left, crop.X);
        var clippedTop = Math.Max(top, crop.Y);
        var clippedRight = Math.Min(right, crop.X + crop.Width);
        var clippedBottom = Math.Min(bottom, crop.Y + crop.Height);
        if (clippedRight <= clippedLeft || clippedBottom <= clippedTop)
        {
            return null;
        }

        var clippedWidth = clippedRight - clippedLeft;
        var clippedHeight = clippedBottom - clippedTop;
        var centerX = ((clippedLeft + clippedRight) / 2d) - crop.X;
        var centerY = ((clippedTop + clippedBottom) / 2d) - crop.Y;

        if (value.Length == 3 && clippedWidth == width && clippedHeight == height)
        {
            return [ToUShort(centerX), ToUShort(centerY), value[2]];
        }

        return [ToUShort(centerX), ToUShort(centerY), ToUShort(clippedWidth), ToUShort(clippedHeight)];
    }

    public static ushort[]? OrientSubjectArea(
        ushort[] value,
        uint sourceWidth,
        uint sourceHeight,
        ushort orientation)
    {
        if (value.Length == 2)
        {
            return OrientSubjectLocation(value, sourceWidth, sourceHeight, orientation);
        }

        if (value.Length is not (3 or 4) || sourceWidth == 0 || sourceHeight == 0)
        {
            return null;
        }

        var (x, y) = OrientPoint(value[0], value[1], sourceWidth, sourceHeight, orientation);
        var swapsAxes = orientation is >= 5 and <= 8;
        if (value.Length == 3)
        {
            return [ToUShort(x), ToUShort(y), value[2]];
        }

        return
        [
            ToUShort(x),
            ToUShort(y),
            swapsAxes ? value[3] : value[2],
            swapsAxes ? value[2] : value[3]
        ];
    }

    public static bool RemapMwgRegions(
        IXmpMeta xmp,
        uint sourceWidth,
        uint sourceHeight,
        CropRectangle crop)
    {
        ValidateDimensions(sourceWidth, sourceHeight, crop);
        const string regionsPath = "Regions";
        const string regionListPath = "Regions/mwg-rs:RegionList";

        if (!xmp.DoesPropertyExist(MwgRs, regionsPath))
        {
            return false;
        }

        XmpMetaFactory.SchemaRegistry.RegisterNamespace(MwgRs, "mwg-rs");
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(StArea, "stArea");
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(StDim, "stDim");

        var dimensionsPath = $"{regionsPath}/mwg-rs:AppliedToDimensions";
        var appliedWidth = ReadPositiveDoubleOrDefault(xmp, dimensionsPath, StDim, "w", sourceWidth);
        var appliedHeight = ReadPositiveDoubleOrDefault(xmp, dimensionsPath, StDim, "h", sourceHeight);

        for (var index = xmp.CountArrayItems(MwgRs, regionListPath); index >= 1; index--)
        {
            var itemPath = $"{regionListPath}[{index}]";
            var areaPath = $"{itemPath}/mwg-rs:Area";
            var x = ReadRequiredDouble(xmp, areaPath, StArea, "x");
            var y = ReadRequiredDouble(xmp, areaPath, StArea, "y");
            var width = ReadRequiredDouble(xmp, areaPath, StArea, "w");
            var height = ReadRequiredDouble(xmp, areaPath, StArea, "h");
            var unit = xmp.GetStructField(MwgRs, areaPath, StArea, "unit")?.Value;

            if (string.Equals(unit, "pixel", StringComparison.OrdinalIgnoreCase))
            {
                x /= appliedWidth;
                y /= appliedHeight;
                width /= appliedWidth;
                height /= appliedHeight;
            }
            else if (!string.IsNullOrEmpty(unit) &&
                     !string.Equals(unit, "normalized", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Unsupported MWG region unit '{unit}'.");
            }

            var topLeftRegion = new NormalizedRegion(
                x - (width / 2d),
                y - (height / 2d),
                width,
                height);
            var remapped = RemapNormalizedRegion(topLeftRegion, sourceWidth, sourceHeight, crop);
            if (remapped is null)
            {
                xmp.DeleteArrayItem(MwgRs, regionListPath, index);
                continue;
            }

            var centerX = remapped.Value.X + (remapped.Value.Width / 2d);
            var centerY = remapped.Value.Y + (remapped.Value.Height / 2d);
            SetAreaValue(xmp, areaPath, "x", centerX);
            SetAreaValue(xmp, areaPath, "y", centerY);
            SetAreaValue(xmp, areaPath, "w", remapped.Value.Width);
            SetAreaValue(xmp, areaPath, "h", remapped.Value.Height);
            xmp.SetStructField(MwgRs, areaPath, StArea, "unit", "normalized");
        }

        xmp.SetStructField(
            MwgRs,
            regionsPath,
            MwgRs,
            "AppliedToDimensions",
            null,
            new PropertyOptions { IsStruct = true });
        xmp.SetStructField(MwgRs, dimensionsPath, StDim, "w", crop.Width.ToString(CultureInfo.InvariantCulture));
        xmp.SetStructField(MwgRs, dimensionsPath, StDim, "h", crop.Height.ToString(CultureInfo.InvariantCulture));
        xmp.SetStructField(MwgRs, dimensionsPath, StDim, "unit", "pixel");
        return true;
    }

    private static double ReadRequiredDouble(IXmpMeta xmp, string areaPath, string fieldNamespace, string field)
    {
        var value = xmp.GetStructField(MwgRs, areaPath, fieldNamespace, field)?.Value;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ||
            !double.IsFinite(result))
        {
            throw new InvalidDataException($"MWG region field '{field}' is missing or invalid.");
        }

        return result;
    }

    private static double ReadPositiveDoubleOrDefault(
        IXmpMeta xmp,
        string structPath,
        string fieldNamespace,
        string field,
        double fallback)
    {
        var value = xmp.GetStructField(MwgRs, structPath, fieldNamespace, field)?.Value;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) &&
               double.IsFinite(result) &&
               result > 0
            ? result
            : fallback;
    }

    private static void SetAreaValue(IXmpMeta xmp, string areaPath, string field, double value) =>
        xmp.SetStructField(
            MwgRs,
            areaPath,
            StArea,
            field,
            value.ToString("0.######", CultureInfo.InvariantCulture));

    private static bool Contains(CropRectangle crop, double x, double y) =>
        x >= crop.X &&
        y >= crop.Y &&
        x < crop.X + crop.Width &&
        y < crop.Y + crop.Height;

    private static (double X, double Y) OrientPoint(
        double x,
        double y,
        uint sourceWidth,
        uint sourceHeight,
        ushort orientation) =>
        orientation switch
        {
            2 => (sourceWidth - 1 - x, y),
            3 => (sourceWidth - 1 - x, sourceHeight - 1 - y),
            4 => (x, sourceHeight - 1 - y),
            5 => (y, x),
            6 => (sourceHeight - 1 - y, x),
            7 => (sourceHeight - 1 - y, sourceWidth - 1 - x),
            8 => (y, sourceWidth - 1 - x),
            _ => (x, y)
        };

    private static ushort ToUShort(double value) =>
        (ushort)Math.Clamp(Math.Round(value), ushort.MinValue, ushort.MaxValue);

    private static void ValidateDimensions(uint sourceWidth, uint sourceHeight, CropRectangle crop)
    {
        if (sourceWidth == 0 || sourceHeight == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "Source dimensions must be non-zero.");
        }

        if (crop.Width == 0 || crop.Height == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(crop), "Crop dimensions must be non-zero.");
        }
    }
}
