// Copyright (c) Duncan Lawler. All Rights Reserved.

namespace PhotoLibrarian.Services;

internal static class PhotoImportStrings
{
    public static string Get(string key) => ResourceStrings.Get(key);

    public static string Format(string key, params object?[] values) =>
        ResourceStrings.Format(key, values);
}
