using XmpCore;

namespace PhotoLibrarian.Core.Services;

/// <summary>
/// Writes editable metadata (rating, caption, date taken) into image files.
/// 
/// Strategy: write <b>directly into the image file</b> using WIC (see <see cref="EmbeddedMetadataWriter"/>)
/// for supported formats (JPEG/TIFF/PNG/HEIC/JPEG-XR). For unsupported formats (RAW: CR2/CR3/NEF/ARW etc.),
/// fall back to an XMP sidecar — RAW files are designed to be read-only and Lightroom/Photoshop also
/// resort to sidecars for them.
/// 
/// The premise: edits stay with the original file wherever possible, so moving an image to another drive
/// or computer carries the metadata with it.
/// </summary>
public static class MetadataWriterService
{
    private const string XapNamespace = "http://ns.adobe.com/xap/1.0/";
    private const string DcNamespace = "http://purl.org/dc/elements/1.1/";
    private const string ExifNamespace = "http://ns.adobe.com/exif/1.0/";

    /// <summary>XMP namespace for PhotoLibrarian-specific properties (there is no standard flag field).</summary>
    public const string PhotoLibrarianNamespace = EmbeddedMetadataWriter.PhotoLibrarianXmpNamespace;
    private const string FlagPropertyName = "plib:Flagged";

    static MetadataWriterService()
    {
        try { XmpMetaFactory.SchemaRegistry.RegisterNamespace(PhotoLibrarianNamespace, "plib"); }
        catch { /* already registered */ }
    }

    /// <summary>
    /// Writes the user flag to the image (in-file XMP) or sidecar (RAW / in-place write failure).
    /// </summary>
    public static async Task WriteFlagAsync(string imagePath, bool flagged)
    {
        if (EmbeddedMetadataWriter.IsSupported(imagePath))
        {
            try
            {
                await EmbeddedMetadataWriter.WriteAsync(imagePath, flagged: flagged);
                return;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[METADATA] In-place flag write failed for {imagePath}: {ex.Message}. Falling back to sidecar.");
            }
        }

        await Task.Run(() =>
        {
            var xmp = LoadOrCreateSidecar(imagePath);
            if (flagged)
                xmp.SetProperty(PhotoLibrarianNamespace, FlagPropertyName, "True");
            else
                try { xmp.DeleteProperty(PhotoLibrarianNamespace, FlagPropertyName); } catch { }
            SaveSidecar(imagePath, xmp);
        });
    }

    /// <summary>
    /// Reads the user flag from a legacy/RAW XMP sidecar. Returns null when there is no sidecar
    /// or it carries no flag property (so callers can fall back to the cache DB value).
    /// </summary>
    public static bool? ReadFlagFromSidecar(string imagePath)
    {
        var sidecarPath = Path.ChangeExtension(imagePath, ".xmp");
        if (!File.Exists(sidecarPath)) return null;

        try
        {
            var xmp = XmpMetaFactory.ParseFromString(File.ReadAllText(sidecarPath));
            if (!xmp.DoesPropertyExist(PhotoLibrarianNamespace, FlagPropertyName)) return null;
            return string.Equals(
                xmp.GetPropertyString(PhotoLibrarianNamespace, FlagPropertyName),
                "True",
                StringComparison.OrdinalIgnoreCase);
        }
        catch { }

        return null;
    }

    /// <summary>Writes rating (0=clear, 1-5 stars) to the image (in-file) or sidecar (RAW fallback).</summary>
    public static async Task WriteRatingAsync(string imagePath, int? rating)
    {
        if (EmbeddedMetadataWriter.IsSupported(imagePath))
        {
            try
            {
                await EmbeddedMetadataWriter.WriteAsync(imagePath, rating: rating ?? 0);
                return;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[METADATA] In-place rating write failed for {imagePath}: {ex.Message}. Falling back to sidecar.");
            }
        }

        // Fallback: XMP sidecar (for RAW or in-place write failure)
        await Task.Run(() =>
        {
            var xmp = LoadOrCreateSidecar(imagePath);
            if (rating.HasValue && rating.Value > 0)
                xmp.SetPropertyInteger(XapNamespace, "xmp:Rating", rating.Value);
            else
                try { xmp.DeleteProperty(XapNamespace, "xmp:Rating"); } catch { }
            SaveSidecar(imagePath, xmp);
        });
    }

    /// <summary>Writes caption to the image (in-file: System.Title + System.Comment) or sidecar fallback.</summary>
    public static async Task WriteCaptionAsync(string imagePath, string? caption)
    {
        var safe = caption ?? "";

        if (EmbeddedMetadataWriter.IsSupported(imagePath))
        {
            try
            {
                await EmbeddedMetadataWriter.WriteAsync(imagePath, title: safe);
                return;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[METADATA] In-place caption write failed for {imagePath}: {ex.Message}. Falling back to sidecar.");
            }
        }

        await Task.Run(() =>
        {
            var xmp = LoadOrCreateSidecar(imagePath);
            if (!string.IsNullOrEmpty(safe))
                xmp.SetLocalizedText(DcNamespace, "dc:description", "", "x-default", safe);
            else
                try { xmp.DeleteProperty(DcNamespace, "dc:description"); } catch { }
            SaveSidecar(imagePath, xmp);
        });
    }

