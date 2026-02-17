using System.Diagnostics;
using System.Text;

namespace PhotoLibrarian.Diagnostics;

/// <summary>
/// High-precision performance profiler that writes detailed timing data to a file for analysis.
/// </summary>
public class PerformanceProfiler : IDisposable
{
    private readonly string _sessionName;
    private readonly Stopwatch _sessionWatch;
    private readonly List<ProfileEntry> _entries = new();
    private readonly object _lock = new();
    
    public PerformanceProfiler(string sessionName)
    {
        _sessionName = sessionName;
        _sessionWatch = Stopwatch.StartNew();
    }
    
    public void Log(string operation, string details = "", long durationMs = 0)
    {
        lock (_lock)
        {
            _entries.Add(new ProfileEntry
            {
                TimestampMs = _sessionWatch.ElapsedMilliseconds,
                ThreadId = Environment.CurrentManagedThreadId,
                Operation = operation,
                Details = details,
                DurationMs = durationMs
            });
        }
    }
    
    public ScopedTimer StartTimer(string operation, string details = "")
    {
        return new ScopedTimer(this, operation, details);
    }
    
    public void Dispose()
    {
        _sessionWatch.Stop();
        WriteReport();
    }
    
    private void WriteReport()
    {
        var outputPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            $"PhotoLibrarian_Perf_{_sessionName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        );
        
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Timestamp(ms),Thread,Operation,Details,Duration(ms)");
            
            foreach (var entry in _entries)
            {
                sb.AppendLine($"{entry.TimestampMs},{entry.ThreadId},{EscapeCsv(entry.Operation)},{EscapeCsv(entry.Details)},{entry.DurationMs}");
            }
            
            File.WriteAllText(outputPath, sb.ToString());
            System.Diagnostics.Debug.WriteLine($"[PROFILER] Report written to: {outputPath}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PROFILER] Failed to write report: {ex.Message}");
        }
    }
    
    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
    
    private class ProfileEntry
    {
        public long TimestampMs { get; set; }
        public int ThreadId { get; set; }
        public string Operation { get; set; } = "";
        public string Details { get; set; } = "";
        public long DurationMs { get; set; }
    }
    
    public class ScopedTimer : IDisposable
    {
        private readonly PerformanceProfiler _profiler;
        private readonly string _operation;
        private readonly string _details;
        private readonly Stopwatch _sw;
        
        public ScopedTimer(PerformanceProfiler profiler, string operation, string details)
        {
            _profiler = profiler;
            _operation = operation;
            _details = details;
            _sw = Stopwatch.StartNew();
        }
        
        public void Dispose()
        {
            _sw.Stop();
            _profiler.Log(_operation, _details, _sw.ElapsedMilliseconds);
        }
    }
}
