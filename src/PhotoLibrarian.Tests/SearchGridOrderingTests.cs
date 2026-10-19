using PhotoLibrarian.Core.Models;
using PhotoLibrarian.ViewModels;
using System.Reflection;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class SearchGridOrderingTests
{
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
        var method = typeof(ImageGridViewModel).GetMethod(
            "ApplySortToGroup", BindingFlags.Static | BindingFlags.NonPublic)!;
        var allSorted = Assert.IsType<List<ImageThumbnailViewModel>>(
            method.Invoke(null, [items, sort, descending]));
        var subset = items.Where(item => item.Entry.Id != 2).Reverse().ToList();
        var searchSorted = Assert.IsType<List<ImageThumbnailViewModel>>(
            method.Invoke(null, [subset, sort, descending]));
        Assert.Equal(allSorted.Where(item => item.Entry.Id != 2), searchSorted);
        var increasing = sort is SortByOption.DateTaken or SortByOption.FileSize;
        var expected = increasing ? new long[] { 1, 3, 4 } : [4, 3, 1];
        Assert.Equal(descending ? Enumerable.Reverse(expected) : expected,
            searchSorted.Select(item => item.Entry.Id));
    }

    [Fact]
    public void FileTypeGrouping_UsesFilteredSnapshotInsteadOfLiveGridCollection()
    {
        var grid = new ImageGridViewModel(null!, null!, null!, null!, null!, null!, null!);
        var excluded = new ImageThumbnailViewModel(new ImageEntry { FilePath = "excluded.png", FileName = "excluded.png" });
        var included = new ImageThumbnailViewModel(new ImageEntry { FilePath = "included.jpg", FileName = "included.jpg" });
        grid.Images.Add(excluded);
        var method = typeof(ImageGridViewModel).GetMethod(
            "GroupByFileType", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var groups = Assert.IsType<List<(string Header, List<ImageThumbnailViewModel> Items)>>(
            method.Invoke(grid, [new List<ImageThumbnailViewModel> { included }]));
        var group = Assert.Single(groups);
        Assert.Equal("JPG Files", group.Header);
        Assert.Equal(included, Assert.Single(group.Items));
    }
}