    /// <summary>
    /// Reads caption from the image. Prefers in-file value (via MetadataExtractor); falls back
    /// to legacy XMP sidecar so old edits remain visible during the migration period.
    /// </summary>
    public static string? ReadCaptionFromSidecar(string imagePath)
    {
        // 1. Prefer in-file caption (primary source after the in-place writer migration)
        var fileCaption = ReadCaptionFromFile(imagePath);
        if (!string.IsNullOrWhiteSpace(fileCaption)) return fileCaption;

        // 2. Legacy fallback: XMP sidecar (for files edited before in-place writes existed)
        var sidecarPath = Path.ChangeExtension(imagePath, ".xmp");
        if (!File.Exists(sidecarPath)) return null;

        try
        {
            var xml = File.ReadAllText(sidecarPath);
            var xmp = XmpMetaFactory.ParseFromString(xml);
            if (xmp.DoesPropertyExist(DcNamespace, "dc:description"))
            {
                var prop = xmp.GetLocalizedText(DcNamespace, "dc:description", "", "x-default");
                return prop?.Value;
            }
        }
        catch { }

        return null;
    }

    private static string? ReadCaptionFromFile(string imagePath)
    {
        try
        {
            var dirs = MetadataExtractor.ImageMetadataReader.ReadMetadata(imagePath);

            // Try XPTitle (System.Title) — UTF-16 string in EXIF tag 0x9C9B
            var exif = dirs.OfType<MetadataExtractor.Formats.Exif.ExifIfd0Directory>().FirstOrDefault();
            if (exif != null)
            {
                var xpTitle = exif.GetDescription(0x9C9B);
                if (!string.IsNullOrWhiteSpace(xpTitle)) return xpTitle;
                var xpComment = exif.GetDescription(0x9C9C);
                if (!string.IsNullOrWhiteSpace(xpComment)) return xpComment;
                var imgDesc = exif.GetDescription(0x010E); // ImageDescription
                if (!string.IsNullOrWhiteSpace(imgDesc)) return imgDesc;
            }

            // Try XMP packet embedded in the file (not the sidecar)
            var xmpDir = dirs.OfType<MetadataExtractor.Formats.Xmp.XmpDirectory>().FirstOrDefault();
            if (xmpDir?.XmpMeta != null && xmpDir.XmpMeta.DoesPropertyExist(DcNamespace, "dc:description"))
            {
                var prop = xmpDir.XmpMeta.GetLocalizedText(DcNamespace, "dc:description", "", "x-default");
                return prop?.Value;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Writes capture date directly into the image (in-file) or to sidecar (RAW fallback).</summary>
    public static async Task WriteDateTakenAsync(string imagePath, DateTime? dateTaken)
    {
        if (EmbeddedMetadataWriter.IsSupported(imagePath) && dateTaken.HasValue)
        {
            try
            {
                await EmbeddedMetadataWriter.WriteAsync(imagePath, dateTaken: dateTaken.Value);
                return;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[METADATA] In-place date write failed for {imagePath}: {ex.Message}. Falling back to sidecar.");
            }
        }

        await Task.Run(() =>
        {
            var xmp = LoadOrCreateSidecar(imagePath);
            if (dateTaken.HasValue)
            {
                var iso = dateTaken.Value.ToString("yyyy-MM-ddTHH:mm:ss");
                xmp.SetProperty(XapNamespace, "xmp:CreateDate", iso);
                xmp.SetProperty(XapNamespace, "xmp:ModifyDate", iso);
                xmp.SetProperty(ExifNamespace, "exif:DateTimeOriginal", iso);
            }
            else
            {
                try { xmp.DeleteProperty(XapNamespace, "xmp:CreateDate"); } catch { }
                try { xmp.DeleteProperty(XapNamespace, "xmp:ModifyDate"); } catch { }
                try { xmp.DeleteProperty(ExifNamespace, "exif:DateTimeOriginal"); } catch { }
            }
            SaveSidecar(imagePath, xmp);
        });
    }

    private static IXmpMeta LoadOrCreateSidecar(string imagePath)
    {
        var sidecarPath = Path.ChangeExtension(imagePath, ".xmp");
        try
        {
            if (File.Exists(sidecarPath))
            {
                var xml = File.ReadAllText(sidecarPath);
                return XmpMetaFactory.ParseFromString(xml);
            }
        }
        catch { }
        return XmpMetaFactory.Create();
    }

    private static void SaveSidecar(string imagePath, IXmpMeta xmp)
    {
        var sidecarPath = Path.ChangeExtension(imagePath, ".xmp");
        var serialized = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
        File.WriteAllText(sidecarPath, serialized);
    }
}
