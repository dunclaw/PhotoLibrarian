using CommunityToolkit.Mvvm.ComponentModel;
using PhotoLibrarian.Core.Data;
using System.Collections.ObjectModel;

namespace PhotoLibrarian.ViewModels;

/// <summary>
/// ViewModel for tag-based navigation tree.
/// Shows all unique tags with their usage count.
/// </summary>
public partial class TagNavigationViewModel : ObservableObject
{
    private readonly TagRepository _tagRepo;

    public ObservableCollection<TagNode> Tags { get; } = [];

    public TagNavigationViewModel(TagRepository tagRepo)
    {
        _tagRepo = tagRepo;
    }

    public async Task LoadTagsAsync()
    {
        Tags.Clear();

        // Get all unique tags with their counts
        var tagCounts = await _tagRepo.GetAllTagsWithCountAsync();

        foreach (var (tag, count) in tagCounts)
        {
            Tags.Add(new TagNode
            {
                Tag = tag,
                Count = count
            });
        }
    }
}

/// <summary>
/// Represents a tag with its usage count.
/// </summary>
public class TagNode
{
    public string Tag { get; set; } = "";
    public int Count { get; set; }
}
