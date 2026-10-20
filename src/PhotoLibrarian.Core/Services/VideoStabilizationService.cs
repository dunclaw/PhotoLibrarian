using System.Diagnostics;

namespace PhotoLibrarian.Core.Services;

/// <summary>Exports a stabilized copy using a separately installed FFmpeg executable.</summary>
public sealed class VideoStabilizationService
{
    public async Task StabilizeAsync(
        string executablePath, string sourcePath, string destinationPath,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidatePaths(executablePath, sourcePath, destinationPath);
        cancellationToken.ThrowIfCancellationRequested();
        string temporaryPath = Path.Combine(Path.GetDirectoryName(destinationPath)!,
            $".photolibrarian-{Guid.NewGuid():N}.tmp");
        try
        {
            using var process = new Process
            {
                StartInfo = CreateStartInfo(executablePath, sourcePath, temporaryPath)
            };
            process.Start();
            var errors = new Queue<string>();
            var readErrors = ReadErrorsAsync();
            var readProgress = ReadProgressAsync();
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            await Task.WhenAll(readErrors, readProgress).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"FFmpeg failed (exit {process.ExitCode}). {string.Join(Environment.NewLine, errors)}");
            if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0)
                throw new IOException("FFmpeg did not produce a video.");
            File.Move(temporaryPath, destinationPath, overwrite: false);

            async Task ReadErrorsAsync()
            {
                while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    if (errors.Count == 20) errors.Dequeue();
                    errors.Enqueue(line);
                }
            }

            async Task ReadProgressAsync()
            {
                while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    if (line.StartsWith("out_time=", StringComparison.Ordinal))
                        progress?.Report($"Processed {line["out_time=".Length..]}");
                }
            }
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    internal static void ValidatePaths(string executablePath, string sourcePath, string destinationPath)
    {
        if (!Path.IsPathFullyQualified(executablePath) || !File.Exists(executablePath) ||
            !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose an existing, trusted FFmpeg .exe file.");
        if (!Path.IsPathFullyQualified(sourcePath) || !File.Exists(sourcePath))
            throw new ArgumentException("The source video no longer exists.");
        if (!Path.IsPathFullyQualified(destinationPath) ||
            !Directory.Exists(Path.GetDirectoryName(destinationPath)) ||
            !string.Equals(Path.GetExtension(destinationPath), ".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose an MP4 filename in an existing output folder.");
        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destinationPath),
                StringComparison.OrdinalIgnoreCase) || File.Exists(destinationPath) || Directory.Exists(destinationPath))
            throw new IOException("The output already exists. Choose a new filename; files are never overwritten.");
    }

    internal static ProcessStartInfo CreateStartInfo(string executablePath, string sourcePath, string outputPath)
    {
        var info = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        string[] arguments =
        [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-n",
            "-i", sourcePath, "-map", "0:v:0", "-map", "0:a:0?",
            "-map_metadata", "0", "-vf",
            "deshake=rx=16:ry=16:edge=mirror,pad=ceil(iw/2)*2:ceil(ih/2)*2",
            "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart",
            "-progress", "pipe:1", "-nostats", "-f", "mp4", outputPath
        ];
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }
}
