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
            var node = new TreeViewNode { Content = folder.Name, IsExpanded = true };
            foreach (var child in folder.Children)
            {
                var childNode = new TreeViewNode { Content = child.Name };
                node.Children.Add(childNode);
            }
            FolderTree.RootNodes.Add(node);
        }

        if (FolderTree.RootNodes.Count == 0)
        {
            FolderTree.RootNodes.Add(new TreeViewNode { Content = "No folders added" });
        }
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
        var content = node?.Content?.ToString();
        if (content is null || content == "No folders added") return;

        // Find the matching folder by name
        foreach (var root in ViewModel.RootFolders)
        {
            if (root.Name == content)
            {
                ViewModel.SelectedFolder = root;
                return;
            }
            foreach (var child in root.Children)
            {
                if (child.Name == content)
                {
                    ViewModel.SelectedFolder = child;
                    return;
                }
            }
        }
    }
}
