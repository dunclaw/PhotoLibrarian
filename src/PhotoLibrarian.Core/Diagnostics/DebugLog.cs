namespace PhotoLibrarian.Core.Diagnostics;

/// <summary>
/// Global debug logging control for Core library.
/// Set EnableLogging = true to see diagnostic output.
/// </summary>
public static class DebugLog
{
    public static bool EnableLogging { get; set; } = true;

    public static void WriteLine(string message)
    {
        if (EnableLogging)
        {
            System.Diagnostics.Debug.WriteLine(message);
        }
    }

    public static void WriteLine(string format, params object[] args)
    {
        if (EnableLogging)
        {
            System.Diagnostics.Debug.WriteLine(string.Format(format, args));
        }
    }
}
