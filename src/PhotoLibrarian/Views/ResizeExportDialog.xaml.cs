using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Views;

public sealed partial class ResizeExportDialog : ContentDialog
{
    public ImageExportOptions Options { get; private set; } = new();
    public string OutputFolder => FolderBox.Text.Trim();

    public ResizeExportDialog(int imageCount)
    {
        InitializeComponent();
        SelectionText.Text = $"Export {imageCount:N0} selected image(s) as resized copies.";
    }

    private void OnResizeModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SizeBox is null) return;
        bool percentage = ResizeModeBox.SelectedIndex == 1;
        SizeBox.Header = percentage ? "Percentage (1-100)" : "Maximum width or height (pixels)";
        SizeBox.Maximum = percentage ? 100 : 65535;
        SizeBox.Value = percentage ? 50 : 1600;
    }

    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(
                XamlRoot.ContentIslandEnvironment.AppWindowId);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
                FolderBox.Text = folder.Path;
        }
        catch (Exception ex)
        {
            ErrorBar.Message = $"Could not choose an output folder: {ex.Message}";
            ErrorBar.IsOpen = true;
        }
    }

    private void OnExportClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        try
        {
            if (!double.IsFinite(QualityBox.Value) || QualityBox.Value != Math.Truncate(QualityBox.Value))
                throw new ArgumentException("Enter a whole-number JPEG quality from 1 to 100.");
            Options = new ImageExportOptions
            {
                ResizeMode = ResizeModeBox.SelectedIndex == 1
                    ? ExportResizeMode.Percentage : ExportResizeMode.MaximumDimension,
                Size = SizeBox.Value,
                Quality = checked((int)QualityBox.Value),
                StripMetadata = StripMetadataBox.IsChecked == true
            };
            Options.Validate();
            if (!System.IO.Path.IsPathFullyQualified(OutputFolder) || !System.IO.Directory.Exists(OutputFolder))
                throw new ArgumentException("Choose an existing output folder.");
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            args.Cancel = true;
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
    }
}
