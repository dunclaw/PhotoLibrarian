// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Windows.ApplicationModel.Resources;

namespace PhotoLibrarian.Services;

internal static class ComparisonStrings
{
    private static readonly ResourceLoader Loader = new(
        Path.Combine(AppContext.BaseDirectory, "PhotoLibrarian.pri"), "Resources");
    internal static string Get(string key) => Loader.GetString(key);
    internal static string Format(string key, params object?[] values) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, Get(key), values);
}
