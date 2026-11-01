// Copyright (c) Duncan Lawler. All Rights Reserved.
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Iptc;
using MetadataExtractor.Formats.Xmp;
using PhotoLibrarian.Core.Models;
using XmpCore;

namespace PhotoLibrarian.Core.Services;

/// <summary>
/// Reads EXIF/XMP/IPTC metadata from image files using MetadataExtractor.
/// </summary>
public sealed class MetadataReaderService
{
    /// <summary>
    /// Reads metadata from an image file and populates an ImageEntry.
    /// </summary>
    public ImageEntry ReadMetadata(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        var entry = new ImageEntry
        {
            FilePath = filePath,
            FileName = fileInfo.Name,
            FileSize = fileInfo.Length,
            DateModified = fileInfo.LastWriteTimeUtc,
            MediaType = FolderScannerService.IsVideoFile(filePath) ? MediaType.Video : MediaType.Image
        };

        try
        {
            var directories = ImageMetadataReader.ReadMetadata(filePath);
            ReadExifData(directories, entry);
            ReadIptcData(directories, entry);
            ReadXmpData(directories, entry);
        }
        catch (Exception)
        {
            // File may be corrupt or unsupported — return basic info
        }

        // RAW (and other in-place-unsupported) formats keep the flag in an XMP sidecar.
        if (!entry.IsFlagged && MetadataWriterService.ReadFlagFromSidecar(filePath) == true)
            entry.IsFlagged = true;

        ReadSidecarText(entry);
        return entry;
    }

    private static void ReadSidecarText(ImageEntry entry)
    {
        var sidecarPath = Path.ChangeExtension(entry.FilePath, ".xmp");
        if (!File.Exists(sidecarPath)) return;
        try
        {
            var xmp = XmpMetaFactory.ParseFromString(File.ReadAllText(sidecarPath));
            ReadXmpData(xmp, entry, replaceText: false);
        }
        catch (Exception exception) when (exception is XmpException or IOException or UnauthorizedAccessException)
        {
            Diagnostics.DebugLog.WriteLine($"Could not read search metadata from {sidecarPath}: {exception.Message}");
        }
    }

