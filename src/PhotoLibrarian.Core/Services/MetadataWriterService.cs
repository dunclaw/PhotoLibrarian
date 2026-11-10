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
    /// <summary>Raised after an unexpected sidecar fallback has been saved successfully.</summary>
    public static event EventHandler<MetadataWriteFallbackEventArgs>? SidecarFallback;

    internal static void ReportSidecarFallback(
        string imagePath, string propertyName, Exception error, string? sidecarPath = null)
    {
        var args = new MetadataWriteFallbackEventArgs(imagePath, propertyName, error, sidecarPath);
        System.Diagnostics.Trace.TraceWarning(
            $"[METADATA] Saved {propertyName} to {args.SidecarPath}, not {imagePath}: {error.Message}");
        Diagnostics.DebugLog.WriteLine(
            $"[METADATA] Saved {propertyName} to {args.SidecarPath} because {imagePath} could not be updated: " +
            $"{error.GetType().Name} 0x{error.HResult:X8} {error.Message}");
        SidecarFallback?.Invoke(null, args);
    }

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
        Exception? embeddedError = null;
        if (EmbeddedMetadataWriter.IsSupported(imagePath))
        {
            try
            {
                await EmbeddedMetadataWriter.WriteAsync(imagePath, flagged: flagged);
                return;
            }
            catch (Exception ex)
            {
                embeddedError = ex;
                System.Diagnostics.Debug.WriteLine($"[METADATA] In-place flag write failed for {imagePath}: {ex.Message}. Falling back to sidecar.");
            }
        }

        var sidecarPath = await UpdateSidecarAsync(imagePath, MetadataSidecarLocator.OwnsFlag, xmp =>
        {
            if (flagged)
                xmp.SetProperty(PhotoLibrarianNamespace, FlagPropertyName, "True");
            else
                try { xmp.DeleteProperty(PhotoLibrarianNamespace, FlagPropertyName); } catch { }
        });
        if (embeddedError is not null)
            ReportSidecarFallback(imagePath, "flag", embeddedError, sidecarPath);
    }

    /// <summary>
    /// Reads the user flag from a legacy/RAW XMP sidecar. Returns null when there is no sidecar
    /// or it carries no flag property (so callers can fall back to the cache DB value).
    /// </summary>
    public static bool? ReadFlagFromSidecar(string imagePath)
    {
        var sidecarPath = MetadataSidecarLocator.ResolveReadPath(imagePath, MetadataSidecarLocator.OwnsFlag);
        if (sidecarPath is null) return null;

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
        Exception? embeddedError = null;
        if (EmbeddedMetadataWriter.IsSupported(imagePath))
        {
            try
            {
                await EmbeddedMetadataWriter.WriteAsync(imagePath, rating: rating ?? 0);
                return;
            }
            catch (Exception ex)
            {
                embeddedError = ex;
                System.Diagnostics.Debug.WriteLine($"[METADATA] In-place rating write failed for {imagePath}: {ex.Message}. Falling back to sidecar.");
            }
        }

        // Fallback: XMP sidecar (for RAW or in-place write failure)
        var sidecarPath = await UpdateSidecarAsync(imagePath, MetadataSidecarLocator.OwnsRating, xmp =>
        {
            if (rating.HasValue && rating.Value > 0)
                xmp.SetPropertyInteger(XapNamespace, "xmp:Rating", rating.Value);
            else
                try { xmp.DeleteProperty(XapNamespace, "xmp:Rating"); } catch { }
        });
        if (embeddedError is not null)
            ReportSidecarFallback(imagePath, "rating", embeddedError, sidecarPath);
    }

    /// <summary>Writes caption to the image (in-file: System.Title + System.Comment) or sidecar fallback.</summary>
    public static async Task WriteCaptionAsync(string imagePath, string? caption)
    {
        var safe = caption ?? "";
        Exception? embeddedError = null;

        if (EmbeddedMetadataWriter.IsSupported(imagePath))
        {
            try
            {
                await EmbeddedMetadataWriter.WriteAsync(imagePath, title: safe);
                return;
            }
            catch (Exception ex)
            {
                embeddedError = ex;
                System.Diagnostics.Debug.WriteLine($"[METADATA] In-place caption write failed for {imagePath}: {ex.Message}. Falling back to sidecar.");
            }
        }

        var sidecarPath = await UpdateSidecarAsync(imagePath, MetadataSidecarLocator.OwnsCaption, xmp =>
        {
            if (!string.IsNullOrEmpty(safe))
                xmp.SetLocalizedText(DcNamespace, "dc:description", "", "x-default", safe);
            else
                try { xmp.DeleteProperty(DcNamespace, "dc:description"); } catch { }
        });
        if (embeddedError is not null)
            ReportSidecarFallback(imagePath, "caption", embeddedError, sidecarPath);
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

        // 2. Sidecar fallback (RAW, failed in-place writes, or edits that predate them)
        var sidecarPath = MetadataSidecarLocator.ResolveReadPath(imagePath, MetadataSidecarLocator.OwnsCaption);
        if (sidecarPath is null) return null;

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
        Exception? embeddedError = null;
        if (EmbeddedMetadataWriter.IsSupported(imagePath) && dateTaken.HasValue)
        {
            try
            {
                await EmbeddedMetadataWriter.WriteAsync(imagePath, dateTaken: dateTaken.Value);
                return;
            }
            catch (Exception ex)
            {
                embeddedError = ex;
                System.Diagnostics.Debug.WriteLine($"[METADATA] In-place date write failed for {imagePath}: {ex.Message}. Falling back to sidecar.");
            }
        }

        var sidecarPath = await UpdateSidecarAsync(imagePath, MetadataSidecarLocator.OwnsDateTaken, xmp =>
        {
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
        });
        if (embeddedError is not null)
            ReportSidecarFallback(imagePath, "date taken", embeddedError, sidecarPath);
    }

    /// <summary>
    /// Applies one property change to the sidecar that owns that property for the photo and
    /// returns the sidecar path. Other properties in the sidecar are preserved; an existing
    /// sidecar that cannot be parsed is reported rather than replaced.
    /// </summary>
    private static Task<string> UpdateSidecarAsync(
        string imagePath, Func<IXmpMeta, bool> ownsProperty, Action<IXmpMeta> apply) =>
        Task.Run(() =>
        {
            var sidecarPath = MetadataSidecarLocator.ResolveTargetPath(imagePath, ownsProperty);
            var xmp = LoadOrCreateSidecar(sidecarPath);
            apply(xmp);
            SaveSidecar(sidecarPath, xmp);
            return sidecarPath;
        });

    private static IXmpMeta LoadOrCreateSidecar(string sidecarPath)
    {
        if (!File.Exists(sidecarPath))
            return XmpMetaFactory.Create();

        try
        {
            return XmpMetaFactory.ParseFromString(File.ReadAllText(sidecarPath));
        }
        catch (XmpException exception)
        {
            throw new IOException(
                $"The existing XMP sidecar could not be read and was left unchanged: {sidecarPath}.",
                exception);
        }
    }

    private static void SaveSidecar(string sidecarPath, IXmpMeta xmp)
    {
        var serialized = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
        File.WriteAllText(sidecarPath, serialized);
    }
}
