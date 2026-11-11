// Copyright (c) Duncan Lawler. All Rights Reserved.

using System.Globalization;

namespace PhotoLibrarian.Services;

public static class MapLinkService
{
    public static bool TryCreateUri(MapProvider provider, double? latitude, double? longitude, out Uri? uri)
    {
        uri = null;
        if (latitude is not double lat || longitude is not double lon ||
            !double.IsFinite(lat) || !double.IsFinite(lon) ||
            lat is < -90 or > 90 || lon is < -180 or > 180)
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
}
