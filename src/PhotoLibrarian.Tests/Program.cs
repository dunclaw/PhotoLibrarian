namespace PhotoLibrarian.Tests;

public static class BenchmarkProgram
{
    public static async Task RunAsync()
    {
        await ConcurrentThumbnailBenchmark.RunBenchmark();

        Console.WriteLine("\nPress any key to exit...");
        Console.ReadKey();
    }
}
