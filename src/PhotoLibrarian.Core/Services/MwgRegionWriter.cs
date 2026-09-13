using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Core.Services;

/// <summary>
/// Writes face regions + names to XMP using the MWG Region Schema standard.
/// This is the same format used by Windows Photo Gallery, Picasa, digiKam, etc.
/// Schema: mwg-rs:Regions/mwg-rs:RegionList with mwg-rs:Area and stArea:* properties.
/// </summary>
public static class MwgRegionWriter
{
    /// <summary>
    /// Writes face regions to an XMP sidecar file following MWG Region Schema.
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
