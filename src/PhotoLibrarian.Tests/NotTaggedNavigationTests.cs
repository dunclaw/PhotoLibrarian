// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.ViewModels;
using System.Xml.Linq;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class NotTaggedNavigationTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "PhotoLibrarianTests", Guid.NewGuid().ToString("N"));
    private CacheDatabase _database = null!;
    private ImageRepository _images = null!;
    private TagRepository _tags = null!;
    private NotTaggedNavigationViewModel _navigation = null!;

    public async ValueTask InitializeAsync()
    {
        _database = new CacheDatabase(Path.Combine(_directory, "cache.db"));
        await _database.InitializeAsync();
        _images = new ImageRepository(_database);
        _tags = new TagRepository(_database);
        _navigation = new NotTaggedNavigationViewModel(_tags);
    }

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task EmptyLibrary_HasZeroCountAndNoResults()
    {
        await _navigation.LoadAsync();

        Assert.Equal(0, _navigation.Count);
        Assert.Equal("(0)", _navigation.CountLabel);
        Assert.Empty(await GetResultsAsync(notTaggedSelected: true));
        Assert.Empty(await GetResultsAsync(notTaggedSelected: false));
    }

    [Fact]
    public async Task PopulatedLibrary_UsesSamePredicateAsMissingMetadataIncludingVideosAndAllTagSources()
    {
        var photo = await AddImageAsync("untagged.jpg");
        var video = await AddImageAsync("untagged.mp4", MediaType.Video);
        foreach (var source in Enum.GetValues<TagSource>())
        {
            var tagged = await AddImageAsync($"{source}.jpg");
            await _tags.AddTagAsync(new ImageTag
            {
                ImageId = tagged.Id,
                Tag = source == TagSource.AutoML ? "Auto/animal/bird" : "Places/Beach",
                Source = source
            });
        }

        await _navigation.LoadAsync();
        var navigationResults = await GetResultsAsync(notTaggedSelected: true);
        var flyoutResults = await GetResultsAsync(notTaggedSelected: false,
            matchesOtherNavigation: true,
            refinement: new ImageRefinementFilter { MissingMetadata = MissingMetadataFilter.Tags });

        Assert.Equal(2, _navigation.Count);
        Assert.Equal(new[] { photo.Id, video.Id }, navigationResults.Select(image => image.Id).Order());
        Assert.Equal(navigationResults.Select(image => image.Id), flyoutResults.Select(image => image.Id));
        Assert.Empty(await GetResultsAsync(notTaggedSelected: false));
    }

    [Fact]
    public async Task Selection_UnionsWithOtherBrowseNodesThenAndComposesRefinementAndSearch()
    {
        var untagged = await AddImageAsync("holiday.jpg");
        untagged.Rating = 5;
        untagged.DateTaken = new DateTime(2026, 6, 15);
        await _images.UpsertImageAsync(untagged);
        await AddImageAsync("unrated.jpg");
        var tagged = await AddImageAsync("tagged.jpg");
        await _tags.AddTagAsync(new ImageTag { ImageId = tagged.Id, Tag = "Travel" });
        await _navigation.LoadAsync();

        Assert.Equal(2, (await GetResultsAsync(notTaggedSelected: true)).Count);
        Assert.Equal(3, (await GetResultsAsync(notTaggedSelected: true, matchesOtherNavigation: true)).Count);
        var refinement = new ImageRefinementFilter
        {
            Rating = 4,
            DateFrom = new DateTime(2026, 1, 1),
            DateTo = new DateTime(2026, 12, 31),
            MediaKind = MediaKindFilter.Photos,
            Extensions = ["jpg"]
        };
        var refined = await GetResultsAsync(notTaggedSelected: true, refinement: refinement);
        Assert.Equal(untagged.Id, Assert.Single(refined).Id);
        var search = new ImageSearchRepository(_database);
        Assert.Equal(untagged.Id, Assert.Single(await search.FilterAsync("holiday", refined)).Id);
        Assert.Empty(await search.FilterAsync("tagged", refined));
        Assert.Empty(await GetResultsAsync(notTaggedSelected: true,
            refinement: new ImageRefinementFilter { IncludedTags = ["Travel"] }));
        Assert.Equal(2, _navigation.Count); // Peer Browse counts are not narrowed by refinement or search.
    }

    [Fact]
    public void NotTaggedSelection_StillRespectsPeopleFlagsAndExcludedTags()
    {
        var image = new ImageEntry { FilePath = "photo.jpg", FileName = "photo.jpg", IsFlagged = true };
        var refinement = new ImageRefinementFilter
        {
            PersonId = 42,
            Flag = FlagFilterMode.Flagged,
            ExcludedTags = ["Reject"]
        };
        Assert.True(ImageGridViewModel.MatchesNavigationAndRefinement(
            image, false, true, refinement, [], [42]));
        Assert.False(ImageGridViewModel.MatchesNavigationAndRefinement(
            image, false, true, refinement, [], [7]));
        Assert.False(ImageGridViewModel.MatchesNavigationAndRefinement(
            image, true, true, refinement, ["Reject"], [42]));
        image.IsFlagged = false;
        Assert.False(ImageGridViewModel.MatchesNavigationAndRefinement(
            image, false, true, refinement, [], [42]));
    }

    [Fact]
    public async Task CountAndResults_TrackTaggingLastTagRemovalIndexUpdatesAndDeletion()
    {
        var image = await AddImageAsync("photo.jpg");
        await _navigation.LoadAsync();
        Assert.Equal(1, _navigation.Count);
        var notifications = new List<string?>();
        _navigation.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        await _tags.AddTagAsync(new ImageTag { ImageId = image.Id, Tag = "Places/Beach" });
        await _navigation.LoadAsync();
        Assert.Equal(0, _navigation.Count);
        Assert.Empty(await GetResultsAsync(notTaggedSelected: true));
        Assert.Contains(nameof(NotTaggedNavigationViewModel.CountLabel), notifications);

        await _tags.RemoveTagAsync(image.Id, "Places/Beach");
        await _navigation.LoadAsync();
        Assert.Equal(0, _navigation.Count); // The remaining parent tag still counts as a tag.
        await _tags.RemoveTagAsync(image.Id, "Places");
        await _navigation.LoadAsync();
        Assert.Equal(image.Id, Assert.Single(await GetResultsAsync(notTaggedSelected: true)).Id);
        Assert.Equal(1, _navigation.Count);

        await _images.UpsertImageAsync(image); // Reindexing an existing item does not duplicate it.
        var imported = await AddImageAsync("imported.mp4", MediaType.Video);
        await _navigation.LoadAsync();
        Assert.Equal(2, _navigation.Count);
        Assert.Equal(2, (await GetResultsAsync(notTaggedSelected: true)).Count);
        await _tags.AddTagAsync(new ImageTag
        {
            ImageId = imported.Id, Tag = "Imported", Source = TagSource.Imported
        });
        await _images.DeleteByPathAsync(image.FilePath);
        await _navigation.LoadAsync();
        Assert.Equal(0, _navigation.Count);
        Assert.Empty(await GetResultsAsync(notTaggedSelected: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TreeRefreshAndReload_KeepStableCheckedIdentityIndependentOfCount(bool isChecked)
    {
        var store = new NavigationPaneStateStore(Path.Combine(_directory, "navigation-pane.json"));
        var state = store.Load();
        state.SetNode("NotTaggedTree", NotTaggedNavigationViewModel.NodeKey, new(false, isChecked));
        store.Save(state);

        await AddImageAsync("new.jpg");
        await _navigation.LoadAsync();
        var rebuiltNavigation = new NotTaggedNavigationViewModel(_tags);
        await rebuiltNavigation.LoadAsync();
        state = store.Load();
        state.SetNode("TagsTree", "tag:New", new(true, true));
        store.Save(state);

        Assert.Equal(_navigation.CountLabel, rebuiltNavigation.CountLabel);
        Assert.Equal(new NavigationNodeState(false, isChecked),
            store.Load().GetNode("NotTaggedTree", NotTaggedNavigationViewModel.NodeKey));
        Assert.Null(state.GetNode("TagsTree", NotTaggedNavigationViewModel.NodeKey));
    }

    [Fact]
    public void Node_IsAccessibleLocalizableAndSeparateFromTheTaggedRoot()
    {
        var document = XDocument.Load(Path.Combine(
            AppContext.BaseDirectory, "TestAssets", "FolderNavigationPanel.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var tree = Assert.Single(document.Descendants().Where(
            element => (string?)element.Attribute(xaml + "Name") == "NotTaggedTree"));
        Assert.Equal("Multiple", (string?)tree.Attribute("SelectionMode"));
        Assert.Equal("OnTagsItemInvoked", (string?)tree.Attribute("ItemInvoked"));
        Assert.Equal("OnTagsSelectionChanged", (string?)tree.Attribute("SelectionChanged"));
        Assert.Equal("NotTaggedTree", (string?)tree.Attribute("AutomationProperties.AutomationId"));
        Assert.Equal("NotTaggedTree", (string?)tree.Attribute(xaml + "Uid"));
        var item = Assert.Single(tree.Descendants().Where(element => element.Name.LocalName == "TreeViewItem"));
        Assert.Equal("Not tagged", (string?)item.Attribute("AutomationProperties.Name"));
        Assert.Equal("NotTaggedNode", (string?)item.Attribute(xaml + "Uid"));
        Assert.Contains(tree.Descendants(), element =>
            (string?)element.Attribute("Text") == "{Binding Content.CountLabel, Mode=OneWay}");
        Assert.Contains(tree.Parent!.Elements(), element =>
            (string?)element.Attribute(xaml + "Name") == "TagsTree");
    }

    private async Task<ImageEntry> AddImageAsync(string fileName, MediaType mediaType = MediaType.Image)
    {
        var image = new ImageEntry
        {
            FilePath = Path.Combine(_directory, fileName),
            FileName = fileName,
            MediaType = mediaType,
            DateModified = DateTime.UtcNow
        };
        image.Id = await _images.UpsertImageAsync(image);
        return image;
    }

    private async Task<List<ImageEntry>> GetResultsAsync(
        bool notTaggedSelected,
        bool matchesOtherNavigation = false,
        ImageRefinementFilter? refinement = null)
    {
        var images = await _images.GetAllAsync();
        var tagsByImageId = await _tags.GetTagsByImageIdAsync();
        return images.Where(image =>
        {
            tagsByImageId.TryGetValue(image.Id, out var tags);
            return ImageGridViewModel.MatchesNavigationAndRefinement(image,
                matchesOtherNavigation, notTaggedSelected,
                refinement ?? ImageRefinementFilter.Empty, tags);
        }).ToList();
    }
}
