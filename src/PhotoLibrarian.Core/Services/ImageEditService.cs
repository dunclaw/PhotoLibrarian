using PhotoLibrarian.Core.Models;
using XmpCore;

namespace PhotoLibrarian.Core.Services;

/// <summary>
/// Applies image edits by saving parameters to XMP metadata and rendering
/// the final image through the effect pipeline.
/// </summary>
public sealed class ImageEditService
{
    private readonly OriginalBackupService _backupService;

    public ImageEditService(OriginalBackupService backupService)
    {
        _backupService = backupService;
    }

    /// <summary>
    /// Saves edit parameters as XMP metadata in the image file.
    /// Backs up the original before first edit.
    /// </summary>
    public async Task SaveEditParametersAsync(string filePath, EditParameters parameters)
    {
        // Backup original first
        await _backupService.BackupOriginalAsync(filePath);

        // Read existing XMP or create new
        await Task.Run(() =>
        {
            IXmpMeta xmp;
            try
            {
                var dirs = MetadataExtractor.ImageMetadataReader.ReadMetadata(filePath);
                var xmpDir = dirs.OfType<MetadataExtractor.Formats.Xmp.XmpDirectory>().FirstOrDefault();
                xmp = xmpDir?.XmpMeta ?? XmpMetaFactory.Create();
            }
            catch
            {
                xmp = XmpMetaFactory.Create();
            }

            EditParametersSerializer.WriteToXmp(xmp, parameters);

            // Write XMP back to file using sidecar approach for now
            // Full embedded XMP write requires WIC — saving as .xmp sidecar
            var sidecarPath = Path.ChangeExtension(filePath, ".xmp");
            using var writer = File.CreateText(sidecarPath);
            writer.Write(XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions()));
        });
    }
}
