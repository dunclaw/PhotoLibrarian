// Copyright (c) Duncan Lawler. All Rights Reserved.

using PhotoLibrarian.Diagnostics;
using Windows.ApplicationModel.DataTransfer;

namespace PhotoLibrarian.Services;

internal static class PhotoClipboardService
{
    public static string CopyText(string? text, string description) =>
        CopyText(text, description, WriteText);

    internal static string CopyText(string? text, string description, Action<string> writeText)
    {
        if (string.IsNullOrWhiteSpace(text))
            return $"Could not copy {description}: no value is available.";

        try
        {
            writeText(text);
            return $"Copied {description} to the clipboard.";
        }
        catch (Exception exception)
        {
            DebugLog.WriteLine($"Could not copy {description}: {exception}");
            return $"Could not copy {description}: {exception.Message}";
        }
    }

    private static void WriteText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }
}
