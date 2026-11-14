// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Services;
using System.IO.MemoryMappedFiles;
using XmpCore;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class FileEditTransactionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoLibrarianHistoryTests", Guid.NewGuid().ToString("N"));

    public FileEditTransactionTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ColorLabelBatchUndoAndRedoRestoresSidecars()
    {
        var first = CreatePhoto("first.cr3", [1, 2]);
        var second = CreatePhoto("second.cr3", [3, 4]);
        await MetadataWriterService.WriteColorLabelAsync(first, "Purple");
        using var transaction = await PrepareAsync([first, second], async copies =>
        {
            foreach (var copy in copies.Values)
                await MetadataWriterService.WriteColorLabelAsync(copy, "Red");
        });
        var reader = new MetadataReaderService();
        Assert.Equal(EditHistoryStatus.Applied, (await transaction.MoveAsync(false, RefreshAsync)).Status);
        Assert.Equal("Red", reader.ReadMetadata(first).ColorLabel);
        Assert.Equal("Red", reader.ReadMetadata(second).ColorLabel);
        Assert.Equal(EditHistoryStatus.Applied, (await transaction.MoveAsync(true, RefreshAsync)).Status);
        Assert.Equal("Purple", reader.ReadMetadata(first).ColorLabel);
        Assert.Null(reader.ReadMetadata(second).ColorLabel);
        Assert.Equal(EditHistoryStatus.Applied, (await transaction.MoveAsync(false, RefreshAsync)).Status);
        Assert.Equal("Red", reader.ReadMetadata(second).ColorLabel);
        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(first));
    }

    [Fact]
    public async Task ReplacingMappedPhotoDoesNotTruncateItsOpenViewerSection()
    {
        var photo = CreatePhoto("mapped.jpg", [1, 2, 3, 4]);
        using var source = new FileStream(photo, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var map = MemoryMappedFile.CreateFromFile(source, null, 0,
            MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
        using var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        using var transaction = await PrepareAsync([photo],
            copies => File.WriteAllBytesAsync(copies[photo], [7, 8]));

        var result = await transaction.MoveAsync(false, RefreshAsync);
        Assert.Equal(EditHistoryStatus.Applied, result.Status);
        Assert.Null(result.Message);
        Assert.Equal(new byte[] { 7, 8 }, File.ReadAllBytes(photo));
        Assert.Equal(1, view.ReadByte(0));
        Assert.Equal(EditHistoryStatus.Applied, (await transaction.MoveAsync(true, RefreshAsync)).Status);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(photo));
    }

    [Fact]
    public async Task PhotoAndNewSidecarReplayAsOneBatchWithoutTouchingOtherFiles()
    {
        var first = CreatePhoto("first.jpg", [1, 2, 3]);
        var second = CreatePhoto("second.jpg", [4, 5]);
        var unrelated = CreatePhoto("unrelated.jpg", [9]);
        using var transaction = await PrepareAsync([first, second], async copies =>
        {
            foreach (var copy in copies.Values)
            {
                await File.WriteAllBytesAsync(copy, [7, 8, 9, 10]);
                await File.WriteAllTextAsync(copy + ".xmp", Packet());
            }
        });
        Assert.Equal(EditHistoryStatus.Applied, (await transaction.MoveAsync(false, RefreshAsync)).Status);
        Assert.Equal(new byte[] { 7, 8, 9, 10 }, File.ReadAllBytes(first));
        Assert.True(File.Exists(second + ".xmp"));
        Assert.Equal(EditHistoryStatus.Applied, (await transaction.MoveAsync(true, RefreshAsync)).Status);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(first));
        Assert.Equal(new byte[] { 4, 5 }, File.ReadAllBytes(second));
        Assert.False(File.Exists(first + ".xmp"));
        Assert.Equal(EditHistoryStatus.Applied, (await transaction.MoveAsync(false, RefreshAsync)).Status);
        Assert.True(File.Exists(first + ".xmp"));
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(unrelated));
    }

    [Fact]
    public async Task ExternalChangeBeyondFirst64KbBlocksEntireBatchAndPreservesIt()
    {
        var first = CreatePhoto("first.jpg", new byte[100000]);
        var second = CreatePhoto("second.jpg", [2]);
        using var transaction = await PrepareAsync([first, second], async copies =>
        {
            foreach (var copy in copies.Values) await File.WriteAllBytesAsync(copy, [3]);
        });
        var changed = new byte[100000];
        changed[^1] = 4;
        File.WriteAllBytes(first, changed);
        var result = await transaction.MoveAsync(false, RefreshAsync);
        Assert.Equal(EditHistoryStatus.Conflict, result.Status);
        Assert.Equal(changed, File.ReadAllBytes(first));
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(second));
    }

    [Fact]
    public async Task PreparationFailureDoesNotPublishAnyBatchMember()
    {
        var first = CreatePhoto("first.jpg", [1]);
        var second = CreatePhoto("second.jpg", [2]);
        await Assert.ThrowsAsync<IOException>(() => PrepareAsync([first, second], async copies =>
        {
            await File.WriteAllBytesAsync(copies[first], [3]);
            throw new IOException("Second photo could not be edited");
        }));
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(first));
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(second));
    }

    [Fact]
    public async Task RefreshFailureRollsBackFilesAndHistoryCanRetry()
    {
        var photo = CreatePhoto("first.jpg", [1, 2]);
        using var transaction = await PrepareAsync([photo], async copies =>
        {
            await File.WriteAllBytesAsync(copies[photo], [3]);
            await File.WriteAllTextAsync(copies[photo] + ".xmp", Packet());
        });
        var history = new EditHistoryService();
        await history.ExecuteAsync("pixels",
            () => transaction.MoveAsync(false, RefreshAsync),
            undo => transaction.MoveAsync(undo, () => throw new IOException("Cache unavailable")));
        await Assert.ThrowsAsync<IOException>(() => history.UndoAsync());
        Assert.True(history.CanUndo);
        Assert.Equal(new byte[] { 3 }, File.ReadAllBytes(photo));
        Assert.True(File.Exists(photo + ".xmp"));
        Assert.Equal(EditHistoryStatus.Applied, (await transaction.MoveAsync(true, RefreshAsync)).Status);
        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(photo));
        Assert.False(File.Exists(photo + ".xmp"));
    }

    [Fact]
    public async Task LaterBatchConflictRemovesEarlierAbsentSidecarLease()
    {
        var first = CreatePhoto("a.jpg", [1]);
        var second = CreatePhoto("z.jpg", [2]);
        using var transaction = await PrepareAsync([first, second], async copies =>
        {
            foreach (var copy in copies.Values)
                await File.WriteAllTextAsync(copy + ".xmp", Packet());
        });
        using var locked = new FileStream(second, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(EditHistoryStatus.Conflict, (await transaction.MoveAsync(false, RefreshAsync)).Status);
        Assert.False(File.Exists(first + ".xmp"));
        Assert.False(File.Exists(second + ".xmp"));
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(first));
    }

    [Fact]
    public async Task BusyOrNewlyCreatedSidecarIsNotOverwritten()
    {
        var photo = CreatePhoto("first.jpg", [1]);
        using var transaction = await PrepareAsync([photo],
            copies => File.WriteAllTextAsync(copies[photo] + ".xmp", Packet()));
        using (var locked = new FileStream(photo, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(EditHistoryStatus.Conflict, (await transaction.MoveAsync(false, RefreshAsync)).Status);
        File.WriteAllText(photo + ".xmp", "external");
        Assert.Equal(EditHistoryStatus.Conflict, (await transaction.MoveAsync(false, RefreshAsync)).Status);
        Assert.Equal("external", File.ReadAllText(photo + ".xmp"));
    }

    [Fact]
    public async Task RawBytesAreImmutableAndCompanionSidecarOwnershipIsPreserved()
    {
        var raw = CreatePhoto("pair.nef", [1, 2]);
        var jpeg = CreatePhoto("pair.jpg", [3, 4]);
        File.WriteAllText(Path.ChangeExtension(raw, ".xmp"), Packet());
        var originalSidecar = File.ReadAllBytes(Path.ChangeExtension(raw, ".xmp"));
        using var transaction = await PrepareAsync([jpeg], async copies =>
        {
            await MetadataWriterService.WriteFlagAsync(copies[jpeg], true);
        });
        Assert.Equal(EditHistoryStatus.Applied, (await transaction.MoveAsync(false, RefreshAsync)).Status);
        Assert.True(File.Exists(jpeg + ".xmp"));
        Assert.Equal(originalSidecar, File.ReadAllBytes(Path.ChangeExtension(raw, ".xmp")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => PrepareAsync([raw],
            copies => File.WriteAllBytesAsync(copies[raw], [8])));
        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(raw));
    }

    private string CreatePhoto(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private Task<FileEditTransaction> PrepareAsync(string[] paths,
        Func<IReadOnlyDictionary<string, string>, Task> prepare) =>
        FileEditTransaction.PrepareAsync(Path.Combine(_root, "history"), paths, prepare);

    private static string Packet() => XmpMetaFactory.SerializeToString(
        XmpMetaFactory.Create(), new XmpCore.Options.SerializeOptions());

    private static Task RefreshAsync() => Task.CompletedTask;

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
