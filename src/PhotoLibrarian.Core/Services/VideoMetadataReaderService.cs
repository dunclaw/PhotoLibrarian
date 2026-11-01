// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Runtime.InteropServices;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace PhotoLibrarian.Core.Services;

public sealed record VideoFileMetadata(
    TimeSpan? Duration,
    uint? Width,
    uint? Height,
    uint? Bitrate,
    double? FrameRate,
    string? Codec,
    string? Title,
    string? Artist,
    string? Album,
    uint? Year,
    DateTimeOffset? DateTaken,
    DateTimeOffset? DateEncoded,
    string? CameraMake,
    string? CameraModel,
    double? Latitude,
    double? Longitude,
    string? ExtractionError);

/// <summary>
/// Reads local video properties through Windows without blocking the calling thread.
/// </summary>
public static class VideoMetadataReaderService
{
    public static Task<VideoFileMetadata> ReadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(filePath))
                throw new FileNotFoundException("The selected video no longer exists.", filePath);

            var file = await StorageFile.GetFileFromPathAsync(filePath)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            var shell = await file.Properties.RetrievePropertiesAsync(
                [
                    "System.Title", "System.Author", "System.Music.AlbumTitle", "System.Media.Year",
                    "System.Photo.DateTaken", "System.Media.DateEncoded",
                    "System.Photo.CameraManufacturer", "System.Photo.CameraModel",
                    "System.GPS.Latitude", "System.GPS.Longitude"
                ])
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            var properties = await file.Properties.GetVideoPropertiesAsync()
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            double? frameRate = null;
            string? codec = null;
            string? extractionError = null;
            try
            {
                var profile = await MediaEncodingProfile.CreateFromFileAsync(file)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);
                if (profile.Video is { } video)
                {
                    if (video.FrameRate.Numerator > 0 && video.FrameRate.Denominator > 0)
                        frameRate = video.FrameRate.Numerator / (double)video.FrameRate.Denominator;
                    codec = NullIfWhiteSpace(video.Subtype);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or
                                               UnauthorizedAccessException or NotSupportedException or
                                               ArgumentException or
                                               COMException or InvalidOperationException)
            {
                Diagnostics.DebugLog.WriteLine(
                    $"Could not read video encoding details for {filePath}: {exception}");
                extractionError = exception.Message;
            }

            var metadata = new VideoFileMetadata(
                properties.Duration > TimeSpan.Zero ? properties.Duration : null,
                properties.Width > 0 ? properties.Width : null,
                properties.Height > 0 ? properties.Height : null,
                properties.Bitrate > 0 ? properties.Bitrate : null,
                frameRate,
                codec,
                ReadText(shell, "System.Title"),
                ReadAuthors(shell),
                ReadText(shell, "System.Music.AlbumTitle"),
                ReadYear(shell),
                ReadDate(shell, "System.Photo.DateTaken"),
                ReadDate(shell, "System.Media.DateEncoded"),
                ReadText(shell, "System.Photo.CameraManufacturer"),
                ReadText(shell, "System.Photo.CameraModel"),
                ReadDouble(shell, "System.GPS.Latitude"),
                ReadDouble(shell, "System.GPS.Longitude"),
                extractionError);
            if (metadata.Duration is null && metadata.Width is null && metadata.Height is null &&
                metadata.Bitrate is null && metadata.Title is null && metadata.Artist is null &&
                metadata.Album is null && metadata.Year is null && metadata.DateTaken is null &&
                metadata.DateEncoded is null &&
                metadata.CameraMake is null && metadata.CameraModel is null &&
                metadata.Latitude is null && metadata.Longitude is null)
                throw new InvalidDataException("Windows did not return any video properties.");

            return metadata;
        }, cancellationToken);
    }

    private static string? ReadText(IDictionary<string, object> properties, string key) =>
        properties.TryGetValue(key, out var value) && value is string text
            ? NullIfWhiteSpace(text)
            : null;

    private static string? ReadAuthors(IDictionary<string, object> properties)
    {
        if (!properties.TryGetValue("System.Author", out var value)) return null;
        if (value is string author) return NullIfWhiteSpace(author);
        if (value is not IEnumerable<string> authors) return null;

        var names = authors.Where(author => !string.IsNullOrWhiteSpace(author))
            .Select(author => author.Trim());
        var result = string.Join(", ", names);
        return NullIfWhiteSpace(result);
    }

    private static uint? ReadYear(IDictionary<string, object> properties) =>
        properties.TryGetValue("System.Media.Year", out var value) && value is uint year && year > 0
            ? year
            : null;

    private static DateTimeOffset? ReadDate(IDictionary<string, object> properties, string key) =>
        properties.TryGetValue(key, out var value) switch
        {
            true when value is DateTimeOffset date => date,
            true when value is DateTime date => new DateTimeOffset(date),
            _ => null
        };

    private static double? ReadDouble(IDictionary<string, object> properties, string key) =>
        properties.TryGetValue(key, out var value) && value is double coordinate
            ? coordinate
            : null;

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
