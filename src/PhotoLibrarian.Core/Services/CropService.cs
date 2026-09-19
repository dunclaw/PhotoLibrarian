using Windows.Graphics.Imaging;
using XmpCore;
using XmpCore.Options;

namespace PhotoLibrarian.Core.Services;

public readonly record struct CropResult(
    uint Width,
    uint Height,
    uint SourceWidth,
    uint SourceHeight,
    CropRectangle Bounds);

/// <summary>Crops images in oriented display coordinates and remaps sidecar face metadata.</summary>
public static class CropService
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp" };

    public static bool IsSupported(string filePath) =>
        SupportedExtensions.Contains(Path.GetExtension(filePath));

    public static async Task<CropResult> CropImageAsync(string filePath, BitmapBounds bounds)
    {
        if (!IsSupported(filePath))
            throw new NotSupportedException($"Crop not supported for {Path.GetExtension(filePath)}");
        if (bounds.Width == 0 || bounds.Height == 0)
            throw new ArgumentException("Crop bounds must be non-empty", nameof(bounds));

        var decoded = await WindowsImagingCodec.DecodeAsync(filePath);
        var sourceWidth = decoded.Width;
        var sourceHeight = decoded.Height;
        var crop = ClampBounds(bounds, sourceWidth, sourceHeight);
        if (crop.Width == 0 || crop.Height == 0)
            throw new ArgumentOutOfRangeException(nameof(bounds), "Crop bounds do not intersect the image.");

        var sidecar = PrepareRemappedSidecar(filePath, sourceWidth, sourceHeight, crop);
        var pixels = WindowsImagingCodec.Crop(decoded.Pixels, sourceWidth, sourceHeight, crop);
        var temporaryImagePath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        string? temporarySidecarPath = null;
        try
        {
            var encoded = await WindowsImagingCodec.EncodeAsync(
                filePath, pixels, crop.Width, crop.Height);
            await File.WriteAllBytesAsync(temporaryImagePath, encoded);

            if (sidecar is not null)
            {
                temporarySidecarPath = $"{sidecar.Value.Path}.{Guid.NewGuid():N}.tmp";
                await File.WriteAllTextAsync(temporarySidecarPath, sidecar.Value.Content);
            }

            ReplaceOutputs(filePath, temporaryImagePath, sidecar?.Path, temporarySidecarPath);
        }
        finally
        {
            DeleteIfPresent(temporaryImagePath);
            if (temporarySidecarPath is not null)
                DeleteIfPresent(temporarySidecarPath);
        }

        return new CropResult(crop.Width, crop.Height, sourceWidth, sourceHeight, crop);
    }

    private static RemappedSidecar? PrepareRemappedSidecar(
        string imagePath, uint sourceWidth, uint sourceHeight, CropRectangle crop)
    {
        var sidecarPath = FaceMetadataStore.GetSidecarPathForImage(imagePath);
        if (!File.Exists(sidecarPath))
            return null;

        var xmp = XmpMetaFactory.ParseFromString(File.ReadAllText(sidecarPath));
        if (!CropMetadataRemapper.RemapMwgRegions(xmp, sourceWidth, sourceHeight, crop))
            return null;
        return new RemappedSidecar(
            sidecarPath,
            XmpMetaFactory.SerializeToString(xmp, new SerializeOptions()));
    }

    private static void ReplaceOutputs(
        string imagePath, string temporaryImagePath, string? sidecarPath, string? temporarySidecarPath)
    {
        var imageBackupPath = $"{imagePath}.{Guid.NewGuid():N}.rollback";
        var sidecarBackupPath = sidecarPath is null ? null : $"{sidecarPath}.{Guid.NewGuid():N}.rollback";
        try
        {
            File.Copy(imagePath, imageBackupPath);
            if (sidecarPath is not null && sidecarBackupPath is not null)
                File.Copy(sidecarPath, sidecarBackupPath);
            File.Move(temporaryImagePath, imagePath, true);
            if (sidecarPath is not null && temporarySidecarPath is not null)
                File.Move(temporarySidecarPath, sidecarPath, true);
        }
        catch
        {
            if (File.Exists(imageBackupPath))
                File.Copy(imageBackupPath, imagePath, true);
            if (sidecarPath is not null && sidecarBackupPath is not null && File.Exists(sidecarBackupPath))
                File.Copy(sidecarBackupPath, sidecarPath, true);
            throw;
        }
        finally
        {
            DeleteIfPresent(imageBackupPath);
            if (sidecarBackupPath is not null)
                DeleteIfPresent(sidecarBackupPath);
        }
    }

    private static CropRectangle ClampBounds(BitmapBounds bounds, uint maxWidth, uint maxHeight)
    {
        var x = Math.Min(bounds.X, maxWidth);
        var y = Math.Min(bounds.Y, maxHeight);
        return new CropRectangle(x, y, Math.Min(bounds.Width, maxWidth - x), Math.Min(bounds.Height, maxHeight - y));
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private readonly record struct RemappedSidecar(string Path, string Content);
}
