// Copyright (c) Duncan Lawler. All Rights Reserved.

using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Services;

/// <summary>
/// Options for grouping photos in the grid.
/// </summary>
public enum GroupByOption
{
    None,
    FileType,
    MediaType,
    YearTaken,
    MonthTaken,
    FileSize,
    ImageSize,
    Rating,
    Camera
}

/// <summary>
/// Options for sorting photos within groups.
/// </summary>
public enum SortByOption
{
    FileName,
    DateTaken,
    DateModified,
    FileSize,
    Rating,
    Random
}

public sealed record ImageGridGroup<T>(string Header, IReadOnlyList<T> Items);

/// <summary>
/// Applies the grid's grouping and sorting rules without depending on UI types.
/// </summary>
public static class ImageGridGroupingService
{
    public static List<ImageGridGroup<T>> CreateGroups<T>(
        IReadOnlyList<T> items,
        Func<T, ImageEntry> getEntry,
        GroupByOption groupBy,
        SortByOption sortBy,
        bool sortDescending,
        int randomSeed)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(getEntry);

        if (groupBy == GroupByOption.None)
        {
            var sortedItems = SortItems(items, getEntry, sortBy, sortDescending, randomSeed).ToList();
            return
            [
                new ImageGridGroup<T>($"All Photos ({sortedItems.Count:N0})", sortedItems)
            ];
        }

        var groupedData = groupBy switch
        {
            GroupByOption.FileType => GroupByFileType(items, getEntry),
            GroupByOption.MediaType => GroupByMediaType(items, getEntry),
            GroupByOption.YearTaken => GroupByYearTaken(items, getEntry),
            GroupByOption.MonthTaken => GroupByMonthTaken(items, getEntry),
            GroupByOption.FileSize => GroupByFileSize(items, getEntry),
            GroupByOption.ImageSize => GroupByImageSize(items, getEntry),
            GroupByOption.Rating => GroupByRating(items, getEntry),
            GroupByOption.Camera => GroupByCamera(items, getEntry),
            _ => []
        };

