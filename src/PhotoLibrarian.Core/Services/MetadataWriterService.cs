// Copyright (c) Duncan Lawler. All Rights Reserved.
using XmpCore;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using PhotoLibrarian.Core.Models;
using System.Security.Cryptography;

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
    PendingEditRecipe,
    PhotoVersionFamily
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
    private const string VersionFamilyPropertyName = "plib:VirtualCopies";

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
            MetadataWriteProperty.PhotoVersionFamily =>
                extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jpe", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jfif", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase),
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

    internal static (PhotoVersionRecipeStatus Status, string? Value) ReadVersionFamily(
        string imagePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(imagePath))
            return (PhotoVersionRecipeStatus.SourceMissing, null);

        var values = new List<string>();
        if (SupportsEmbeddedWrite(imagePath, MetadataWriteProperty.PhotoVersionFamily))
        {
            var (readStatus, embedded) = ReadEmbeddedVersionXmp(imagePath, cancellationToken);
            if (readStatus != PhotoVersionRecipeStatus.Success)
                return (readStatus, null);
            if (embedded?.DoesPropertyExist(PhotoLibrarianNamespace, VersionFamilyPropertyName) == true)
            {
                if (embedded.GetProperty(PhotoLibrarianNamespace, VersionFamilyPropertyName).Options.HasQualifiers)
                    return (PhotoVersionRecipeStatus.Unsupported, null);
                values.Add(embedded.GetPropertyString(PhotoLibrarianNamespace, VersionFamilyPropertyName));
            }
        }
        var (sidecarStatus, sidecars) = GetVersionFamilySidecars(imagePath, cancellationToken);
        if (sidecarStatus != PhotoVersionRecipeStatus.Success)
            return (sidecarStatus, null);
        foreach (var path in sidecars)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sidecar = LoadOrCreateSidecar(path);
            if (sidecar.DoesPropertyExist(PhotoLibrarianNamespace, VersionFamilyPropertyName))
            {
                if (sidecar.GetProperty(PhotoLibrarianNamespace, VersionFamilyPropertyName).Options.HasQualifiers)
                    return (PhotoVersionRecipeStatus.Unsupported, null);
                values.Add(sidecar.GetPropertyString(PhotoLibrarianNamespace, VersionFamilyPropertyName));
            }
        }
        if (values.Count == 0)
            return (PhotoVersionRecipeStatus.VersionMissing, null);
        if (values.Count > 1)
            return (PhotoVersionRecipeStatus.Conflict, null);
        if (string.IsNullOrWhiteSpace(values[0]))
            return (PhotoVersionRecipeStatus.InvalidMetadata, null);
        return (PhotoVersionRecipeStatus.Success, values[0]);
    }

    internal static void EnsureLegacyPhotoAccess(
        string imagePath, CancellationToken cancellationToken = default)
    {
        var (status, value) = ReadVersionFamily(imagePath, cancellationToken);
        if (status is PhotoVersionRecipeStatus.VersionMissing or PhotoVersionRecipeStatus.SourceMissing)
            return;
        if (status == PhotoVersionRecipeStatus.Success)
        {
            var familyStatus = PhotoVersionDocument.TryRead(value!, out _);
            if (familyStatus == PhotoVersionRecipeStatus.Success)
                throw new NotSupportedException(
                    "This photo has version-family recipes; an explicit photo-version target is required.");
            throw new InvalidDataException(
                $"The photo-version family is malformed or unsupported ({familyStatus}); legacy access is blocked.");
        }
        throw new InvalidDataException(
            $"The photo-version family could not be verified ({status}); legacy access is blocked.");
    }

    private static (PhotoVersionRecipeStatus Status, IXmpMeta? Xmp) ReadEmbeddedVersionXmp(
        string imagePath, CancellationToken cancellationToken)
    {
        if (IsJpeg(imagePath))
        {
            var status = FaceMetadataStore.TryReadCompleteJpegXmp(imagePath, out var xmp, cancellationToken);
            return (status, xmp);
        }
        var extension = Path.GetExtension(imagePath);
        if (extension.Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase))
            return TiffFamilyXmpReader.Read(imagePath, cancellationToken);

        IReadOnlyList<MetadataExtractor.Directory> directories;
        try
        {
            directories = ImageMetadataReader.ReadMetadata(imagePath);
        }
        catch (MetadataExtractor.ImageProcessingException)
        {
            return (PhotoVersionRecipeStatus.InvalidMetadata, null);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var packets = directories.OfType<MetadataExtractor.Formats.Xmp.XmpDirectory>().ToArray();
        if (packets.Any(packet => packet.HasError || packet.XmpMeta is null))
            return (PhotoVersionRecipeStatus.InvalidMetadata, null);
        if (packets.Length > 1)
            return (PhotoVersionRecipeStatus.Conflict, null);
        return (PhotoVersionRecipeStatus.Success, packets.FirstOrDefault()?.XmpMeta);
    }

    /// <summary>
    /// Writes only to a transaction's private copy. The caller must validate the input identity
    /// and publish the copy with <see cref="FileEditTransaction"/>.
    /// </summary>
    internal static async Task<PhotoVersionRecipeStatus> WriteVersionFamilyOnStagedFileAsync(
        string imagePath, string value, string? expectedValue, CancellationToken cancellationToken = default) =>
        await WriteVersionFamilyOnStagedFileAsync(imagePath, value, expectedValue, false, cancellationToken);

    private static async Task<PhotoVersionRecipeStatus> WriteVersionFamilyOnStagedFileAsync(
        string imagePath, string value, string? expectedValue, bool rekey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var newStatus = PhotoVersionDocument.TryRead(value, out var updated);
        if (newStatus != PhotoVersionRecipeStatus.Success)
            return newStatus;

        // Transactions represent other same-stem media by empty placeholders. Their
        // hashes cannot establish ownership of a shared legacy sidecar.
        var legacySidecar = Path.ChangeExtension(imagePath, ".xmp");
        if (MetadataSidecarLocator.HasMediaCompanion(imagePath) &&
            File.Exists(legacySidecar) &&
            MetadataSidecarLocator.OwnsVersionFamily(LoadOrCreateSidecar(legacySidecar)))
            return PhotoVersionRecipeStatus.Conflict;

        var (status, current) = ReadVersionFamily(imagePath, cancellationToken);
        if (status is not (PhotoVersionRecipeStatus.Success or PhotoVersionRecipeStatus.VersionMissing))
            return status;
        if (!string.Equals(current, expectedValue, StringComparison.Ordinal))
            return PhotoVersionRecipeStatus.Conflict;
        if (current is not null)
        {
            var previousStatus = PhotoVersionDocument.TryRead(current, out var previous);
            if (previousStatus != PhotoVersionRecipeStatus.Success)
                return previousStatus;
            if ((rekey
                    ? previous!.SourceId == updated!.SourceId || previous.MainId == updated.MainId
                    : previous!.SourceId != updated!.SourceId || previous.MainId != updated.MainId) ||
                !string.Equals(previous.PixelBase, updated.PixelBase, StringComparison.Ordinal) ||
                previous.Revision == updated.Revision)
                return PhotoVersionRecipeStatus.Conflict;
        }

        IXmpMeta? completeJpeg = null;
        if (IsJpeg(imagePath))
        {
            var readStatus = FaceMetadataStore.TryReadCompleteJpegXmp(imagePath, out completeJpeg, cancellationToken);
            if (readStatus != PhotoVersionRecipeStatus.Success)
                return readStatus;
        }
        await WriteUsingPolicyAsync(
            imagePath, MetadataWriteProperty.PhotoVersionFamily,
            () => WriteEmbeddedAndMigrateSidecarsAsync(
                imagePath,
                "photo versions",
                () => WriteEmbeddedVersionFamilyAsync(imagePath, value, completeJpeg, cancellationToken),
                () =>
                {
                    var (readStatus, embedded) = ReadEmbeddedVersionXmp(imagePath, CancellationToken.None);
                    return readStatus == PhotoVersionRecipeStatus.Success &&
                        string.Equals(embedded?.GetPropertyString(
                            PhotoLibrarianNamespace, VersionFamilyPropertyName), value, StringComparison.Ordinal);
                },
                MetadataSidecarLocator.OwnsVersionFamily,
                xmp => xmp.DeleteProperty(PhotoLibrarianNamespace, VersionFamilyPropertyName)),
            () => UpdateSidecarAtPathAsync(FindVersionFamilySidecarTarget(imagePath, cancellationToken),
                xmp => xmp.SetProperty(PhotoLibrarianNamespace, VersionFamilyPropertyName, value)));
        var (verified, written) = ReadVersionFamily(imagePath, cancellationToken);
        if (verified != PhotoVersionRecipeStatus.Success ||
            !string.Equals(written, value, StringComparison.Ordinal))
            throw new IOException("The photo-version metadata could not be verified after writing.");
        return PhotoVersionRecipeStatus.Success;
    }

    /// <summary>Rekeys a private copy without changing the original family.</summary>
    internal static async Task<(PhotoVersionRecipeStatus Status, Guid SourceId,
        IReadOnlyDictionary<Guid, Guid>? VariantIds)> RekeyCopiedRawFamilyOnStagedFileAsync(
        string imagePath, string expectedFamily, CancellationToken cancellationToken)
    {
        if (!RawToneMappingService.IsRaw(imagePath))
            return (PhotoVersionRecipeStatus.Unsupported, Guid.Empty, null);
        var (status, current) = ReadVersionFamily(imagePath, cancellationToken);
        if (status != PhotoVersionRecipeStatus.Success)
            return (status, Guid.Empty, null);
        if (!string.Equals(current, expectedFamily, StringComparison.Ordinal))
            return (PhotoVersionRecipeStatus.Conflict, Guid.Empty, null);
        status = PhotoVersionDocument.TryRead(current!, out var document);
        if (status != PhotoVersionRecipeStatus.Success)
            return (status, Guid.Empty, null);
        var identity = await RecognitionSourceIdentity.ComputeAsync(imagePath, cancellationToken);
        if (!string.Equals(identity, document!.PixelBase, StringComparison.Ordinal))
            return (PhotoVersionRecipeStatus.Conflict, Guid.Empty, null);
        foreach (var record in document.GetRecords())
        {
            status = PhotoVersionRecipePacket.TryRead(record.RecipeXmp, isRaw: true, out _);
            if (status != PhotoVersionRecipeStatus.Success)
                return (status, Guid.Empty, null);
        }
        status = document.TryRekey(document.Revision, out var updated, out var ids);
        if (status != PhotoVersionRecipeStatus.Success)
            return (status, Guid.Empty, null);
        status = await WriteVersionFamilyOnStagedFileAsync(
            imagePath, updated!, current, true, cancellationToken);
        if (status != PhotoVersionRecipeStatus.Success)
            return (status, Guid.Empty, null);
        status = PhotoVersionDocument.TryRead(updated!, out var rekeyed);
        return (status, rekeyed?.SourceId ?? Guid.Empty, ids);
    }

    /// <summary>Creates a RAW family on a private transaction copy and migrates its legacy recipe.</summary>
    internal static async Task<(PhotoVersionRecipeStatus Status, PhotoVersionTarget? Copy)>
        CreateRawVersionFamilyOnStagedFileAsync(
            string imagePath, string mainName, string copyName, CancellationToken cancellationToken)
    {
        if (!RawToneMappingService.IsRaw(imagePath))
            return (PhotoVersionRecipeStatus.Unsupported, null);
        if (string.IsNullOrWhiteSpace(mainName) || string.IsNullOrWhiteSpace(copyName))
            return (PhotoVersionRecipeStatus.InvalidMetadata, null);

        var (familyStatus, _) = ReadVersionFamily(imagePath, cancellationToken);
        if (familyStatus == PhotoVersionRecipeStatus.Success)
            return (PhotoVersionRecipeStatus.Conflict, null);
        if (familyStatus != PhotoVersionRecipeStatus.VersionMissing)
            return (familyStatus, null);

        var legacyPath = Path.ChangeExtension(imagePath, ".xmp");
        var qualifiedPath = imagePath + ".xmp";
        // A staged companion is only a placeholder, so the shared legacy packet's
        // ownership cannot be proved from its contents.
        if (MetadataSidecarLocator.HasMediaCompanion(imagePath) && File.Exists(legacyPath))
            return (PhotoVersionRecipeStatus.Conflict, null);

        var recipePaths = new List<string>();
        foreach (var path in new[] { legacyPath, qualifiedPath })
        {
            if (File.Exists(path) && LoadOrCreateSidecar(path).Properties.Any(
                    property => property.Namespace == EditParameters.XmpNamespace))
                recipePaths.Add(path);
        }
        if (recipePaths.Count > 1)
            return (PhotoVersionRecipeStatus.Conflict, null);

        var legacy = recipePaths.Count == 0 ? XmpMetaFactory.Create() : LoadOrCreateSidecar(recipePaths[0]);
        var recipe = legacy.Clone();
        foreach (var schema in recipe.Properties.Select(property => property.Namespace)
            .Where(schema => !string.IsNullOrEmpty(schema) && schema != EditParameters.XmpNamespace)
            .Distinct(StringComparer.Ordinal).ToArray())
            XmpUtils.RemoveProperties(recipe, schema, null, true, false);
        var packet = XmpMetaFactory.SerializeToString(
            recipe, new XmpCore.Options.SerializeOptions()).TrimStart('\uFEFF');
        var status = PhotoVersionRecipePacket.TryRead(packet, isRaw: true, out _);
        if (status != PhotoVersionRecipeStatus.Success)
            return (status, null);

        var identity = await RecognitionSourceIdentity.ComputeAsync(imagePath, cancellationToken);
        var sourceId = Guid.NewGuid();
        var mainId = Guid.NewGuid();
        var copyId = Guid.NewGuid();
        status = PhotoVersionDocument.TryCreate(
            sourceId, identity, mainId, mainName, packet, copyId, copyName, out var family);
        if (status != PhotoVersionRecipeStatus.Success)
            return (status, null);
        status = await WriteVersionFamilyOnStagedFileAsync(imagePath, family!, expectedValue: null, cancellationToken);
        if (status != PhotoVersionRecipeStatus.Success)
            return (status, null);

        if (recipePaths.Count != 0)
        {
            // Reload after writing: the family may have been stored in this same packet.
            legacy = LoadOrCreateSidecar(recipePaths[0]);
            XmpUtils.RemoveProperties(legacy, EditParameters.XmpNamespace, null, true, false);
            SaveSidecar(recipePaths[0], legacy);
            var verified = LoadOrCreateSidecar(recipePaths[0]);
            if (verified.Properties.Any(property => property.Namespace == EditParameters.XmpNamespace))
                throw new IOException("The migrated legacy recipe could not be removed from the staged sidecar.");
        }

        var (verifiedStatus, verifiedFamily) = ReadVersionFamily(imagePath, cancellationToken);
        if (verifiedStatus != PhotoVersionRecipeStatus.Success ||
            !string.Equals(verifiedFamily, family, StringComparison.Ordinal))
            throw new IOException("The migrated photo-version family could not be verified.");
        return (PhotoVersionRecipeStatus.Success, new PhotoVersionTarget(imagePath, sourceId, copyId));
    }

    private static string FindVersionFamilySidecarTarget(string imagePath, CancellationToken cancellationToken)
    {
        var (status, paths) = GetVersionFamilySidecars(imagePath, cancellationToken);
        if (status != PhotoVersionRecipeStatus.Success)
            throw new IOException("The photo-version sidecar ownership changed during the write.");
        foreach (var path in paths)
            if (MetadataSidecarLocator.OwnsVersionFamily(LoadOrCreateSidecar(path)))
                return path;
        return MetadataSidecarLocator.GetVersionFamilySidecarTarget(imagePath);
    }

    private static (PhotoVersionRecipeStatus Status, IReadOnlyList<string> Paths)
        GetVersionFamilySidecars(string imagePath, CancellationToken cancellationToken)
    {
        var paths = MetadataSidecarLocator.GetVersionFamilySidecarPaths(imagePath).ToList();
        if (!MetadataSidecarLocator.HasMediaCompanion(imagePath))
            return (PhotoVersionRecipeStatus.Success, paths);

        var legacy = Path.ChangeExtension(imagePath, ".xmp");
        paths.RemoveAll(path => string.Equals(path, legacy, StringComparison.OrdinalIgnoreCase));
        if (!File.Exists(legacy))
            return (PhotoVersionRecipeStatus.Success, paths);
        var xmp = LoadOrCreateSidecar(legacy);
        if (!MetadataSidecarLocator.OwnsVersionFamily(xmp))
            return (PhotoVersionRecipeStatus.Success, paths);

        var value = xmp.GetPropertyString(PhotoLibrarianNamespace, VersionFamilyPropertyName);
        var status = PhotoVersionDocument.TryRead(value, out var document);
        if (status != PhotoVersionRecipeStatus.Success ||
            !document!.PixelBase.StartsWith("file-v1:", StringComparison.Ordinal))
            return (PhotoVersionRecipeStatus.Conflict, []);

        var directory = Path.GetDirectoryName(Path.GetFullPath(imagePath))!;
        var stem = Path.GetFileNameWithoutExtension(imagePath);
        var matches = new List<string>();
        foreach (var candidate in System.IO.Directory.EnumerateFiles(directory, stem + ".*"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!RawToneMappingService.IsRaw(candidate) ||
                !string.Equals(Path.GetFileNameWithoutExtension(candidate), stem,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            using var stream = File.OpenRead(candidate);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            int count;
            while ((count = stream.Read(buffer)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(buffer.AsSpan(0, count));
            }
            cancellationToken.ThrowIfCancellationRequested();
            var identity = "file-v1:" + Convert.ToHexString(hash.GetHashAndReset());
            if (string.Equals(identity, document.PixelBase, StringComparison.Ordinal))
                matches.Add(Path.GetFullPath(candidate));
        }
        if (matches.Count != 1)
            return (PhotoVersionRecipeStatus.Conflict, []);
        if (string.Equals(matches[0], Path.GetFullPath(imagePath), StringComparison.OrdinalIgnoreCase))
            paths.Add(legacy);
        return (PhotoVersionRecipeStatus.Success, paths);
    }

    private static Task WriteEmbeddedVersionFamilyAsync(
        string imagePath, string value, IXmpMeta? completeJpeg, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(imagePath);
        if (extension.Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase))
            return EmbeddedMetadataWriter.WriteVersionFamilyAsync(imagePath, value, cancellationToken);

        var xmp = (IsJpeg(imagePath) ? completeJpeg : ReadEmbeddedXmp(imagePath))
            ?? XmpMetaFactory.Create();
        xmp.SetProperty(PhotoLibrarianNamespace, VersionFamilyPropertyName, value);
        var packet = XmpMetaFactory.SerializeToString(
            xmp, new XmpCore.Options.SerializeOptions()).TrimStart('\uFEFF');
        if (!FaceMetadataStore.TryWriteEmbedded(imagePath, packet, cancellationToken: cancellationToken))
            throw new IOException($"The embedded photo-version writer rejected {imagePath}.");
        return Task.CompletedTask;
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
                () => WriteEmbeddedFlagAsync(imagePath, flagged),
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

    private static Task WriteEmbeddedFlagAsync(string imagePath, bool flagged)
    {
        if (TryWriteEmbeddedFamilyJpeg(imagePath,
            xmp => xmp.SetProperty(PhotoLibrarianNamespace, FlagPropertyName, flagged ? "True" : "False")))
            return Task.CompletedTask;
        return EmbeddedMetadataWriter.WriteAsync(imagePath, flagged: flagged);
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
                () => WriteEmbeddedColorLabelAsync(imagePath, label ?? string.Empty),
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

    private static Task WriteEmbeddedColorLabelAsync(string imagePath, string label)
    {
        if (TryWriteEmbeddedFamilyJpeg(imagePath, xmp => xmp.SetProperty(XapNamespace, "xmp:Label", label)))
            return Task.CompletedTask;
        return EmbeddedMetadataWriter.WriteAsync(imagePath, colorLabel: label);
    }

    private static bool TryWriteEmbeddedFamilyJpeg(string imagePath, Action<IXmpMeta> update)
    {
        if (IsJpeg(imagePath))
        {
            var status = FaceMetadataStore.TryReadCompleteJpegXmp(imagePath, out var xmp);
            if (status != PhotoVersionRecipeStatus.Success)
                throw new IOException($"The embedded JPEG metadata cannot be safely read ({status}): {imagePath}.");
            if (xmp?.DoesPropertyExist(PhotoLibrarianNamespace, VersionFamilyPropertyName) == true)
            {
                update(xmp);
                var packet = XmpMetaFactory.SerializeToString(
                    xmp, new XmpCore.Options.SerializeOptions()).TrimStart('\uFEFF');
                if (!FaceMetadataStore.TryWriteEmbedded(imagePath, packet))
                    throw new IOException($"The embedded metadata writer rejected {imagePath}.");
                return true;
            }
        }
        return false;
    }

    private static bool IsJpeg(string imagePath)
    {
        var extension = Path.GetExtension(imagePath);
        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jpe", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jfif", StringComparison.OrdinalIgnoreCase);
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
            UpdateSidecar(sidecarPath, apply);
            return sidecarPath;
        });

    private static Task<string> UpdateSidecarAtPathAsync(string sidecarPath, Action<IXmpMeta> apply) =>
        Task.Run(() =>
        {
            UpdateSidecar(sidecarPath, apply);
            return sidecarPath;
        });

    private static void UpdateSidecar(string sidecarPath, Action<IXmpMeta> apply)
    {
        var snapshot = TagWriterService.CaptureSidecarSnapshotAt(sidecarPath);
        var xmp = LoadOrCreateSidecar(sidecarPath);
        if (TagWriterService.CaptureSidecarSnapshotAt(sidecarPath) != snapshot)
            throw new IOException($"The XMP sidecar changed during metadata editing: {sidecarPath}.");
        apply(xmp);
        var serialized = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
        TagWriterService.WriteOwnedSidecarAtomically(sidecarPath, serialized, snapshot);
    }

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
            var snapshot = TagWriterService.CaptureSidecarSnapshotAt(path);
            var xmp = await Task.Run(() => LoadOrCreateSidecar(path));
            if (TagWriterService.CaptureSidecarSnapshotAt(path) != snapshot)
                throw new IOException($"The XMP sidecar changed while metadata was being read: {path}.");
            if (ownsProperty(xmp))
                sidecars.Add((path, xmp, snapshot));
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
            await Task.Run(() => TagWriterService.WriteOwnedSidecarAtomically(path, serialized, snapshot));
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
