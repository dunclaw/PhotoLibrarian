// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace PhotoLibrarian.Services;

internal static class DuplicateReviewStrings
{
    private static readonly ResourceLoader Loader = new(
        Path.Combine(AppContext.BaseDirectory, "PhotoLibrarian.pri"),
        "Resources");

    public static string Get(string key) => Loader.GetString(key);

    public static string Format(string key, params object?[] values) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), values);

    public static string GetExceptionMessage(Exception exception) =>
        exception is DuplicateCandidateValidationException validationException
            ? Get(validationException.ResourceKey)
            : exception.Message;
}
