// Copyright (c) Duncan Lawler. All Rights Reserved.
using MetadataExtractor.Formats.Exif;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using XmpCore;

namespace PhotoLibrarian.Core.Services;

public readonly record struct GeoCoordinate
{
    public double Latitude { get; }
    public double Longitude { get; }

    public GeoCoordinate(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || latitude is < -90 or > 90)
            throw new ArgumentOutOfRangeException(nameof(latitude), "Latitude must be between -90 and 90 degrees.");
        if (!double.IsFinite(longitude) || longitude is < -180 or > 180)
            throw new ArgumentOutOfRangeException(nameof(longitude), "Longitude must be between -180 and 180 degrees.");

        Latitude = latitude;
        Longitude = longitude;
    }
}

public enum GeotagWriteLocation
{
    EmbeddedExif,
    XmpSidecar
}

public sealed record GeotagWriteAttempt(
    string ImagePath,
    GeotagWriteLocation? Location,
    string? Error);

/// <summary>
/// Writes GPS coordinates into JPEG/TIFF EXIF metadata, using an adjacent XMP sidecar for
/// formats without a supported EXIF GPS writer. Embedded edits are staged and verified before
/// replacing the original; the original is backed up before commit.
/// </summary>
public sealed class GeotagMetadataWriterService
{
    private const string ExifNamespace = "http://ns.adobe.com/exif/1.0/";
    private static readonly HashSet<string> ExifGpsExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".tif", ".tiff"
    };

    private readonly OriginalBackupService _backupService;

    public GeotagMetadataWriterService(OriginalBackupService backupService)
    {
        _backupService = backupService;
    }

    public async Task<GeotagWriteLocation> WriteAsync(string imagePath, GeoCoordinate coordinate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        if (!File.Exists(imagePath))
            throw new FileNotFoundException("The selected photo no longer exists.", imagePath);

        if (!ExifGpsExtensions.Contains(Path.GetExtension(imagePath)))
        {
            await WriteSidecarAsync(imagePath, coordinate);
            return GeotagWriteLocation.XmpSidecar;
        }

        try
        {
            await WriteEmbeddedExifAsync(imagePath, coordinate);
            return GeotagWriteLocation.EmbeddedExif;
        }
        catch (Exception exception) when (IsEmbeddedWriteFailure(exception))
        {
            await WriteSidecarAsync(imagePath, coordinate);
            MetadataWriterService.ReportSidecarFallback(imagePath, "GPS coordinates", exception);
            return GeotagWriteLocation.XmpSidecar;
        }
    }

    public async Task<IReadOnlyList<GeotagWriteAttempt>> WriteBatchAsync(
        IReadOnlyList<string> imagePaths,
        GeoCoordinate coordinate)
    {
        var attempts = new List<GeotagWriteAttempt>(imagePaths.Count);
        foreach (var imagePath in imagePaths)
        {
            try
            {
                var location = await WriteAsync(imagePath, coordinate);
                attempts.Add(new GeotagWriteAttempt(imagePath, location, null));
            }
            catch (Exception exception) when (
                exception is not OutOfMemoryException and not StackOverflowException)
            {
                attempts.Add(new GeotagWriteAttempt(imagePath, null, exception.Message));
            }
        }

        return attempts;
    }

    private async Task WriteEmbeddedExifAsync(string imagePath, GeoCoordinate coordinate)
    {
        await _backupService.BackupOriginalAsync(imagePath);

        var extension = Path.GetExtension(imagePath);
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(imagePath)!,
            $"{Path.GetFileNameWithoutExtension(imagePath)}.geotag-{Guid.NewGuid():N}{extension}");

        try
        {
            File.Copy(imagePath, temporaryPath);
            await EmbeddedMetadataWriter.WriteGeotagAsync(temporaryPath, coordinate);
            VerifyEmbeddedLocation(temporaryPath, coordinate);
            File.Replace(temporaryPath, imagePath, null);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void VerifyEmbeddedLocation(string imagePath, GeoCoordinate expected)
    {
        var location = MetadataExtractor.ImageMetadataReader.ReadMetadata(imagePath)
            .OfType<GpsDirectory>()
            .FirstOrDefault()
            ?.GetGeoLocation();

        if (location is null ||
            Math.Abs(location.Value.Latitude - expected.Latitude) > 0.000001 ||
            Math.Abs(location.Value.Longitude - expected.Longitude) > 0.000001)
        {
            throw new InvalidDataException("The written EXIF GPS coordinates could not be verified.");
        }
    }

    private static async Task WriteSidecarAsync(string imagePath, GeoCoordinate coordinate)
    {
        var sidecarPath = Path.ChangeExtension(imagePath, ".xmp");
        var directory = Path.GetDirectoryName(sidecarPath)!;
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(sidecarPath)}.{Guid.NewGuid():N}.tmp");
        var originalSnapshot = TagWriterService.CaptureSidecarSnapshot(imagePath);

        try
        {
            IXmpMeta xmp = originalSnapshot.Exists
                ? XmpMetaFactory.ParseFromString(await File.ReadAllTextAsync(sidecarPath))
                : XmpMetaFactory.Create();

            xmp.SetProperty(ExifNamespace, "exif:GPSLatitude", FormatXmpCoordinate(coordinate.Latitude, 'N', 'S'));
            xmp.SetProperty(ExifNamespace, "exif:GPSLongitude", FormatXmpCoordinate(coordinate.Longitude, 'E', 'W'));

            var serialized = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
            var bytes = Encoding.UTF8.GetBytes(serialized);
            await using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }

            if (TagWriterService.CaptureSidecarSnapshot(imagePath) != originalSnapshot)
                throw new IOException("The XMP sidecar changed during geotag editing. Refresh and retry.");

            if (originalSnapshot.Exists)
            {
                var backupPath = $"{sidecarPath}.{Guid.NewGuid():N}.bak";
                File.Replace(temporaryPath, sidecarPath, backupPath);
            }
            else
                File.Move(temporaryPath, sidecarPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static string FormatXmpCoordinate(double coordinate, char positiveReference, char negativeReference)
    {
        var components = ToDegreesMinutesSeconds(coordinate);
        var reference = coordinate < 0 ? negativeReference : positiveReference;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{components.Degrees},{components.Minutes},{components.Seconds:0.######}{reference}");
    }

    internal static (uint Degrees, uint Minutes, double Seconds) ToDegreesMinutesSeconds(double coordinate)
    {
        var absolute = Math.Abs(coordinate);
        var degrees = (uint)Math.Floor(absolute);
        var totalMinutes = (absolute - degrees) * 60;
        var minutes = (uint)Math.Floor(totalMinutes);
        var seconds = (totalMinutes - minutes) * 60;

        seconds = Math.Round(seconds, 6, MidpointRounding.AwayFromZero);
        if (seconds >= 60)
        {
            seconds = 0;
            minutes++;
        }
        if (minutes >= 60)
        {
            minutes = 0;
            degrees++;
        }

        return (degrees, minutes, seconds);
    }

    private static bool IsEmbeddedWriteFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
            NotSupportedException or InvalidOperationException or ArgumentException or
            COMException;
}
