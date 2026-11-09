// Copyright (c) Duncan Lawler. All Rights Reserved.
using XmpCore;
using PhotoLibrarian.Core.Models;
using System.Security.Cryptography;

namespace PhotoLibrarian.Core.Services;

/// <summary>
/// Writes the full keyword set into image files. Prefers in-file (System.Keywords →
/// XMP dc:subject + EXIF XPKeywords) via WIC for supported formats, falls back to XMP
/// sidecar for RAW formats.
/// </summary>
public static class TagWriterService
{
    private const string DcNamespace = "http://purl.org/dc/elements/1.1/";
    private const string PhotoLibrarianNamespace =
        EmbeddedMetadataWriter.PhotoLibrarianXmpNamespace;

    static TagWriterService()
    {
        try { XmpMetaFactory.SchemaRegistry.RegisterNamespace(PhotoLibrarianNamespace, "plib"); }
        catch { }
    }

    /// <summary>
    /// Writes the complete tag set to the image file. Pass an empty enumeration to clear all tags.
    /// </summary>
    public static async Task WriteTagsToSidecarAsync(
        string imagePath,
        IEnumerable<string> tags,
        IReadOnlyCollection<ImageTag>? tagProvenance = null,
        string? autoTagScanVersion = null,
        TagMetadataSidecarSnapshot? expectedSidecarSnapshot = null,
        string? sourceIdentity = null)
    {
        var distinct = tags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var portableMetadata = tagProvenance is null
            ? null
            : PortableImageTagMetadataSerializer.Serialize(
                tagProvenance,
                autoTagScanVersion,
                sourceIdentity);
        Exception? embeddedError = null;
        EnsureSidecarSnapshot(imagePath, expectedSidecarSnapshot);

        var reader = new MetadataReaderService();
        // A portable fallback packet remains authoritative until migrated into
        // the image; don't leave it stale by switching back to embedded writes.
        if (EmbeddedMetadataWriter.IsSupported(imagePath) &&
            reader.ReadPortableTagSidecarMetadata(imagePath) is null)
        {
            try
            {
                await EmbeddedMetadataWriter.WriteAsync(
                    imagePath,
                    keywords: distinct,
                    photoTagMetadata: portableMetadata);
                if (portableMetadata is not null &&
                    !HasMatchingPortableMetadata(reader, imagePath, portableMetadata))
                    throw new IOException("The photo did not retain the portable tag metadata.");
                return;
            }
            catch (Exception ex)
            {
                embeddedError = ex;
                Diagnostics.DebugLog.WriteLine(
                    $"[TAGS] Embedded keyword write failed for {imagePath}: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}. Falling back to sidecar.");
            }
        }

        // RAW fallback: write XMP sidecar
        var sidecarPath = await Task.Run(() =>
        {
            var sidecarPath = GetTagSidecarTargetPath(imagePath);
            EnsureSidecarSnapshot(imagePath, expectedSidecarSnapshot);
            IXmpMeta xmp;
            try
            {
                xmp = File.Exists(sidecarPath)
                    ? XmpMetaFactory.ParseFromString(File.ReadAllText(sidecarPath))
                    : XmpMetaFactory.Create();
            }
            catch (Exception exception)
            {
                throw new IOException(
                    $"The existing XMP sidecar could not be read: {sidecarPath}.",
                    exception);
            }

            try { xmp.DeleteProperty(DcNamespace, "dc:subject"); } catch { }

            // An explicit (possibly empty) keyword bag records that this sidecar owns the
            // photo's tags, so clearing every tag cannot resurrect stale embedded keywords.
            xmp.SetProperty(DcNamespace, "dc:subject", null,
                new XmpCore.Options.PropertyOptions { IsArray = true, IsArrayOrdered = false });
            foreach (var tag in distinct)
            {
                xmp.AppendArrayItem(DcNamespace, "dc:subject",
                    new XmpCore.Options.PropertyOptions { IsArray = true, IsArrayOrdered = false },
                    tag, null);
            }

            if (portableMetadata is not null)
            {
                xmp.SetProperty(
                    PhotoLibrarianNamespace,
                    EmbeddedMetadataWriter.PhotoTagMetadataPropertyName,
                    portableMetadata);
            }

            var serialized = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
            WriteSidecarAtomically(
                imagePath,
                sidecarPath,
                serialized,
                expectedSidecarSnapshot);
            return sidecarPath;
        });
        if (embeddedError is not null)
            MetadataWriterService.ReportSidecarFallback(imagePath, "tags", embeddedError, sidecarPath);
    }

    /// <summary>
    /// Returns the sidecar that tag writes for this photo update: the sidecar already holding
    /// its tags when one can be attributed to it, otherwise the format's preferred sidecar.
    /// </summary>
    public static string GetTagSidecarTargetPath(string imagePath) =>
        TagSidecarLocator.ResolveReadPath(imagePath) ??
        TagSidecarLocator.GetWritePath(imagePath);

    /// <summary>
    /// Reads existing dc:subject keywords from an XMP sidecar (RAW fallback path).
    /// For supported formats, use <see cref="EmbeddedMetadataWriter.ReadKeywordsAsync"/> instead.
    /// </summary>
    public static List<string> ReadTagsFromSidecar(string imagePath)
    {
        var tags = new List<string>();
        var sidecarPath = TagSidecarLocator.ResolveReadPath(imagePath);
        if (sidecarPath is null) return tags;

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

    public static TagMetadataSidecarSnapshot CaptureSidecarSnapshot(
        string imagePath)
    {
        var sidecarPath = GetTagSidecarTargetPath(imagePath);
        if (!File.Exists(sidecarPath))
            return new TagMetadataSidecarSnapshot(false, 0, default, null);

        using var stream = new FileStream(
            sidecarPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        return new TagMetadataSidecarSnapshot(
            true,
            stream.Length,
            File.GetLastWriteTimeUtc(sidecarPath),
            hash);
    }

    private static bool HasMatchingPortableMetadata(
        MetadataReaderService reader,
        string imagePath,
        string expectedJson)
    {
        var actual = reader.ReadPortableTagMetadata(imagePath);
        if (actual is null ||
            !PortableImageTagMetadataSerializer.TryDeserialize(expectedJson, out var expected) ||
            expected is null)
            return false;

        return actual.AutoTagScanVersion == expected.AutoTagScanVersion &&
            actual.SourceIdentity == expected.SourceIdentity &&
            actual.Tags.SequenceEqual(expected.Tags);
    }

    private static void EnsureSidecarSnapshot(
        string imagePath,
        TagMetadataSidecarSnapshot? expected)
    {
        if (expected is null)
            return;

        var current = CaptureSidecarSnapshot(imagePath);
        if (current != expected)
        {
            throw new IOException(
                "The photo's XMP sidecar changed during automatic tagging. " +
                "Refresh the library and retry so newer metadata is preserved.");
        }
    }

    private static void WriteSidecarAtomically(
        string imagePath,
        string sidecarPath,
        string contents,
        TagMetadataSidecarSnapshot? expected)
    {
        var directory = Path.GetDirectoryName(sidecarPath)
            ?? throw new InvalidOperationException(
                "The XMP sidecar path has no parent directory.");
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(sidecarPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, contents);
            EnsureSidecarSnapshot(imagePath, expected);
            if (expected is { Exists: false })
                File.Move(temporaryPath, sidecarPath);
            else
                File.Move(temporaryPath, sidecarPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}

public sealed record TagMetadataSidecarSnapshot(
    bool Exists,
    long Length,
    DateTime LastWriteTimeUtc,
    string? ContentHash);
