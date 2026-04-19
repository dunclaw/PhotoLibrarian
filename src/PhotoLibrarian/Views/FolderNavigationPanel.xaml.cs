using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoLibrarian.ViewModels;
using PhotoLibrarian.Diagnostics;
using System.Collections.Generic;
using System.Linq;

namespace PhotoLibrarian.Views;

public sealed partial class FolderNavigationPanel : UserControl
{
    private FolderNavigationViewModel? ViewModel => App.ViewModel?.FolderNav;

    public FolderNavigationPanel()
    {
        this.InitializeComponent();
        this.Loaded += OnLoaded;
    }

    public async Task RefreshAllTreesAsync()
    {
        RefreshLibraryTree();
        await RefreshDateTreeAsync();
        await RefreshTagsTreeAsync();
    }

    public async Task RefreshMetadataTreesAsync()
    {
        await RefreshDateTreeAsync();
        await RefreshTagsTreeAsync();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.RootFolders.CollectionChanged += (s, args) => RefreshLibraryTree();
        RefreshLibraryTree();
        
        // Initial load of date and tag trees (don't bind to CollectionChanged to avoid recursion)
        _ = RefreshDateTreeAsync();
        _ = RefreshTagsTreeAsync();
    }

    private void RefreshLibraryTree()
    {
        if (ViewModel is null) return;

        LibraryTree.RootNodes.Clear();

        // Create "Photo Library" root node
        var photoLibraryRoot = new TreeViewNode
        {
            Content = "📚 Photo Library",
            IsExpanded = true
        };

        // Add root folders under Photo Library
        foreach (var rootFolder in ViewModel.RootFolders)
        {
            var rootNode = BuildFolderNode(rootFolder, isRootFolder: true);
            photoLibraryRoot.Children.Add(rootNode);
        }

        LibraryTree.RootNodes.Add(photoLibraryRoot);
    }

    private async Task RefreshDateTreeAsync()
    {
        if (App.ViewModel?.DateNav is null) return;

        await App.ViewModel.DateNav.LoadDatesAsync();
        
        // Must update UI on dispatcher queue
        DispatcherQueue.TryEnqueue(() =>
        {
            DateTree.RootNodes.Clear();

            foreach (var rootNode in App.ViewModel.DateNav.RootNodes)
            {
                var treeNode = BuildDateNode(rootNode);
                DateTree.RootNodes.Add(treeNode);
            }
        });
    }

    private TreeViewNode BuildDateNode(DateNode dateNode)
    {
        var treeNode = new TreeViewNode
        {
            Content = new DateNodeWrapper(dateNode),
            IsExpanded = false
        };

        foreach (var child in dateNode.Children)
        {
            treeNode.Children.Add(BuildDateNode(child));
        }

        return treeNode;
    }

    private async Task RefreshTagsTreeAsync()
    {
        if (App.ViewModel?.TagNav is null) return;

        await App.ViewModel.TagNav.LoadTagsAsync();

        // Must update UI on dispatcher queue
        DispatcherQueue.TryEnqueue(() =>
        {
            TagsTree.RootNodes.Clear();

            foreach (var tagNode in App.ViewModel.TagNav.RootTags)
            {
                var treeNode = BuildTagNode(tagNode);
                TagsTree.RootNodes.Add(treeNode);
            }
        });
    }

    private static TreeViewNode BuildTagNode(TagNode tagNode)
    {
        var treeNode = new TreeViewNode
        {
            Content = new TagNodeWrapper(tagNode),
            HasUnrealizedChildren = tagNode.Children.Count > 0
        };

        // Add children
        foreach (var child in tagNode.Children)
        {
            treeNode.Children.Add(BuildTagNode(child));
        }

        return treeNode;
    }

    private static TreeViewNode BuildFolderNode(FolderNode folderNode, bool isRootFolder = false)
    {
        // Create display text with icon
        string icon = "📁";
        string displayName = folderNode.Name;
        string displayText = isRootFolder 
            ? $"{icon} {folderNode.Path}"  // Show full path for root folders
            : $"{icon} {displayName}";     // Show just name for subfolders

        // Store FolderNode in a wrapper for event handlers to retrieve
        var treeNode = new TreeViewNode 
        { 
            Content = new FolderNodeWrapper(folderNode, displayText),
            IsExpanded = false
        };

        // Check if this node has placeholder children (lazy-load marker)
        bool hasPlaceholder = folderNode.Children.Count == 1 && folderNode.Children[0].Path == "";

        if (hasPlaceholder)
        {
            treeNode.HasUnrealizedChildren = true;
        }
        else if (folderNode.Children.Count > 0)
        {
            // Add realized children
            foreach (var child in folderNode.Children)
            {
                treeNode.Children.Add(BuildFolderNode(child, isRootFolder: false));
            }
        }

        return treeNode;
    }

