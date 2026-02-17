using PhotoLibrarian.Core.Services;
using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;

namespace PhotoLibrarian.Tests;

public class ConcurrentThumbnailBenchmark
{
    public static async Task RunBenchmark()
    {
        var testFolder = @"D:\Temp\Pictures\2025\Dec";
        
        if (!Directory.Exists(testFolder))
        {
            Console.WriteLine($"Test folder not found: {testFolder}");
            return;
        }
        
        // Get all image files
        var files = Directory.GetFiles(testFolder, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => 
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                return ext == ".cr3" || ext == ".jpg" || ext == ".jpeg" || ext == ".png";
            })
            .Take(100)
            .ToList();
        
        Console.WriteLine($"Found {files.Count} images in {testFolder}");
        Console.WriteLine();
        
        // Test different parallelism levels
        var parallelismLevels = new[] { 1, 2, 4, 8, Environment.ProcessorCount - 2, Environment.ProcessorCount };
        
        foreach (var maxParallel in parallelismLevels)
        {
            if (maxParallel < 1) continue;
            
            Console.WriteLine($"=== Testing with {maxParallel} concurrent threads ===");
            
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var results = new ConcurrentBag<(string fileName, long ms, int bytes)>();
            
            // Use SemaphoreSlim to limit parallelism
            var semaphore = new SemaphoreSlim(maxParallel, maxParallel);
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
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }
            
            await Task.WhenAll(tasks);
            sw.Stop();
            
            // Calculate statistics
            var resultsList = results.ToList();
            var avgTime = resultsList.Average(r => r.ms);
            var minTime = resultsList.Min(r => r.ms);
            var maxTime = resultsList.Max(r => r.ms);
            var throughput = files.Count / (sw.ElapsedMilliseconds / 1000.0);
            
            Console.WriteLine($"Total time: {sw.ElapsedMilliseconds}ms ({sw.ElapsedMilliseconds / 1000.0:F1}s)");
            Console.WriteLine($"Per-image: avg={avgTime:F0}ms, min={minTime}ms, max={maxTime}ms");
            Console.WriteLine($"Throughput: {throughput:F1} images/sec");
            
            // Show slowest files
            Console.WriteLine($"Slowest 5 files:");
            foreach (var result in resultsList.OrderByDescending(r => r.ms).Take(5))
            {
                Console.WriteLine($"  {result.fileName}: {result.ms}ms ({result.bytes} bytes)");
            }
            
            Console.WriteLine();
        }
        
        // Now test UI marshalling overhead
        Console.WriteLine("=== Testing UI Thread Marshalling Overhead ===");
        await TestUIMarshalling(files.Take(20).ToList());
    }
    
    private static async Task TestUIMarshalling(List<string> files)
    {
        Console.WriteLine($"Testing with {files.Count} files...");
        
        // Test 1: Generate thumbnails without UI marshalling
        var sw1 = System.Diagnostics.Stopwatch.StartNew();
        var thumbnails = new List<byte[]?>();
        
        foreach (var file in files)
        {
            var data = await ThumbnailService.GenerateThumbnailAsync(file, 180);
            thumbnails.Add(data);
        }
        sw1.Stop();
        Console.WriteLine($"Generate only (sequential): {sw1.ElapsedMilliseconds}ms");
        
        // Test 2: Just measure stream creation overhead (no UI thread available in test)
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        var completed = 0;
        
        foreach (var data in thumbnails)
        {
            if (data != null)
            {
                // Simulate what we do with the data
                using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                await stream.WriteAsync(data.AsBuffer());
                await stream.FlushAsync();
                stream.Seek(0);
                
                completed++;
            }
        }
        sw2.Stop();
        Console.WriteLine($"Generate + stream creation: {sw2.ElapsedMilliseconds}ms ({completed} completed)");
        Console.WriteLine($"Stream creation overhead: {sw2.ElapsedMilliseconds - sw1.ElapsedMilliseconds}ms");
        Console.WriteLine($"Per-image stream overhead: {(sw2.ElapsedMilliseconds - sw1.ElapsedMilliseconds) / (double)completed:F1}ms");
    }
}
