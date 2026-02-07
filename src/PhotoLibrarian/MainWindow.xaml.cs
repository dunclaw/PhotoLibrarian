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
            };
        }
    }

    private void UpdateViewerVisibility()
    {
        ViewerOverlay.Visibility = ViewModel.ImageViewer.IsOpen
            ? Visibility.Visible : Visibility.Collapsed;
    }
}
