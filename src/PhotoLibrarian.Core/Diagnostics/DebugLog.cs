// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Collections.Concurrent;
using System.Text;

namespace PhotoLibrarian.Core.Diagnostics;

/// <summary>
/// Global diagnostic log. Messages are timestamped and handed to a background writer thread,
/// which sends them to the debugger output and, once <see cref="EnableFileLogging"/> is called,
/// to a size-limited log file. Callers (including the UI thread) never wait on the debugger's
/// output lock or on disk I/O.
/// </summary>
public static class DebugLog
{
    private const long MaxLogBytes = 5 * 1024 * 1024;
    private const int MaxQueuedMessages = 20000;

    private static readonly BlockingCollection<string> Pending =
        new(new ConcurrentQueue<string>(), MaxQueuedMessages);
    private static readonly object FileLock = new();
    private static string? _logFilePath;
    private static StreamWriter? _writer;

    static DebugLog()
    {
        var thread = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = "DebugLog writer",
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    public static bool EnableLogging { get; set; } = true;

    /// <summary>The current log file, or null when only debugger output is enabled.</summary>
    public static string? LogFilePath => _logFilePath;

    /// <summary>
    /// Starts appending to PhotoLibrarian.log in <paramref name="directory"/>. A log that is
    /// already over the size limit is renamed to PhotoLibrarian.previous.log first.
    /// </summary>
    public static void EnableFileLogging(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "PhotoLibrarian.log");
            lock (FileLock)
            {
                _writer?.Dispose();
                _writer = null;
                RotateIfTooLarge(path);
                _logFilePath = path;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not enable file logging in {directory}: {ex.Message}");
        }
    }

    public static void WriteLine(string message)
    {
        if (!EnableLogging) return;
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.CurrentManagedThreadId,3}] {message}";
        // Drop rather than block if the writer falls far behind.
        Pending.TryAdd(line);
    }

    public static void WriteLine(string format, params object[] args)
    {
        if (!EnableLogging) return;
        WriteLine(string.Format(format, args));
    }

    /// <summary>Waits briefly for queued messages to reach the log (for shutdown and tests).</summary>
    public static void Flush(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Pending.Count > 0 && DateTime.UtcNow < deadline)
            Thread.Sleep(10);
        lock (FileLock)
        {
            try
            {
                _writer?.Flush();
            }
            catch (IOException)
            {
            }
        }
    }

    private static void WriteLoop()
    {
        foreach (var line in Pending.GetConsumingEnumerable())
        {
            System.Diagnostics.Debug.WriteLine(line);
            AppendToFile(line, flush: Pending.Count == 0);
        }
    }

    private static void AppendToFile(string line, bool flush)
    {
        lock (FileLock)
        {
            if (_logFilePath is null) return;
            try
            {
                if (_writer is null)
                {
                    var stream = new FileStream(_logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    _writer = new StreamWriter(stream, new UTF8Encoding(false));
                }
                _writer.WriteLine(line);
                if (!flush) return;
                _writer.Flush();
                if (_writer.BaseStream.Length <= MaxLogBytes) return;
                _writer.Dispose();
                _writer = null;
                RotateIfTooLarge(_logFilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the app down; retry opening on the next message.
                _writer?.Dispose();
                _writer = null;
            }
        }
    }

    private static void RotateIfTooLarge(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= MaxLogBytes) return;
        string previous = Path.Combine(info.DirectoryName!, "PhotoLibrarian.previous.log");
        File.Move(path, previous, overwrite: true);
    }
}
