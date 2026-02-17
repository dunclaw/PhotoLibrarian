using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Diagnostics;
using System.Collections.Concurrent;

namespace PhotoLibrarian.Services;

public static class BenchmarkService
{
    public static async Task RunWICBenchmark()
    {
        try
        {
            var testFolder = @"D:\Temp\Pictures\2025\Dec";
            
            System.Diagnostics.Debug.WriteLine($"Benchmark: Checking folder {testFolder}");
            
            if (!Directory.Exists(testFolder))
            {
                System.Diagnostics.Debug.WriteLine($"Test folder not found: {testFolder}");
                return;
            }
            
            // Get first 20 image files
            var files = Directory.GetFiles(testFolder, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => 
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    return ext == ".cr3" || ext == ".jpg" || ext == ".jpeg" || ext == ".png";
                })
                .Take(20)
                .ToList();
            
            System.Diagnostics.Debug.WriteLine($"=== WIC BENCHMARK: Testing {files.Count} images with 8 threads ===");
            
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var results = new ConcurrentBag<(string fileName, long ms, int bytes)>();
            
            // Use SemaphoreSlim to limit parallelism to 8
            var semaphore = new SemaphoreSlim(8, 8);
            var tasks = new List<Task>();
            
            foreach (var file in files)
            {
                tasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        var fileSw = System.Diagnostics.Stopwatch.StartNew();
                        var data = await ThumbnailService.GenerateThumbnailAsync(file, 180);
                        fileSw.Stop();
                        
                        if (data != null)
                        {
                            results.Add((Path.GetFileName(file), fileSw.ElapsedMilliseconds, data.Length));
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error processing {Path.GetFileName(file)}: {ex.Message}");
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }
            
            System.Diagnostics.Debug.WriteLine($"Waiting for {tasks.Count} tasks to complete...");
            await Task.WhenAll(tasks);
            sw.Stop();
            
            // Calculate statistics
            var resultsList = results.ToList();
            
            if (resultsList.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine("ERROR: No results collected!");
                return;
            }
            
            var avgTime = resultsList.Average(r => r.ms);
            var minTime = resultsList.Min(r => r.ms);
            var maxTime = resultsList.Max(r => r.ms);
            var throughput = files.Count / (sw.ElapsedMilliseconds / 1000.0);
            
            System.Diagnostics.Debug.WriteLine($"Total time: {sw.ElapsedMilliseconds}ms ({sw.ElapsedMilliseconds / 1000.0:F1}s)");
            System.Diagnostics.Debug.WriteLine($"Per-image: avg={avgTime:F0}ms, min={minTime}ms, max={maxTime}ms");
            System.Diagnostics.Debug.WriteLine($"Throughput: {throughput:F1} images/sec");
            
            // Show slowest files
            System.Diagnostics.Debug.WriteLine($"Slowest 5 files:");
            foreach (var result in resultsList.OrderByDescending(r => r.ms).Take(5))
            {
                System.Diagnostics.Debug.WriteLine($"  {result.fileName}: {result.ms}ms ({result.bytes} bytes)");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"BENCHMARK EXCEPTION: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Stack: {ex.StackTrace}");
        }
    }
}
