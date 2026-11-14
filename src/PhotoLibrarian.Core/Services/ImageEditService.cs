// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;
using XmpCore;
using System.Text.Json;

namespace PhotoLibrarian.Core.Services;

/// <summary>
/// Applies image edits to a file: backs the original up, hands the pixel rendering to the
/// platform-specific renderer supplied by the caller, and keeps the portable edit recipe in sync.
///
/// The rendering itself lives in the UI layer because it needs Win2D, which this assembly does
/// not reference — see <c>PhotoLibrarian.Services.ImageEditRenderer</c>.
/// </summary>
public sealed class ImageEditService
{
    private readonly OriginalBackupService _backupService;
    private static readonly SemaphoreSlim RawRecipeWrites = new(1, 1);

    /// <summary>
    /// Bakes <paramref name="parameters"/> into the pixels of the file at the given path,
    /// returning the new pixel dimensions.
    /// </summary>
    public delegate Task<(uint Width, uint Height)> PixelRenderer(string filePath, EditParameters parameters);

    public ImageEditService(OriginalBackupService backupService)
    {
        _backupService = backupService;
    }

    /// <summary>
    /// Applies the edits to the file on disk. The original is backed up first (no-op when a
    /// backup already exists), then <paramref name="renderer"/> bakes the adjustments into the
    /// pixels, and finally any pending edit parameters are cleared from portable metadata — the
    /// values now live in the pixels, so keeping them would double-apply on the next open.
    /// Returns the new pixel dimensions of the file.
    /// </summary>
    public async Task<(uint Width, uint Height)> ApplyEditsAsync(
        string filePath, EditParameters parameters, PixelRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        if (RawToneMappingService.IsRaw(filePath))
            throw new NotSupportedException("Overwriting RAW originals is not supported. Use Export SDR derivative.");

        await _backupService.BackupOriginalAsync(filePath);
        var size = await renderer(filePath, parameters);
        await ClearPendingEditParametersAsync(filePath);
        return size;
    }

    /// <summary>
    /// Saves edit parameters as pending (not yet rendered) edits in embedded XMP when supported,
    /// or in an owned sidecar when the image format cannot store them. Backs up the original first.
    /// </summary>
    public async Task SaveEditParametersAsync(string filePath, EditParameters parameters)
    {
        // Backup original first
        await _backupService.BackupOriginalAsync(filePath);
        await SavePendingRecipeAtomicallyAsync(filePath, parameters);
    }

    /// <summary>Writes a recipe on a transaction's private copy; the caller owns original backup.</summary>
    public static Task WritePendingRecipeAsync(string filePath, EditParameters parameters) =>
        SavePendingRecipeAtomicallyAsync(filePath, parameters);

    /// <summary>Atomically keeps a portable RAW preview recipe without touching sensor bytes.</summary>
    public static async Task SaveRawPreviewRecipeAsync(string filePath, EditParameters parameters,
        CancellationToken cancellationToken = default)
    {
        if (!RawToneMappingService.IsRaw(filePath))
            throw new NotSupportedException("A RAW preview recipe requires a RAW source.");
        RawToneMappingService.ValidateRecipe(parameters);
        await SavePendingRecipeAtomicallyAsync(filePath, parameters, cancellationToken);
    }

