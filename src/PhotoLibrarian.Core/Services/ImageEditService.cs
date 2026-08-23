using PhotoLibrarian.Core.Models;
using XmpCore;

namespace PhotoLibrarian.Core.Services;

/// <summary>
/// Applies image edits to a file: backs the original up, hands the pixel rendering to the
/// platform-specific renderer supplied by the caller, and keeps the XMP edit record in sync.
///
/// The rendering itself lives in the UI layer because it needs Win2D, which this assembly does
/// not reference — see <c>PhotoLibrarian.Services.ImageEditRenderer</c>.
/// </summary>
public sealed class ImageEditService
{
    private readonly OriginalBackupService _backupService;

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
    /// pixels, and finally any pending edit parameters are cleared from the XMP sidecar — the
    /// values now live in the pixels, so keeping them would double-apply on the next open.
    /// Returns the new pixel dimensions of the file.
    /// </summary>
    public async Task<(uint Width, uint Height)> ApplyEditsAsync(
        string filePath, EditParameters parameters, PixelRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);

        await _backupService.BackupOriginalAsync(filePath);
        var size = await renderer(filePath, parameters);
        await ClearPendingEditParametersAsync(filePath);
        return size;
    }

    /// <summary>
    /// Saves edit parameters as pending (not yet rendered) edits in the image's XMP sidecar.
    /// Backs up the original before first edit.
    /// </summary>
    public async Task SaveEditParametersAsync(string filePath, EditParameters parameters)
    {
        // Backup original first
        await _backupService.BackupOriginalAsync(filePath);

        await Task.Run(() =>
        {
            var xmp = ReadXmp(filePath);
            EditParametersSerializer.WriteToXmp(xmp, parameters);
            WriteSidecar(filePath, xmp);
        });
    }

    /// <summary>
    /// Strips PhotoLibrarian's edit properties from the sidecar, leaving any other XMP content
    /// (a Lightroom sidecar, for example) alone. Does nothing when no sidecar exists.
    /// </summary>
    private static Task ClearPendingEditParametersAsync(string filePath) => Task.Run(() =>
    {
        var sidecarPath = GetSidecarPath(filePath);
        if (!File.Exists(sidecarPath)) return;

        try
        {
            IXmpMeta xmp;
            using (var stream = File.OpenRead(sidecarPath))
            {
                xmp = XmpMetaFactory.Parse(stream);
            }

            EditParametersSerializer.ClearFromXmp(xmp);
            WriteSidecar(filePath, xmp);
        }
        catch
        {
            // A sidecar we can't parse isn't ours to rewrite — leave it as-is.
        }
    });

    /// <summary>
    /// Reads the pending (not yet rendered) edit parameters for an image, preferring our sidecar
    /// and falling back to XMP embedded in the file.
    /// </summary>
    public static EditParameters ReadEditParameters(string filePath) =>
        EditParametersSerializer.ReadFromXmp(ReadXmp(filePath));

    private static IXmpMeta ReadXmp(string filePath)
    {
        // Prefer the sidecar we own, since that is where our edits are stored.
        var sidecarPath = GetSidecarPath(filePath);
        if (File.Exists(sidecarPath))
        {
            try
            {
                using var stream = File.OpenRead(sidecarPath);
                return XmpMetaFactory.Parse(stream);
            }
            catch { /* Fall through to embedded XMP */ }
        }

        try
        {
            var dirs = MetadataExtractor.ImageMetadataReader.ReadMetadata(filePath);
            var xmpDir = dirs.OfType<MetadataExtractor.Formats.Xmp.XmpDirectory>().FirstOrDefault();
            return xmpDir?.XmpMeta ?? XmpMetaFactory.Create();
        }
        catch
        {
            return XmpMetaFactory.Create();
        }
    }

    private static void WriteSidecar(string filePath, IXmpMeta xmp)
    {
        // Full embedded XMP write requires WIC — saving as .xmp sidecar for now.
        using var writer = File.CreateText(GetSidecarPath(filePath));
        writer.Write(XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions()));
    }

    private static string GetSidecarPath(string filePath) => Path.ChangeExtension(filePath, ".xmp");
}