        return groupedData
            .Select(group => new ImageGridGroup<T>(
                $"{group.Header} ({group.Items.Count:N0})",
                SortItems(group.Items, getEntry, sortBy, sortDescending, randomSeed).ToList()))
            .ToList();
    }

    public static IEnumerable<T> SortItems<T>(
        IEnumerable<T> items,
        Func<T, ImageEntry> getEntry,
        SortByOption sortBy,
        bool sortDescending,
        int randomSeed = 0)
    {
        if (sortBy == SortByOption.Random)
            return items.OrderBy(item => RandomKey(getEntry(item).FilePath, randomSeed))
                .ThenBy(item => getEntry(item).FilePath, StringComparer.OrdinalIgnoreCase);

        var sorted = sortBy switch
        {
            SortByOption.FileName => items.OrderBy(item => getEntry(item).FileName),
            SortByOption.DateTaken => items.OrderBy(item => getEntry(item).DateTaken ?? DateTime.MaxValue),
            SortByOption.DateModified => items.OrderBy(item => getEntry(item).DateModified),
            SortByOption.FileSize => items.OrderBy(item => getEntry(item).FileSize),
            SortByOption.Rating => items.OrderBy(item => getEntry(item).Rating ?? 0),
            _ => items.AsEnumerable()
        };

        if (sortDescending)
            sorted = sorted.Reverse();

        return sorted;
    }

    internal static ulong RandomKey(string path, int seed)
    {
        ulong hash = 14695981039346656037UL ^ (uint)seed;
        foreach (var character in path.ToUpperInvariant())
        {
            hash ^= character;
            hash *= 1099511628211UL;
        }
        hash ^= hash >> 30;
        hash *= 0xbf58476d1ce4e5b9UL;
        hash ^= hash >> 27;
        hash *= 0x94d049bb133111ebUL;
        hash ^= hash >> 31;
        return hash;
    }

    private static List<(string Header, List<T> Items)> GroupByFileType<T>(
        IEnumerable<T> items, Func<T, ImageEntry> getEntry) =>
        items
            .GroupBy(item => Path.GetExtension(getEntry(item).FilePath).ToUpperInvariant())
            .OrderBy(group => group.Key)
            .Select(group => (group.Key.TrimStart('.') + " Files", group.ToList()))
            .ToList();

    private static List<(string Header, List<T> Items)> GroupByMediaType<T>(
        IEnumerable<T> items, Func<T, ImageEntry> getEntry) =>
        items
            .GroupBy(item => getEntry(item).MediaType)
            .OrderBy(group => group.Key)
            .Select(group => (group.Key == MediaType.Video ? "Videos" : "Images", group.ToList()))
            .ToList();

    private static List<(string Header, List<T> Items)> GroupByYearTaken<T>(
        IEnumerable<T> items, Func<T, ImageEntry> getEntry) =>
        items
            .GroupBy(item => getEntry(item).DateTaken?.Year ?? 0)
            .OrderByDescending(group => group.Key)
            .Select(group => (group.Key == 0 ? "Unknown Date" : group.Key.ToString(), group.ToList()))
            .ToList();

    private static List<(string Header, List<T> Items)> GroupByMonthTaken<T>(
        IEnumerable<T> items, Func<T, ImageEntry> getEntry) =>
        items
            .GroupBy(item => getEntry(item).DateTaken is DateTime dateTaken
                ? new DateTime(dateTaken.Year, dateTaken.Month, 1)
                : DateTime.MinValue)
            .OrderByDescending(group => group.Key)
            .Select(group => (group.Key == DateTime.MinValue ? "Unknown Date" : group.Key.ToString("MMMM yyyy"), group.ToList()))
            .ToList();

    private static List<(string Header, List<T> Items)> GroupByFileSize<T>(
        IEnumerable<T> items, Func<T, ImageEntry> getEntry) =>
        items
            .GroupBy(item =>
            {
                var sizeMb = getEntry(item).FileSize / (1024.0 * 1024.0);
                return sizeMb switch
                {
                    > 10 => (0, "Large (>10 MB)"),
                    > 1 => (1, "Medium (1-10 MB)"),
                    _ => (2, "Small (<1 MB)")
                };
            })
            .OrderBy(group => group.Key.Item1)
            .Select(group => (group.Key.Item2, group.ToList()))
            .ToList();

    private static List<(string Header, List<T> Items)> GroupByImageSize<T>(
        IEnumerable<T> items, Func<T, ImageEntry> getEntry) =>
        items
            .GroupBy(item =>
            {
                var megapixels = (getEntry(item).Width * getEntry(item).Height) / 1_000_000.0;
                return megapixels switch
                {
                    > 8 => (0, "4K+ (>8 MP)"),
                    > 2 => (1, "Full HD (2-8 MP)"),
                    _ => (2, "HD (<2 MP)")
                };
            })
            .OrderBy(group => group.Key.Item1)
            .Select(group => (group.Key.Item2, group.ToList()))
            .ToList();

    private static List<(string Header, List<T> Items)> GroupByRating<T>(
        IEnumerable<T> items, Func<T, ImageEntry> getEntry) =>
        items
            .GroupBy(item => getEntry(item).Rating ?? 0)
            .OrderByDescending(group => group.Key)
            .Select(group => (group.Key == 0 ? "Unrated" : $"{group.Key} Stars", group.ToList()))
            .ToList();

    private static List<(string Header, List<T> Items)> GroupByCamera<T>(
        IEnumerable<T> items, Func<T, ImageEntry> getEntry) =>
        items
            .GroupBy(item => string.IsNullOrWhiteSpace(getEntry(item).CameraModel)
                ? "Unknown Camera"
                : getEntry(item).CameraModel!)
            .OrderBy(group => group.Key)
            .Select(group => (group.Key, group.ToList()))
            .ToList();
}
