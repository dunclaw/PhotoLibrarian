// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Windows.ApplicationModel.Resources;

namespace PhotoLibrarian.Services;

internal static class VideoTrimStrings
{
    private static readonly ResourceLoader Loader = new(
        Path.Combine(AppContext.BaseDirectory, "PhotoLibrarian.pri"),
        "Resources");

    public static string Get(string key) => Loader.GetString(key);

    public static string Format(string key, params object?[] values) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, Get(key), values);
}
