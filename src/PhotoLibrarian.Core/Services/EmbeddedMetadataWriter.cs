using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PhotoLibrarian.Core.Services;

/// <summary>
/// Writes metadata (rating, caption, tags, date taken) <b>directly into the image file</b>
/// using Windows Imaging Component (WIC) in-place property encoding. This preserves the
/// image bytes exactly — only the metadata block is rewritten — so files remain portable
/// across drives and computers without sidecars.
/// 
/// Supported formats: JPEG, TIFF, PNG, HEIC, JPEG-XR (anything WIC can decode + re-encode props).
/// Unsupported (CR2/CR3/NEF/ARW RAW etc.) callers must fall back to a sidecar.
/// </summary>
public static class EmbeddedMetadataWriter
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif",
        ".tif", ".tiff",
        ".png",
        ".heic", ".heif",
        ".jxr", ".wdp"
    };

    /// <summary>Returns true if the file format supports in-place metadata writing.</summary>
    public static bool IsSupported(string filePath)
    {
        return SupportedExtensions.Contains(Path.GetExtension(filePath));
    }

    /// <summary>
    /// Writes all provided properties into the image file. Pass null for a property's value
    /// to omit it (does not delete existing). Use ClearPropertyAsync to remove a value.
    /// </summary>
    /// <param name="rating">0 = clear, 1..5 = star count.</param>
    /// <param name="title">Caption text (also written to Comment for max reader compat).</param>
    /// <param name="keywords">Full set of keywords/tags. Pass empty array to clear.</param>
    /// <param name="dateTaken">Capture date.</param>
    public static async Task WriteAsync(
        string filePath,
        int? rating = null,
        string? title = null,
        IReadOnlyList<string>? keywords = null,
        DateTime? dateTaken = null)
    {
        if (!IsSupported(filePath))
            throw new NotSupportedException($"Format not supported for in-place metadata write: {Path.GetExtension(filePath)}");

        var file = await StorageFile.GetFileFromPathAsync(filePath);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var encoder = await BitmapEncoder.CreateForInPlacePropertyEncodingAsync(decoder);

        var props = new BitmapPropertySet();

        if (rating.HasValue)
        {
            // WIC System.Rating uses 1-99 scale: 1=1*, 25=2*, 50=3*, 75=4*, 99=5*; 0 = unrated
            ushort ratingPct = rating.Value switch
            {
                <= 0 => 0,
                1 => 1,
                2 => 25,
                3 => 50,
                4 => 75,
                _ => 99
            };
            props.Add("System.Rating", new BitmapTypedValue(ratingPct, Windows.Foundation.PropertyType.UInt16));
            // Also write SimpleRating (0-5 star integer) for apps that read it
            ushort starRating = (ushort)Math.Clamp(rating.Value, 0, 5);
            props.Add("System.SimpleRating", new BitmapTypedValue(starRating, Windows.Foundation.PropertyType.UInt16));
        }

        if (title != null)
        {
            // Caption: write to both Title and Comment so all readers (Windows Explorer,
            // Photo Gallery, Lightroom) pick it up
            props.Add("System.Title", new BitmapTypedValue(title, Windows.Foundation.PropertyType.String));
            props.Add("System.Comment", new BitmapTypedValue(title, Windows.Foundation.PropertyType.String));
        }

        if (keywords != null)
        {
            // System.Keywords accepts string[] as StringVector
            props.Add("System.Keywords",
                new BitmapTypedValue(keywords.ToArray(), Windows.Foundation.PropertyType.StringArray));
        }

        if (dateTaken.HasValue)
        {
            // System.Photo.DateTaken maps to EXIF DateTimeOriginal (0x9003)
            // Use DateTimeOffset (WIC requires it)
            var dto = new DateTimeOffset(dateTaken.Value, TimeZoneInfo.Local.GetUtcOffset(dateTaken.Value));
            props.Add("System.Photo.DateTaken",
                new BitmapTypedValue(dto, Windows.Foundation.PropertyType.DateTime));
        }

        if (props.Count == 0) return;

        await encoder.BitmapProperties.SetPropertiesAsync(props);
        await encoder.FlushAsync();
    }

    /// <summary>
    /// Removes a property from the file (e.g. clearing a rating). Pass any of the System.* keys.
    /// </summary>
    public static async Task ClearPropertiesAsync(string filePath, params string[] propertyKeys)
    {
        if (!IsSupported(filePath))
            throw new NotSupportedException($"Format not supported for in-place metadata write: {Path.GetExtension(filePath)}");
        if (propertyKeys.Length == 0) return;

        var file = await StorageFile.GetFileFromPathAsync(filePath);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var encoder = await BitmapEncoder.CreateForInPlacePropertyEncodingAsync(decoder);

        // SetPropertiesAsync with a null/empty BitmapTypedValue won't delete properties;
        // the WIC pattern is to write an empty/default value of the right type.
        var props = new BitmapPropertySet();
        foreach (var key in propertyKeys)
        {
            switch (key)
            {
                case "System.Rating":
                    props.Add(key, new BitmapTypedValue((ushort)0, Windows.Foundation.PropertyType.UInt16));
                    break;
                case "System.SimpleRating":
                    props.Add(key, new BitmapTypedValue((ushort)0, Windows.Foundation.PropertyType.UInt16));
                    break;
                case "System.Title":
                case "System.Comment":
                    props.Add(key, new BitmapTypedValue("", Windows.Foundation.PropertyType.String));
                    break;
                case "System.Keywords":
                    props.Add(key, new BitmapTypedValue(Array.Empty<string>(), Windows.Foundation.PropertyType.StringArray));
                    break;
            }
        }
        if (props.Count == 0) return;

        await encoder.BitmapProperties.SetPropertiesAsync(props);
        await encoder.FlushAsync();
    }

    /// <summary>
    /// Reads currently-stored keywords from the image. Used to merge tag adds/removes.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ReadKeywordsAsync(string filePath)
    {
        if (!IsSupported(filePath)) return Array.Empty<string>();
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(filePath);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var view = await decoder.BitmapProperties.GetPropertiesAsync(new[] { "System.Keywords" });
            if (view.TryGetValue("System.Keywords", out var val) && val?.Value is string[] arr)
                return arr;
        }
        catch { }
        return Array.Empty<string>();
    }
}
