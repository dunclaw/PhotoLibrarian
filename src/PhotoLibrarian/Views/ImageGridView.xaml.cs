using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoLibrarian.ViewModels;
using System;
using System.Collections.Generic;

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

        System.Diagnostics.Debug.WriteLine($"[GRIDVIEW] OnLoaded - Images.Count={ViewModel.Images.Count}, GroupedImages.Count={ViewModel.GroupedImages.Count}");

        // Listen for viewport changes to request thumbnail loading
        PhotoGrid.VisibleItemsChanged += OnVisibleItemsChanged;
        
        // Single click → select item and show metadata
        PhotoGrid.ItemClicked += OnItemClicked;
        
        // Double click → open image viewer
        PhotoGrid.ItemDoubleClicked += OnItemDoubleClicked;

        // Selection changed (multi-select aware)
        PhotoGrid.SelectionChanged += OnGridSelectionChanged;
        
        // Listen for GroupedImages changes and update grid
        ViewModel.GroupedImages.CollectionChanged += (s, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[GRIDVIEW] GroupedImages changed: Action={e.Action}, NewItemsCount={e.NewItems?.Count ?? 0}");
            PhotoGrid.SetGroups(ViewModel.GroupedImages);
        };
        
        // Wire up custom virtualization control with initial (possibly empty) collection
        PhotoGrid.SetGroups(ViewModel.GroupedImages);
        
        // Hide empty state when images are loaded
        ViewModel.Images.CollectionChanged += (s, e) =>
        {
            var shouldShow = ViewModel.Images.Count == 0;
            System.Diagnostics.Debug.WriteLine($"[GRIDVIEW] Images changed, EmptyState visibility={shouldShow}");
            EmptyState.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
        };
        
        EmptyState.Visibility = ViewModel.Images.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
            
        // Initialize UI controls to match ViewModel defaults
        UpdateGroupByCombo();
        UpdateSortByCombo();
        UpdateSortOrderIcon();
    }
    
    private void OnVisibleItemsChanged(object? sender, List<ImageThumbnailViewModel> visibleItems)
    {
        // Request thumbnails for visible items
        System.Diagnostics.Debug.WriteLine($"[GRIDVIEW] OnVisibleItemsChanged: {visibleItems.Count} items");
        ViewModel?.OnViewportChangedGrouped(visibleItems);
    }
    
    private void OnItemClicked(object? sender, ImageThumbnailViewModel vm)
    {
        // SelectionChanged handler does the heavy lifting; nothing else to do for plain click.
    }
    
    private void OnItemDoubleClicked(object? sender, ImageThumbnailViewModel vm)
    {
        if (ViewModel is null) return;
        // Ensure selection state reflects the double-click target before opening the viewer
        ViewModel.UpdateSelection(new[] { vm }, vm);
        ViewModel.OpenViewerCommand.Execute(null);
    }

    private void OnGridSelectionChanged(object? sender, IReadOnlyList<ImageThumbnailViewModel> selected)
    {
        if (ViewModel is null) return;
        ViewModel.UpdateSelection(selected, PhotoGrid.PrimaryItem);
    }
    
    // Group By / Sort handlers
    private void OnGroupByChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null || GroupByCombo.SelectedItem is not ComboBoxItem item) return;
        
        if (Enum.TryParse<GroupByOption>(item.Tag?.ToString(), out var groupBy))
        {
            ViewModel.GroupBy = groupBy;
        }
    }
    
    private void OnSortByChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null || SortByCombo.SelectedItem is not ComboBoxItem item) return;
        
        if (Enum.TryParse<SortByOption>(item.Tag?.ToString(), out var sortBy))
        {
            ViewModel.SortBy = sortBy;
        }
    }
    
    private void OnToggleSortOrder(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        
        ViewModel.SortDescending = !ViewModel.SortDescending;
        UpdateSortOrderIcon();
    }
    
    private void UpdateGroupByCombo()
    {
        if (ViewModel is null) return;
        
        var index = ViewModel.GroupBy switch
        {
            GroupByOption.None => 0,
            GroupByOption.FileType => 1,
            GroupByOption.MediaType => 2,
            GroupByOption.YearTaken => 3,
            GroupByOption.MonthTaken => 4,
            GroupByOption.FileSize => 5,
            GroupByOption.ImageSize => 6,
            GroupByOption.Rating => 7,
            GroupByOption.Camera => 8,
            _ => 0
        };
        
        GroupByCombo.SelectedIndex = index;
    }
    
    private void UpdateSortByCombo()
    {
        if (ViewModel is null) return;
        
        var index = ViewModel.SortBy switch
        {
            SortByOption.FileName => 0,
            SortByOption.DateTaken => 1,
            SortByOption.DateModified => 2,
            SortByOption.FileSize => 3,
            SortByOption.Rating => 4,
            _ => 1
        };
        
        SortByCombo.SelectedIndex = index;
    }
    
    private void UpdateSortOrderIcon()
    {
        if (ViewModel is null) return;
        
        // &#xE014; = SortDown (descending), &#xE015; = SortUp (ascending)
        SortOrderIcon.Glyph = ViewModel.SortDescending ? "\uE014" : "\uE015";
        ToolTipService.SetToolTip(SortOrderBtn, 
            ViewModel.SortDescending ? "Descending" : "Ascending");
    }

    // Thumbnail size controls
    private void OnSizeSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (PhotoGrid is null) return;
        PhotoGrid.ItemSize = e.NewValue;
    }

    private void OnDecreaseSize(object sender, RoutedEventArgs e)
    {
        SizeSlider.Value = Math.Max(SizeSlider.Value - 40, 100);
    }

    private void OnIncreaseSize(object sender, RoutedEventArgs e)
    {
        SizeSlider.Value = Math.Min(SizeSlider.Value + 40, 400);
    }
}
