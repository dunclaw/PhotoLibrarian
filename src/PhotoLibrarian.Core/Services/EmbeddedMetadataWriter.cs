// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Runtime.InteropServices.WindowsRuntime;
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
    public const string PhotoTagMetadataPropertyName = "TagMetadata";

    /// <summary>
    /// WIC metadata query path for the flag. There is no standard EXIF/XMP "flag" field, so the
    /// value lives in our own XMP namespace — safe to add and ignored by other tools.
    /// </summary>
    private const string FlagQueryPath = "/xmp/{wstr=http://ns.photolibrarian.app/1.0/}:Flagged";
    private const string TagMetadataQueryPath =
        "/xmp/{wstr=http://ns.photolibrarian.app/1.0/}:TagMetadata";

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
        bool? flagged = null,
        string? photoTagMetadata = null)
    {
        if (!IsSupported(filePath))
            throw new NotSupportedException($"Format not supported for in-place metadata write: {Path.GetExtension(filePath)}");

        var props = BuildPropertySet(
            rating,
            title,
            keywords,
            dateTaken,
            orientation,
            flagged,
            photoTagMetadata);
        if (props.Count == 0) return;

        await WritePropertiesAsync(filePath, props);
    }

    /// <summary>Writes EXIF GPS coordinates and hemisphere references into a JPEG or TIFF.</summary>
    public static async Task WriteGeotagAsync(string filePath, GeoCoordinate coordinate)
    {
        if (!IsSupported(filePath))
            throw new NotSupportedException($"Format not supported for in-place metadata write: {Path.GetExtension(filePath)}");

        var latitude = GeotagMetadataWriterService.ToDegreesMinutesSeconds(coordinate.Latitude);
        var longitude = GeotagMetadataWriterService.ToDegreesMinutesSeconds(coordinate.Longitude);
        const uint secondsDenominator = 10_000;

        var properties = new BitmapPropertySet();
        properties.Add("System.GPS.LatitudeNumerator", new BitmapTypedValue(
            CreateGpsNumerators(latitude, secondsDenominator),
            Windows.Foundation.PropertyType.UInt32Array));
        properties.Add("System.GPS.LatitudeDenominator", new BitmapTypedValue(
            CreateGpsDenominators(secondsDenominator),
            Windows.Foundation.PropertyType.UInt32Array));
        properties.Add("System.GPS.LatitudeRef", new BitmapTypedValue(
            coordinate.Latitude < 0 ? "S" : "N",
            Windows.Foundation.PropertyType.String));
        properties.Add("System.GPS.LongitudeNumerator", new BitmapTypedValue(
            CreateGpsNumerators(longitude, secondsDenominator),
            Windows.Foundation.PropertyType.UInt32Array));
        properties.Add("System.GPS.LongitudeDenominator", new BitmapTypedValue(
            CreateGpsDenominators(secondsDenominator),
            Windows.Foundation.PropertyType.UInt32Array));
        properties.Add("System.GPS.LongitudeRef", new BitmapTypedValue(
            coordinate.Longitude < 0 ? "W" : "E",
            Windows.Foundation.PropertyType.String));

        await WritePropertiesAsync(filePath, properties);
    }

    private static uint[] CreateGpsNumerators(
        (uint Degrees, uint Minutes, double Seconds) coordinate,
        uint secondsDenominator) =>
    [
        coordinate.Degrees,
        coordinate.Minutes,
        (uint)Math.Round(coordinate.Seconds * secondsDenominator)
    ];

    private static uint[] CreateGpsDenominators(uint secondsDenominator) => [1, 1, secondsDenominator];

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
        ushort? orientation = null, bool? flagged = null, string? photoTagMetadata = null)
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

        if (photoTagMetadata is not null)
        {
            props.Add(TagMetadataQueryPath,
                new BitmapTypedValue(photoTagMetadata, Windows.Foundation.PropertyType.String));
        }

        return props;
    }

    /// <summary>
    /// Two-tier write: try in-place first (byte-exact); fall back to transcoding when in-place
    /// doesn't have room for the new metadata (the common case when adding a tag to a complex file).
    /// </summary>
    private static Task WritePropertiesAsync(string filePath, BitmapPropertySet props) =>
        RetryOnFileLockAsync(() => WritePropertiesOnceAsync(filePath, props));

    internal static async Task RetryOnFileLockAsync(
        Func<Task> write, Func<TimeSpan, Task>? delay = null)
    {
        delay ??= Task.Delay;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await write();
                return;
            }
            catch (Exception ex) when (IsFileLock(ex) && attempt < 3)
            {
                await delay(TimeSpan.FromMilliseconds(100 * (1 << attempt)));
            }
        }
    }

    private static bool IsFileLock(Exception exception) =>
        exception.HResult is unchecked((int)0x80070020) // ERROR_SHARING_VIOLATION
            or unchecked((int)0x80070021) // ERROR_LOCK_VIOLATION
            or unchecked((int)0x80030020) // STG_E_SHAREVIOLATION
            or unchecked((int)0x80030021); // STG_E_LOCKVIOLATION

    private static async Task WritePropertiesOnceAsync(string filePath, BitmapPropertySet props)
    {
        byte[]? compatibleJpeg = null;
        // === Tier 1: in-place (preserves bytes exactly) ===
        try
        {
            // FileStream preserves sharing-violation HRESULTs; StorageFile can map them to access denied.
            using (var fileStream = File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            using (var stream = fileStream.AsRandomAccessStream())
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
        catch (System.Runtime.InteropServices.COMException ex) when (!IsFileLock(ex))
        {
            // Other WIC error — also try transcoding as a more resilient path
            System.Diagnostics.Debug.WriteLine($"[METADATA] In-place encoder threw 0x{ex.HResult:X8}, attempting transcode…");
        }
        catch (ArgumentException ex) when (ex.HResult == unchecked((int)0x80070057))
        {
            var bytes = await File.ReadAllBytesAsync(filePath);
            compatibleJpeg = RemoveJpegXmpByteOrderMarks(bytes);
            if (compatibleJpeg.Length == bytes.Length)
                throw;
            System.Diagnostics.Debug.WriteLine(
                $"[METADATA] Normalizing legacy JPEG XMP for {filePath} before transcode: {ex.Message}");
        }

        // === Tier 2: transcode (rewrites file, but preserves encoded image data losslessly) ===
        await TranscodeWriteAsync(filePath, props, compatibleJpeg);
    }

    private static async Task TranscodeWriteAsync(
        string filePath, BitmapPropertySet props, byte[]? compatibleJpeg)
    {
        using var destBuffer = new InMemoryRandomAccessStream();

        // 1) Read source + transcode into memory with merged properties
        using (var fileStream = compatibleJpeg is not null
            ? new MemoryStream(compatibleJpeg, writable: false)
            : (Stream)File.OpenRead(filePath))
        using (var srcStream = fileStream.AsRandomAccessStream())
        {
            var decoder = await BitmapDecoder.CreateAsync(srcStream);
            var encoder = await BitmapEncoder.CreateForTranscodingAsync(destBuffer, decoder);
            await encoder.BitmapProperties.SetPropertiesAsync(props);
            await encoder.FlushAsync();
        }

        // 2) Atomically replace the source file with the in-memory result
        destBuffer.Seek(0);
        using (var fileStream = File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        using (var outStream = fileStream.AsRandomAccessStream())
        {
            outStream.Size = 0;
            await RandomAccessStream.CopyAsync(destBuffer.GetInputStreamAt(0), outStream);
            await outStream.FlushAsync();
        }
    }

    private static byte[] RemoveJpegXmpByteOrderMarks(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8)
            return bytes;

        ReadOnlySpan<byte> xmpHeader = "http://ns.adobe.com/xap/1.0/\0"u8;
        ReadOnlySpan<byte> byteOrderMark = [0xef, 0xbb, 0xbf];
        var length = bytes.Length;
        var offset = 2;
        while (offset + 4 <= length && bytes[offset] == 0xff)
        {
            var marker = bytes[offset + 1];
            if (marker is 0xda or 0xd9)
                break;
            if (marker is >= 0xd0 and <= 0xd7 or 0x01)
            {
                offset += 2;
                continue;
            }
            var segmentLength = bytes[offset + 2] << 8 | bytes[offset + 3];
            if (segmentLength < 2 || offset + segmentLength + 2 > length)
                throw new InvalidDataException("The JPEG metadata segment is invalid.");
            var payload = bytes.AsSpan(offset + 4, segmentLength - 2);
            if (marker == 0xe1 && payload.StartsWith(xmpHeader) &&
                payload[xmpHeader.Length..].StartsWith(byteOrderMark))
            {
                // Normalize only the in-memory input; a failed write leaves the original intact.
                var bomOffset = offset + 4 + xmpHeader.Length;
                System.Buffer.BlockCopy(bytes, bomOffset + 3, bytes, bomOffset, length - bomOffset - 3);
                length -= 3;
                segmentLength -= 3;
                bytes[offset + 2] = (byte)(segmentLength >> 8);
                bytes[offset + 3] = (byte)segmentLength;
            }
            offset += segmentLength + 2;
        }
        if (length != bytes.Length)
            Array.Resize(ref bytes, length);
        return bytes;
    }
}
