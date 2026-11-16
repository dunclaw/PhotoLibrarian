// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PhotoLibrarian.ViewModels;

namespace PhotoLibrarian.Views;

public sealed partial class SettingsPanel
{
    private async void OnPreviewSidecarMigration(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.IsEnabled = false;
        try
        {
            var folder = await PickFolderAsync();
            if (folder is null) return;
            var panel = new SidecarMigrationPanel();
            var model = panel.ViewModel;
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Title = SidecarMigrationViewModel.Text("MigrationTitle"),
                Content = panel,
                PrimaryButtonText = SidecarMigrationViewModel.Text("MigrationConfirm"),
                CloseButtonText = SidecarMigrationViewModel.Text("MigrationClose"),
                DefaultButton = ContentDialogButton.Close,
                IsPrimaryButtonEnabled = false
            };
            AutomationProperties.SetAutomationId(dialog, "SidecarMigrationDialog");
            model.PropertyChanged += (_, _) => dialog.IsPrimaryButtonEnabled = model.CanApply;
            dialog.Opened += async (_, _) => await model.ScanAsync(folder.Path);
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                args.Cancel = true;
                if (model.CanApply)
                    await model.MigrateAsync();
            };
            dialog.CloseButtonClick += (_, args) =>
            {
                if (!model.IsBusy) return;
                args.Cancel = true;
                model.CancelCommand.Execute(null);
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
        {
            var error = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = SidecarMigrationViewModel.Text("MigrationTitle"),
                Content = ex.Message,
                CloseButtonText = SidecarMigrationViewModel.Text("MigrationClose")
            };
            await error.ShowAsync();
        }
        finally
        {
            button.IsEnabled = true;
        }
    }
}
