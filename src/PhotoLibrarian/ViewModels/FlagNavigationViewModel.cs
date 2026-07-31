using CommunityToolkit.Mvvm.ComponentModel;
using PhotoLibrarian.Core.Data;

namespace PhotoLibrarian.ViewModels;

/// <summary>
/// Backs the left panel's "Flagged" filter node — a Photo Gallery style working set of
/// images the user has flagged.
/// </summary>
public partial class FlagNavigationViewModel : ObservableObject
{
    private readonly ImageRepository _imageRepo;

    public FlagNavigationViewModel(ImageRepository imageRepo)
    {
        _imageRepo = imageRepo;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    public partial int Count { get; set; }

    public string DisplayName => "🚩 Flagged";

    /// <summary>
    /// Text shown on the left-panel node. Bound (not stringified into TreeViewNode.Content) so the
    /// count repaints when it changes — a realized TreeViewItem ignores Content reassignment.
    /// </summary>
    public string Label => $"{DisplayName} ({Count})";

    public async Task LoadAsync()
    {
        Count = await _imageRepo.GetFlaggedCountAsync();
    }
}
