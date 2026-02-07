using XmpCore;

namespace PhotoLibrarian.Core.Services;

/// <summary>
/// Writes approved tags as dc:subject keywords into image XMP metadata.
/// </summary>
public static class TagWriterService
{
    private const string DcNamespace = "http://purl.org/dc/elements/1.1/";

    /// <summary>
    /// Writes tags to an image file's XMP sidecar as dc:subject keywords.
    /// </summary>
    public static async Task WriteTagsToSidecarAsync(string imagePath, IEnumerable<string> tags)
    {
        await Task.Run(() =>
        {
            // Read existing XMP sidecar or create new
            var sidecarPath = Path.ChangeExtension(imagePath, ".xmp");
            IXmpMeta xmp;

            try
            {
                if (File.Exists(sidecarPath))
                {
                    var xml = File.ReadAllText(sidecarPath);
                    xmp = XmpMetaFactory.ParseFromString(xml);
                }
                else
                {
                    xmp = XmpMetaFactory.Create();
                }
            }
            catch
            {
                xmp = XmpMetaFactory.Create();
            }

            // Clear existing dc:subject
            try { xmp.DeleteProperty(DcNamespace, "dc:subject"); } catch { }

            // Write each tag as a dc:subject array item
            foreach (var tag in tags.Distinct())
            {
                xmp.AppendArrayItem(DcNamespace, "dc:subject",
                    new XmpCore.Options.PropertyOptions { IsArray = true, IsArrayOrdered = false },
                    tag, null);
            }

            // Save sidecar
            var serialized = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
            File.WriteAllText(sidecarPath, serialized);
        });
    }

    /// <summary>
    /// Reads existing dc:subject tags from an XMP sidecar.
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
