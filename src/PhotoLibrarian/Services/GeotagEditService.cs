// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;

namespace PhotoLibrarian.Services;

public sealed record GeotagBatchResult(
    int AppliedCount,
    int TotalCount,
    int SidecarCount,
    IReadOnlyList<string> Failures);

internal sealed class GeotagEditService
{
    private readonly GeotagMetadataWriterService _writer;
    private readonly ImageRepository? _imageRepo;

    public GeotagEditService(GeotagMetadataWriterService writer, ImageRepository? imageRepo)
    {
        _writer = writer;
        _imageRepo = imageRepo;
    }

    public async Task<int> WritePreparedCopiesAsync(
        IEnumerable<string> copies,
        GeoCoordinate coordinate)
    {
        var sidecarCount = 0;
        foreach (var copy in copies)
        {
            if (await _writer.WriteAsync(copy, coordinate) == GeotagWriteLocation.XmpSidecar)
                sidecarCount++;
        }
        return sidecarCount;
    }

    public async Task<GeotagBatchResult> WriteDirectAsync(
        IReadOnlyList<ImageEntry> entries,
        GeoCoordinate coordinate)
    {
        var failures = new List<string>();
        var appliedCount = 0;
        var sidecarCount = 0;
        var attempts = await _writer.WriteBatchAsync(
            entries.Select(entry => entry.FilePath).ToList(),
            coordinate);

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var attempt = attempts[index];
            if (attempt.Location is not { } writeLocation)
            {
                failures.Add($"{entry.FileName}: {attempt.Error}");
                continue;
            }

            if (writeLocation == GeotagWriteLocation.XmpSidecar)
                sidecarCount++;

            entry.GpsLatitude = coordinate.Latitude;
            entry.GpsLongitude = coordinate.Longitude;
            appliedCount++;

            if (_imageRepo is not null && entry.Id > 0)
            {
                try
                {
                    var fileInfo = new FileInfo(entry.FilePath);
                    entry.FileSize = fileInfo.Length;
                    entry.DateModified = fileInfo.LastWriteTimeUtc;
                    await _imageRepo.UpdateGpsCoordinatesAsync(
                        entry.Id,
                        coordinate.Latitude,
                        coordinate.Longitude,
                        entry.FileSize,
                        entry.DateModified);
                }
                catch (Exception exception) when (
                    exception is not OutOfMemoryException and not StackOverflowException)
                {
                    failures.Add(
                        $"{entry.FileName}: coordinates were written, but the library index could not be updated: {exception.Message}");
                }
            }
        }

        return new GeotagBatchResult(appliedCount, entries.Count, sidecarCount, failures);
    }
}
