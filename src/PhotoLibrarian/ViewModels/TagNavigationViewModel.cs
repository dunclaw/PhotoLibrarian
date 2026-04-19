using CommunityToolkit.Mvvm.ComponentModel;
using PhotoLibrarian.Core.Data;
using System.Collections.ObjectModel;

namespace PhotoLibrarian.ViewModels;

/// <summary>
/// ViewModel for tag-based navigation tree.
/// Shows all unique tags organized hierarchically by slash separators.
/// </summary>
public partial class TagNavigationViewModel : ObservableObject
{
    private readonly TagRepository _tagRepo;

    public ObservableCollection<TagNode> RootTags { get; } = [];

    public TagNavigationViewModel(TagRepository tagRepo)
    {
        _tagRepo = tagRepo;
    }

    public async Task LoadTagsAsync()
    {
        RootTags.Clear();

        // Get all unique tags with their counts
        var tagCounts = await _tagRepo.GetAllTagsWithCountAsync();
        
        // Calculate total count
        int totalCount = tagCounts.Sum(t => t.Count);

        // Build hierarchical structure
        var rootDict = new Dictionary<string, TagNode>();
        var tempRootTags = new List<TagNode>();

        foreach (var (tag, count) in tagCounts)
        {
            // Split tag by slashes to create hierarchy
            var parts = tag.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            var currentDict = rootDict;
            TagNode? parentNode = null;
            string currentPath = "";

            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                currentPath = string.IsNullOrEmpty(currentPath) ? part : $"{currentPath}/{part}";

                if (!currentDict.ContainsKey(part))
                {
                    var node = new TagNode
                    {
                        Name = part,
                        FullPath = currentPath,
                        Count = 0 // Will be updated
                    };

                    currentDict[part] = node;

                    if (parentNode == null)
                    {
                        tempRootTags.Add(node);
                    }
                    else
                    {
                        parentNode.Children.Add(node);
                    }
                }

                var currentNode = currentDict[part];

                // If this is the leaf node (final part), add the count
                if (i == parts.Length - 1)
                {
                    currentNode.Count += count;
                }

                // Move to children dictionary for next level
                parentNode = currentNode;
                currentDict = GetOrCreateChildDict(currentNode);
            }
        }

        // Calculate totals for parent nodes (sum of all children)
        foreach (var node in tempRootTags)
        {
            UpdateParentCounts(node);
        }
        
        // Sort alphabetically at each level
        SortTagNodeRecursive(tempRootTags);
        
        // Create root "Tags" node containing all tag hierarchies
        var rootNode = new TagNode
        {
            Name = "🏷️ Tags",
            FullPath = "",
            Count = totalCount,
            IsRoot = true
        };
        
        foreach (var node in tempRootTags)
        {
            rootNode.Children.Add(node);
        }
        
        RootTags.Add(rootNode);
    }

    private void SortTagNodeRecursive(List<TagNode> nodes)
    {
        // Sort current level alphabetically by name (case-insensitive)
        nodes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        
        // Recursively sort children
        foreach (var node in nodes)
        {
            if (node.Children.Count > 0)
            {
                var childList = node.Children.ToList();
                SortTagNodeRecursive(childList);
                
                // Clear and re-add in sorted order
                node.Children.Clear();
                foreach (var child in childList)
                {
                    node.Children.Add(child);
                }
            }
        }
    }

    private Dictionary<string, TagNode> GetOrCreateChildDict(TagNode node)
    {
        var dict = new Dictionary<string, TagNode>();
        foreach (var child in node.Children)
        {
            dict[child.Name] = child;
        }
        return dict;
    }

    private int UpdateParentCounts(TagNode node)
    {
        if (node.Children.Count == 0)
        {
            // Leaf node, count is already set
            return node.Count;
        }

        // Sum counts from all children
        int total = node.Count; // Start with direct count (if any)
        foreach (var child in node.Children)
        {
            total += UpdateParentCounts(child);
        }

        node.Count = total;
        return total;
    }
}

/// <summary>
/// Represents a tag node in the hierarchy.
/// </summary>
public class TagNode
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public int Count { get; set; }
    public bool IsRoot { get; set; }
    public ObservableCollection<TagNode> Children { get; } = [];
}
