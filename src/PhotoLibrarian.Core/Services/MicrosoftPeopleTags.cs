using PhotoLibrarian.Core.Models;
using System.Globalization;
using XmpCore;
using XmpCore.Options;

namespace PhotoLibrarian.Core.Services;

internal static class MicrosoftPeopleTags
{
    internal const string Namespace = "http://ns.microsoft.com/photo/1.2/";
    internal const string RegionInfoNamespace = "http://ns.microsoft.com/photo/1.2/t/RegionInfo#";
    internal const string RegionNamespace = "http://ns.microsoft.com/photo/1.2/t/Region#";
    internal const string RegionsPath = "RegionInfo/MPRI:Regions";

    static MicrosoftPeopleTags()
    {
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(Namespace, "MP");
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(RegionInfoNamespace, "MPRI");
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(RegionNamespace, "MPReg");
    }

    internal static void Clear(IXmpMeta xmp) =>
        xmp.DeleteProperty(Namespace, RegionsPath);

    internal static void Write(IXmpMeta xmp, IReadOnlyList<PortableFaceMetadata> faces)
    {
        Clear(xmp);
        foreach (var face in faces.Where(face => !string.IsNullOrWhiteSpace(face.PersonName)))
        {
            xmp.AppendArrayItem(
                Namespace,
                RegionsPath,
                new PropertyOptions { IsArray = true },
                null,
                new PropertyOptions { IsStruct = true });
            var path = $"{RegionsPath}[{xmp.CountArrayItems(Namespace, RegionsPath)}]";
            xmp.SetStructField(Namespace, path, RegionNamespace, "PersonDisplayName", face.PersonName);
            SetRectangle(xmp, path, new NormalizedRegion(face.X, face.Y, face.Width, face.Height));
        }
    }

    internal static IEnumerable<PortableFaceMetadata> Read(IXmpMeta xmp)
    {
        for (var index = 1; index <= xmp.CountArrayItems(Namespace, RegionsPath); index++)
        {
            var path = $"{RegionsPath}[{index}]";
            var name = xmp.GetStructField(Namespace, path, RegionNamespace, "PersonDisplayName")?.Value;
            if (string.IsNullOrWhiteSpace(name) || !TryReadRectangle(xmp, path, out var region))
            {
                continue;
            }

            yield return new PortableFaceMetadata(
                0, region.X, region.Y, region.Width, region.Height, name.Trim(), false, false, []);
        }
    }

    internal static bool Remap(
        IXmpMeta xmp,
        uint sourceWidth,
        uint sourceHeight,
        CropRectangle crop)
    {
        if (!xmp.DoesPropertyExist(Namespace, RegionsPath)) return false;

        for (var index = xmp.CountArrayItems(Namespace, RegionsPath); index >= 1; index--)
        {
            var path = $"{RegionsPath}[{index}]";
            // A name-only tag is valid and has no geometry to remap.
            if (!xmp.DoesStructFieldExist(Namespace, path, RegionNamespace, "Rectangle")) continue;
            if (!TryReadRectangle(xmp, path, out var region))
            {
                throw new InvalidDataException("Microsoft People Tag rectangle is invalid.");
            }

            var remapped = CropMetadataRemapper.RemapNormalizedRegion(
                region, sourceWidth, sourceHeight, crop);
            if (remapped is null)
            {
                xmp.DeleteArrayItem(Namespace, RegionsPath, index);
            }
            else
            {
                SetRectangle(xmp, path, remapped.Value);
            }
        }

        return true;
    }

    private static bool TryReadRectangle(IXmpMeta xmp, string path, out NormalizedRegion region)
    {
        region = default;
        var text = xmp.GetStructField(Namespace, path, RegionNamespace, "Rectangle")?.Value;
        var parts = text?.Split(',');
        if (parts?.Length != 4) return false;

        var values = new double[4];
        for (var index = 0; index < values.Length; index++)
        {
            if (!double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out values[index]) ||
                !double.IsFinite(values[index]))
            {
                return false;
            }
        }

        if (values[0] < 0 || values[1] < 0 || values[2] <= 0 || values[3] <= 0 ||
            values[0] + values[2] > 1.000001 || values[1] + values[3] > 1.000001)
        {
            return false;
        }

        region = new NormalizedRegion(values[0], values[1], values[2], values[3]);
        return true;
    }

    private static void SetRectangle(IXmpMeta xmp, string path, NormalizedRegion region) =>
        xmp.SetStructField(
            Namespace,
            path,
            RegionNamespace,
            "Rectangle",
            // Microsoft rectangles use top-left coordinates, unlike MWG's center coordinates.
            string.Join(", ", new[] { region.X, region.Y, region.Width, region.Height }
                .Select(value => value.ToString("F6", CultureInfo.InvariantCulture))));
}
