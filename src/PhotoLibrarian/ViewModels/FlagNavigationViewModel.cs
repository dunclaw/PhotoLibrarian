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
    public partial int Count { get; set; }

    public string DisplayName => "🚩 Flagged";

    public async Task LoadAsync()
    {
        Count = await _imageRepo.GetFlaggedCountAsync();
    }
}
