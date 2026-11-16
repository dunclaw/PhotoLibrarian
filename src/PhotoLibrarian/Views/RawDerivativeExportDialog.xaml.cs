// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoLibrarian.Core.Services;

namespace PhotoLibrarian.Views;

public sealed partial class RawDerivativeExportDialog : ContentDialog
{
    public string OutputFolder => FolderBox.Text.Trim();
    public RawDerivativeFormat Format => FormatBox.SelectedIndex == 0
        ? RawDerivativeFormat.Png : RawDerivativeFormat.Jpeg;

    public RawDerivativeExportDialog(string folder)
    {
        InitializeComponent();
        FolderBox.Text = folder;
    }

    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(
                XamlRoot.ContentIslandEnvironment.AppWindowId);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) FolderBox.Text = folder.Path;
        }
        catch (Exception ex)
        {
            ErrorBar.Message = $"Could not choose a destination: {ex.Message}";
            ErrorBar.IsOpen = true;
        }
    }

    private void OnExportClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (System.IO.Path.IsPathFullyQualified(OutputFolder) && System.IO.Directory.Exists(OutputFolder)) return;
        args.Cancel = true;
        ErrorBar.Message = "Choose an existing destination folder.";
        ErrorBar.IsOpen = true;
    }
}
