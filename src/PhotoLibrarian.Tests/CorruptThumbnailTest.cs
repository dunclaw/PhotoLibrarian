using PhotoLibrarian.Core.Services;

namespace PhotoLibrarian.Tests;

public class CorruptThumbnailTest
{
    public static async Task TestSpecificImage()
    {
        var testFiles = new[]
        {
            @"D:\Temp\Pictures\2025\Feb\2025-02-19T12_34_52-10_00.JPEG",
            @"D:\Temp\Pictures\2025\Dec\R5C_2336.CR3"
        };
        
        foreach (var filePath in testFiles)
        {
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"File not found: {filePath}");
                continue;
            }
            
            Console.WriteLine($"\n=== Testing: {Path.GetFileName(filePath)} ===");
            Console.WriteLine($"File size: {new FileInfo(filePath).Length / 1024.0:F1} KB");
            
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var data = await ThumbnailService.GenerateThumbnailAsync(filePath, 180);
                var elapsed = sw.ElapsedMilliseconds;
                
                if (data != null)
                {
                    Console.WriteLine($"✓ Thumbnail generated: {data.Length} bytes in {elapsed}ms");
                    
                    // Save thumbnail to temp file for inspection
                    var tempPath = Path.Combine(Path.GetTempPath(), $"thumb_{Path.GetFileNameWithoutExtension(filePath)}.jpg");
                    await File.WriteAllBytesAsync(tempPath, data);
                    Console.WriteLine($"  Saved to: {tempPath}");
                }
                else
                {
                    Console.WriteLine("✗ Thumbnail generation returned NULL");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Exception: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
