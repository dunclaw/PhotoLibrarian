using PhotoLibrarian.Tests;

// Run concurrent thumbnail benchmark
await ConcurrentThumbnailBenchmark.RunBenchmark();

Console.WriteLine("\nPress any key to exit...");
Console.ReadKey();
