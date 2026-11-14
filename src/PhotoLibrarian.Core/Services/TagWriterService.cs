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
        string? sourceIdentity = null,
        string? formatPath = null)
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
        EnsureSidecarSnapshot(imagePath, expectedSidecarSnapshot);
        await MetadataWriterService.WriteUsingPolicyAsync(
            imagePath,
            MetadataWriteProperty.Tags,
            () => WriteEmbeddedAsync(
                imagePath,
                distinct,
                portableMetadata,
                expectedSidecarSnapshot,
                formatPath),
            () => WriteSidecarAsync(
                imagePath,
                distinct,
                portableMetadata,
                expectedSidecarSnapshot),
            policyPath: formatPath);
    }

    private static async Task WriteEmbeddedAsync(
        string imagePath,
        IReadOnlyList<string> tags,
        string? portableMetadata,
        TagMetadataSidecarSnapshot? expectedSidecarSnapshot,
        string? formatPath)
    {
        var ownedSidecars = new List<(string Path, IXmpMeta Xmp, TagMetadataSidecarSnapshot Snapshot)>();
        string? sidecarMetadata = null;
        foreach (var path in MetadataSidecarLocator.GetOwnedSidecarPaths(imagePath))
        {
            if (!File.Exists(path)) continue;
            var snapshot = CaptureSidecarSnapshotAt(path);
            IXmpMeta xmp;
            try
            {
                xmp = XmpMetaFactory.ParseFromString(await File.ReadAllTextAsync(path));
            }
            catch (Exception exception)
            {
                throw new IOException($"The existing XMP sidecar could not be read: {path}.", exception);
            }
            if (!MetadataSidecarLocator.OwnsTags(xmp)) continue;

            var metadataValue = xmp.GetPropertyString(
                PhotoLibrarianNamespace,
                EmbeddedMetadataWriter.PhotoTagMetadataPropertyName);
            if (metadataValue is not null && sidecarMetadata is not null &&
                !string.Equals(metadataValue, sidecarMetadata, StringComparison.Ordinal))
                throw new IOException("Conflicting portable tag metadata exists in multiple owned sidecars.");
            sidecarMetadata ??= metadataValue;
            ownedSidecars.Add((path, xmp, snapshot));
        }

        var metadataToWrite = portableMetadata ?? sidecarMetadata;
        await EmbeddedMetadataWriter.WriteAsync(
            imagePath,
            keywords: tags,
            photoTagMetadata: metadataToWrite,
            formatExtension: Path.GetExtension(formatPath ?? imagePath));

        var formatExtension = Path.GetExtension(formatPath ?? imagePath);
        var actualTags = await EmbeddedMetadataWriter.ReadKeywordsAsync(imagePath, formatExtension);
        if (!actualTags.Order(StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(tags.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            throw new IOException("The embedded keywords could not be verified after writing.");
        if (metadataToWrite is not null &&
            !HasMatchingPortableMetadata(imagePath, metadataToWrite))
            throw new IOException("The photo did not retain the portable tag metadata.");

        EnsureSidecarSnapshot(imagePath, expectedSidecarSnapshot);
        foreach (var (path, xmp, snapshot) in ownedSidecars)
        {
            xmp.DeleteProperty(DcNamespace, "dc:subject");
            xmp.DeleteProperty(
                PhotoLibrarianNamespace,
                EmbeddedMetadataWriter.PhotoTagMetadataPropertyName);
            WriteOwnedSidecarAtomically(path,
                XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions()),
                snapshot);
        }
    }

    private static Task WriteSidecarAsync(
        string imagePath,
        IReadOnlyList<string> tags,
        string? portableMetadata,
        TagMetadataSidecarSnapshot? expectedSidecarSnapshot) =>
        Task.Run(() =>
        {
            var sidecarPath = GetTagSidecarTargetPath(imagePath);
            EnsureSidecarSnapshot(imagePath, expectedSidecarSnapshot);
            var xmp = LoadSidecar(sidecarPath);
            xmp.DeleteProperty(DcNamespace, "dc:subject");
            xmp.SetProperty(DcNamespace, "dc:subject", null,
                new XmpCore.Options.PropertyOptions { IsArray = true, IsArrayOrdered = false });
            foreach (var tag in tags)
            {
                xmp.AppendArrayItem(DcNamespace, "dc:subject",
                    new XmpCore.Options.PropertyOptions { IsArray = true, IsArrayOrdered = false },
                    tag, null);
            }
            if (portableMetadata is not null)
            {
                xmp.SetProperty(PhotoLibrarianNamespace,
                    EmbeddedMetadataWriter.PhotoTagMetadataPropertyName, portableMetadata);
            }

            var serialized = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
            WriteSidecarAtomically(imagePath, sidecarPath, serialized, expectedSidecarSnapshot);
        });

    /// <summary>
    /// Returns the sidecar that tag writes for this photo update: the sidecar already holding
    /// its tags when one can be attributed to it, otherwise the format's preferred sidecar.
    /// </summary>
    public static string GetTagSidecarTargetPath(string imagePath) =>
        MetadataSidecarLocator.ResolveReadPath(imagePath, MetadataSidecarLocator.OwnsTags) ??
        MetadataSidecarLocator.GetWritePath(imagePath);

    /// <summary>
    /// Reads existing dc:subject keywords from an XMP sidecar (RAW fallback path).
    /// For supported formats, use <see cref="EmbeddedMetadataWriter.ReadKeywordsAsync"/> instead.
    /// </summary>
    public static List<string> ReadTagsFromSidecar(string imagePath)
    {
        var tags = new List<string>();
        var sidecarPath = MetadataSidecarLocator.ResolveReadPath(imagePath, MetadataSidecarLocator.OwnsTags);
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
        string imagePath) =>
        CaptureSidecarSnapshotAt(GetTagSidecarTargetPath(imagePath));

    /// <summary>Captures the size, timestamp and content hash of a specific sidecar file.</summary>
    public static TagMetadataSidecarSnapshot CaptureSidecarSnapshotAt(string sidecarPath)
    {
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
        string imagePath,
        string expectedJson)
    {
        var actual = MetadataReaderService.ReadPortableTagMetadata(
            FaceMetadataStore.ReadEmbeddedXmp(imagePath));
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

    private static IXmpMeta LoadSidecar(string sidecarPath)
    {
        if (!File.Exists(sidecarPath)) return XmpMetaFactory.Create();
        try
        {
            return XmpMetaFactory.ParseFromString(File.ReadAllText(sidecarPath));
        }
        catch (Exception exception)
        {
            throw new IOException($"The existing XMP sidecar could not be read: {sidecarPath}.", exception);
        }
    }

    private static void WriteOwnedSidecarAtomically(
        string sidecarPath,
        string contents,
        TagMetadataSidecarSnapshot expected)
    {
        if (CaptureSidecarSnapshotAt(sidecarPath) != expected)
            throw new IOException("The XMP sidecar changed while tags were being migrated.");
        var directory = Path.GetDirectoryName(sidecarPath)
            ?? throw new InvalidOperationException("The XMP sidecar path has no parent directory.");
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(sidecarPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, contents);
            if (CaptureSidecarSnapshotAt(sidecarPath) != expected)
                throw new IOException("The XMP sidecar changed while tags were being migrated.");
            File.Move(temporaryPath, sidecarPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}

public sealed record TagMetadataSidecarSnapshot(
    bool Exists,
    long Length,
    DateTime LastWriteTimeUtc,
    string? ContentHash);
