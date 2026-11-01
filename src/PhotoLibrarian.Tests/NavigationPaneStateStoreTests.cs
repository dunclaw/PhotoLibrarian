// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class NavigationPaneStateStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "PhotoLibrarianTests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "navigation-pane.json");

    [Fact]
    public void Reload_PreservesExpandedCollapsedCheckedAndUncheckedNodes()
    {
        var store = new NavigationPaneStateStore(SettingsPath);
        var state = new NavigationPaneState();
        state.SetNode("LibraryTree", "root", new(true, false));
        state.SetNode("LibraryTree", @"C:\PHOTOS|C:\PHOTOS\TRIP", new(false, true));
        state.SetNode("DateTree", "2026-10", new(true, true));
        state.SetNode("PeopleTree", "person:42", new(false, true));
        state.SetNode("TagsTree", "tag:Places/Beach", new(true, false));
        state.SetNode("FlagsTree", "flagged", new(false, true));
        store.Save(state);

        var reloaded = new NavigationPaneStateStore(SettingsPath).Load();

        Assert.Equal(state.Version, reloaded.Version);
        foreach (var (tree, nodes) in state.Trees)
        {
            foreach (var (key, expected) in nodes)
                Assert.Equal(expected, reloaded.GetNode(tree, key));
        }
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void MissingPreferences_LeaveExistingUserDefaultsUnspecified()
    {
        var state = new NavigationPaneStateStore(SettingsPath).Load();

        Assert.Empty(state.Trees);
        Assert.Null(state.GetNode("LibraryTree", "root"));
        Assert.Null(state.GetNode("PeopleTree", "root"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("""{"Version":2,"Trees":{}}""")]
    [InlineData("""{"Version":1,"Trees":null}""")]
    [InlineData("""{"Version":1,"Trees":{"TagsTree":null}}""")]
    [InlineData("""{"Version":1,"Trees":{"TagsTree":{"root":null}}}""")]
    public void InvalidPreferences_UseExistingDefaults(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, json);

        var state = new NavigationPaneStateStore(SettingsPath).Load();

        Assert.Empty(state.Trees);
        // Loading preferences never changes the source file.
        Assert.Equal(json, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void UnknownAndRenamedNodes_DoNotInheritOldChecks()
    {
        var state = new NavigationPaneState();
        state.SetNode("TagsTree", "tag:Old name", new(true, true));
        state.SetNode("PeopleTree", "person:42", new(false, true));

        Assert.Null(state.GetNode("TagsTree", "tag:New name"));
        Assert.Null(state.GetNode("PeopleTree", "person:43"));
        Assert.Null(state.GetNode("DateTree", "person:42"));
    }

    [Fact]
    public void TreeRebuildOrderAndPartialFolderRealization_DoNotEraseOtherPreferences()
    {
        var store = new NavigationPaneStateStore(SettingsPath);
        var state = new NavigationPaneState();
        state.SetNode("TagsTree", "root", new(true, true));
        state.SetNode("LibraryTree", @"C:\PHOTOS|C:\PHOTOS\TRIP\DAY1", new(false, true));
        store.Save(state);
        state = store.Load();

        // Capture only currently realized nodes while other trees or lazy descendants await rebuilding.
        state.SetNode("LibraryTree", "root", new(true, false));
        state.SetNode("LibraryTree", @"C:\PHOTOS|C:\PHOTOS\TRIP", new(false, false));
        state.SetNode("DateTree", "__root__", new(false, false));
        store.Save(state);
        state = store.Load();

        Assert.Equal(new NavigationNodeState(true, true), state.GetNode("TagsTree", "root"));
        Assert.Equal(new NavigationNodeState(false, true),
            state.GetNode("LibraryTree", @"C:\PHOTOS|C:\PHOTOS\TRIP\DAY1"));
        Assert.True(state.HasFolderDescendant("LibraryTree", @"C:\PHOTOS|C:\PHOTOS\TRIP"));
        Assert.False(state.GetNode("LibraryTree", @"C:\PHOTOS|C:\PHOTOS\TRIP")!.Expanded);
    }

    [Fact]
    public void FolderRoutes_AreScopedToWatchedRootsAndDirectoryBoundaries()
    {
        var state = new NavigationPaneState();
        state.SetNode("LibraryTree", @"C:\PHOTOS|C:\PHOTOS\TRIP\DAY1", new(false, true));
        state.SetNode("LibraryTree", @"C:\PHOTOS|C:\PHOTOS\OTHER\DAY1", new(false, false));

        Assert.True(state.HasFolderDescendant("LibraryTree", @"C:\PHOTOS|C:\PHOTOS\TRIP"));
        Assert.False(state.HasFolderDescendant("LibraryTree", @"C:\PHOTOS|C:\PHOTOS\TR"));
        Assert.False(state.HasFolderDescendant("LibraryTree", @"C:\PHOTOS\TRIP|C:\PHOTOS\TRIP"));
        Assert.False(state.HasFolderDescendant("LibraryTree", @"C:\PHOTOS|C:\PHOTOS\OTHER"));
    }

    [Fact]
    public void DuplicateKeys_UpdateInPlaceWithoutMergingDistinctTags()
    {
        var store = new NavigationPaneStateStore(SettingsPath);
        var state = new NavigationPaneState();
        state.SetNode("TagsTree", "tag:Beach", new(true, true));
        state.SetNode("TagsTree", "tag:Beach", new(false, false));
        state.SetNode("TagsTree", "tag:beach", new(true, true));
        store.Save(state);
        state = store.Load();

        Assert.Equal(2, state.Trees["TagsTree"].Count);
        Assert.Equal(new NavigationNodeState(false, false), state.GetNode("TagsTree", "tag:Beach"));
        Assert.Equal(new NavigationNodeState(true, true), state.GetNode("TagsTree", "tag:beach"));
    }

    [Fact]
    public void SaveFailure_IsNotReportedAsSuccess()
    {
        Directory.CreateDirectory(SettingsPath);
        var store = new NavigationPaneStateStore(SettingsPath);

        var exception = Record.Exception(() => store.Save(new NavigationPaneState()));
        Assert.True(exception is IOException or UnauthorizedAccessException,
            $"Expected a file access failure, received {exception?.GetType().Name ?? "no exception"}.");
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
