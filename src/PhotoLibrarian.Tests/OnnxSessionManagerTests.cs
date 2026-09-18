using PhotoLibrarian.ML.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class OnnxSessionManagerTests
{
    [Fact]
    public async Task RunInference_SerializesNativeInference()
    {
        var modelDirectory = Path.Combine(
            Path.GetTempPath(),
            $"PhotoLibrarian-{Guid.NewGuid():N}");
        try
        {
            using var manager = new OnnxSessionManager(modelDirectory);
            var firstEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var secondStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var secondEntered = 0;

            var first = Task.Run(() => manager.RunInference(() =>
            {
                firstEntered.SetResult();
                releaseFirst.Task.GetAwaiter().GetResult();
                return 1;
            }));
            await firstEntered.Task;

            var second = Task.Run(() =>
            {
                secondStarted.SetResult();
                return manager.RunInference(() =>
                {
                    Interlocked.Exchange(ref secondEntered, 1);
                    return 2;
                });
            });
            await secondStarted.Task;
            await Task.Delay(100, TestContext.Current.CancellationToken);

            Assert.Equal(0, Volatile.Read(ref secondEntered));

            releaseFirst.SetResult();
            var results = await Task.WhenAll(first, second);
            Assert.Equal(new[] { 1, 2 }, results);
        }
        finally
        {
            Directory.Delete(modelDirectory, recursive: true);
        }
    }
}
