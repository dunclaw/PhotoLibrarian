// Copyright (c) Duncan Lawler. All Rights Reserved.

using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Services;

/// <summary>A single displayed file, captured before a context menu opens.</summary>
public sealed class PhotoContextMenuTarget
{
    private readonly string _filePath;

    public ImageEntry Entry { get; }
    public bool CanRotate => SupportsRotation(Entry);

    private PhotoContextMenuTarget(ImageEntry entry)
    {
        Entry = entry;
        _filePath = entry.FilePath;
    }

    public static PhotoContextMenuTarget? Create(ImageEntry? entry, bool isEditing) =>
        isEditing || entry is null || string.IsNullOrWhiteSpace(entry.FilePath)
            ? null
            : new PhotoContextMenuTarget(entry);

    public bool IsCurrent(ImageEntry? current) =>
        current is not null &&
        string.Equals(_filePath, current.FilePath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(_filePath, Entry.FilePath, StringComparison.OrdinalIgnoreCase);

    public static bool SupportsRotation(ImageEntry entry) =>
        entry.MediaType == MediaType.Image && ImageEditRenderer.IsSupported(entry.FilePath);

    public static bool SupportsWallpaper(ImageEntry entry) =>
        entry.MediaType == MediaType.Image &&
        Path.GetExtension(entry.FilePath).ToLowerInvariant() is ".bmp" or ".jpg" or ".jpeg" or ".png";

    public async Task<ConfirmedPhotoActionResult> ExecuteConfirmedAsync(
        Func<ImageEntry?> getCurrent,
        Func<Task<bool>> confirm,
        Func<Task> execute)
    {
        if (!IsCurrent(getCurrent())) return ConfirmedPhotoActionResult.Stale;
        if (!await confirm()) return ConfirmedPhotoActionResult.Cancelled;
        if (!IsCurrent(getCurrent())) return ConfirmedPhotoActionResult.Stale;
        await execute();
        return ConfirmedPhotoActionResult.Completed;
    }
}

public enum ConfirmedPhotoActionResult { Completed, Cancelled, Stale }
