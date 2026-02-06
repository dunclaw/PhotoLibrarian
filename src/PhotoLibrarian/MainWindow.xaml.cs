using Microsoft.UI.Xaml;

namespace PhotoLibrarian;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        this.InitializeComponent();

        // Set initial window size
        var appWindow = this.AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(1600, 900));
        appWindow.Title = "PhotoLibrarian";
    }
}
