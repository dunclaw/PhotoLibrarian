using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoLibrarian.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;

namespace PhotoLibrarian.Controls;

public sealed partial class VirtualizingPhotoGrid : UserControl
{
    // Configuration
    private const double ItemSpacing = 4;
    private const double HeaderHeight = 50;
    private const double BufferZone = 500; // Extra pixels to render above/below viewport
    private const int InitialPoolSize = 150; // Pre-create this many elements
    
    // Dynamic item size (default 180, controlled by slider)
    public static readonly DependencyProperty ItemSizeProperty =
        DependencyProperty.Register(nameof(ItemSize), typeof(double), typeof(VirtualizingPhotoGrid),
            new PropertyMetadata(180.0, OnItemSizeChanged));

    public double ItemSize
    {
        get => (double)GetValue(ItemSizeProperty);
        set => SetValue(ItemSizeProperty, value);
    }

    private static void OnItemSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is VirtualizingPhotoGrid grid)
        {
            grid.OnItemSizeUpdated();
        }
    }
    
    // Element pools (separate pools for different element types)
    private readonly Queue<FrameworkElement> _availablePhotoElements = new();
    private readonly Queue<FrameworkElement> _availableHeaderElements = new();
    private readonly Dictionary<object, FrameworkElement> _activeElements = new(); // dataContext -> element
    
    // Layout state
    private double _totalContentHeight = 0;
    private int _columnCount = 1;
    private bool _isUpdating = false;
    
    // Selection state
    private ImageThumbnailViewModel? _selectedItem;
    private FrameworkElement? _selectedElement;
    private static readonly Windows.UI.Color SelectionColor = Windows.UI.Color.FromArgb(255, 100, 149, 237); // CornflowerBlue
    
    // Data source
    private System.Collections.ObjectModel.ObservableCollection<PhotoGroup>? _groups;
    
    // Events
    public event EventHandler<List<ImageThumbnailViewModel>>? VisibleItemsChanged;
    public event EventHandler<ImageThumbnailViewModel>? ItemClicked;
    public event EventHandler<ImageThumbnailViewModel>? ItemDoubleClicked;
    
    public VirtualizingPhotoGrid()
    {
        this.InitializeComponent();
        this.Loaded += OnLoaded;
        this.SizeChanged += OnSizeChanged;
    }
    
    /// <summary>
    /// Sets the data source (grouped photos)
    /// </summary>
    public void SetGroups(System.Collections.ObjectModel.ObservableCollection<PhotoGroup> groups)
    {
        Debug.WriteLine($"[VIRTUAL] SetGroups called with {groups?.Count ?? 0} groups");
        
        // Unsubscribe from old collection
        if (_groups != null)
        {
            _groups.CollectionChanged -= OnGroupsCollectionChanged;
        }
        
        _groups = groups;
        
        // Subscribe to new collection
        if (_groups != null)
        {
            _groups.CollectionChanged += OnGroupsCollectionChanged;
            
            int totalItems = 0;
            foreach (var g in _groups)
            {
                totalItems += g.Items?.Count ?? 0;
            }
            Debug.WriteLine($"[VIRTUAL] Total items across all groups: {totalItems}");
        }
        
        // Initial render
        RecalculateLayout();
    }
    
    private void OnGroupsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Debug.WriteLine($"[VIRTUAL] OnGroupsCollectionChanged: {e.Action}");
        // Data changed - recalculate layout
        RecalculateLayout();
    }
    
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Pre-create pools of elements
        for (int i = 0; i < InitialPoolSize; i++)
        {
            _availablePhotoElements.Enqueue(CreatePhotoElement());
        }
        
        // Pre-create fewer header elements (typically much fewer groups than items)
        for (int i = 0; i < 20; i++)
        {
            _availableHeaderElements.Enqueue(CreateHeaderElement());
        }
        
        Debug.WriteLine($"[VIRTUAL] Created pool of {InitialPoolSize} photo elements and 20 header elements");
    }
    
    /// <summary>
    /// Called when ItemSize dependency property changes - resize all elements and recalculate layout.
    /// </summary>
    private void OnItemSizeUpdated()
    {
        double size = ItemSize;
        
        // Resize all active photo elements
        foreach (var kvp in _activeElements)
        {
            if (kvp.Value is Grid grid && kvp.Key is ImageThumbnailViewModel)
            {
                grid.Width = size;
                grid.Height = size;
            }
        }
        
        // Resize pooled photo elements
        foreach (var element in _availablePhotoElements)
        {
            if (element is Grid grid)
            {
                grid.Width = size;
                grid.Height = size;
            }
        }
        
        // Recalculate layout
        RecalculateLayout();
    }
    
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Width changed - recalculate columns
        RecalculateLayout();
    }
    
    private void OnScrollViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        // Update viewport on every scroll change (both during and after scrolling)
        UpdateViewport();
    }
    
    /// <summary>
    /// Recalculates layout and updates viewport
    /// </summary>
    private void RecalculateLayout()
    {
        if (_isUpdating) return;
        
        _isUpdating = true;
        
        try
        {
            // Handle empty collection - clear everything
            if (_groups == null || _groups.Count == 0)
            {
                Debug.WriteLine($"[VIRTUAL] Groups empty, clearing all elements");
                
                // Release all active elements
                var allActive = _activeElements.ToList();
                foreach (var kvp in allActive)
                {
                    ReleaseElement(kvp.Key, kvp.Value);
                }
                
                ContentPlaceholder.Height = 0;
                _totalContentHeight = 0;
                return;
            }
            
            // Calculate columns based on canvas width and current item size
            double canvasWidth = ScrollContainer.ActualWidth;
            if (canvasWidth <= 0) return;
            
            double cellSize = ItemSize + ItemSpacing;
            _columnCount = Math.Max(1, (int)((canvasWidth - ItemSpacing) / cellSize));
            
            // Calculate total height by iterating groups
            _totalContentHeight = CalculateTotalHeight();
            
            // Update placeholder height
            ContentPlaceholder.Height = _totalContentHeight;
            
            // Update visible elements
            UpdateViewport();
            
            Debug.WriteLine($"[VIRTUAL] Layout updated: {_columnCount} columns, {_totalContentHeight:F0}px tall, ItemSize={ItemSize}");
        }
        finally
        {
            _isUpdating = false;
        }
    }
    
    /// <summary>
    /// Calculates total content height by summing all groups
    /// </summary>
    private double CalculateTotalHeight()
    {
        if (_groups == null) return 0;
        
        double totalHeight = 0;
        double cellSize = ItemSize + ItemSpacing;
        
        foreach (var group in _groups)
        {
            // Header
            totalHeight += HeaderHeight;
            
            // Items in rows
            int itemCount = group.Items?.Count ?? 0;
            int rows = (int)Math.Ceiling((double)itemCount / _columnCount);
            totalHeight += rows * cellSize;
        }
        
        return totalHeight;
    }
    
    /// <summary>
    /// Updates viewport - releases non-visible elements, acquires and positions visible ones
    /// </summary>
    private void UpdateViewport()
    {
        if (_groups == null || _groups.Count == 0) return;
        
        double scrollOffset = ScrollContainer.VerticalOffset;
        double viewportHeight = ScrollContainer.ViewportHeight;
        double viewportStart = scrollOffset - BufferZone;
        double viewportEnd = scrollOffset + viewportHeight + BufferZone;
        
        Debug.WriteLine($"[VIRTUAL] Viewport: {viewportStart:F0} - {viewportEnd:F0}");
        
        // Calculate which items are visible
        var visibleItems = CalculateVisibleItems(viewportStart, viewportEnd);
        
        // Release elements no longer visible
        var toRelease = _activeElements.Where(kvp => !visibleItems.Any(vi => vi.dataContext == kvp.Key)).ToList();
        foreach (var kvp in toRelease)
        {
            ReleaseElement(kvp.Key, kvp.Value);
        }
        
        // Acquire and position visible elements
        foreach (var item in visibleItems)
        {
            PositionItem(item);
        }
        
        Debug.WriteLine($"[VIRTUAL] Rendered {_activeElements.Count} elements ({toRelease.Count} released, {visibleItems.Count} visible)");
        
        // Notify listeners of visible photo items (exclude headers)
        var photoItems = visibleItems
            .Where(vi => !vi.isHeader && vi.dataContext is ImageThumbnailViewModel)
            .Select(vi => (ImageThumbnailViewModel)vi.dataContext)
            .ToList();
        
        Debug.WriteLine($"[VIRTUAL] Notifying {photoItems.Count} visible photos for thumbnail loading");
        VisibleItemsChanged?.Invoke(this, photoItems);
    }
    
    /// <summary>
    /// Calculates which items are visible in the viewport
    /// Returns list of (dataContext, x, y, isHeader)
    /// </summary>
    private List<(object dataContext, double x, double y, bool isHeader)> CalculateVisibleItems(double viewportStart, double viewportEnd)
    {
        var visible = new List<(object, double, double, bool)>();
        if (_groups == null) return visible;
        
        double currentY = 0;
        double cellSize = ItemSize + ItemSpacing;
        
        foreach (var group in _groups)
        {
            // Check if group header is visible
            double headerY = currentY;
            if (headerY >= viewportStart && headerY <= viewportEnd)
            {
                visible.Add((group, 0, headerY, true)); // Header
            }
            currentY += HeaderHeight;
            
            // Check if group is in viewport
            int itemCount = group.Items?.Count ?? 0;
            int rows = (int)Math.Ceiling((double)itemCount / _columnCount);
            double groupEndY = currentY + (rows * cellSize);
            
            if (groupEndY < viewportStart || currentY > viewportEnd)
            {
                // Group not visible
                currentY = groupEndY;
                continue;
            }
            
            // Group is visible - calculate visible items
            int firstVisibleRow = Math.Max(0, (int)((viewportStart - currentY) / cellSize));
            int lastVisibleRow = Math.Min(rows - 1, (int)((viewportEnd - currentY) / cellSize) + 1);
            
            for (int row = firstVisibleRow; row <= lastVisibleRow; row++)
            {
                for (int col = 0; col < _columnCount; col++)
                {
                    int itemIndex = row * _columnCount + col;
                    if (itemIndex >= itemCount) break;
                    
                    var item = group.Items[itemIndex];
                    double x = col * cellSize;
                    double y = currentY + (row * cellSize);
                    
                    visible.Add((item, x, y, false)); // Photo item
                }
            }
            
            currentY = groupEndY;
        }
        
        return visible;
    }
    
    /// <summary>
    /// Positions an item at the specified coordinates
    /// </summary>
    private void PositionItem((object dataContext, double x, double y, bool isHeader) item)
    {
        double size = ItemSize;
        
        // Get or create element
        if (!_activeElements.TryGetValue(item.dataContext, out var element))
        {
            // Acquire from appropriate pool or create new
            if (item.isHeader)
            {
                if (_availableHeaderElements.Count > 0)
                {
                    element = _availableHeaderElements.Dequeue();
                }
                else
                {
                    element = CreateHeaderElement();
                    Debug.WriteLine($"[VIRTUAL] Header pool exhausted, created new header");
                }
            }
            else
            {
                if (_availablePhotoElements.Count > 0)
                {
                    element = _availablePhotoElements.Dequeue();
                }
                else
                {
                    element = CreatePhotoElement();
                    Debug.WriteLine($"[VIRTUAL] Photo pool exhausted, created new photo element");
                }
                
                // Size the photo element to current ItemSize
                element.Width = size;
                element.Height = size;
                
                // Apply selection visual if this is the selected item
                ApplySelectionVisual(element, item.dataContext as ImageThumbnailViewModel == _selectedItem && _selectedItem != null);
            }
            
            element.DataContext = item.dataContext;
            _activeElements[item.dataContext] = element;
            ItemsCanvas.Children.Add(element);
        }
        
        // Set width for headers to span full canvas width
        if (item.isHeader)
        {
            element.Width = ScrollContainer.ActualWidth;
        }
        
        // Position element
        Canvas.SetLeft(element, item.x);
        Canvas.SetTop(element, item.y);
    }
    
    /// <summary>
    /// Releases an element back to the pool
    /// </summary>
    private void ReleaseElement(object dataContext, FrameworkElement element)
    {
        _activeElements.Remove(dataContext);
        ItemsCanvas.Children.Remove(element);
        
        // Clear selection visual before returning to pool
        if (element is Grid grid && dataContext is ImageThumbnailViewModel)
        {
            ApplySelectionVisual(element, false);
        }
        
        // Track if this was the selected element
        if (dataContext == _selectedItem)
        {
            _selectedElement = null;
        }
        
        element.DataContext = null;
        
        // Return to appropriate pool based on element type
        if (element is Border) // Headers are Borders
        {
            _availableHeaderElements.Enqueue(element);
        }
        else // Photos are Grids
        {
            _availablePhotoElements.Enqueue(element);
        }
    }
    
    /// <summary>
    /// Creates a photo item element with click and double-click support
    /// </summary>
    private FrameworkElement CreatePhotoElement()
    {
        double size = ItemSize;
        
        var grid = new Grid
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Microsoft.UI.Colors.DarkGray),
            BorderThickness = new Thickness(3),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };
        
        var image = new Image
        {
            Stretch = Stretch.Uniform
        };
        image.SetBinding(Image.SourceProperty, new Microsoft.UI.Xaml.Data.Binding
        {
            Path = new PropertyPath("Thumbnail"),
            Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay
        });
        grid.Children.Add(image);
        
        var loader = new ProgressRing
        {
            Width = 24,
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        loader.SetBinding(ProgressRing.IsActiveProperty, new Microsoft.UI.Xaml.Data.Binding
        {
            Path = new PropertyPath("IsLoading"),
            Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay
        });
        grid.Children.Add(loader);
        
        // File name overlay
        var overlay = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            CornerRadius = new CornerRadius(0, 0, 4, 4),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(153, 0, 0, 0)), // #99000000
            Padding = new Thickness(6, 3, 6, 3)
        };
        
        var fileName = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        };
        fileName.SetBinding(TextBlock.TextProperty, new Microsoft.UI.Xaml.Data.Binding
        {
            Path = new PropertyPath("FileName"),
            Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay
        });
        
        overlay.Child = fileName;
        grid.Children.Add(overlay);
        
        // Click and double-click handling
        grid.Tapped += OnPhotoTapped;
        grid.DoubleTapped += OnPhotoDoubleTapped;
        grid.PointerEntered += OnPhotoPointerEntered;
        grid.PointerExited += OnPhotoPointerExited;
        
        return grid;
    }
    
    private void OnPhotoTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is ImageThumbnailViewModel vm)
        {
            SelectItem(vm, element);
            ItemClicked?.Invoke(this, vm);
        }
    }
    
    private void OnPhotoDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is ImageThumbnailViewModel vm)
        {
            SelectItem(vm, element);
            ItemDoubleClicked?.Invoke(this, vm);
        }
    }
    
    private void OnPhotoPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid && grid.DataContext is ImageThumbnailViewModel vm && vm != _selectedItem)
        {
            grid.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.LightGray);
        }
    }
    
    private void OnPhotoPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid && grid.DataContext is ImageThumbnailViewModel vm && vm != _selectedItem)
        {
            grid.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }
    
    /// <summary>
    /// Selects an item and updates visual state
    /// </summary>
    private void SelectItem(ImageThumbnailViewModel? vm, FrameworkElement? element)
    {
        // Deselect previous
        if (_selectedElement != null)
        {
            ApplySelectionVisual(_selectedElement, false);
        }
        
        _selectedItem = vm;
        _selectedElement = element;
        
        // Select new
        if (_selectedElement != null && vm != null)
        {
            ApplySelectionVisual(_selectedElement, true);
        }
    }
    
    private static void ApplySelectionVisual(FrameworkElement element, bool selected)
    {
        if (element is Grid grid)
        {
            grid.BorderBrush = new SolidColorBrush(selected ? SelectionColor : Microsoft.UI.Colors.Transparent);
        }
    }
    
    /// <summary>
    /// Creates a group header element
    /// </summary>
    private FrameworkElement CreateHeaderElement()
    {
        var border = new Border
        {
            Height = HeaderHeight,
            Padding = new Thickness(12, 8, 12, 8),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            CornerRadius = new CornerRadius(4)
        };
        
        var text = new TextBlock
        {
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        text.SetBinding(TextBlock.TextProperty, new Microsoft.UI.Xaml.Data.Binding
        {
            Path = new PropertyPath("Header"),
            Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay
        });
        
        border.Child = text;
        return border;
    }
}
