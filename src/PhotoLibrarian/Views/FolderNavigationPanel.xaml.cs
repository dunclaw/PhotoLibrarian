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
        // If this node will start expanded, eagerly resolve placeholder children
        if (isExpanded && folderNode.Children.Count == 1 && folderNode.Children[0].Path == "")
        {
            folderNode.Children.Clear();
            FolderNavigationViewModel.BuildChildNodes(folderNode);
        }

        var treeNode = new TreeViewNode { Content = folderNode, IsExpanded = isExpanded };
        foreach (var child in folderNode.Children)
        {
            treeNode.Children.Add(BuildTreeNode(child));
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

    private void OnShowAllClick(object sender, RoutedEventArgs e)
    {
        App.ViewModel?.ImageGrid.ClearFilterCommand.Execute(null);
    }

    private void OnFolderInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (ViewModel is null) return;
        var node = args.InvokedItem as TreeViewNode;
        if (node?.Content is not FolderNode folderNode) return;
        if (folderNode.Path.Length == 0) return;

        ViewModel.SelectedFolder = folderNode;
    }

    private void OnFolderExpanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (ViewModel is null) return;
        if (args.Node.Content is not FolderNode folderNode) return;

        // Lazy-load: if the FolderNode has a single placeholder child, expand it
        if (folderNode.Children.Count == 1 && folderNode.Children[0].Path == "")
        {
            folderNode.Children.Clear();
            FolderNavigationViewModel.BuildChildNodes(folderNode);

            // Rebuild TreeViewNode children to match
            args.Node.Children.Clear();
            foreach (var child in folderNode.Children)
            {
                args.Node.Children.Add(BuildTreeNode(child));
            }
        }
    }
}
