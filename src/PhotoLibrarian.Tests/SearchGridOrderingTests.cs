// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Services;
using PhotoLibrarian.ViewModels;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class SearchGridOrderingTests
{
    [Fact]
    public void RandomOrderKeepsRelativeOrderAcrossMembershipChangesAndChangesWithSeed()
    {
        var paths = Enumerable.Range(1, 50).Select(i => $@"C:\Photos\{i}.jpg").ToArray();
        string[] Sort(IEnumerable<string> source, int seed)
        {
            var entries = source.Select(path => new ImageEntry { FilePath = path, FileName = Path.GetFileName(path) });
            return ImageGridGroupingService.SortItems(
                entries, entry => entry, SortByOption.Random, false, seed)
                .Select(entry => entry.FilePath)
                .ToArray();
        }

        var original = Sort(paths, 123);
        var subset = paths.Where(path => !path.EndsWith("5.jpg", StringComparison.Ordinal))
            .Reverse();
        Assert.Equal(original.Where(path => !path.EndsWith("5.jpg", StringComparison.Ordinal)), Sort(subset, 123));
        Assert.NotEqual(original, Sort(paths, 456));
        Assert.Equal(ImageGridGroupingService.RandomKey(paths[0], 123),
            ImageGridGroupingService.RandomKey(paths[0].ToUpperInvariant(), 123));
    }

    [Theory]
    [InlineData(SortByOption.FileName, false)]
    [InlineData(SortByOption.FileName, true)]
    [InlineData(SortByOption.DateTaken, false)]
    [InlineData(SortByOption.DateTaken, true)]
    [InlineData(SortByOption.DateModified, false)]
    [InlineData(SortByOption.DateModified, true)]
    [InlineData(SortByOption.FileSize, false)]
    [InlineData(SortByOption.FileSize, true)]
    [InlineData(SortByOption.Rating, false)]
    [InlineData(SortByOption.Rating, true)]
    public void SearchSubset_KeepsDeclaredSortAndDirection(SortByOption sort, bool descending)
    {
        var items = Enumerable.Range(1, 4).Select(i => new ImageThumbnailViewModel(new ImageEntry
        {
            Id = i, FileName = $"{5 - i}.jpg", FilePath = $"{5 - i}.jpg",
            DateTaken = new DateTime(2024, 1, i),
            DateModified = new DateTime(2024, 1, 5 - i), FileSize = i * 100, Rating = 5 - i
        })).ToList();
        var allSorted = ImageGridGroupingService.SortItems(
            items, item => item.Entry, sort, descending).ToList();
        var subset = items.Where(item => item.Entry.Id != 2).Reverse().ToList();
        var searchSorted = ImageGridGroupingService.SortItems(
            subset, item => item.Entry, sort, descending).ToList();
        Assert.Equal(allSorted.Where(item => item.Entry.Id != 2), searchSorted);
        var increasing = sort is SortByOption.DateTaken or SortByOption.FileSize;
        var expected = increasing ? new long[] { 1, 3, 4 } : [4, 3, 1];
        Assert.Equal(descending ? Enumerable.Reverse(expected) : expected,
            searchSorted.Select(item => item.Entry.Id));
    }

    [Fact]
    public void FileTypeGrouping_UsesProvidedItems()
    {
        var included = new ImageThumbnailViewModel(new ImageEntry { FilePath = "included.jpg", FileName = "included.jpg" });

        var groups = ImageGridGroupingService.CreateGroups(
            [included], item => item.Entry, GroupByOption.FileType, SortByOption.DateTaken, true, 0);
        var group = Assert.Single(groups);
        Assert.Equal("JPG Files (1)", group.Header);
        Assert.Equal(included, Assert.Single(group.Items));
    }

    [Fact]
    public void UngroupedItems_AreSortedAndCountedInTheSingleGroup()
    {
        var first = new ImageEntry { FilePath = "a.jpg", FileName = "a.jpg" };
        var second = new ImageEntry { FilePath = "b.jpg", FileName = "b.jpg" };

        var groups = ImageGridGroupingService.CreateGroups(
            [second, first], entry => entry, GroupByOption.None, SortByOption.FileName, false, 0);

        var group = Assert.Single(groups);
        Assert.Equal("All Photos (2)", group.Header);
        Assert.Equal(new[] { first, second }, group.Items);
    }

    [Fact]
    public void GroupingOptions_PreserveBucketLabelsAndOrder()
    {
        var entries = new[]
        {
            new ImageEntry
            {
                FilePath = "photo.CR3",
                FileName = "photo.CR3",
                DateTaken = new DateTime(2024, 1, 15),
                FileSize = 12 * 1024 * 1024,
                Width = 4000,
                Height = 3000,
                Rating = 5,
                CameraModel = "Canon EOS"
            },
            new ImageEntry
            {
                FilePath = "clip.mp4",
                FileName = "clip.mp4",
                MediaType = MediaType.Video,
                FileSize = 2 * 1024 * 1024,
                Width = 1920,
                Height = 1080,
                CameraModel = " "
            }
        };
        var expectedHeaders = new Dictionary<GroupByOption, string[]>
        {
            [GroupByOption.FileType] = ["CR3 Files (1)", "MP4 Files (1)"],
            [GroupByOption.MediaType] = ["Images (1)", "Videos (1)"],
            [GroupByOption.YearTaken] = ["2024 (1)", "Unknown Date (1)"],
            [GroupByOption.MonthTaken] =
                [$"{new DateTime(2024, 1, 1):MMMM yyyy} (1)", "Unknown Date (1)"],
            [GroupByOption.FileSize] = ["Large (>10 MB) (1)", "Medium (1-10 MB) (1)"],
            [GroupByOption.ImageSize] = ["4K+ (>8 MP) (1)", "Full HD (2-8 MP) (1)"],
            [GroupByOption.Rating] = ["5 Stars (1)", "Unrated (1)"],
            [GroupByOption.Camera] = ["Canon EOS (1)", "Unknown Camera (1)"]
        };

        foreach (var (groupBy, headers) in expectedHeaders)
        {
            var groups = ImageGridGroupingService.CreateGroups(
                entries, entry => entry, groupBy, SortByOption.DateTaken, true, 0);

            Assert.Equal(headers, groups.Select(group => group.Header));
        }
    }
}
