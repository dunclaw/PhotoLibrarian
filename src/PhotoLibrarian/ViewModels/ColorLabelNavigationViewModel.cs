// Copyright (c) Duncan Lawler. All Rights Reserved.

using CommunityToolkit.Mvvm.ComponentModel;
using PhotoLibrarian.Core.Data;

namespace PhotoLibrarian.ViewModels;

public sealed class ColorLabelNavigationViewModel(ImageRepository imageRepo)
{
    public IReadOnlyList<ColorLabelNavigationItem> Items { get; } =
    [
        new("Red"),
        new("Yellow"),
        new("Green"),
        new("Blue")
    ];

    public async Task LoadAsync()
    {
        var counts = await imageRepo.GetColorLabelCountsAsync(watchedOnly: true);
        foreach (var item in Items)
            item.Count = counts.GetValueOrDefault(item.Name);
    }
}

public partial class ColorLabelNavigationItem(string name) : ObservableObject
{
    public string Name { get; } = name;
    public string NodeKey => $"color:{Name}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    public partial int Count { get; set; }

    public string Label => $"{Name} ({Count})";
}
