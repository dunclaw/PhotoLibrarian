using CommunityToolkit.Mvvm.ComponentModel;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Diagnostics;
using System.Collections.ObjectModel;

namespace PhotoLibrarian.ViewModels;

/// <summary>
/// ViewModel for date-based navigation tree.
/// Organizes images by capture date into Year > Month hierarchy.
/// </summary>
public partial class DateNavigationViewModel : ObservableObject
{
    private readonly ImageRepository _imageRepo;

    public ObservableCollection<DateNode> YearNodes { get; } = [];

    public DateNavigationViewModel(ImageRepository imageRepo)
    {
        _imageRepo = imageRepo;
    }

    public async Task LoadDatesAsync()
    {
        YearNodes.Clear();

        // Get all images with DateTaken
        var images = await _imageRepo.GetAllAsync();
        var imagesWithDates = images.Where(i => i.DateTaken.HasValue).ToList();

        DebugLog.WriteLine($"DateNavigationViewModel.LoadDatesAsync: Total images={images.Count}, with dates={imagesWithDates.Count}");

        // Group by year, then month
        var yearGroups = imagesWithDates
            .GroupBy(i => i.DateTaken!.Value.Year)
            .OrderByDescending(g => g.Key);

        foreach (var yearGroup in yearGroups)
        {
            var yearNode = new DateNode
            {
                DisplayName = yearGroup.Key.ToString(),
                Year = yearGroup.Key,
                Count = yearGroup.Count()
            };

            // Group months under this year
            var monthGroups = yearGroup
                .GroupBy(i => i.DateTaken!.Value.Month)
                .OrderByDescending(g => g.Key);

            foreach (var monthGroup in monthGroups)
            {
                var monthNode = new DateNode
                {
                    DisplayName = new DateTime(yearGroup.Key, monthGroup.Key, 1).ToString("MMMM"),
                    Year = yearGroup.Key,
                    Month = monthGroup.Key,
                    Count = monthGroup.Count()
                };
                yearNode.Children.Add(monthNode);
            }

            YearNodes.Add(yearNode);
        }
        
        DebugLog.WriteLine($"  Built {YearNodes.Count} year nodes");
    }
}

/// <summary>
/// Represents a node in the date hierarchy (Year or Month).
/// </summary>
public class DateNode
{
    public string DisplayName { get; set; } = "";
    public int Year { get; set; }
    public int? Month { get; set; }
    public int Count { get; set; }
    public ObservableCollection<DateNode> Children { get; } = [];
}
