// Copyright (c) Duncan Lawler. All Rights Reserved.
namespace PhotoLibrarian.Diagnostics;

/// <summary>
/// App-side entry point for diagnostic logging. Forwards to the shared Core log so app and
/// library messages are written, in order, by the same background writer.
/// </summary>
public static class DebugLog
{
    public static bool EnableLogging
    {
        get => Core.Diagnostics.DebugLog.EnableLogging;
        set => Core.Diagnostics.DebugLog.EnableLogging = value;
    }

    public static void WriteLine(string message) => Core.Diagnostics.DebugLog.WriteLine(message);

    public static void WriteLine(string format, params object[] args) =>
        Core.Diagnostics.DebugLog.WriteLine(format, args);
}
