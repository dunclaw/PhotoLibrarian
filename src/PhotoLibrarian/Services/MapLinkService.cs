// Copyright (c) Duncan Lawler. All Rights Reserved.

using System.Globalization;
using PhotoLibrarian.Core.Services;

namespace PhotoLibrarian.Services;

public static class MapLinkService
{
    public static bool TryCreateUri(MapProvider provider, double? latitude, double? longitude, out Uri? uri)
    {
        uri = null;
        if (latitude is not double lat || longitude is not double lon ||
            !GeoCoordinate.TryCreate(lat, lon, out _))
            return false;

        var coordinates = $"{lat.ToString("R", CultureInfo.InvariantCulture)},{lon.ToString("R", CultureInfo.InvariantCulture)}";
        var address = provider switch
        {
            MapProvider.Google => $"https://www.google.com/maps/search/?api=1&query={coordinates}",
            MapProvider.Bing => $"https://www.bing.com/maps?cp={lat.ToString("R", CultureInfo.InvariantCulture)}~{lon.ToString("R", CultureInfo.InvariantCulture)}&lvl=16",
            _ => null
        };
        if (address is null)
            return false;

        uri = new Uri(address);
        return true;
    }

    internal static Uri? GetBrowseUri(MapProvider provider) => provider switch
    {
        MapProvider.Google => new Uri("https://www.google.com/maps"),
        MapProvider.Bing => new Uri("https://www.bing.com/maps"),
        _ => null
    };

    internal static bool TryParsePoint(string text, out GeoCoordinate coordinate)
    {
        coordinate = default;
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) ||
            !uri.IsWellFormedOriginalString() || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            !TryReadQuery(uri.Query, out var query))
            return false;

        return uri.IdnHost switch
        {
            "www.google.com" or "google.com" => TryParseGooglePoint(uri.AbsolutePath, query, out coordinate),
            "www.bing.com" or "bing.com" => TryParseBingPoint(uri.AbsolutePath, query, out coordinate),
            "www.openstreetmap.org" or "openstreetmap.org" =>
                uri.AbsolutePath == "/" && !HasAny(query, "node", "way", "relation", "query", "route") &&
                query.TryGetValue("mlat", out var latitude) && query.TryGetValue("mlon", out var longitude) &&
                TryParseCoordinate(latitude, longitude, out coordinate),
            _ => false
        };
    }

    private static bool TryParseGooglePoint(
        string path, Dictionary<string, string> query, out GeoCoordinate coordinate)
    {
        coordinate = default;
        if (HasAny(query, "query_place_id", "q", "ftid", "cid", "place_id", "map_action", "data"))
            return false;

        if (path is "/maps/search" or "/maps/search/")
            return query.GetValueOrDefault("api") == "1" &&
                query.TryGetValue("query", out var point) &&
                TryParsePair(point, ',', out coordinate);

        var segments = path.Split('/');
        if (segments.Length is not (5 or 6) ||
            segments[1] != "maps" || segments[2] != "place" || segments[3].Length == 0 ||
            (segments.Length == 6 && !segments[4].StartsWith('@')) ||
            !segments[^1].StartsWith("data=", StringComparison.Ordinal) ||
            HasAny(query, "api", "query"))
            return false;

        // A browser place URL can carry both a camera position and a place. Accept
        // only one place coordinate pair from the data block, never the @ position.
        var fields = segments[^1][5..].Split('!');
        if (fields[0].Length != 0)
            return false;

        var latitudeIndex = -1;
        var longitudeIndex = -1;
        for (var index = 1; index < fields.Length; index++)
        {
            if (fields[index].StartsWith("3d", StringComparison.Ordinal))
            {
                if (latitudeIndex != -1) return false;
                latitudeIndex = index;
            }
            else if (fields[index].StartsWith("4d", StringComparison.Ordinal))
            {
                if (longitudeIndex != -1) return false;
                longitudeIndex = index;
            }
        }

        return latitudeIndex > 1 && longitudeIndex == latitudeIndex + 1 &&
            fields[latitudeIndex - 1] == "8m2" &&
            TryParseCoordinate(fields[latitudeIndex][2..], fields[longitudeIndex][2..], out coordinate);
    }

    private static bool TryParseBingPoint(
        string path, Dictionary<string, string> query, out GeoCoordinate coordinate)
    {
        coordinate = default;
        if (path is not ("/maps" or "/maps/" or "/maps/default.aspx") ||
            HasAny(query, "rtp", "cid", "ss", "where1") ||
            !query.TryGetValue("sp", out var pin) || pin.Contains('~') ||
            !pin.StartsWith("point.", StringComparison.OrdinalIgnoreCase))
            return false;

        var fields = pin[6..].Split('_', 3);
        return fields.Length >= 2 && TryParseCoordinate(fields[0], fields[1], out coordinate);
    }

    private static bool TryReadQuery(string rawQuery, out Dictionary<string, string> query)
    {
        query = new(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in rawQuery.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = parameter.Split('=', 2);
            var key = DecodeQueryComponent(parts[0]);
            var value = parts.Length == 2 ? DecodeQueryComponent(parts[1]) : "";
            if (key.Length == 0 || !query.TryAdd(key, value))
                return false;
        }
        return true;
    }

    private static string DecodeQueryComponent(string component) =>
        Uri.UnescapeDataString(component.Replace('+', ' '));

    private static bool HasAny(Dictionary<string, string> query, params string[] keys) =>
        keys.Any(query.ContainsKey);

    private static bool TryParsePair(string text, char separator, out GeoCoordinate coordinate)
    {
        var fields = text.Split(separator, 2);
        coordinate = default;
        return fields.Length == 2 && TryParseCoordinate(fields[0], fields[1], out coordinate);
    }

    private static bool TryParseCoordinate(string latitude, string longitude, out GeoCoordinate coordinate)
    {
        coordinate = default;
        return !latitude.Any(char.IsControl) && !longitude.Any(char.IsControl) &&
            double.TryParse(latitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) &&
            double.TryParse(longitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) &&
            GeoCoordinate.TryCreate(lat, lon, out coordinate);
    }
}
