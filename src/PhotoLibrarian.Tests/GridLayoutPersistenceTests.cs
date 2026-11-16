// Copyright (c) Duncan Lawler. All Rights Reserved.

using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Services;
using PhotoLibrarian.ViewModels;
using System.Text.Json;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class GridLayoutPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"PhotoLibrarian-GridLayout-{Guid.NewGuid():N}");

    private string SettingsPath => Path.Combine(_directory, "grid-layout.json");

    [Fact]
    public void MissingPreferences_KeepExistingDefaultsWithoutCreatingAFile()
    {
        var grid = CreateGrid();

        Assert.Equal(GroupByOption.None, grid.GroupBy);
        Assert.Equal(SortByOption.DateTaken, grid.SortBy);
        Assert.True(grid.SortDescending);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void LayoutChanges_AreSavedImmediatelyAndRestoredByANewGrid()
    {
        var grid = CreateGrid();
        grid.GroupBy = GroupByOption.MonthTaken;
        Assert.Equal(GroupByOption.MonthTaken, LoadSettings().GroupBy);
        grid.SortBy = SortByOption.Rating;
        Assert.Equal(SortByOption.Rating, LoadSettings().SortBy);
        grid.SortDescending = false;
        Assert.False(LoadSettings().SortDescending);
        grid.SelectedImages.Add(new ImageThumbnailViewModel(
            new ImageEntry { FilePath = "selected.jpg", FileName = "selected.jpg" }));

        var beforeReload = File.ReadAllText(SettingsPath);
        var reloaded = CreateGrid();

        Assert.Equal(GroupByOption.MonthTaken, reloaded.GroupBy);
        Assert.Equal(SortByOption.Rating, reloaded.SortBy);
        Assert.False(reloaded.SortDescending);
        Assert.Equal(beforeReload, File.ReadAllText(SettingsPath));
        Assert.Null(reloaded.SelectedImage);
        Assert.Empty(reloaded.SelectedImages);
        Assert.Equal(ImageRefinementFilter.Empty, reloaded.Refinement);
        Assert.Empty(reloaded.Images);

        using var document = JsonDocument.Parse(beforeReload);
        Assert.Equal(
            new[] { "GroupBy", "SortBy", "SortDescending" },
            document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void AllLayoutChoices_RoundTripThroughANewStore()
    {
        foreach (var group in Enum.GetValues<GroupByOption>())
        foreach (var sort in Enum.GetValues<SortByOption>())
        foreach (var descending in new[] { false, true })
        {
            var settings = new GridLayoutSettings
            {
                GroupBy = group,
                SortBy = sort,
                SortDescending = descending
            };
            new GridLayoutSettingsStore(SettingsPath).Save(settings);

            Assert.Equal(settings, LoadSettings());
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("not json")]
    [InlineData("""{"GroupBy":"unknown"}""")]
    [InlineData("""{"SortDescending":"invalid"}""")]
    [InlineData("""{"GroupBy":999,"SortBy":-1}""")]
    public void MissingOrInvalidPreferences_UseExistingDefaults(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, json);

        Assert.Equal(new GridLayoutSettings(), LoadSettings());
        Assert.Equal(json, File.ReadAllText(SettingsPath));
    }

    [Theory]
    [InlineData("""{"GroupBy":999,"SortBy":4,"SortDescending":false}""", GroupByOption.None, SortByOption.Rating, false)]
    [InlineData("""{"GroupBy":4,"SortBy":999}""", GroupByOption.MonthTaken, SortByOption.DateTaken, true)]
    [InlineData("""{"SortDescending":false}""", GroupByOption.None, SortByOption.DateTaken, false)]
    public void InvalidOrMissingOptions_DoNotDiscardOtherValidChoices(
        string json, GroupByOption group, SortByOption sort, bool descending)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, json);

        var settings = LoadSettings();

        Assert.Equal(group, settings.GroupBy);
        Assert.Equal(sort, settings.SortBy);
        Assert.Equal(descending, settings.SortDescending);
    }

    [Fact]
    public void FailedReplacement_ReportsFailureAndRemovesTemporaryFile()
    {
        Directory.CreateDirectory(SettingsPath);
        var store = new GridLayoutSettingsStore(SettingsPath);

        Assert.Throws<UnauthorizedAccessException>(() => store.Save(new GridLayoutSettings()));
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    private ImageGridViewModel CreateGrid() => new(
        null!, null!, null!, null!, null!, null!, null!,
        new GridLayoutSettingsStore(SettingsPath));

    private GridLayoutSettings LoadSettings() =>
        new GridLayoutSettingsStore(SettingsPath).Load();

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