    private static void ReadExifData(IReadOnlyList<MetadataExtractor.Directory> directories, ImageEntry entry)
    {
        // IFD0 - basic image info
        var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        if (ifd0 is not null)
        {
            entry.CameraMake = ifd0.GetDescription(ExifDirectoryBase.TagMake)?.Trim();
            entry.CameraModel = ifd0.GetDescription(ExifDirectoryBase.TagModel)?.Trim();
            entry.Title = ifd0.GetDescription(0x9C9B)?.Trim(); // XPTitle (System.Title)
            entry.Caption = ifd0.GetDescription(0x9C9C)?.Trim()
                ?? ifd0.GetDescription(ExifDirectoryBase.TagImageDescription)?.Trim();
            if (ifd0.TryGetInt32(ExifDirectoryBase.TagOrientation, out var orientation))
                entry.Orientation = orientation;
        }

        // SubIFD - detailed shooting info
        var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        if (subIfd is not null)
        {
            if (subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dateTaken))
                entry.DateTaken = dateTaken;

            // Format exposure consistently: "1/N" for sub-second, "N" or "N.X" for >= 1 sec.
            // Stored WITHOUT unit; UI appends " sec".
            if (subIfd.TryGetRational(ExifDirectoryBase.TagExposureTime, out var expRational))
            {
                entry.ExposureTime = FormatExposureTime(expRational.ToDouble());
            }
            else
            {
                // Fallback: take MetadataExtractor's pre-formatted string and strip any unit suffix.
                var raw = subIfd.GetDescription(ExifDirectoryBase.TagExposureTime);
                entry.ExposureTime = StripSecondsUnit(raw);
            }

            entry.LensModel = subIfd.GetDescription(ExifDirectoryBase.TagLensModel)?.Trim();

            if (subIfd.TryGetRational(ExifDirectoryBase.TagFNumber, out var fNumber))
                entry.Aperture = fNumber.ToDouble();
            if (subIfd.TryGetRational(ExifDirectoryBase.TagFocalLength, out var focal))
                entry.FocalLength = focal.ToDouble();
            if (subIfd.TryGetInt32(ExifDirectoryBase.TagIsoEquivalent, out var iso))
                entry.Iso = iso;
            if (subIfd.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out var w))
                entry.Width = w;
            if (subIfd.TryGetInt32(ExifDirectoryBase.TagExifImageHeight, out var h))
                entry.Height = h;
        }

        // GPS
        var gps = directories.OfType<GpsDirectory>().FirstOrDefault();
        if (gps is not null)
        {
            var location = gps.GetGeoLocation();
            if (location is not null)
            {
                entry.GpsLatitude = location.Value.Latitude;
                entry.GpsLongitude = location.Value.Longitude;
            }
        }
    }

    private static void ReadIptcData(IReadOnlyList<MetadataExtractor.Directory> directories, ImageEntry entry)
    {
        var iptc = directories.OfType<IptcDirectory>().FirstOrDefault();
        if (iptc is null) return;

        entry.Title ??= iptc.GetDescription(IptcDirectory.TagObjectName);
        entry.Caption ??= iptc.GetDescription(IptcDirectory.TagCaption);
        if (iptc.TryGetInt32(IptcDirectory.TagUrgency, out var rating))
            entry.Rating ??= Math.Clamp(rating, 0, 5);
    }

    private static void ReadXmpData(IReadOnlyList<MetadataExtractor.Directory> directories, ImageEntry entry)
    {
        var xmp = directories.OfType<XmpDirectory>().FirstOrDefault();
        if (xmp?.XmpMeta is null) return;
        ReadXmpData(xmp.XmpMeta, entry, replaceText: true);
    }

    private static void ReadXmpData(IXmpMeta xmp, ImageEntry entry, bool replaceText)
    {

        const string dcNs = "http://purl.org/dc/elements/1.1/";
        const string tiffNs = "http://ns.adobe.com/tiff/1.0/";
        const string exifNs = "http://ns.adobe.com/exif/1.0/";
        const string xapNs = "http://ns.adobe.com/xap/1.0/";
        entry.CameraMake ??= xmp.GetPropertyString(tiffNs, "Make");
        entry.CameraModel ??= xmp.GetPropertyString(tiffNs, "Model");
        if (entry.Width == 0 && xmp.DoesPropertyExist(tiffNs, "ImageWidth"))
            entry.Width = xmp.GetPropertyInteger(tiffNs, "ImageWidth");
        if (entry.Height == 0 && xmp.DoesPropertyExist(tiffNs, "ImageLength"))
            entry.Height = xmp.GetPropertyInteger(tiffNs, "ImageLength");
        if (!entry.DateTaken.HasValue)
        {
            var date = xmp.GetPropertyString(exifNs, "DateTimeOriginal")
                ?? xmp.GetPropertyString(xapNs, "CreateDate");
            if (DateTime.TryParse(date, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dateTaken))
                entry.DateTaken = dateTaken;
        }
        if (replaceText || string.IsNullOrWhiteSpace(entry.Title))
            entry.Title = xmp.GetLocalizedText(dcNs, "dc:title", "", "x-default")?.Value ?? entry.Title;
        if (replaceText || string.IsNullOrWhiteSpace(entry.Caption))
            entry.Caption = xmp.GetLocalizedText(dcNs, "dc:description", "", "x-default")?.Value ?? entry.Caption;
        entry.GpsLatitude ??= ReadCoordinate(xmp.GetPropertyString(exifNs, "GPSLatitude"));
        entry.GpsLongitude ??= ReadCoordinate(xmp.GetPropertyString(exifNs, "GPSLongitude"));
        // Read rating from XMP if not already set
        if (xmp.DoesPropertyExist(xapNs, "xmp:Rating"))
        {
            var ratingProp = xmp.GetPropertyInteger(xapNs, "xmp:Rating");
            entry.Rating ??= Math.Clamp(ratingProp, 0, 5);
        }

        // PhotoLibrarian user flag (custom namespace — see MetadataWriterService)
        if (xmp.DoesPropertyExist(MetadataWriterService.PhotoLibrarianNamespace, "plib:Flagged"))
        {
            var flag = xmp.GetPropertyString(MetadataWriterService.PhotoLibrarianNamespace, "plib:Flagged");
            entry.IsFlagged = string.Equals(flag, "True", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static double? ReadCoordinate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var direction = char.ToUpperInvariant(value[^1]);
        if (direction is not ('N' or 'S' or 'E' or 'W')) return null;
        var parts = value[..^1].Split(',');
        if (parts.Length is not (2 or 3)) return null;
        double coordinate = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], System.Globalization.CultureInfo.InvariantCulture, out var part) ||
                !double.IsFinite(part) || part < 0 || (i > 0 && part >= 60))
                return null;
            coordinate += part / Math.Pow(60, i);
        }
        return coordinate <= (direction is 'N' or 'S' ? 90 : 180)
            ? (direction is 'S' or 'W' ? -coordinate : coordinate)
            : null;
    }

    /// <summary>
    /// Reads tags (dc:subject keywords) from an image file's XMP metadata.
    /// </summary>
    public List<string> ReadTags(string filePath)
    {
        var tags = new List<string>();
        try
        {
            var directories = ImageMetadataReader.ReadMetadata(filePath);
            var xmp = directories.OfType<XmpDirectory>().FirstOrDefault();
            if (xmp?.XmpMeta is not null)
            {
                const string dcNs = "http://purl.org/dc/elements/1.1/";
                int count = xmp.XmpMeta.CountArrayItems(dcNs, "dc:subject");
                for (int i = 1; i <= count; i++)
                {
                    var tag = xmp.XmpMeta.GetArrayItem(dcNs, "dc:subject", i)?.Value;
                    if (!string.IsNullOrWhiteSpace(tag))
                        tags.Add(tag);
                }
            }
        }
        catch { /* File may not have XMP */ }

        foreach (var tag in TagWriterService.ReadTagsFromSidecar(filePath))
        {
            if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                tags.Add(tag);
        }
        return tags;
    }

    /// <summary>
    /// Formats an exposure time (in seconds) consistently:
    /// - >= 1 second: "N" or "N.X" (e.g. "1", "1.3", "30")
    /// - &lt; 1 second: "1/N" (e.g. "1/60", "1/4000") — N is rounded to the nearest standard.
    /// The caller appends the " sec" unit.
    /// </summary>
    public static string FormatExposureTime(double seconds)
    {
        if (seconds <= 0) return "";

        if (seconds >= 1.0)
        {
            // Whole seconds when close enough, otherwise one decimal place.
            if (Math.Abs(seconds - Math.Round(seconds)) < 0.05)
                return Math.Round(seconds).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
            return seconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        }

        // Sub-second — always as 1/N for camera-friendly display.
        // Use AwayFromZero so e.g. 0.4 sec → 1/round(2.5) → 1/3 (closer approximation than 1/2).
        double denom = Math.Round(1.0 / seconds, MidpointRounding.AwayFromZero);
        if (denom < 1) denom = 1;
        return $"1/{denom.ToString("0", System.Globalization.CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Strips a trailing " sec" / "sec" / "s" unit from a pre-formatted exposure string.
    /// Used as a safety net for legacy DB rows and for MetadataExtractor's auto-formatted output.
    /// </summary>
    public static string? StripSecondsUnit(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        var trimmed = raw.Trim();
        foreach (var suffix in new[] { " sec", " seconds", " s" })
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(0, trimmed.Length - suffix.Length).TrimEnd();
        }
        // Try to re-parse + reformat if it's a plain number — gives us consistent fractions for legacy rows
        if (double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds))
        {
            return FormatExposureTime(seconds);
        }
        return trimmed;
    }
}