    private static async Task SavePendingRecipeAtomicallyAsync(string filePath, EditParameters parameters,
        CancellationToken cancellationToken = default)
    {
        var snapshot = parameters.Clone();
        await RawRecipeWrites.WaitAsync(cancellationToken);
        try
        {
            await MetadataWriterService.WriteUsingPolicyAsync(
                filePath,
                MetadataWriteProperty.PendingEditRecipe,
                () => Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteEmbeddedRecipe(filePath, snapshot);
                }, cancellationToken),
                () => Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteSidecarRecipe(
                        filePath,
                        snapshot,
                        keepBackup: RawToneMappingService.IsRaw(filePath));
                }, cancellationToken));
        }
        finally { RawRecipeWrites.Release(); }
    }

    /// <summary>
    /// Strips PhotoLibrarian's edit properties from the embedded packet or owned sidecars, leaving
    /// any unrelated XMP content (a Lightroom sidecar, for example) alone.
    /// </summary>
    public static Task ClearPendingEditParametersAsync(string filePath) =>
        MetadataWriterService.WriteUsingPolicyAsync(
            filePath,
            MetadataWriteProperty.PendingEditRecipe,
            () => Task.Run(() => ClearEmbeddedRecipe(filePath)),
            () => Task.Run(() => ClearSidecarRecipe(filePath)));

    /// <summary>
    /// Reads pending edit parameters from the photo when supported, otherwise from its owned sidecar.
    /// </summary>
    public static EditParameters ReadEditParameters(string filePath)
    {
        var sidecar = MetadataSidecarLocator.ResolveReadPath(
            filePath, MetadataSidecarLocator.OwnsPendingEditRecipe);
        var xmp = sidecar is not null
            ? LoadRecipeSidecar(sidecar)
            : MetadataWriterService.SupportsEmbeddedWrite(
                filePath, MetadataWriteProperty.PendingEditRecipe)
                ? LoadEmbeddedXmp(filePath) ?? XmpMetaFactory.Create()
                : XmpMetaFactory.Create();
        if (RawToneMappingService.IsRaw(filePath) &&
            xmp.DoesPropertyExist(EditParameters.XmpNamespace, "pledit:RawToneMapping") &&
            xmp.GetPropertyString(EditParameters.XmpNamespace, "pledit:RawToneMapping") != RawToneMappingService.RecipeVersion)
            throw new NotSupportedException("The RAW preview recipe uses an unsupported tone-mapping version.");
        if (RawToneMappingService.IsRaw(filePath) &&
            xmp.DoesPropertyExist(EditParameters.XmpNamespace, "pledit:RawDevelopment") &&
            xmp.GetPropertyString(EditParameters.XmpNamespace, "pledit:RawDevelopment") != RawDevelopmentService.RecipeVersion)
            throw new NotSupportedException("The RAW recipe uses an unsupported development version.");
        var parameters = EditParametersSerializer.ReadFromXmp(xmp);
        if (RawToneMappingService.IsRaw(filePath)) RawToneMappingService.ValidateRecipe(parameters);
        return parameters;
    }

    private static void WriteEmbeddedRecipe(string filePath, EditParameters parameters)
    {
        var sidecars = LoadOwnedRecipeSidecars(filePath);
        var xmp = LoadEmbeddedXmp(filePath) ?? XmpMetaFactory.Create();
        EditParametersSerializer.WriteToXmp(xmp, parameters);
        var packet = XmpMetaFactory.SerializeToString(
            xmp, new XmpCore.Options.SerializeOptions()).TrimStart('\uFEFF');
        if (!FaceMetadataStore.TryWriteEmbedded(filePath, packet))
            throw new IOException($"The embedded edit-recipe writer rejected {filePath}.");

        var written = LoadEmbeddedXmp(filePath)
            ?? throw new IOException("The embedded edit recipe could not be read after writing.");
        if (!RecipesMatch(parameters, EditParametersSerializer.ReadFromXmp(written)))
            throw new IOException("The embedded edit recipe could not be verified after writing.");

        foreach (var (path, sidecar) in sidecars)
        {
            EditParametersSerializer.ClearFromXmp(sidecar, includeRawParameters: false);
            SaveRecipeSidecar(path, sidecar);
        }
    }

    private static void WriteSidecarRecipe(
        string filePath,
        EditParameters parameters,
        bool keepBackup = false)
    {
        var xmp = LoadOrCreateRecipeSidecar(filePath);
        EditParametersSerializer.WriteToXmp(xmp, parameters);
        SaveRecipeSidecar(GetRecipeSidecarPath(filePath), xmp, keepBackup);
    }

    private static void ClearEmbeddedRecipe(string filePath)
    {
        var sidecars = LoadOwnedRecipeSidecars(filePath);
        var xmp = LoadEmbeddedXmp(filePath);
        if (xmp is not null)
        {
            EditParametersSerializer.ClearFromXmp(xmp, includeRawParameters: false);
            var packet = XmpMetaFactory.SerializeToString(
                xmp, new XmpCore.Options.SerializeOptions()).TrimStart('\uFEFF');
            if (!FaceMetadataStore.TryWriteEmbedded(filePath, packet))
                throw new IOException($"The embedded edit-recipe writer rejected {filePath}.");
            var written = LoadEmbeddedXmp(filePath);
            if (written is not null && EditParametersSerializer.HasEditProperties(written, includeRawParameters: false))
                throw new IOException("The embedded edit recipe could not be cleared.");
        }

        foreach (var (path, sidecar) in sidecars)
        {
            EditParametersSerializer.ClearFromXmp(sidecar, includeRawParameters: false);
            SaveRecipeSidecar(path, sidecar);
        }
    }

    private static void ClearSidecarRecipe(string filePath)
    {
        var sidecarPath = MetadataSidecarLocator.ResolveReadPath(
            filePath, MetadataSidecarLocator.OwnsPendingEditRecipe);
        if (sidecarPath is null) return;
        var xmp = LoadRecipeSidecar(sidecarPath);
        EditParametersSerializer.ClearFromXmp(xmp, includeRawParameters: false);
        SaveRecipeSidecar(sidecarPath, xmp);
    }

    private static List<(string Path, IXmpMeta Xmp)> LoadOwnedRecipeSidecars(string filePath)
    {
        var sidecars = new List<(string Path, IXmpMeta Xmp)>();
        var ownedPaths = MetadataSidecarLocator.GetOwnedSidecarPaths(filePath).ToList();
        var sharedRawSidecar = Path.ChangeExtension(filePath, ".xmp");
        if (HasRawCompanion(filePath) &&
            File.Exists(sharedRawSidecar) &&
            !ownedPaths.Contains(sharedRawSidecar, StringComparer.OrdinalIgnoreCase))
        {
            ownedPaths.Add(sharedRawSidecar);
        }

        foreach (var path in ownedPaths)
        {
            if (!File.Exists(path)) continue;
            var xmp = LoadRecipeSidecar(path);
            if (MetadataSidecarLocator.OwnsPendingEditRecipe(xmp))
                sidecars.Add((path, xmp));
        }
        return sidecars;
    }

    private static bool HasRawCompanion(string filePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (directory is null) return false;
        var stem = Path.GetFileNameWithoutExtension(filePath);
        return Directory.EnumerateFiles(directory, stem + ".*")
            .Any(RawToneMappingService.IsRaw);
    }

    private static IXmpMeta LoadOrCreateRecipeSidecar(string filePath)
    {
        var sidecarPath = GetRecipeSidecarPath(filePath);
        return File.Exists(sidecarPath)
            ? LoadRecipeSidecar(sidecarPath)
            : XmpMetaFactory.Create();
    }

    private static string GetRecipeSidecarPath(string filePath) =>
        MetadataSidecarLocator.ResolveTargetPath(
            filePath, MetadataSidecarLocator.OwnsPendingEditRecipe);

    private static IXmpMeta LoadRecipeSidecar(string sidecarPath)
    {
        try
        {
            return XmpMetaFactory.ParseFromString(File.ReadAllText(sidecarPath));
        }
        catch (Exception exception)
        {
            throw new IOException($"The existing XMP sidecar could not be read: {sidecarPath}.", exception);
        }
    }

    private static IXmpMeta? LoadEmbeddedXmp(string filePath)
    {
        return FaceMetadataStore.ReadEmbeddedXmp(filePath);
    }

    private static bool RecipesMatch(EditParameters expected, EditParameters actual) =>
        JsonSerializer.Serialize(expected) == JsonSerializer.Serialize(actual);

    private static void SaveRecipeSidecar(string path, IXmpMeta xmp, bool keepBackup = false)
    {
        var serialized = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The XMP sidecar path has no parent directory.");
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, serialized);
            if (File.Exists(path))
            {
                if (keepBackup)
                    File.Replace(temporary, path, path + "." + Guid.NewGuid().ToString("N") + ".bak");
                else
                    File.Move(temporary, path, overwrite: true);
            }
            else
                File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
