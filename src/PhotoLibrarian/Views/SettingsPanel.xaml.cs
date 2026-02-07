using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoLibrarian.ViewModels;

namespace PhotoLibrarian.Views;

public sealed partial class SettingsPanel : UserControl
{
    private SettingsViewModel? ViewModel => App.ViewModel?.Settings;

    public SettingsPanel()
    {
        this.InitializeComponent();
        this.Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.PropertyChanged += (s, args) => DispatcherQueue.TryEnqueue(UpdateDisplay);
        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        if (ViewModel is null) return;
        CacheSizeText.Text = $"Cache size: {ViewModel.ThumbnailCacheSizeMB} MB";
        CachePathText.Text = ViewModel.CacheLocation;
    }

    private async void OnRebuildCache(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.RebuildCacheCommand.CanExecute(null) == true)
            await ViewModel.RebuildCacheCommand.ExecuteAsync(null);
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        ViewModel?.CloseCommand.Execute(null);
    }
}