    private async void OnManageFoldersClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ManageFoldersDialog
        {
            XamlRoot = this.XamlRoot
        };
        await dialog.ShowAsync();
        
        // Refresh tree after dialog closes
        RefreshLibraryTree();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.RefreshCommand.CanExecute(null) == true)
            await ViewModel.RefreshCommand.ExecuteAsync(null);
    }

    private void OnLibraryItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        // When user clicks on a folder (not checkbox), toggle its selection
        if (args.InvokedItem is TreeViewNode node)
        {
            if (sender.SelectedNodes.Contains(node))
            {
                // Already selected, deselect it
                sender.SelectedNodes.Remove(node);
            }
            else
            {
                // Not selected, add it
                sender.SelectedNodes.Add(node);
            }

            // Manually trigger grid update since programmatic selection change
            // might not fire SelectionChanged event
            UpdateGridFromSelection();
        }
    }

    private void OnDateItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is TreeViewNode node)
        {
            DebugLog.WriteLine($"OnDateItemInvoked: Node={node.Content}, IsSelected={sender.SelectedNodes.Contains(node)}");
            
            if (sender.SelectedNodes.Contains(node))
            {
                sender.SelectedNodes.Remove(node);
            }
            else
            {
                sender.SelectedNodes.Add(node);
            }
            
            DebugLog.WriteLine($"  After toggle: IsSelected={sender.SelectedNodes.Contains(node)}, TotalSelected={sender.SelectedNodes.Count}");
            UpdateGridFromSelection();
        }
    }

    private void OnTagsItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is TreeViewNode node)
        {
            if (sender.SelectedNodes.Contains(node))
            {
                sender.SelectedNodes.Remove(node);
            }
            else
            {
                sender.SelectedNodes.Add(node);
            }
            UpdateGridFromSelection();
        }
    }

    private void OnLibraryExpanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Node.Content is not FolderNodeWrapper wrapper || wrapper.FolderNode is null) return;

        var folderNode = wrapper.FolderNode;

        // Check if we need to load placeholder children
        bool hasPlaceholder = folderNode.Children.Count == 1 && folderNode.Children[0].Path == "";
        
        if (hasPlaceholder)
        {
            // Clear placeholder and load real children
            folderNode.Children.Clear();
            FolderNavigationViewModel.BuildChildNodes(folderNode);

            // Rebuild the TreeViewNode children
            args.Node.Children.Clear();
            args.Node.HasUnrealizedChildren = false;

            foreach (var child in folderNode.Children)
            {
                args.Node.Children.Add(BuildFolderNode(child, isRootFolder: false));
            }
        }
    }

    private void OnLibrarySelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        UpdateGridFromSelection();
    }

    private void OnDateSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        DebugLog.WriteLine($"OnDateSelectionChanged: AddedItems={args.AddedItems.Count}, RemovedItems={args.RemovedItems.Count}, TotalSelected={sender.SelectedNodes.Count}");
        UpdateGridFromSelection();
    }

    private void OnTagsSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        DebugLog.WriteLine($"OnTagsSelectionChanged: AddedItems={args.AddedItems.Count}, RemovedItems={args.RemovedItems.Count}, TotalSelected={sender.SelectedNodes.Count}");
        UpdateGridFromSelection();
    }

    private void UpdateGridFromSelection()
    {
        if (App.ViewModel?.ImageGrid is null) return;

        // Collect all selected folder paths
        var selectedFolders = new List<string>();
        bool photoLibraryRootSelected = false;
        
        foreach (var node in LibraryTree.SelectedNodes)
        {
            if (node.Content is string str && str.StartsWith("📚"))
            {
                // Photo Library root node selected - means "show all folders"
                photoLibraryRootSelected = true;
            }
            else if (node.Content is FolderNodeWrapper wrapper && wrapper.FolderNode != null)
            {
                selectedFolders.Add(wrapper.FolderNode.Path);
            }
        }

        // Collect selected date ranges (year/month/root)
        var selectedYears = new List<int>();
        var selectedMonths = new List<(int Year, int Month)>();
        bool dateRootSelected = false;
        foreach (var node in DateTree.SelectedNodes)
        {
            if (node.Content is DateNodeWrapper wrapper)
            {
                DebugLog.WriteLine($"  Date node selected: IsRoot={wrapper.DateNode.IsRoot}, Year={wrapper.DateNode.Year}, Month={wrapper.DateNode.Month}, Count={wrapper.DateNode.Count}");
                
                if (wrapper.DateNode.IsRoot)
                {
                    // Root "Dates" node - show all dated images
                    dateRootSelected = true;
                }
                else if (wrapper.DateNode.Month.HasValue)
                {
                    // Specific month
                    selectedMonths.Add((wrapper.DateNode.Year, wrapper.DateNode.Month.Value));
                }
                else
                {
                    // Whole year
                    selectedYears.Add(wrapper.DateNode.Year);
                }
            }
        }

        // Collect selected tags
        var selectedTags = new List<string>();
        bool tagRootSelected = false;
        foreach (var node in TagsTree.SelectedNodes)
        {
            if (node.Content is TagNodeWrapper wrapper)
            {
                if (wrapper.TagNode.IsRoot)
                {
                    // Root "Tags" node - show all tagged images
                    tagRootSelected = true;
                }
                else
                {
                    selectedTags.Add(wrapper.TagNode.FullPath);
                }
            }
        }

        DebugLog.WriteLine($"UpdateGridFromSelection: PhotoLibraryRoot={photoLibraryRootSelected}, Folders={selectedFolders.Count}, DateRoot={dateRootSelected}, Years={selectedYears.Count}, Months={selectedMonths.Count}, TagRoot={tagRootSelected}, Tags={selectedTags.Count}");

        // If nothing selected anywhere, clear filters to show empty grid
        if (!photoLibraryRootSelected && !dateRootSelected && !tagRootSelected && selectedFolders.Count == 0 && selectedYears.Count == 0 && 
            selectedMonths.Count == 0 && selectedTags.Count == 0)
        {
            DebugLog.WriteLine("  No selections - clearing filter");
            App.ViewModel.ImageGrid.ClearFilterCommand.Execute(null);
            return;
        }

        // If Photo Library root is selected and nothing else, treat as "show all from all folders"
        if (photoLibraryRootSelected && selectedFolders.Count == 0)
        {
            // Add all root folder paths
            selectedFolders.AddRange(ViewModel?.RootFolders.Select(f => f.Path) ?? []);
        }

        // Apply multi-criteria filter
        _ = App.ViewModel.ImageGrid.FilterByMultipleCriteriaAsync(
            selectedFolders.Count > 0 ? selectedFolders : null,
            dateRootSelected,
            selectedYears.Count > 0 ? selectedYears : null,
            selectedMonths.Count > 0 ? selectedMonths : null,
            tagRootSelected,
            selectedTags.Count > 0 ? selectedTags : null);
    }

    // Helper class to wrap FolderNode with display text for TreeView
    private class FolderNodeWrapper
    {
        public FolderNode? FolderNode { get; }
        public string DisplayText { get; }

        public FolderNodeWrapper(FolderNode? folderNode, string displayText)
        {
            FolderNode = folderNode;
            DisplayText = displayText;
        }

        public override string ToString() => DisplayText;
    }

    // Helper class to wrap DateNode for TreeView
    private class DateNodeWrapper
    {
        public DateNode DateNode { get; }

        public DateNodeWrapper(DateNode dateNode)
        {
            DateNode = dateNode;
        }

        public override string ToString()
        {
            // Root node already has emoji in DisplayName, others need the calendar icon
            if (DateNode.IsRoot)
                return $"{DateNode.DisplayName} ({DateNode.Count})";
            else
                return $"📅 {DateNode.DisplayName} ({DateNode.Count})";
        }
    }

    // Helper class to wrap TagNode for TreeView
    private class TagNodeWrapper
    {
        public TagNode TagNode { get; }

        public TagNodeWrapper(TagNode tagNode)
        {
            TagNode = tagNode;
        }

        public override string ToString()
        {
            // Root node already has emoji in Name, others need the tag icon
            if (TagNode.IsRoot)
                return $"{TagNode.Name} ({TagNode.Count})";
            else
                return $"🏷️ {TagNode.Name} ({TagNode.Count})";
        }
    }
}
