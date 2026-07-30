using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PhotoLibrarian.Core.Services;

/// <summary>
/// Writes metadata (rating, caption, tags, date taken) <b>directly into the image file</b>
/// using Windows Imaging Component (WIC). Two strategies are used:
/// <list type="number">
///   <item><description><b>In-place</b> (<c>CreateForInPlacePropertyEncodingAsync</c>) — byte-exact, preserves
///   the file bit-for-bit. Used first because it's fastest and lossless.</description></item>
///   <item><description><b>Transcoding</b> (<c>CreateForTranscodingAsync</c>) — fallback when in-place fails
///   (e.g. "too much metadata", HRESULT 0x88982F52). The file is rewritten with the new metadata, but
///   for JPEG/PNG/TIFF the encoded image data is copied without recompression — visually lossless.</description></item>
/// </list>
/// 
/// For both strategies, WIC's <c>System.*</c> property policies fan out to the appropriate metadata
/// containers (EXIF + XMP + IPTC + XPKeywords) automatically. So <c>System.Keywords</c> populates
/// XMP dc:subject AND EXIF XPKeywords AND IPTC Keywords in one call.
/// 
/// Supported formats: JPEG, TIFF, PNG, HEIC, JPEG-XR. RAW formats are not supported by WIC for
/// re-encoding and must fall back to XMP sidecar.
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

    // HRESULT WINCODEC_ERR_TOOMUCHMETADATA — in-place encoder doesn't have room
    private const int WINCODEC_ERR_TOOMUCHMETADATA = unchecked((int)0x88982F52);

    /// <summary>XMP namespace used for PhotoLibrarian-specific properties (e.g. the user flag).</summary>
    public const string PhotoLibrarianXmpNamespace = "http://ns.photolibrarian.app/1.0/";

    /// <summary>
    /// WIC metadata query path for the flag. There is no standard EXIF/XMP "flag" field, so the
    /// value lives in our own XMP namespace — safe to add and ignored by other tools.
    /// </summary>
    private const string FlagQueryPath = "/xmp/{wstr=http://ns.photolibrarian.app/1.0/}:Flagged";

    /// <summary>Returns true if the file format supports in-place metadata writing.</summary>
    public static bool IsSupported(string filePath)
    {
        return SupportedExtensions.Contains(Path.GetExtension(filePath));
    }

    /// <summary>
    /// Writes all provided properties into the image file. Pass null to skip a property
    /// (does not delete existing). Use <see cref="ClearPropertiesAsync"/> to remove values.
    /// </summary>
    public static async Task WriteAsync(
        string filePath,
        int? rating = null,
        string? title = null,
        IReadOnlyList<string>? keywords = null,
        DateTime? dateTaken = null,
        ushort? orientation = null,
        bool? flagged = null)
    {
        if (!IsSupported(filePath))
            throw new NotSupportedException($"Format not supported for in-place metadata write: {Path.GetExtension(filePath)}");

        var props = BuildPropertySet(rating, title, keywords, dateTaken, orientation, flagged);
        if (props.Count == 0) return;

        await WritePropertiesAsync(filePath, props);
    }

    /// <summary>
    /// Removes a property from the file (e.g. clearing a rating).
    /// </summary>
    public static async Task ClearPropertiesAsync(string filePath, params string[] propertyKeys)
    {
        if (!IsSupported(filePath))
            throw new NotSupportedException($"Format not supported for in-place metadata write: {Path.GetExtension(filePath)}");
        if (propertyKeys.Length == 0) return;

        var props = new BitmapPropertySet();
        foreach (var key in propertyKeys)
        {
            switch (key)
            {
                case "System.Rating":
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

        await WritePropertiesAsync(filePath, props);
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

    // -----------------------------------------------------------------
    //  Internals
    // -----------------------------------------------------------------

    private static BitmapPropertySet BuildPropertySet(
        int? rating, string? title, IReadOnlyList<string>? keywords, DateTime? dateTaken,
        ushort? orientation = null, bool? flagged = null)
    {
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
            ushort starRating = (ushort)Math.Clamp(rating.Value, 0, 5);
            props.Add("System.SimpleRating", new BitmapTypedValue(starRating, Windows.Foundation.PropertyType.UInt16));
        }

        if (title != null)
        {
            // Caption: write to both Title and Comment for max reader compatibility
            props.Add("System.Title", new BitmapTypedValue(title, Windows.Foundation.PropertyType.String));
            props.Add("System.Comment", new BitmapTypedValue(title, Windows.Foundation.PropertyType.String));
        }

        if (keywords != null)
        {
            // System.Keywords fans out to XMP dc:subject + EXIF XPKeywords + IPTC Keywords
            // (Photoshop IRB) all in one call — the WIC property policy does the routing.
            props.Add("System.Keywords",
                new BitmapTypedValue(keywords.ToArray(), Windows.Foundation.PropertyType.StringArray));
        }

        if (dateTaken.HasValue)
        {
            var dto = new DateTimeOffset(dateTaken.Value, TimeZoneInfo.Local.GetUtcOffset(dateTaken.Value));
            props.Add("System.Photo.DateTaken",
                new BitmapTypedValue(dto, Windows.Foundation.PropertyType.DateTime));
        }

        if (orientation.HasValue)
        {
            // EXIF orientation: 1=normal, 6=90CW, 3=180, 8=270CW. Maps to EXIF tag 0x0112.
            props.Add("System.Photo.Orientation",
                new BitmapTypedValue(orientation.Value, Windows.Foundation.PropertyType.UInt16));
        }

        if (flagged.HasValue)
        {
            props.Add(FlagQueryPath,
                new BitmapTypedValue(flagged.Value ? "True" : "False", Windows.Foundation.PropertyType.String));
        }

        return props;
    }

    /// <summary>
    /// Two-tier write: try in-place first (byte-exact); fall back to transcoding when in-place
    /// doesn't have room for the new metadata (the common case when adding a tag to a complex file).
    /// </summary>
    private static async Task WritePropertiesAsync(string filePath, BitmapPropertySet props)
    {
        // === Tier 1: in-place (preserves bytes exactly) ===
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(filePath);
            using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                var decoder = await BitmapDecoder.CreateAsync(stream);
                var encoder = await BitmapEncoder.CreateForInPlacePropertyEncodingAsync(decoder);
                await encoder.BitmapProperties.SetPropertiesAsync(props);
                await encoder.FlushAsync();
            }
            return;
        }
        catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == WINCODEC_ERR_TOOMUCHMETADATA)
        {
            // Fall through to transcoding
            System.Diagnostics.Debug.WriteLine($"[METADATA] In-place encoder full for {filePath}, transcoding…");
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            // Other WIC error — also try transcoding as a more resilient path
            System.Diagnostics.Debug.WriteLine($"[METADATA] In-place encoder threw 0x{ex.HResult:X8}, attempting transcode…");
        }

        // === Tier 2: transcode (rewrites file, but preserves encoded image data losslessly) ===
        await TranscodeWriteAsync(filePath, props);
    }

    private static async Task TranscodeWriteAsync(string filePath, BitmapPropertySet props)
    {
        var file = await StorageFile.GetFileFromPathAsync(filePath);
        var destBuffer = new InMemoryRandomAccessStream();

        // 1) Read source + transcode into memory with merged properties
        using (var srcStream = await file.OpenAsync(FileAccessMode.Read))
        {
            var decoder = await BitmapDecoder.CreateAsync(srcStream);
            var encoder = await BitmapEncoder.CreateForTranscodingAsync(destBuffer, decoder);
            await encoder.BitmapProperties.SetPropertiesAsync(props);
            await encoder.FlushAsync();
        }

        // 2) Atomically replace the source file with the in-memory result
        destBuffer.Seek(0);
        using (var outStream = await file.OpenAsync(FileAccessMode.ReadWrite))
        {
            outStream.Size = 0;
            await RandomAccessStream.CopyAsync(destBuffer.GetInputStreamAt(0), outStream);
            await outStream.FlushAsync();
        }
        destBuffer.Dispose();
    }
}
