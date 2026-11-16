// Copyright (c) Duncan Lawler. All Rights Reserved.
using XmpCore;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace PhotoLibrarian.Core.Services;

internal enum MetadataWriteProperty
{
    Tags,
    Rating,
    Flag,
    Caption,
    DateTaken,
    Gps,
    Faces,
    ColorLabel,
    PendingEditRecipe
}

/// <summary>
/// Coordinates portable metadata writes for all editable properties. Each property has an explicit
/// embedded-format capability; unsupported formats use an owned XMP sidecar, while failures on a
/// supported embedded write are surfaced without changing storage location.
///
/// Embedded writes are verified before matching owned sidecar values are removed. This keeps edits
/// portable with the photo while preserving unrelated XMP and legacy metadata.
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

    internal static bool SupportsEmbeddedWrite(string imagePath, MetadataWriteProperty property)
    {
        var extension = Path.GetExtension(imagePath);
        return property switch
        {
            MetadataWriteProperty.Tags or
            MetadataWriteProperty.Rating or
            MetadataWriteProperty.Flag or
            MetadataWriteProperty.Caption or
            MetadataWriteProperty.DateTaken or
            MetadataWriteProperty.ColorLabel => EmbeddedMetadataWriter.IsSupported(imagePath),
            MetadataWriteProperty.Gps => extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jpe", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jfif", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase),
            MetadataWriteProperty.Faces or MetadataWriteProperty.PendingEditRecipe =>
                extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jpe", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jfif", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".png", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    internal static Task WriteUsingPolicyAsync(
        string imagePath,
        MetadataWriteProperty property,
        Func<Task> writeEmbedded,
        Func<Task> writeSidecar,
        string? policyPath = null) =>
        SupportsEmbeddedWrite(policyPath ?? imagePath, property) ? writeEmbedded() : writeSidecar();

    internal static void WriteUsingPolicy(
        string imagePath,
        MetadataWriteProperty property,
        Action writeEmbedded,
        Action writeSidecar)
    {
        if (SupportsEmbeddedWrite(imagePath, property))
            writeEmbedded();
        else
            writeSidecar();
    }

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
        await WriteUsingPolicyAsync(
            imagePath,
            MetadataWriteProperty.Flag,
            () => WriteEmbeddedAndMigrateSidecarsAsync(
                imagePath,
                "flag",
                () => EmbeddedMetadataWriter.WriteAsync(imagePath, flagged: flagged),
                () => string.Equals(
                    ReadEmbeddedXmp(imagePath)?.GetPropertyString(PhotoLibrarianNamespace, FlagPropertyName),
                    flagged ? "True" : "False",
                    StringComparison.OrdinalIgnoreCase),
                MetadataSidecarLocator.OwnsFlag,
                xmp => xmp.DeleteProperty(PhotoLibrarianNamespace, FlagPropertyName)),
            () => UpdateSidecarAsync(imagePath, MetadataSidecarLocator.OwnsFlag, xmp =>
        {
            if (flagged)
                xmp.SetProperty(PhotoLibrarianNamespace, FlagPropertyName, "True");
            else
                try { xmp.DeleteProperty(PhotoLibrarianNamespace, FlagPropertyName); } catch { }
            }));
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
        var expected = rating.GetValueOrDefault();
        await WriteUsingPolicyAsync(
            imagePath,
            MetadataWriteProperty.Rating,
            () => WriteEmbeddedAndMigrateSidecarsAsync(
                imagePath,
                "rating",
                () => EmbeddedMetadataWriter.WriteAsync(imagePath, rating: expected),
                () => ReadEmbeddedXmp(imagePath)?.GetPropertyInteger(XapNamespace, "xmp:Rating") == expected,
                MetadataSidecarLocator.OwnsRating,
                xmp => xmp.DeleteProperty(XapNamespace, "xmp:Rating")),
            () => UpdateSidecarAsync(imagePath, MetadataSidecarLocator.OwnsRating, xmp =>
            {
                if (rating.HasValue && rating.Value > 0)
                    xmp.SetPropertyInteger(XapNamespace, "xmp:Rating", rating.Value);
                else
                    try { xmp.DeleteProperty(XapNamespace, "xmp:Rating"); } catch { }
            }));
    }

    /// <summary>Writes a standard XMP label in the image, or in a sidecar for unsupported formats.</summary>
    public static async Task WriteColorLabelAsync(string imagePath, string? label)
    {
        await WriteUsingPolicyAsync(
            imagePath,
            MetadataWriteProperty.ColorLabel,
            () => WriteEmbeddedAndMigrateSidecarsAsync(
                imagePath,
                "color label",
                () => EmbeddedMetadataWriter.WriteAsync(imagePath, colorLabel: label ?? string.Empty),
                () => string.Equals(
                    ReadEmbeddedXmp(imagePath)?.GetPropertyString(XapNamespace, "xmp:Label") ?? string.Empty,
                    label ?? string.Empty,
                    StringComparison.Ordinal),
                MetadataSidecarLocator.OwnsColorLabel,
                xmp => xmp.DeleteProperty(XapNamespace, "xmp:Label")),
            async () =>
            {
                await UpdateSidecarAsync(imagePath, MetadataSidecarLocator.OwnsColorLabel,
                    xmp => xmp.SetProperty(XapNamespace, "xmp:Label", label ?? string.Empty));
            });
    }

    /// <summary>Writes caption into supported image formats, or an owned sidecar when unsupported.</summary>
    public static async Task WriteCaptionAsync(string imagePath, string? caption)
    {
        var safe = caption ?? "";
        await WriteUsingPolicyAsync(
            imagePath,
            MetadataWriteProperty.Caption,
        () => WriteEmbeddedAndMigrateSidecarsAsync(
            imagePath,
            "caption",
            () => WriteEmbeddedCaptionAsync(imagePath, safe),
            () => string.Equals(
                ReadCaptionFromFile(imagePath) ?? string.Empty,
                safe,
                StringComparison.Ordinal),
            MetadataSidecarLocator.OwnsCaption,
            xmp => xmp.DeleteProperty(DcNamespace, "dc:description")),
            () => UpdateSidecarAsync(imagePath, MetadataSidecarLocator.OwnsCaption, xmp =>
            {
                if (!string.IsNullOrEmpty(safe))
                    xmp.SetLocalizedText(DcNamespace, "dc:description", "", "x-default", safe);
                else
                    try { xmp.DeleteProperty(DcNamespace, "dc:description"); } catch { }
            }));
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

        // Unsupported formats and older files may still carry the authoritative caption in XMP.
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

    private static Task WriteEmbeddedCaptionAsync(string imagePath, string caption)
    {
        if (!Path.GetExtension(imagePath).Equals(".png", StringComparison.OrdinalIgnoreCase))
            return EmbeddedMetadataWriter.WriteAsync(imagePath, title: caption);

        return Task.Run(() =>
        {
            var xmp = ReadEmbeddedXmp(imagePath) ?? XmpMetaFactory.Create();
            if (string.IsNullOrEmpty(caption))
                xmp.DeleteProperty(DcNamespace, "dc:description");
            else
                xmp.SetLocalizedText(DcNamespace, "dc:description", "", "x-default", caption);
            var packet = XmpMetaFactory.SerializeToString(
                xmp, new XmpCore.Options.SerializeOptions()).TrimStart('\uFEFF');
            if (!FaceMetadataStore.TryWriteEmbedded(imagePath, packet))
                throw new IOException($"The embedded caption writer rejected {imagePath}.");
        });
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

            foreach (var png in dirs.OfType<MetadataExtractor.Formats.Png.PngDirectory>())
            {
                if (png.GetObject(MetadataExtractor.Formats.Png.PngDirectory.TagTextualData) is
                    not IEnumerable<MetadataExtractor.KeyValuePair> text)
                    continue;
                foreach (var pair in text)
                    if ((pair.Key is "Comment" or "Description" or "Title") &&
                        !string.IsNullOrWhiteSpace(pair.Value.ToString()))
                        return pair.Value.ToString();
            }

            // Try XMP packet embedded in the file (not the sidecar)
            var xmpDir = dirs.OfType<MetadataExtractor.Formats.Xmp.XmpDirectory>().FirstOrDefault();
            if (xmpDir?.XmpMeta is { } xmp)
            {
                var description = xmp.GetLocalizedText(DcNamespace, "dc:description", "", "x-default")?.Value
                    ?? xmp.GetLocalizedText(ExifNamespace, "exif:UserComment", "", "x-default")?.Value;
                if (!string.IsNullOrWhiteSpace(description)) return description;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Writes capture dates into supported image formats, or an owned sidecar when unsupported.</summary>
    public static async Task WriteDateTakenAsync(string imagePath, DateTime? dateTaken)
    {
        await WriteUsingPolicyAsync(
            imagePath,
            MetadataWriteProperty.DateTaken,
            () => WriteEmbeddedAndMigrateSidecarsAsync(
                imagePath,
                "date taken",
                async () =>
            {
                if (dateTaken.HasValue)
                    await EmbeddedMetadataWriter.WriteAsync(imagePath, dateTaken: dateTaken.Value);
                else
                    await EmbeddedMetadataWriter.ClearPropertiesAsync(imagePath, "System.Photo.DateTaken");
            },
                () => ReadEmbeddedDateTaken(imagePath) == dateTaken,
                MetadataSidecarLocator.OwnsDateTaken,
                xmp =>
                {
                    xmp.DeleteProperty(XapNamespace, "xmp:CreateDate");
                    xmp.DeleteProperty(XapNamespace, "xmp:ModifyDate");
                    xmp.DeleteProperty(ExifNamespace, "exif:DateTimeOriginal");
                }),
            () => UpdateSidecarAsync(imagePath, MetadataSidecarLocator.OwnsDateTaken, xmp =>
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
            }));
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

    internal static async Task WriteEmbeddedAndMigrateSidecarsAsync(
        string imagePath,
        string propertyName,
        Func<Task> writeEmbedded,
        Func<bool> verifyEmbedded,
        Func<IXmpMeta, bool> ownsProperty,
        Action<IXmpMeta> removeProperty)
    {
        var sidecars = await LoadOwnedSidecarsAsync(imagePath, ownsProperty);
        await writeEmbedded();
        if (!verifyEmbedded())
        {
            var actual = propertyName == "date taken"
                ? ReadEmbeddedDateTaken(imagePath)?.ToString("O") ?? "<null>"
                : "the stored value does not match the requested value";
            throw new IOException($"The embedded {propertyName} could not be verified after writing; found {actual}.");
        }
        await SaveSidecarPropertyRemovalsAsync(sidecars, removeProperty);
    }

    private static async Task<List<(string Path, IXmpMeta Xmp, TagMetadataSidecarSnapshot Snapshot)>>
        LoadOwnedSidecarsAsync(string imagePath, Func<IXmpMeta, bool> ownsProperty)
    {
        var sidecars = new List<(string, IXmpMeta, TagMetadataSidecarSnapshot)>();
        foreach (var path in MetadataSidecarLocator.GetOwnedSidecarPaths(imagePath))
        {
            if (!File.Exists(path)) continue;
            var xmp = await Task.Run(() => LoadOrCreateSidecar(path));
            if (ownsProperty(xmp))
                sidecars.Add((path, xmp, TagWriterService.CaptureSidecarSnapshotAt(path)));
        }
        return sidecars;
    }

    private static async Task SaveSidecarPropertyRemovalsAsync(
        List<(string Path, IXmpMeta Xmp, TagMetadataSidecarSnapshot Snapshot)> sidecars,
        Action<IXmpMeta> removeProperty)
    {
        foreach (var (path, xmp, snapshot) in sidecars)
        {
            removeProperty(xmp);
            var serialized = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
            await Task.Run(() =>
            {
                if (TagWriterService.CaptureSidecarSnapshotAt(path) != snapshot)
                    throw new IOException($"The XMP sidecar changed while metadata was being migrated: {path}.");
                var temporary = Path.Combine(
                    Path.GetDirectoryName(path)!,
                    $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
                try
                {
                    File.WriteAllText(temporary, serialized);
                    if (TagWriterService.CaptureSidecarSnapshotAt(path) != snapshot)
                        throw new IOException($"The XMP sidecar changed while metadata was being migrated: {path}.");
                    File.Move(temporary, path, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            });
        }
    }

    private static IXmpMeta? ReadEmbeddedXmp(string imagePath) =>
        FaceMetadataStore.ReadEmbeddedXmp(imagePath);

    private static DateTime? ReadEmbeddedDateTaken(string imagePath)
    {
        var directories = MetadataExtractor.ImageMetadataReader.ReadMetadata(imagePath);
        var exif = directories.OfType<ExifSubIfdDirectory>()
            .FirstOrDefault();
        if (exif is not null &&
            exif.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var date))
            return date;

        var xmp = directories.OfType<MetadataExtractor.Formats.Xmp.XmpDirectory>()
            .FirstOrDefault()?.XmpMeta;
        var text = xmp?.GetPropertyString(ExifNamespace, "exif:DateTimeOriginal")
            ?? xmp?.GetPropertyString(XapNamespace, "xmp:CreateDate");
        return DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
    }
}
