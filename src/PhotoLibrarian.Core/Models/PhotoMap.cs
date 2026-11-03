// Copyright (c) Duncan Lawler. All Rights Reserved.

namespace PhotoLibrarian.Core.Models;

public sealed record PhotoMapPoint(string FilePath, double Latitude, double Longitude);

/// <summary>Unwrapped longitude bounds; equirectangular, not a distance-preserving map.</summary>
public sealed record PhotoMapViewport(double West, double South, double Width, double Height)
{
    public double North => South + Height;
    public double East => West + Width;

    public PhotoMapViewport Zoom(double factor) => Resize(
        Math.Clamp(Width * factor, 0.00001, 360),
        Math.Clamp(Height * factor, 0.00001, 180));

    private PhotoMapViewport Resize(double width, double height) =>
        new(West + (Width - width) / 2,
            Math.Clamp(South + (Height - height) / 2, -90, 90 - height), width, height);

    public PhotoMapViewport Pan(double horizontal, double vertical) =>
        new(West + horizontal * Width,
            Math.Clamp(South + vertical * Height, -90, 90 - Height), Width, Height);
}

public sealed record PhotoMapCluster(
    double X, double Y, double Latitude, double Longitude, IReadOnlyList<string> Paths);

/// <summary>
/// Pure in-memory GPS geometry: no map SDK, tiles, network, file or index writes.
/// Screen-cell clustering bounds both visual and keyboard targets to 128.
/// </summary>
public sealed class PhotoMap
{
    public IReadOnlyList<PhotoMapPoint> Points { get; }
    public int ExcludedCount { get; }
    public PhotoMapViewport Bounds { get; }

    private PhotoMap(IReadOnlyList<PhotoMapPoint> points, int excludedCount, PhotoMapViewport bounds)
    {
        Points = points;
        ExcludedCount = excludedCount;
        Bounds = bounds;
    }

    public static bool HasValidCoordinates(ImageEntry entry) =>
        entry.GpsLatitude is { } latitude && double.IsFinite(latitude) && Math.Abs(latitude) <= 90 &&
        entry.GpsLongitude is { } longitude && double.IsFinite(longitude) && Math.Abs(longitude) <= 180;

    public static double NormalizeLongitude(double longitude) =>
        ((longitude + 180) % 360 + 360) % 360 - 180;

    public static PhotoMap Create(IEnumerable<ImageEntry> entries, CancellationToken cancellationToken = default)
    {
        var points = new List<PhotoMapPoint>();
        int excluded = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasValidCoordinates(entry))
                points.Add(new(entry.FilePath, entry.GpsLatitude!.Value, entry.GpsLongitude!.Value));
            else
                excluded++;
        }
        if (points.Count == 0)
            return new(points, excluded, new(-180, -90, 360, 180));

        var longitudes = points.Select(point => (point.Longitude + 360) % 360).Order().ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        double largestGap = -1;
        double west = 0;
        for (int i = 0; i < longitudes.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double next = i + 1 == longitudes.Length ? longitudes[0] + 360 : longitudes[i + 1];
            if (next - longitudes[i] > largestGap)
            {
                largestGap = next - longitudes[i];
                west = next % 360;
            }
        }
        double arc = 360 - largestGap;
        double width = Math.Min(360, Math.Max(0.02, arc * 1.1));
        double south = points.Min(point => point.Latitude);
        double north = points.Max(point => point.Latitude);
        double height = Math.Min(180, Math.Max(0.02, (north - south) * 1.1));
        var bounds = new PhotoMapViewport(west - (width - arc) / 2,
            Math.Clamp((north + south - height) / 2, -90, 90 - height), width, height);
        return new(points, excluded, bounds);
    }

    public IReadOnlyList<PhotoMapCluster> Cluster(
        PhotoMapViewport viewport, double pixelWidth, double pixelHeight,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(pixelWidth) || !double.IsFinite(pixelHeight) ||
            pixelWidth <= 0 || pixelHeight <= 0 ||
            !double.IsFinite(viewport.West) || !double.IsFinite(viewport.South) ||
            !double.IsFinite(viewport.Width) || !double.IsFinite(viewport.Height) ||
            viewport.Width <= 0 || viewport.Width > 360 || viewport.Height <= 0 ||
            viewport.South < -90 || viewport.North > 90)
            throw new ArgumentOutOfRangeException(nameof(viewport), "Map dimensions and bounds must be finite and valid.");

        int columns = Math.Clamp((int)(Math.Min(pixelWidth, 1024) / 64), 1, 16);
        int rows = Math.Clamp((int)(Math.Min(pixelHeight, 512) / 64), 1, 8);
        var cells = new SortedDictionary<int, List<(PhotoMapPoint Point, double X, double Y)>>();
        double center = viewport.West + viewport.Width / 2;
        const double boundaryTolerance = 1e-9; // Longitude normalization can round exact viewport edges.
        foreach (var point in Points)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double longitude = center + NormalizeLongitude(point.Longitude - center);
            if (longitude < viewport.West - boundaryTolerance || longitude > viewport.East + boundaryTolerance ||
                point.Latitude < viewport.South - boundaryTolerance || point.Latitude > viewport.North + boundaryTolerance)
                continue;
            double x = Math.Clamp((longitude - viewport.West) / viewport.Width, 0, 1);
            double y = Math.Clamp((viewport.North - point.Latitude) / viewport.Height, 0, 1);
            int column = Math.Min(columns - 1, (int)(x * columns));
            int row = Math.Min(rows - 1, (int)(y * rows));
            int key = row * columns + column;
            if (!cells.TryGetValue(key, out var cell))
                cells[key] = cell = [];
            cell.Add((point, x, y));
        }
        var clusters = new List<PhotoMapCluster>(cells.Count);
        foreach (var cell in cells.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double x = cell.Average(item => item.X);
            double y = cell.Average(item => item.Y);
            clusters.Add(new(x, y, viewport.North - y * viewport.Height,
                NormalizeLongitude(viewport.West + x * viewport.Width),
                cell.Select(item => item.Point.FilePath).ToArray()));
        }
        return clusters;
    }
}
