// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Diagnostics;
using XmpCore;

namespace PhotoLibrarian.Core.Services;

/// <summary>
/// Locates the XMP sidecar that holds a photo's portable sidecar metadata (tags, rating,
/// flag, caption, capture date and GPS).
/// </summary>
/// <remarks>
/// Formats that can embed metadata (JPEG, TIFF, PNG) only fall back to a sidecar when
/// Windows cannot update the file. When another media file shares the stem, those fallbacks
/// use the unambiguous <c>filename.ext.xmp</c> name so a camera RAW+JPEG pair such as
/// <c>IMG_1.CR3</c> and <c>IMG_1.JPG</c> never share one packet: the RAW file owns
/// <c>IMG_1.xmp</c>, and two photos overwriting each other's metadata there mixed their values
/// and made every launch re-queue one of them. A lone embeddable photo keeps using its legacy
/// <c>filename.xmp</c>, matching the sidecar migration rule. Tag fallbacks always use the
/// qualified name, which also holds face-region fallbacks.
/// Ownership is resolved per property, so a photo keeps updating whichever sidecar already
/// holds a value instead of leaving a stale copy behind.
/// </remarks>
public static class MetadataSidecarLocator
{
    private const string DcNamespace = "http://purl.org/dc/elements/1.1/";
    private const string XapNamespace = "http://ns.adobe.com/xap/1.0/";
    private const string ExifNamespace = "http://ns.adobe.com/exif/1.0/";

    /// <summary>Matches sidecars that record the photo's keywords or tag provenance.</summary>
    public static bool OwnsTags(IXmpMeta xmp) =>
        xmp.DoesPropertyExist(DcNamespace, "dc:subject") ||
        xmp.DoesPropertyExist(
            MetadataWriterService.PhotoLibrarianNamespace,
            EmbeddedMetadataWriter.PhotoTagMetadataPropertyName);

    /// <summary>Matches sidecars that record a star rating.</summary>
    public static bool OwnsRating(IXmpMeta xmp) => xmp.DoesPropertyExist(XapNamespace, "xmp:Rating");

    /// <summary>Matches sidecars that record the PhotoLibrarian flag.</summary>
    public static bool OwnsFlag(IXmpMeta xmp) =>
        xmp.DoesPropertyExist(MetadataWriterService.PhotoLibrarianNamespace, "plib:Flagged");

    /// <summary>Matches sidecars that record a caption.</summary>
    public static bool OwnsCaption(IXmpMeta xmp) => xmp.DoesPropertyExist(DcNamespace, "dc:description");

    /// <summary>Matches sidecars that record a capture date.</summary>
    public static bool OwnsDateTaken(IXmpMeta xmp) =>
        xmp.DoesPropertyExist(XapNamespace, "xmp:CreateDate") ||
        xmp.DoesPropertyExist(ExifNamespace, "exif:DateTimeOriginal");

    /// <summary>Matches sidecars that record GPS coordinates.</summary>
    public static bool OwnsGps(IXmpMeta xmp) =>
        xmp.DoesPropertyExist(ExifNamespace, "exif:GPSLatitude") ||
        xmp.DoesPropertyExist(ExifNamespace, "exif:GPSLongitude");

    /// <summary>
    /// Returns the photo's own qualified sidecar path (<c>filename.ext.xmp</c> for embeddable
    /// formats, <c>filename.xmp</c> otherwise), used for tag and face-region fallbacks.
    /// </summary>
    public static string GetWritePath(string imagePath) =>
        EmbeddedMetadataWriter.IsSupported(imagePath)
            ? imagePath + ".xmp"
            : Path.ChangeExtension(imagePath, ".xmp");

    /// <summary>
    /// Returns the existing sidecar whose value for a property belongs to the photo, or null
    /// when none does.
    /// </summary>
    public static string? ResolveReadPath(string imagePath, Func<IXmpMeta, bool> ownsProperty)
    {
        var writePath = GetWritePath(imagePath);
        if (!EmbeddedMetadataWriter.IsSupported(imagePath))
            return File.Exists(writePath) ? writePath : null;

        // The qualified sidecar may also hold face or other fallback data; it only takes over
        // a property once a write has recorded that property in it.
        if (File.Exists(writePath) && SidecarOwns(writePath, ownsProperty))
            return writePath;

        var legacyPath = GetLegacyPathIfOwned(imagePath);
        return legacyPath;
    }

    /// <summary>
    /// Returns the sidecar that a property write should update: the sidecar already holding the
    /// property when one can be attributed to the photo; otherwise the legacy
    /// <c>filename.xmp</c> for a lone photo, or <c>filename.ext.xmp</c> when another media file
    /// shares the stem and owns <c>filename.xmp</c>.
    /// </summary>
    public static string ResolveTargetPath(string imagePath, Func<IXmpMeta, bool> ownsProperty)
    {
        if (ResolveReadPath(imagePath, ownsProperty) is { } existingPath)
            return existingPath;

        return HasMediaCompanion(imagePath)
            ? GetWritePath(imagePath)
            : Path.ChangeExtension(imagePath, ".xmp");
    }

    /// <summary>
    /// Returns every existing sidecar attributable to the photo, most authoritative first.
    /// A shared legacy sidecar in a RAW+JPEG pair belongs to the RAW file and is excluded.
    /// </summary>
    public static IReadOnlyList<string> GetOwnedSidecarPaths(string imagePath)
    {
        var paths = new List<string>(2);
        var writePath = GetWritePath(imagePath);
        if (File.Exists(writePath))
            paths.Add(writePath);
        if (EmbeddedMetadataWriter.IsSupported(imagePath) && GetLegacyPathIfOwned(imagePath) is { } legacy)
            paths.Add(legacy);
        return paths;
    }

    /// <summary>
    /// Returns true when another supported media file shares the photo's file-name stem,
    /// which makes a legacy <c>filename.xmp</c> sidecar ambiguous.
    /// </summary>
    internal static bool HasMediaCompanion(string imagePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(imagePath));
        if (directory is null || !Directory.Exists(directory))
            return false;

        var stem = Path.GetFileNameWithoutExtension(imagePath);
        var fullImagePath = Path.GetFullPath(imagePath);
        foreach (var candidate in Directory.EnumerateFiles(directory, stem + ".*"))
        {
            if (string.Equals(candidate, fullImagePath, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.Equals(
                    Path.GetFileNameWithoutExtension(candidate),
                    stem,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            if (FolderScannerService.IsSupportedFile(candidate))
                return true;
        }

        return false;
    }

    private static string? GetLegacyPathIfOwned(string imagePath)
    {
        var legacyPath = Path.ChangeExtension(imagePath, ".xmp");
        if (!File.Exists(legacyPath))
            return null;

        if (HasMediaCompanion(imagePath))
        {
            DebugLog.WriteLine(
                $"[METADATA] Ignoring shared sidecar {legacyPath} for {imagePath}: another media file uses the same name, so its metadata belongs to that file.");
            return null;
        }

        return legacyPath;
    }

    private static bool SidecarOwns(string sidecarPath, Func<IXmpMeta, bool> ownsProperty)
    {
        try
        {
            var xmp = XmpMetaFactory.ParseFromString(File.ReadAllText(sidecarPath));
            return ownsProperty(xmp);
        }
        catch (Exception exception) when (
            exception is XmpException or IOException or UnauthorizedAccessException)
        {
            // An unreadable qualified sidecar is still this photo's own file; report it as the
            // source so readers and writers surface the problem instead of skipping it.
            DebugLog.WriteLine(
                $"[METADATA] Could not inspect sidecar {sidecarPath}: {exception.Message}");
            return true;
        }
    }
}
