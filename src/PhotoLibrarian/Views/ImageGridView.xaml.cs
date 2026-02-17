using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoLibrarian.ViewModels;

namespace PhotoLibrarian.Views;

public sealed partial class ImageGridView : UserControl
{
    private ImageGridViewModel? ViewModel => App.ViewModel?.ImageGrid;

    public ImageGridView()
    {
        this.InitializeComponent();
        this.Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;

        ImageRepeater.ItemsSource = ViewModel.Images;
        ViewModel.Images.CollectionChanged += OnImagesCollectionChanged;
        EmptyState.Visibility = ViewModel.Images.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
            
        // Wire up scroll viewer for viewport detection
        GridScrollViewer.ViewChanged += OnScrollViewChanged;
    }
    
    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (ViewModel is null || GridLayout is null) return;
        if (ViewModel.Images.Count == 0) return;
        
        // Calculate which items are visible based on scroll position
        var scrollOffset = GridScrollViewer.VerticalOffset;
        var viewportHeight = GridScrollViewer.ViewportHeight;
        
        // Get layout info
        var itemHeight = GridLayout.MinItemHeight + GridLayout.MinRowSpacing;
        var itemWidth = GridLayout.MinItemWidth + GridLayout.MinColumnSpacing;
        var availableWidth = GridScrollViewer.ActualWidth;
        var columnsPerRow = Math.Max(1, (int)(availableWidth / itemWidth));
        
        // Calculate visible row range
        var firstVisibleRow = (int)(scrollOffset / itemHeight);
        var lastVisibleRow = (int)((scrollOffset + viewportHeight) / itemHeight) + 1; // +1 for partial row
        
        // Calculate visible item indices
        var firstVisibleIndex = firstVisibleRow * columnsPerRow;
        var lastVisibleIndex = Math.Min((lastVisibleRow * columnsPerRow) - 1, ViewModel.Images.Count - 1);
        
        // Notify ViewModel to reorder queue
        ViewModel.OnViewportChanged(firstVisibleIndex, lastVisibleIndex);
    }

    private void OnImagesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
    {
        if (ViewModel is null) return;
        
        EmptyState.Visibility = ViewModel.Images.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
            
        // Trigger initial viewport detection when images first populate
        if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset ||
            (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && args.NewStartingIndex == 0))
        {
            // Delay slightly to let layout happen
            _ = Task.Run(async () =>
            {
                await Task.Delay(50);
                App.MainWindow?.DispatcherQueue.TryEnqueue(() => OnScrollViewChanged(null, null!));
            });
        }
    }

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not Grid grid) return;
        if (grid.DataContext is not ImageThumbnailViewModel vm) return;

        // Update properties not bound in XAML
        var fileName = FindChild<TextBlock>(grid, "FileNameText");
        var videoIcon = FindChild<FontIcon>(grid, "VideoIcon");

        if (fileName is not null) fileName.Text = vm.FileName;
        if (videoIcon is not null)
            videoIcon.Visibility = vm.IsVideo ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnThumbnailTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is Grid grid && grid.DataContext is ImageThumbnailViewModel vm && ViewModel is not null)
        {
            ViewModel.SelectedImage = vm;
        }
    }

    private void OnThumbnailDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is Grid grid && grid.DataContext is ImageThumbnailViewModel vm && ViewModel is not null)
        {
            ViewModel.SelectedImage = vm;
            ViewModel.OpenViewerCommand.Execute(null);
        }
    }

    private void OnThumbnailPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid)
            grid.Opacity = 0.85;
    }

    private void OnThumbnailPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid)
            grid.Opacity = 1.0;
    }

    private async void OnSortDate(object sender, RoutedEventArgs e) =>
        await (ViewModel?.SortByDateCommand.ExecuteAsync(null) ?? Task.CompletedTask);

    private async void OnSortName(object sender, RoutedEventArgs e) =>
        await (ViewModel?.SortByNameCommand.ExecuteAsync(null) ?? Task.CompletedTask);

    private async void OnSortRating(object sender, RoutedEventArgs e) =>
        await (ViewModel?.SortByRatingCommand.ExecuteAsync(null) ?? Task.CompletedTask);

    private void OnSizeSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (GridLayout is not null)
        {
            GridLayout.MinItemWidth = e.NewValue;
            GridLayout.MinItemHeight = e.NewValue;
        }
    }

    private void OnDecreaseSize(object sender, RoutedEventArgs e)
    {
        SizeSlider.Value = Math.Max(SizeSlider.Value - 40, 100);
    }

    private void OnIncreaseSize(object sender, RoutedEventArgs e)
    {
        SizeSlider.Value = Math.Min(SizeSlider.Value + 40, 400);
    }

    private static T? FindChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typed && typed.Name == name)
                return typed;
            var found = FindChild<T>(child, name);
            if (found is not null)
                return found;
        }
        return null;
    }
}
