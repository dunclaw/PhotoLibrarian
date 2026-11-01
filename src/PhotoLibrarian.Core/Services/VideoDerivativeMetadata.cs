// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Globalization;
using MetadataExtractor;
using MetadataExtractor.Formats.Xmp;
using Windows.Storage;
using XmpCore;
using XmpCore.Options;

namespace PhotoLibrarian.Core.Services;

/// <summary>Portable descriptive metadata only; encoded track details and pixel geometry are not copied.</summary>
internal static class VideoDerivativeMetadata
{
    private const string Dc = "http://purl.org/dc/elements/1.1/";
    private const string Xap = "http://ns.adobe.com/xap/1.0/";
    private const string Exif = "http://ns.adobe.com/exif/1.0/";
    private const string Tiff = "http://ns.adobe.com/tiff/1.0/";
    private static readonly string[] ShellKeys =
    [
        "System.Title", "System.Comment", "System.Subject", "System.Author",
        "System.Copyright", "System.Keywords", "System.Rating",
        "System.Photo.DateTaken", "System.Media.DateEncoded",
        "System.Photo.CameraManufacturer", "System.Photo.CameraModel"
    ];

    internal static async Task<string> ReadAsync(StorageFile source, CancellationToken token)
    {
        IXmpMeta? embedded = null;
        try
        {
            embedded = ImageMetadataReader.ReadMetadata(source.Path)
                .OfType<XmpDirectory>().FirstOrDefault()?.XmpMeta;
        }
        catch (ImageProcessingException ex)
        {
            // Windows supports some video containers that MetadataExtractor does not.
            Diagnostics.DebugLog.WriteLine($"Reading video description through Windows properties: {ex.Message}");
        }

        var sidecarPath = FaceMetadataStore.GetSidecarPathForImage(source.Path);
        var sidecar = File.Exists(sidecarPath)
            ? XmpMetaFactory.ParseFromString(await File.ReadAllTextAsync(sidecarPath, token))
            : null;
        var shell = await source.Properties.RetrievePropertiesAsync(ShellKeys).AsTask(token);
        var video = await source.Properties.GetVideoPropertiesAsync().AsTask(token);
        return CreateXmp(embedded, sidecar, shell, video.Latitude, video.Longitude);
    }

    internal static string CreateXmp(
        IXmpMeta? embedded, IXmpMeta? sidecar, IDictionary<string, object> shell,
        double? latitude = null, double? longitude = null)
    {
        var output = XmpMetaFactory.Create();
        foreach (var (ns, name) in new[]
        {
            (Dc, "title"), (Dc, "description"), (Dc, "subject"), (Dc, "creator"),
            (Dc, "rights"), (Dc, "publisher"), (Dc, "contributor"),
            (Xap, "Rating"), (Xap, "CreateDate"),
            (Exif, "DateTimeOriginal"), (Exif, "GPSLatitude"), (Exif, "GPSLongitude"),
            (Exif, "GPSAltitude"), (Exif, "GPSAltitudeRef"),
            (Tiff, "Make"), (Tiff, "Model"),
            ("http://ns.adobe.com/photoshop/1.0/", "City"),
            ("http://ns.adobe.com/photoshop/1.0/", "State"),
            ("http://ns.adobe.com/photoshop/1.0/", "Country"),
            (MetadataWriterService.PhotoLibrarianNamespace, "Flagged")
        })
        {
            var owner = sidecar?.DoesPropertyExist(ns, name) == true ? sidecar : embedded;
            if (owner?.DoesPropertyExist(ns, name) == true)
                XmpUtils.DuplicateSubtree(owner, output, ns, name, ns, name, new PropertyOptions());
        }

        SetText("title", "System.Title");
        SetText("description", "System.Comment");
        SetText("description", "System.Subject");
        SetText("rights", "System.Copyright");
        SetArray("creator", "System.Author", ordered: true);
        SetArray("subject", "System.Keywords", ordered: false);
        SetString(Tiff, "Make", "System.Photo.CameraManufacturer");
        SetString(Tiff, "Model", "System.Photo.CameraModel");
        if (!output.DoesPropertyExist(Xap, "Rating") &&
            shell.TryGetValue("System.Rating", out var rating) && rating is uint value && value > 0)
        {
            // Windows uses 1-99, whereas XMP uses 1-5 stars.
            output.SetPropertyInteger(Xap, "Rating", value < 13 ? 1 : value < 38 ? 2 : value < 63 ? 3 : value < 88 ? 4 : 5);
        }
        if (!output.DoesPropertyExist(Exif, "DateTimeOriginal") && !output.DoesPropertyExist(Xap, "CreateDate"))
        {
            if (!SetDate("System.Photo.DateTaken"))
                SetDate("System.Media.DateEncoded");
        }
        SetCoordinate("GPSLatitude", latitude, "N", "S");
        SetCoordinate("GPSLongitude", longitude, "E", "W");
        return XmpMetaFactory.SerializeToString(output, new SerializeOptions()).TrimStart('\uFEFF');

        void SetText(string name, string key)
        {
            if (!output.DoesPropertyExist(Dc, name) &&
                shell.TryGetValue(key, out var property) && property is string { Length: > 0 } text)
                output.SetLocalizedText(Dc, name, "", "x-default", text);
        }

        void SetString(string ns, string name, string key)
        {
            if (!output.DoesPropertyExist(ns, name) &&
                shell.TryGetValue(key, out var property) && property is string { Length: > 0 } text)
                output.SetProperty(ns, name, text);
        }

        void SetArray(string name, string key, bool ordered)
        {
            if (output.DoesPropertyExist(Dc, name) || !shell.TryGetValue(key, out var property) ||
                property is not IEnumerable<string> values) return;
            foreach (var text in values.Where(text => !string.IsNullOrWhiteSpace(text)))
                output.AppendArrayItem(Dc, name,
                    new PropertyOptions { IsArray = true, IsArrayOrdered = ordered }, text, null);
        }

        bool SetDate(string key)
        {
            if (!shell.TryGetValue(key, out var property) || property is not DateTimeOffset date)
                return false;
            output.SetProperty(Exif, "DateTimeOriginal", date.ToString("o", CultureInfo.InvariantCulture));
            return true;
        }

        void SetCoordinate(string name, double? coordinate, string positive, string negative)
        {
            if (output.DoesPropertyExist(Exif, name) || !coordinate.HasValue) return;
            var absolute = Math.Abs(coordinate.Value);
            var degrees = (int)absolute;
            var minutes = (absolute - degrees) * 60;
            output.SetProperty(Exif, name,
                $"{degrees},{minutes.ToString("R", CultureInfo.InvariantCulture)}{(coordinate >= 0 ? positive : negative)}");
        }
    }
}
