using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoLibrarian.ViewModels;

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
        ViewModel.RootFolders.CollectionChanged += (s, args) => RefreshTree();
        RefreshTree();
    }

    private void RefreshTree()
    {
        FolderTree.RootNodes.Clear();
        if (ViewModel is null) return;

        foreach (var folder in ViewModel.RootFolders)
        {
            var node = BuildTreeNode(folder, isExpanded: true);
            FolderTree.RootNodes.Add(node);
        }

        if (FolderTree.RootNodes.Count == 0)
        {
            FolderTree.RootNodes.Add(new TreeViewNode { Content = "No folders added" });
        }
    }

    private static TreeViewNode BuildTreeNode(FolderNode folderNode, bool isExpanded = false)
    {
        // Check if this node has a placeholder child (lazy-load marker)
        bool hasPlaceholder = folderNode.Children.Count == 1 && folderNode.Children[0].Path == "";

        if (isExpanded && hasPlaceholder)
        {
            // Eagerly resolve placeholder children for expanded nodes
            folderNode.Children.Clear();
            FolderNavigationViewModel.BuildChildNodes(folderNode);
            hasPlaceholder = false;
        }

        var treeNode = new TreeViewNode { Content = folderNode, IsExpanded = isExpanded };

        if (hasPlaceholder)
        {
            // Don't recurse into the placeholder — just mark the node as having children
            // so the TreeView shows an expand chevron
            treeNode.HasUnrealizedChildren = true;
        }
        else
        {
            foreach (var child in folderNode.Children)
            {
                treeNode.Children.Add(BuildTreeNode(child));
            }
        }

        return treeNode;
    }

    private async void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.AddFolderCommand.CanExecute(null) == true)
            await ViewModel.AddFolderCommand.ExecuteAsync(null);
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.RefreshCommand.CanExecute(null) == true)
            await ViewModel.RefreshCommand.ExecuteAsync(null);
    }

    private async void OnRemoveFolderClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.RemoveFolderCommand.CanExecute(null) == true)
            await ViewModel.RemoveFolderCommand.ExecuteAsync(null);
    }

    private void OnShowAllClick(object sender, RoutedEventArgs e)
    {
        App.ViewModel?.ImageGrid.ClearFilterCommand.Execute(null);
    }

    private void OnFolderInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (ViewModel is null) return;
        // InvokedItem is the TreeViewNode, get the Content from it
        if (args.InvokedItem is not TreeViewNode node)
        {
            PhotoLibrarian.Diagnostics.DebugLog.WriteLine($"OnFolderInvoked: InvokedItem is not TreeViewNode, type={args.InvokedItem?.GetType().Name}");
            return;
        }
        if (node.Content is not FolderNode folderNode)
        {
            PhotoLibrarian.Diagnostics.DebugLog.WriteLine($"OnFolderInvoked: Node.Content is not FolderNode, type={node.Content?.GetType().Name}");
            return;
        }
        if (folderNode.Path.Length == 0) return;

        PhotoLibrarian.Diagnostics.DebugLog.WriteLine($"OnFolderInvoked: Setting SelectedFolder to {folderNode.Path}");
        ViewModel.SelectedFolder = folderNode;
    }

    private void OnFolderExpanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (ViewModel is null) return;
        if (args.Node.Content is not FolderNode folderNode) return;

        // Lazy-load: if the FolderNode has a placeholder child, resolve it
        if (folderNode.Children.Count == 1 && folderNode.Children[0].Path == "")
        {
            folderNode.Children.Clear();
            FolderNavigationViewModel.BuildChildNodes(folderNode);

            // Rebuild TreeViewNode children to match
            args.Node.HasUnrealizedChildren = false;
            args.Node.Children.Clear();
            foreach (var child in folderNode.Children)
            {
                args.Node.Children.Add(BuildTreeNode(child));
            }
        }
    }
}
