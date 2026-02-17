using System.Diagnostics;
using PhotoLibrarian.Core.Services;

namespace PhotoLibrarian.Tests;

public class ThumbnailPerformanceTest
{
    public static async Task RunBenchmark()
    {
        var testFolders = new[]
        {
            @"D:\Temp\Pictures\2025\Dec",  // Large files
            @"D:\Temp\Pictures\1991"        // Small files
        };

        foreach (var folder in testFolders)
        {
            if (!Directory.Exists(folder))
            {
                Console.WriteLine($"Folder not found: {folder}");
                continue;
            }

            Console.WriteLine($"\n=== Testing folder: {folder} ===");
            
            var files = Directory.GetFiles(folder, "*.*")
                .Where(f => {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    return ext == ".jpg" || ext == ".jpeg" || ext == ".cr3" || ext == ".arw" || ext == ".nef";
                })
                .Take(10)
                .ToArray();

            Console.WriteLine($"Found {files.Length} test files");

            var totalTime = 0.0;
            var successCount = 0;
            var failCount = 0;

            foreach (var file in files)
            {
                var fileInfo = new FileInfo(file);
                var sizeMB = fileInfo.Length / (1024.0 * 1024.0);
                
                var sw = Stopwatch.StartNew();
                var thumbnail = await ThumbnailService.GenerateThumbnailAsync(file, 180);
                sw.Stop();

                if (thumbnail != null)
                {
                    successCount++;
                    totalTime += sw.Elapsed.TotalMilliseconds;
                    Console.WriteLine($"  ✓ {Path.GetFileName(file)} ({sizeMB:F1} MB): {sw.ElapsedMilliseconds}ms → {thumbnail.Length / 1024}KB");
                }
                else
                {
                    failCount++;
                    Console.WriteLine($"  ✗ {Path.GetFileName(file)} ({sizeMB:F1} MB): FAILED after {sw.ElapsedMilliseconds}ms");
                }
            }

            if (successCount > 0)
            {
                Console.WriteLine($"\nResults: {successCount} success, {failCount} failed");
                Console.WriteLine($"Average time: {totalTime / successCount:F0}ms");
                Console.WriteLine($"Total time: {totalTime:F0}ms");
            }
        }
    }
}
