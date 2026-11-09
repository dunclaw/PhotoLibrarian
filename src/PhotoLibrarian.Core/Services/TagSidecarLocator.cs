// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Diagnostics;
using XmpCore;

namespace PhotoLibrarian.Core.Services;

/// <summary>
/// Locates the XMP sidecar that holds a photo's portable tag metadata.
/// </summary>
/// <remarks>
/// Formats that can embed metadata (JPEG, TIFF, PNG) only fall back to a sidecar when
/// Windows cannot update the file. Those fallbacks use the unambiguous
/// <c>filename.ext.xmp</c> name so a camera RAW+JPEG pair such as <c>IMG_1.CR3</c> and
/// <c>IMG_1.JPG</c> never share one packet: RAW editors own <c>IMG_1.xmp</c>, and two photos
/// overwriting each other's tag provenance there made every launch re-queue one of them.
/// A legacy <c>filename.xmp</c> fallback is still read for an embeddable photo when it is the
/// only media file with that stem, matching the sidecar migration rule.
/// </remarks>
public static class TagSidecarLocator
{
    private const string DcNamespace = "http://purl.org/dc/elements/1.1/";

    /// <summary>Returns the sidecar path that tag writes should target for the photo.</summary>
    public static string GetWritePath(string imagePath) =>
        EmbeddedMetadataWriter.IsSupported(imagePath)
            ? imagePath + ".xmp"
            : Path.ChangeExtension(imagePath, ".xmp");

    /// <summary>
    /// Returns the existing sidecar whose tag data belongs to the photo, or null when none does.
    /// </summary>
    public static string? ResolveReadPath(string imagePath)
    {
        var writePath = GetWritePath(imagePath);
        if (!EmbeddedMetadataWriter.IsSupported(imagePath))
            return File.Exists(writePath) ? writePath : null;

        // The qualified sidecar may also hold face fallback data; it only takes over tag
        // authority once a tag write has recorded keywords or provenance in it.
        if (File.Exists(writePath) && ContainsTagData(writePath))
            return writePath;

        var legacyPath = Path.ChangeExtension(imagePath, ".xmp");
        if (!File.Exists(legacyPath))
            return null;

        if (HasMediaCompanion(imagePath))
        {
            DebugLog.WriteLine(
                $"[TAGS] Ignoring shared sidecar {legacyPath} for {imagePath}: another media file uses the same name, so its tags cannot be attributed to this photo.");
            return null;
        }

        return legacyPath;
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

    private static bool ContainsTagData(string sidecarPath)
    {
        try
        {
            var xmp = XmpMetaFactory.ParseFromString(File.ReadAllText(sidecarPath));
            return xmp.DoesPropertyExist(DcNamespace, "dc:subject") ||
                xmp.DoesPropertyExist(
                    MetadataWriterService.PhotoLibrarianNamespace,
                    EmbeddedMetadataWriter.PhotoTagMetadataPropertyName);
        }
        catch (Exception exception) when (
            exception is XmpException or IOException or UnauthorizedAccessException)
        {
            // An unreadable qualified sidecar is still this photo's own file; report it as the
            // tag source so readers and writers surface the problem instead of skipping it.
            DebugLog.WriteLine(
                $"[TAGS] Could not inspect tag sidecar {sidecarPath}: {exception.Message}");
            return true;
        }
    }
}
