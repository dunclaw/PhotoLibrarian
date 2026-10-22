using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoLibrarian.Core.Services;
using System.IO;

namespace PhotoLibrarian.Views;

public sealed partial class VideoStabilizationDialog : ContentDialog
{
    private readonly string _sourcePath;
    private CancellationTokenSource? _cancellation;
    public string? ExportedPath { get; private set; }

    public VideoStabilizationDialog(string sourcePath)
    {
        InitializeComponent();
        _sourcePath = sourcePath;
        OutputBox.Text = Path.Combine(Path.GetDirectoryName(sourcePath)!,
            Path.GetFileNameWithoutExtension(sourcePath) + "-stabilized.mp4");
    }

    private async void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(
                XamlRoot.ContentIslandEnvironment.AppWindowId);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
                OutputBox.Text = Path.Combine(folder.Path,
                    Path.GetFileNameWithoutExtension(_sourcePath) + "-stabilized.mp4");
        }
        catch (Exception ex) { ShowError($"Could not choose an output folder: {ex.Message}"); }
    }

    private async void OnExportClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        OptionsPanel.IsEnabled = false;
        IsPrimaryButtonEnabled = false;
        CloseButtonText = "Cancel";
        ExportProgress.Visibility = Visibility.Visible;
        ExportProgress.Value = 0;
        ResultBar.IsOpen = false;
        StatusText.Text = "Stabilizing video...";
        try
        {
            string destination = OutputBox.Text.Trim();
            await new VideoStabilizationService().StabilizeAsync(
                _sourcePath, destination,
                new Progress<double>(percent =>
                {
                    if (_cancellation != cancellation || cancellation.IsCancellationRequested) return;
                    ExportProgress.Value = percent;
                    StatusText.Text = $"Stabilizing video: {percent:F0}%";
                }), cancellation.Token);
            ExportedPath = destination;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Export canceled. No copy was saved.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "";
            ShowError(ex.Message);
        }
        finally
        {
            _cancellation = null;
            OptionsPanel.IsEnabled = true;
            IsPrimaryButtonEnabled = true;
            CloseButtonText = "Close";
            ExportProgress.Visibility = Visibility.Collapsed;
        }
        if (ExportedPath is not null) Hide();
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (_cancellation is null) return;
        args.Cancel = true;
        StatusText.Text = "Canceling export...";
        _cancellation.Cancel();
    }

    private void ShowError(string message)
    {
        ResultBar.Severity = InfoBarSeverity.Error;
        ResultBar.Message = message;
        ResultBar.IsOpen = true;
    }
}
