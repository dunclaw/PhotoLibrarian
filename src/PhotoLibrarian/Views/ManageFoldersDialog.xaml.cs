// Copyright (c) Duncan Lawler. All Rights Reserved.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoLibrarian.ViewModels;
using PhotoLibrarian.Core.Models;
using System.Linq;

namespace PhotoLibrarian.Views;

public sealed partial class ManageFoldersDialog : ContentDialog
{
    private FolderNavigationViewModel? ViewModel => App.ViewModel?.FolderNav;

    internal bool IndexMaintenanceRequested { get; private set; }

    public ManageFoldersDialog()
    {
        this.InitializeComponent();
        this.Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshFolderList();
    }

    private void RefreshFolderList()
    {
        if (ViewModel is null) return;
        FoldersList.ItemsSource = ViewModel.RootFolders.ToList();
    }

    private async void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.AddFolderCommand.CanExecute(null) == true)
        {
            await ViewModel.AddFolderCommand.ExecuteAsync(null);
            RefreshFolderList();
        }
    }

    private async void OnRemoveFolderItemClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;

        // Get the folder from the button's DataContext
        if (sender is Button button && button.DataContext is FolderNode folder)
        {
            await ViewModel.RemoveWatchedFolderAsync(folder);
            RefreshFolderList();
        }
    }

    private void OnIndexMaintenanceClick(object sender, RoutedEventArgs e)
    {
        IndexMaintenanceRequested = true;
        Hide();
    }
}
