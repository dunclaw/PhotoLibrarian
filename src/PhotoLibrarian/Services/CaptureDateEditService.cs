// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;

namespace PhotoLibrarian.Services;

internal sealed record CaptureDateEdit(ImageEntry Entry, DateTime Date);

internal sealed class CaptureDateEditService
{
    private readonly ImageRepository? _imageRepo;

    public CaptureDateEditService(ImageRepository? imageRepo) => _imageRepo = imageRepo;

    public static IReadOnlyList<CaptureDateEdit> PrepareSet(
        IEnumerable<ImageEntry> entries, DateTime date) =>
        entries.Select(entry => new CaptureDateEdit(entry, date)).ToArray();

    public static bool TryPrepareOffset(
        IEnumerable<ImageEntry> entries, TimeSpan offset, out IReadOnlyList<CaptureDateEdit> edits)
    {
        var prepared = new List<CaptureDateEdit>();
        foreach (var entry in entries)
        {
            if (entry.DateTaken is not { } date)
                continue;
            if (!TryOffset(date, offset, out var shifted))
            {
                edits = [];
                return false;
            }
            prepared.Add(new CaptureDateEdit(entry, shifted));
        }
        edits = prepared;
        return true;
    }

    public static bool TryOffset(DateTime date, TimeSpan offset, out DateTime shifted)
    {
        if (offset.Ticks > DateTime.MaxValue.Ticks - date.Ticks || offset.Ticks < -date.Ticks)
        {
            shifted = default;
            return false;
        }
        shifted = date.AddTicks(offset.Ticks);
        return true;
    }

    public async Task WritePreparedCopiesAsync(
        IReadOnlyList<CaptureDateEdit> edits, IReadOnlyDictionary<string, string> copies)
    {
        foreach (var edit in edits)
            await MetadataWriterService.WriteDateTakenAsync(
                copies[Path.GetFullPath(edit.Entry.FilePath)], edit.Date);
    }

    public async Task WriteDirectAsync(IReadOnlyList<CaptureDateEdit> edits)
    {
        foreach (var edit in edits)
        {
            await MetadataWriterService.WriteDateTakenAsync(edit.Entry.FilePath, edit.Date);
            edit.Entry.DateTaken = edit.Date;
            if (_imageRepo is not null && edit.Entry.Id > 0)
            {
                try
                {
                    await _imageRepo.UpdateDateTakenAsync(edit.Entry.Id, edit.Date);
                }
                catch (Exception exception) when (
                    exception is not OutOfMemoryException and not StackOverflowException)
                {
                    throw new IOException(
                        $"{edit.Entry.FileName}: capture date was written, but the library index could not be updated.",
                        exception);
                }
            }
        }
    }
}
