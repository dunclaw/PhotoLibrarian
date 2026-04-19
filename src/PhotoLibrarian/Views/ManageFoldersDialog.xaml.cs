using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoLibrarian.ViewModels;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Data;
using System.Linq;
using System.IO;

namespace PhotoLibrarian.Views;

public sealed partial class ManageFoldersDialog : ContentDialog
{
    private FolderNavigationViewModel? ViewModel => App.ViewModel?.FolderNav;

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
            ViewModel.SelectedFolder = folder;
            
            if (ViewModel.RemoveFolderCommand.CanExecute(null))
            {
                await ViewModel.RemoveFolderCommand.ExecuteAsync(null);
                RefreshFolderList();
            }
        }
    }

    private async void OnClearDatabaseClick(object sender, RoutedEventArgs e)
    {
        // Confirm with user
        var confirmDialog = new ContentDialog
        {
            Title = "Clear Database?",
            Content = "This will delete all cached metadata and thumbnails. Your original photos are safe. The library will be re-indexed automatically.\n\nThis may take several minutes depending on library size.\n\nContinue?",
            PrimaryButtonText = "Clear and Re-Index",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        var result = await confirmDialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
            return;

        try
        {
            ClearDatabaseBtn.IsEnabled = false;

            // Get database path
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dataDir = Path.Combine(appData, "PhotoLibrarian");
            var dbPath = Path.Combine(dataDir, "cache.db");

            // Close and delete database files
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
            if (File.Exists(dbPath + "-shm"))
            {
                File.Delete(dbPath + "-shm");
            }
            if (File.Exists(dbPath + "-wal"))
            {
                File.Delete(dbPath + "-wal");
            }

            // Show success and close dialog
            var successDialog = new ContentDialog
            {
                Title = "Database Cleared",
                Content = "Database has been cleared. Please restart the application to re-index your library.",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await successDialog.ShowAsync();

            // Close this dialog
            Hide();

            // Request app restart
            Microsoft.Windows.AppLifecycle.AppInstance.Restart("Database cleared");
        }
        catch (Exception ex)
        {
            var errorDialog = new ContentDialog
            {
                Title = "Error",
                Content = $"Failed to clear database: {ex.Message}\n\nYou may need to close the application and manually delete the database file at:\n%LOCALAPPDATA%\\PhotoLibrarian\\cache.db",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await errorDialog.ShowAsync();
        }
        finally
        {
            ClearDatabaseBtn.IsEnabled = true;
        }
    }
}
