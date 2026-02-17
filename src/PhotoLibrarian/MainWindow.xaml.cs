using Microsoft.UI.Xaml;
using PhotoLibrarian.ViewModels;

namespace PhotoLibrarian;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel => App.ViewModel;

    public MainWindow()
    {
        this.InitializeComponent();

        var appWindow = this.AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(1600, 900));
        appWindow.Title = "PhotoLibrarian";

        // Bind status bar to ViewModel
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(ViewModel.StatusText))
                    StatusBarText.Text = ViewModel.StatusText;
                if (e.PropertyName == nameof(ViewModel.IsIndexing))
                    IndexingProgress.IsActive = ViewModel.IsIndexing;
                if (e.PropertyName is nameof(ViewModel.ImageViewer))
                    UpdateViewerVisibility();
                if (e.PropertyName is nameof(ViewModel.Settings))
                    UpdateSettingsVisibility();
            };
        }

        // Wire up settings close handler
        ViewModel.Settings.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.Settings.IsOpen))
                UpdateSettingsVisibility();
        };
    }

    private void UpdateViewerVisibility()
    {
        ViewerOverlay.Visibility = ViewModel.ImageViewer.IsOpen
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSettingsVisibility()
    {
        SettingsOverlay.Visibility = ViewModel.Settings.IsOpen
            ? Visibility.Visible : Visibility.Collapsed;
        
        if (ViewModel.Settings.IsOpen)
        {
            SettingsPanel.Loaded += (s, e) => ViewModel.Settings.OpenCommand.Execute(null);
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        ViewModel.Settings.OpenCommand.Execute(null);
    }
    
    private async void OnBenchmarkClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.RunBenchmarkCommand.ExecuteAsync(null);
    }
}
