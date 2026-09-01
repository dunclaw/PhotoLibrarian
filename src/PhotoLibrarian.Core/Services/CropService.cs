using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.Metadata.Profiles.Xmp;
using SixLabors.ImageSharp.Processing;
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

/// <summary>
/// Crops images in display coordinates and remaps metadata tied to locations in the original frame.
/// </summary>
public static class CropService
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp" };

    public static bool IsSupported(string filePath) =>
        SupportedExtensions.Contains(Path.GetExtension(filePath));

    /// <summary>
    /// Crops the image in-place. <paramref name="bounds"/> is expressed in display (oriented) pixels
    /// matching what the user sees in the viewer.
    /// </summary>
    public static async Task<CropResult> CropImageAsync(string filePath, BitmapBounds bounds)
    {
        if (!IsSupported(filePath))
        {
            throw new NotSupportedException($"Crop not supported for {Path.GetExtension(filePath)}");
        }

        if (bounds.Width == 0 || bounds.Height == 0)
        {
            throw new ArgumentException("Crop bounds must be non-empty", nameof(bounds));
        }

        using var image = await Image.LoadAsync(filePath);
        PromoteFrameOrientation(image);
        var exifStates = CaptureAndRemoveExifLocations(
            image,
            checked((uint)image.Width),
            checked((uint)image.Height));
        image.Mutate(context => context.AutoOrient());

        var sourceWidth = checked((uint)image.Width);
        var sourceHeight = checked((uint)image.Height);
        var crop = ClampBounds(bounds, sourceWidth, sourceHeight);
        if (crop.Width == 0 || crop.Height == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), "Crop bounds do not intersect the image.");
        }

        var remappedSidecar = PrepareRemappedSidecar(filePath, sourceWidth, sourceHeight, crop);
        RemapEmbeddedXmp(image, sourceWidth, sourceHeight, crop);

        image.Mutate(context => context.Crop(
            new Rectangle(checked((int)crop.X), checked((int)crop.Y), checked((int)crop.Width), checked((int)crop.Height))));
        RestoreExifLocations(exifStates, sourceWidth, sourceHeight, crop);

        var temporaryImagePath = CreateTemporaryPath(filePath);
        string? temporarySidecarPath = null;
        try
        {
            await image.SaveAsync(temporaryImagePath);
            if (remappedSidecar is not null)
            {
                temporarySidecarPath = $"{remappedSidecar.Value.Path}.{Guid.NewGuid():N}.tmp";
                await File.WriteAllTextAsync(temporarySidecarPath, remappedSidecar.Value.Content);
            }

            ReplaceOutputs(
                filePath,
                temporaryImagePath,
                remappedSidecar?.Path,
                temporarySidecarPath);
        }
        finally
        {
            if (File.Exists(temporaryImagePath))
            {
                File.Delete(temporaryImagePath);
            }
            if (temporarySidecarPath is not null && File.Exists(temporarySidecarPath))
            {
                File.Delete(temporarySidecarPath);
            }
        }

        return new CropResult(crop.Width, crop.Height, sourceWidth, sourceHeight, crop);
    }

    private static List<ExifLocationState> CaptureAndRemoveExifLocations(
        Image image,
        uint sourceWidth,
        uint sourceHeight)
    {
        var states = new List<ExifLocationState>();
        var profiles = new HashSet<ExifProfile>(ReferenceEqualityComparer.Instance);
        var defaultOrientation = ReadOrientation(image.Metadata.ExifProfile);
        AddProfile(image.Metadata.ExifProfile);
        foreach (var frame in image.Frames)
        {
            AddProfile(frame.Metadata.ExifProfile);
        }

        return states;

        void AddProfile(ExifProfile? profile)
        {
            if (profile is null || !profiles.Add(profile))
            {
                return;
            }

            ushort[]? location = null;
            var orientation = ReadOrientation(profile, defaultOrientation);
            if (profile.TryGetValue(ExifTag.SubjectLocation, out var locationValue) &&
                locationValue.Value is { } rawLocation)
            {
                location = CropMetadataRemapper.OrientSubjectLocation(
                    rawLocation,
                    sourceWidth,
                    sourceHeight,
                    orientation);
            }

            ushort[]? area = null;
            if (profile.TryGetValue(ExifTag.SubjectArea, out var areaValue) &&
                areaValue.Value is { } rawArea)
            {
                area = CropMetadataRemapper.OrientSubjectArea(
                    rawArea,
                    sourceWidth,
                    sourceHeight,
                    orientation);
            }

            profile.RemoveValue(ExifTag.SubjectLocation);
            profile.RemoveValue(ExifTag.SubjectArea);
            states.Add(new ExifLocationState(profile, location, area));
        }
    }

    private static void RestoreExifLocations(
        IEnumerable<ExifLocationState> states,
        uint sourceWidth,
        uint sourceHeight,
        CropRectangle crop)
    {
        foreach (var state in states)
        {
            if (state.SubjectLocation is not null &&
                CropMetadataRemapper.RemapSubjectLocation(
                    state.SubjectLocation,
                    sourceWidth,
                    sourceHeight,
                    crop) is { } location)
            {
                state.Profile.SetValue(ExifTag.SubjectLocation, location);
            }

            if (state.SubjectArea is not null &&
                CropMetadataRemapper.RemapSubjectArea(
                    state.SubjectArea,
                    sourceWidth,
                    sourceHeight,
                    crop) is { } area)
            {
                state.Profile.SetValue(ExifTag.SubjectArea, area);
            }

            state.Profile.SetValue(ExifTag.PixelXDimension, crop.Width);
            state.Profile.SetValue(ExifTag.PixelYDimension, crop.Height);
            state.Profile.SetValue(ExifTag.Orientation, (ushort)1);
        }
    }

    private static void PromoteFrameOrientation(Image image)
    {
        var orientation = ReadOrientation(image.Metadata.ExifProfile, fallback: 0);
        if (orientation == 0)
        {
            foreach (var frame in image.Frames)
            {
                orientation = ReadOrientation(frame.Metadata.ExifProfile, fallback: 0);
                if (orientation != 0)
                {
                    break;
                }
            }
        }

        if (orientation == 0)
        {
            return;
        }

        image.Metadata.ExifProfile ??= new ExifProfile();
        image.Metadata.ExifProfile.SetValue(ExifTag.Orientation, orientation);
    }

    private static ushort ReadOrientation(ExifProfile? profile, ushort fallback = 1)
    {
        if (profile is not null &&
            profile.TryGetValue(ExifTag.Orientation, out var orientation) &&
            orientation.Value is >= 1 and <= 8)
        {
            return orientation.Value;
        }

        return fallback;
    }

    private static void RemapEmbeddedXmp(
        Image image,
        uint sourceWidth,
        uint sourceHeight,
        CropRectangle crop)
    {
        image.Metadata.XmpProfile = RemapProfile(image.Metadata.XmpProfile);
        foreach (var frame in image.Frames)
        {
            frame.Metadata.XmpProfile = RemapProfile(frame.Metadata.XmpProfile);
        }

        XmpProfile? RemapProfile(XmpProfile? profile)
        {
            if (profile is null)
            {
                return null;
            }

            var xmp = XmpMetaFactory.ParseFromBuffer(profile.ToByteArray(), new ParseOptions());
            if (!CropMetadataRemapper.RemapMwgRegions(xmp, sourceWidth, sourceHeight, crop))
            {
                return profile;
            }

            return new XmpProfile(XmpMetaFactory.SerializeToBuffer(xmp, new SerializeOptions()));
        }
    }

    private static RemappedSidecar? PrepareRemappedSidecar(
        string imagePath,
        uint sourceWidth,
        uint sourceHeight,
        CropRectangle crop)
    {
        var sidecarPath = Path.ChangeExtension(imagePath, ".xmp");
        if (!File.Exists(sidecarPath))
        {
            return null;
        }

        var xmp = XmpMetaFactory.ParseFromString(File.ReadAllText(sidecarPath));
        if (!CropMetadataRemapper.RemapMwgRegions(xmp, sourceWidth, sourceHeight, crop))
        {
            return null;
        }

        return new RemappedSidecar(
            sidecarPath,
            XmpMetaFactory.SerializeToString(xmp, new SerializeOptions()));
    }

    private static void ReplaceOutputs(
        string imagePath,
        string temporaryImagePath,
        string? sidecarPath,
        string? temporarySidecarPath)
    {
        var imageBackupPath = $"{imagePath}.{Guid.NewGuid():N}.rollback";
        var sidecarBackupPath = sidecarPath is null
            ? null
            : $"{sidecarPath}.{Guid.NewGuid():N}.rollback";

        try
        {
            File.Copy(imagePath, imageBackupPath);
            if (sidecarPath is not null && sidecarBackupPath is not null)
            {
                File.Copy(sidecarPath, sidecarBackupPath);
            }

            File.Move(temporaryImagePath, imagePath, true);
            if (sidecarPath is not null && temporarySidecarPath is not null)
            {
                File.Move(temporarySidecarPath, sidecarPath, true);
            }
        }
        catch
        {
            if (File.Exists(imageBackupPath))
            {
                File.Copy(imageBackupPath, imagePath, true);
            }
            if (sidecarPath is not null &&
                sidecarBackupPath is not null &&
                File.Exists(sidecarBackupPath))
            {
                File.Copy(sidecarBackupPath, sidecarPath, true);
            }

            throw;
        }
        finally
        {
            if (File.Exists(imageBackupPath))
            {
                File.Delete(imageBackupPath);
            }
            if (sidecarBackupPath is not null && File.Exists(sidecarBackupPath))
            {
                File.Delete(sidecarBackupPath);
            }
        }
    }

    private static string CreateTemporaryPath(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath) ?? "";
        var name = Path.GetFileNameWithoutExtension(filePath);
        var extension = Path.GetExtension(filePath);
        return Path.Combine(directory, $"{name}.{Guid.NewGuid():N}{extension}");
    }

    private static CropRectangle ClampBounds(BitmapBounds bounds, uint maxWidth, uint maxHeight)
    {
        var x = Math.Min(bounds.X, maxWidth);
        var y = Math.Min(bounds.Y, maxHeight);
        var width = Math.Min(bounds.Width, maxWidth - x);
        var height = Math.Min(bounds.Height, maxHeight - y);
        return new CropRectangle(x, y, width, height);
    }

    private sealed record ExifLocationState(
        ExifProfile Profile,
        ushort[]? SubjectLocation,
        ushort[]? SubjectArea);

    private readonly record struct RemappedSidecar(string Path, string Content);
}
