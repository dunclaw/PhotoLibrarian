// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Windows.ApplicationModel.Resources;

namespace PhotoLibrarian.Services;

internal static class MotionPhotoStrings
{
    private static readonly ResourceLoader Loader = new(
        Path.Combine(AppContext.BaseDirectory, "PhotoLibrarian.pri"), "Resources");

    public static string Get(string key) => Loader.GetString(key);
}
