namespace PhotoLibrarian.Diagnostics;

/// <summary>
/// Global debug logging control.
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
