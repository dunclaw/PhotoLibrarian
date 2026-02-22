using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoLibrarian.ViewModels;
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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.RootFolders.CollectionChanged += (s, args) => RefreshLibraryTree();
        RefreshLibraryTree();
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

    private static TreeViewNode BuildFolderNode(FolderNode folderNode, bool isRootFolder = false)
    {
        // Create display text with icon
        string icon = "📁";
        string displayName = folderNode.Name;
        string displayText = isRootFolder 
            ? $"{icon} {displayName}\n    {folderNode.Path}" 
            : $"{icon} {displayName}";

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

    private async void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.AddFolderCommand.CanExecute(null) == true)
            await ViewModel.AddFolderCommand.ExecuteAsync(null);
    }

    private async void OnRemoveFolderClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.RemoveFolderCommand.CanExecute(null) == true)
            await ViewModel.RemoveFolderCommand.ExecuteAsync(null);
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
        UpdateGridFromSelection();
    }

    private void OnTagsSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        UpdateGridFromSelection();
    }

    private void UpdateGridFromSelection()
    {
        if (ViewModel is null || App.ViewModel?.ImageGrid is null) return;

        // Collect all selected folder paths from all three trees
        var selectedPaths = new List<string>();

        // From Library tree
        foreach (var node in LibraryTree.SelectedNodes)
        {
            if (node.Content is FolderNodeWrapper wrapper && wrapper.FolderNode != null)
            {
                selectedPaths.Add(wrapper.FolderNode.Path);
            }
        }

        // TODO: Add Date and Tags tree selections when implemented

        // If nothing selected or "Photo Library" root selected, show all
        if (selectedPaths.Count == 0 || selectedPaths.Any(p => p.Length == 0))
        {
            App.ViewModel.ImageGrid.ClearFilterCommand.Execute(null);
            return;
        }

        // Filter by all selected paths (union)
        _ = App.ViewModel.ImageGrid.FilterByFoldersAsync(selectedPaths);
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
}
