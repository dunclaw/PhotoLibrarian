using XmpCore;

namespace PhotoLibrarian.Core.Services;

/// <summary>
/// Writes the full keyword set into image files. Prefers in-file (System.Keywords →
/// XMP dc:subject + EXIF XPKeywords) via WIC for supported formats, falls back to XMP
/// sidecar for RAW formats.
/// </summary>
public static class TagWriterService
{
    private const string DcNamespace = "http://purl.org/dc/elements/1.1/";

    /// <summary>
    /// Writes the complete tag set to the image file. Pass an empty enumeration to clear all tags.
    /// </summary>
    public static async Task WriteTagsToSidecarAsync(string imagePath, IEnumerable<string> tags)
    {
        var distinct = tags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (EmbeddedMetadataWriter.IsSupported(imagePath))
        {
            try
            {
                await EmbeddedMetadataWriter.WriteAsync(imagePath, keywords: distinct);
                return;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TAGS] In-place keyword write failed for {imagePath}: {ex.Message}. Falling back to sidecar.");
            }
        }

        // RAW fallback: write XMP sidecar
        await Task.Run(() =>
        {
            var sidecarPath = Path.ChangeExtension(imagePath, ".xmp");
            IXmpMeta xmp;
            try
            {
                xmp = File.Exists(sidecarPath)
                    ? XmpMetaFactory.ParseFromString(File.ReadAllText(sidecarPath))
                    : XmpMetaFactory.Create();
            }
            catch { xmp = XmpMetaFactory.Create(); }

            try { xmp.DeleteProperty(DcNamespace, "dc:subject"); } catch { }

            foreach (var tag in distinct)
            {
                xmp.AppendArrayItem(DcNamespace, "dc:subject",
                    new XmpCore.Options.PropertyOptions { IsArray = true, IsArrayOrdered = false },
                    tag, null);
            }

            var serialized = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
            File.WriteAllText(sidecarPath, serialized);
        });
    }

    /// <summary>
    /// Reads existing dc:subject keywords from an XMP sidecar (RAW fallback path).
    /// For supported formats, use <see cref="EmbeddedMetadataWriter.ReadKeywordsAsync"/> instead.
    /// </summary>
    public static List<string> ReadTagsFromSidecar(string imagePath)
    {
        var tags = new List<string>();
        var sidecarPath = Path.ChangeExtension(imagePath, ".xmp");
        if (!File.Exists(sidecarPath)) return tags;

        try
        {
            var xml = File.ReadAllText(sidecarPath);
            var xmp = XmpMetaFactory.ParseFromString(xml);
            int count = xmp.CountArrayItems(DcNamespace, "dc:subject");
            for (int i = 1; i <= count; i++)
            {
                var item = xmp.GetArrayItem(DcNamespace, "dc:subject", i);
                if (!string.IsNullOrWhiteSpace(item?.Value))
                    tags.Add(item.Value);
            }
        }
        catch { }

        return tags;
    }
}
