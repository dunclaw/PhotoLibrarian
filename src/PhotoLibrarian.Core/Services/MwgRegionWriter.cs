using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Core.Services;

/// <summary>
/// Writes face regions + names to XMP using the MWG Region Schema standard,
/// alongside Microsoft People Tags for Windows Photo Gallery and Explorer.
/// Schema: mwg-rs:Regions/mwg-rs:RegionList with mwg-rs:Area and stArea:* properties.
/// </summary>
public static class MwgRegionWriter
{
    /// <summary>
    /// Writes face regions to embedded XMP or a sidecar using both region schemas.
    /// </summary>
    public static async Task WriteFaceRegionsAsync(string imagePath, IEnumerable<FaceRegion> faces, int imageWidth, int imageHeight)
    {
        var portableFaces = faces.Select(face => new PortableFaceMetadata(
            face.Id,
            face.X,
            face.Y,
            face.Width,
            face.Height,
            face.PersonName,
            false,
            false,
            []));
        await new FaceMetadataStore().WriteAsync(
            imagePath,
            new PhotoFaceMetadata(imageWidth, imageHeight, portableFaces.ToList()));
    }
}
