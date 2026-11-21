using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using PhotoLibrarian.Core.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class MetadataWriteRetryTests
{
    [Fact]
    public async Task MappedPhotoIsUnchangedWhileBlockedAndRewrittenFromIntactSourceAfterRelease()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-mapped-write-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "mapped.png");
            await WicTestImage.CreateAsync(path, 16, 12);
            var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            Task write;
            using (var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                using (var map = MemoryMappedFile.CreateFromFile(holder, null, 0,
                    MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true))
                using (map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
                {
                    write = EmbeddedMetadataWriter.WriteAsync(path, keywords: ["mapped"]);
                    // Longer than the former 0.7 s retry budget, so several attempts fail here.
                    await Task.Delay(TimeSpan.FromSeconds(1.5), TestContext.Current.CancellationToken);
                    Assert.False(write.IsCompleted, write.Exception?.ToString());
                    holder.Position = 0;
                    var whileBlocked = new byte[holder.Length];
                    holder.ReadExactly(whileBlocked);
                    Assert.Equal(original, whileBlocked);
                }
            }

            await write;
            Assert.Contains("mapped", await EmbeddedMetadataWriter.ReadKeywordsAsync(path));
            Assert.Equal((16u, 12u), await WicTestImage.ReadSizeAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(unchecked((int)0x80070020), false)]
    [InlineData(unchecked((int)0x80070021), false)]
    [InlineData(unchecked((int)0x80030020), true)]
    [InlineData(unchecked((int)0x80030021), true)]
    [InlineData(unchecked((int)0x80070020), true)]
    public async Task RetryOnFileLock_RetriesWithBackoffUntilSuccess(int hresult, bool comException)
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();
        await EmbeddedMetadataWriter.RetryOnFileLockAsync(
            () =>
            {
                if (++attempts < 4)
                    throw CreateError(hresult, comException);
                return Task.CompletedTask;
            },
            delay =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        Assert.Equal(4, attempts);
        Assert.Equal(
            new[] { 100, 200, 400 }.Select(ms => TimeSpan.FromMilliseconds(ms)), delays);
    }

    [Fact]
    public async Task RetryOnFileLock_StopsAfterFourAttemptsAndPreservesError()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var error = CreateError(unchecked((int)0x80070020), false);
        var thrown = await Assert.ThrowsAsync<IOException>(() =>
            EmbeddedMetadataWriter.RetryOnFileLockAsync(
                () =>
                {
                    attempts++;
                    return Task.FromException(error);
                },
                delay =>
                {
                    delays.Add(delay);
                    return Task.CompletedTask;
                }));

        Assert.Same(error, thrown);
        Assert.Equal(4, attempts);
        Assert.Equal(3, delays.Count);
    }

    [Fact]
    public async Task RetryOnFileLock_WaitsLongerForUserMappedSectionThenPreservesError()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var error = CreateError(unchecked((int)0x800704C8), false);
        var thrown = await Assert.ThrowsAsync<IOException>(() =>
            EmbeddedMetadataWriter.RetryOnFileLockAsync(
                () =>
                {
                    attempts++;
                    return Task.FromException(error);
                },
                delay =>
                {
                    delays.Add(delay);
                    return Task.CompletedTask;
                }));

        Assert.Same(error, thrown);
        Assert.Equal(8, attempts);
        Assert.Equal(
            new[] { 100, 200, 400, 800, 1600, 1600, 1600 }.Select(ms => TimeSpan.FromMilliseconds(ms)), delays);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070005), false)]
    [InlineData(unchecked((int)0x80070002), false)]
    [InlineData(unchecked((int)0x88982F52), true)]
    [InlineData(unchecked((int)0x88982F81), true)]
    public async Task RetryOnFileLock_DoesNotRetryPermanentErrors(int hresult, bool comException)
    {
        var attempts = 0;
        var delays = 0;
        var error = CreateError(hresult, comException);
        var thrown = await Record.ExceptionAsync(() =>
            EmbeddedMetadataWriter.RetryOnFileLockAsync(
                () =>
                {
                    attempts++;
                    return Task.FromException(error);
                },
                _ =>
                {
                    delays++;
                    return Task.CompletedTask;
                }));

        Assert.Same(error, thrown);
        Assert.Equal(1, attempts);
        Assert.Equal(0, delays);
    }

    [Fact]
    public async Task RetryOnFileLock_SuccessDoesNotDelay()
    {
        var delays = 0;
        await EmbeddedMetadataWriter.RetryOnFileLockAsync(
            () => Task.CompletedTask,
            _ =>
            {
                delays++;
                return Task.CompletedTask;
            });

        Assert.Equal(0, delays);
    }

    private static Exception CreateError(int hresult, bool comException) =>
        comException
            ? new COMException("File operation failed", hresult)
            : new IOException("File operation failed", hresult);
}
