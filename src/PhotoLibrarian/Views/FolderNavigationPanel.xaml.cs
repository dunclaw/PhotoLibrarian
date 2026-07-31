using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PhotoLibrarian.ViewModels;
using PhotoLibrarian.Diagnostics;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

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
        RefreshFlagTree();
    }

    public async Task RefreshMetadataTreesAsync()
    {
        await RefreshDateTreeAsync();
        await RefreshTagsTreeAsync();
        RefreshFlagTree();
    }

    /// <summary>
    /// Ensures the single "Flagged" node exists. The node's label is data-bound to
    /// <see cref="FlagNavigationViewModel.Label"/>, so the count repaints on its own.
    /// </summary>
    public void RefreshFlagTree()
    {
        var flagNav = App.ViewModel?.FlagNav;
        if (flagNav is null) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            // Never rebuild the node: clearing RootNodes would drop (and re-fire) the selection,
            // which would momentarily clear an active flag filter.
            if (FlagsTree.RootNodes.Any(n => n.Content is FlagNavigationViewModel)) return;

            FlagsTree.RootNodes.Add(new TreeViewNode { Content = flagNav });
        });
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.RootFolders.CollectionChanged += (s, args) => RefreshLibraryTree();
        RefreshLibraryTree();
        
        // Initial load of date and tag trees (don't bind to CollectionChanged to avoid recursion)
        _ = RefreshDateTreeAsync();
        _ = RefreshTagsTreeAsync();
        RefreshFlagTree();
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
            // Snapshot expansion + selection so editing date taken (or any other refresh trigger)
            // doesn't collapse the user's open year/month and doesn't drop the active filter.
            var expandedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectDateTreeState(DateTree.RootNodes, expandedKeys, selectedKeys);

            DateTree.RootNodes.Clear();

            foreach (var rootNode in App.ViewModel.DateNav.RootNodes)
            {
                var treeNode = BuildDateNode(rootNode);
                DateTree.RootNodes.Add(treeNode);
            }

            RestoreDateTreeState(DateTree.RootNodes, expandedKeys, selectedKeys);
        });
    }

    private static string GetDateNodeKey(DateNode n)
    {
        if (n.IsRoot) return "__root__";
        if (n.Month.HasValue) return $"{n.Year:D4}-{n.Month.Value:D2}";
        return $"{n.Year:D4}";
    }

    private void CollectDateTreeState(
        IList<TreeViewNode> nodes,
        HashSet<string> expanded,
        HashSet<string> selected)
    {
        foreach (var n in nodes)
        {
            if (n.Content is DateNodeWrapper w)
            {
                var key = GetDateNodeKey(w.DateNode);
                if (n.IsExpanded) expanded.Add(key);
                if (DateTree.SelectedNodes.Contains(n)) selected.Add(key);
            }
            if (n.Children.Count > 0)
                CollectDateTreeState(n.Children, expanded, selected);
        }
    }

    private void RestoreDateTreeState(
        IList<TreeViewNode> nodes,
        HashSet<string> expanded,
        HashSet<string> selected)
    {
        foreach (var n in nodes)
        {
            if (n.Content is DateNodeWrapper w)
            {
                var key = GetDateNodeKey(w.DateNode);
                if (expanded.Contains(key))
                    n.IsExpanded = true;
                if (selected.Contains(key))
                    DateTree.SelectedNodes.Add(n);
            }
            if (n.Children.Count > 0)
                RestoreDateTreeState(n.Children, expanded, selected);
        }
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
            // Snapshot the current expansion state (and selection) before rebuild so adding/
            // removing tags doesn't collapse the user's open branches.
            var expandedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectTagTreeState(TagsTree.RootNodes, expandedPaths, selectedPaths);

            TagsTree.RootNodes.Clear();

            foreach (var tagNode in App.ViewModel.TagNav.RootTags)
            {
                var treeNode = BuildTagNode(tagNode);
                TagsTree.RootNodes.Add(treeNode);
            }

            // Restore expansion + selection on matching nodes
            RestoreTagTreeState(TagsTree.RootNodes, expandedPaths, selectedPaths);
        });
    }

    private void CollectTagTreeState(
        IList<TreeViewNode> nodes,
        HashSet<string> expanded,
        HashSet<string> selected)
    {
        foreach (var n in nodes)
        {
            if (n.Content is TagNodeWrapper w)
            {
                if (n.IsExpanded) expanded.Add(w.TagNode.FullPath);
                if (TagsTree.SelectedNodes.Contains(n)) selected.Add(w.TagNode.FullPath);
            }
            if (n.Children.Count > 0)
                CollectTagTreeState(n.Children, expanded, selected);
        }
    }

    private void RestoreTagTreeState(
        IList<TreeViewNode> nodes,
        HashSet<string> expanded,
        HashSet<string> selected)
    {
        foreach (var n in nodes)
        {
            if (n.Content is TagNodeWrapper w)
            {
                if (expanded.Contains(w.TagNode.FullPath))
                    n.IsExpanded = true;
                if (selected.Contains(w.TagNode.FullPath))
                    TagsTree.SelectedNodes.Add(n);
            }
            if (n.Children.Count > 0)
                RestoreTagTreeState(n.Children, expanded, selected);
        }
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

    private void OnFlagsItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is TreeViewNode node)
        {
            if (sender.SelectedNodes.Contains(node))
                sender.SelectedNodes.Remove(node);
            else
                sender.SelectedNodes.Add(node);
            UpdateGridFromSelection();
        }
    }

    private void OnFlagsSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
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

        // Flagged working set
        bool flaggedSelected = FlagsTree.SelectedNodes.Any(n => n.Content is FlagNavigationViewModel);

        // If nothing selected anywhere, clear filters to show empty grid
        if (!photoLibraryRootSelected && !dateRootSelected && !tagRootSelected && !flaggedSelected &&
            selectedFolders.Count == 0 && selectedYears.Count == 0 && 
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
            selectedTags.Count > 0 ? selectedTags : null,
            flaggedSelected);
    }

    // ============================================================================
    //  Drag-and-drop: drop photos from the grid onto a tag node to apply that tag.
    // ============================================================================

    private void OnTagsTreeDragOver(object sender, DragEventArgs e)
    {
        var hasMarker = e.DataView.Properties.ContainsKey(Services.PhotoOperationsService.TagDropFormatId);
        if (!hasMarker)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        var tagNode = FindTagNodeAt(e);
        if (tagNode == null || tagNode.IsRoot)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = $"Apply tag '{tagNode.FullPath}'";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsContentVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
        e.Handled = true;
    }

    private async void OnTagsTreeDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Properties.ContainsKey(Services.PhotoOperationsService.TagDropFormatId)) return;

        var tagNode = FindTagNodeAt(e);
        DebugLog.WriteLine($"OnTagsTreeDrop: tagNode={tagNode?.FullPath ?? "null"}");
        if (tagNode == null || tagNode.IsRoot) return;

        e.Handled = true;
        var deferral = e.GetDeferral();
        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                DebugLog.WriteLine("OnTagsTreeDrop: no StorageItems in DataView");
                return;
            }
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.OfType<StorageFile>().Select(f => f.Path).ToList();
            DebugLog.WriteLine($"OnTagsTreeDrop: applying tag '{tagNode.FullPath}' to {paths.Count} path(s)");
            if (paths.Count == 0) return;

            if (App.ViewModel != null)
            {
                await App.ViewModel.ApplyTagToImagePathsAsync(tagNode.FullPath, paths);
            }
        }
        catch (Exception ex)
        {
            DebugLog.WriteLine($"OnTagsTreeDrop: ERROR {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private TagNode? FindTagNodeAt(DragEventArgs e)
    {
        // WinUI 3 TreeViewItems don't have AllowDrop=true by default, so the OS drag hit-test
        // stops at the TreeView itself and e.OriginalSource is always the TreeView. We work around
        // this by hit-testing from the cursor position (in xaml-root coords) limited to TagsTree's
        // subtree, which finds the actual TreeViewItem under the pointer regardless of AllowDrop.
        try
        {
            var elements = VisualTreeHelper.FindElementsInHostCoordinates(
                e.GetPosition(null),
                TagsTree);

            foreach (var el in elements)
            {
                if (el is TreeViewItem item)
                {
                    var node = TagsTree.NodeFromContainer(item);
                    if (node?.Content is TagNodeWrapper w1) return w1.TagNode;
                    if (item.DataContext is TreeViewNode tvn && tvn.Content is TagNodeWrapper w2) return w2.TagNode;
                    if (item.DataContext is TagNodeWrapper w3) return w3.TagNode;
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.WriteLine($"FindTagNodeAt: hit-test failed: {ex.Message}");
        }
        return null;
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
