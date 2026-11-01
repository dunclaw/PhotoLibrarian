// Copyright (c) Duncan Lawler. All Rights Reserved.
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoLibrarian.Core.Data;

namespace PhotoLibrarian.ViewModels;

public partial class NotTaggedNavigationViewModel : ObservableObject
{
    public const string NodeKey = "not-tagged";
    private readonly TagRepository _tagRepo;

    public NotTaggedNavigationViewModel(TagRepository tagRepo)
    {
        _tagRepo = tagRepo;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountLabel))]
    public partial int Count { get; set; }

    public string CountLabel => $"({Count:N0})";

    public async Task LoadAsync()
    {
        Count = await _tagRepo.GetNotTaggedCountAsync();
    }
}
