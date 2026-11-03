// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;
using PhotoLibrarian.ViewModels;
using System.Collections.Specialized;
using System.ComponentModel;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace PhotoLibrarian.Controls;

public sealed partial class VirtualizingPhotoGrid
{
    private readonly Dictionary<ImageThumbnailViewModel, PhotoItemPeer> _photoPeers = new();
    private readonly HashSet<INotifyCollectionChanged> _observedPhotoCollections = new();
    private readonly HashSet<PhotoGroup> _observedPhotoGroups = new();
    private readonly AccessibilitySettings _accessibilitySettings = new();
    private readonly UISettings _uiSettings = new();
    private ImageThumbnailViewModel? _focusedItem;
    private ImageThumbnailViewModel? _pendingPhotoContextMenu;
    private int _photoDataVersion;
    private bool _accessibilityLoaded;
    private bool _photoFocusEventPending;

    protected override AutomationPeer OnCreateAutomationPeer() => new PhotoGridPeer(this);

    private void ObservePhotoGroups()
    {
        foreach (var collection in _observedPhotoCollections)
            collection.CollectionChanged -= OnPhotoItemsChanged;
        _observedPhotoCollections.Clear();
        foreach (var group in _observedPhotoGroups)
            group.PropertyChanged -= OnPhotoGroupChanged;
        _observedPhotoGroups.Clear();
        if (_groups is null) return;
        foreach (var group in _groups)
        {
            if (_observedPhotoGroups.Add(group))
                group.PropertyChanged += OnPhotoGroupChanged;
            if (_observedPhotoCollections.Add(group.Items))
                group.Items.CollectionChanged += OnPhotoItemsChanged;
        }
    }

    private void OnPhotoGroupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PhotoGroup.Items)) return;
        ObservePhotoGroups();
        OnPhotoDataChanged();
        RecalculateLayout();
    }

    private void OnPhotoItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPhotoDataChanged();
        RecalculateLayout();
    }

    private void OnPhotoDataChanged()
    {
        ResetPhotoAutomation();
        var current = GetFlatItems().ToHashSet();
        _selectedItems.IntersectWith(current);
        if (_primaryItem is not null && !current.Contains(_primaryItem))
            _primaryItem = _selectedItems.FirstOrDefault();
        if (_anchorItem is not null && !current.Contains(_anchorItem))
            _anchorItem = null;
        if (_focusedItem is not null && !current.Contains(_focusedItem))
            _focusedItem = null;
        RefreshAllSelectionVisuals();
        SelectionChanged?.Invoke(this, _selectedItems.ToList());
    }

    private void ResetPhotoAutomation()
    {
        _photoDataVersion++;
        _photoPeers.Clear();
        _pendingPhotoContextMenu = null;
        _photoFocusEventPending = _focusedItem is not null;
        (FrameworkElementAutomationPeer.FromElement(this) as PhotoGridPeer)?.InvalidatePeer();
    }

    private PhotoItemPeer GetPhotoPeer(ImageThumbnailViewModel item)
    {
        if (_photoPeers.TryGetValue(item, out var peer)) return peer;
        peer = new PhotoItemPeer(this, item, _photoDataVersion);
        // Retain realized items and the single logical focus. GetSelection may return transient peers
        // for selected offscreen photos, without creating any visuals or thumbnail work.
        if (_activeElements.ContainsKey(item) || ReferenceEquals(item, _focusedItem))
            _photoPeers.Add(item, peer);
        return peer;
    }

    private void RemovePhotoAutomation(object item)
    {
        if (item is ImageThumbnailViewModel photo && !ReferenceEquals(photo, _focusedItem))
            _photoPeers.Remove(photo);
    }

    private void UpdatePhotoAutomation()
    {
        if (FrameworkElementAutomationPeer.FromElement(this) is PhotoGridPeer peer)
            peer.UpdateChildren();
        if (_focusedItem is null && FocusState != FocusState.Unfocused)
            EnsurePhotoFocus();
        if (_pendingPhotoContextMenu is not null &&
            _activeElements.ContainsKey(_pendingPhotoContextMenu))
            RequestPhotoContextMenu(_pendingPhotoContextMenu);
        RefreshAllSelectionVisuals(notifyAutomation: false);
        RaisePhotoFocusChanged();
    }

    private void NotifyPhotoSelectionChanged()
    {
        foreach (var peer in _photoPeers.Values.ToArray())
            peer.NotifySelectionChanged();
        if (AutomationPeer.ListenerExists(AutomationEvents.SelectionPatternOnInvalidated))
            FrameworkElementAutomationPeer.FromElement(this)?
                .RaiseAutomationEvent(AutomationEvents.SelectionPatternOnInvalidated);
    }

    private void OnPhotoGridGotFocus(object sender, RoutedEventArgs e)
    {
        if (ItemsSource is not null || !ReferenceEquals(e.OriginalSource, this)) return;
        EnsurePhotoFocus();
    }

    private void EnsurePhotoFocus()
    {
        var target = _focusedItem ?? _primaryItem ??
            _activeElements.Where(pair => pair.Key is ImageThumbnailViewModel)
                .Where(pair => Canvas.GetTop(pair.Value) + ItemSize > ScrollContainer.VerticalOffset &&
                    Canvas.GetTop(pair.Value) < ScrollContainer.VerticalOffset + ScrollContainer.ViewportHeight)
                .OrderBy(pair => Canvas.GetTop(pair.Value))
                .ThenBy(pair => Canvas.GetLeft(pair.Value))
                .Select(pair => (ImageThumbnailViewModel)pair.Key)
                .FirstOrDefault() ?? _groups?.FirstOrDefault()?.Items.FirstOrDefault();
        _focusedItem = target;
        _photoFocusEventPending = target is not null;
        if (target is not null) ScrollItemIntoView(target);
        DispatcherQueue.TryEnqueue(() =>
        {
            RefreshAllSelectionVisuals(notifyAutomation: false);
            RaisePhotoFocusChanged();
        });
    }

    private void FocusPhoto(ImageThumbnailViewModel item, FocusState state, bool scrollIntoView = false)
    {
        var changed = !ReferenceEquals(_focusedItem, item) || FocusState == FocusState.Unfocused;
        if (!ReferenceEquals(_focusedItem, item))
        {
            _pendingPhotoContextMenu = null;
            if (_focusedItem is not null && !_activeElements.ContainsKey(_focusedItem))
                _photoPeers.Remove(_focusedItem);
        }
        _focusedItem = item;
        _photoFocusEventPending |= changed;
        if (scrollIntoView) ScrollItemIntoView(item);
        Focus(state);
        RefreshAllSelectionVisuals(notifyAutomation: false);
        RaisePhotoFocusChanged();
    }

    private void RaisePhotoFocusChanged()
    {
        if (!_photoFocusEventPending || _focusedItem is null || FocusState == FocusState.Unfocused ||
            !_activeElements.ContainsKey(_focusedItem)) return;
        _photoFocusEventPending = false;
        if (AutomationPeer.ListenerExists(AutomationEvents.AutomationFocusChanged))
            GetPhotoPeer(_focusedItem).RaiseAutomationEvent(AutomationEvents.AutomationFocusChanged);
    }

    private void RequestPhotoContextMenu(ImageThumbnailViewModel item)
    {
        if (!_selectedItems.Contains(item))
            HandleSelection(item, ModifierState.None);
        ScrollItemIntoView(item);
        if (!_activeElements.TryGetValue(item, out var element))
        {
            // ChangeView can complete on a later layout pass; fulfill after realization.
            _pendingPhotoContextMenu = item;
            return;
        }
        _pendingPhotoContextMenu = null;
        ContextMenuRequested?.Invoke(this, new ContextMenuRequestedEventArgs
        {
            PrimaryItem = item,
            Source = element,
            Position = new Point(element.ActualWidth / 2, element.ActualHeight / 2)
        });
    }

    private void ApplyPhotoFocusVisual(FrameworkElement element, ImageThumbnailViewModel item)
    {
        if (element is not Grid grid) return;
        var border = grid.Children.OfType<Border>().First(child => Equals(child.Tag, "PhotoKeyboardFocus"));
        border.Visibility = ReferenceEquals(item, _focusedItem) && FocusState != FocusState.Unfocused
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private Windows.UI.Color PhotoSelectionColor => _accessibilitySettings.HighContrast
        ? _uiSettings.UIElementColor(UIElementType.Highlight) : SelectionColor;
    private Windows.UI.Color PhotoHoverColor => _accessibilitySettings.HighContrast
        ? _uiSettings.GetColorValue(UIColorType.Foreground) : Microsoft.UI.Colors.LightGray;

    private void ApplyPhotoColors(Grid grid)
    {
        bool contrast = _accessibilitySettings.HighContrast;
        var background = contrast ? _uiSettings.GetColorValue(UIColorType.Background) : Microsoft.UI.Colors.DarkGray;
        var foreground = contrast ? _uiSettings.GetColorValue(UIColorType.Foreground) : Microsoft.UI.Colors.White;
        grid.Background = new SolidColorBrush(background);
        var overlay = grid.Children.OfType<Border>().First(child => child.Child is TextBlock);
        overlay.Background = new SolidColorBrush(contrast ? background : Windows.UI.Color.FromArgb(153, 0, 0, 0));
        ((TextBlock)overlay.Child).Foreground = new SolidColorBrush(foreground);
        grid.Children.OfType<Border>().First(child => Equals(child.Tag, "PhotoKeyboardFocus"))
            .BorderBrush = new SolidColorBrush(foreground);
    }

    private void ApplyHeaderColors(Border border)
    {
        border.Background = new SolidColorBrush(_accessibilitySettings.HighContrast
            ? _uiSettings.GetColorValue(UIColorType.Background) : Microsoft.UI.Colors.Gray);
        ((TextBlock)border.Child).Foreground = new SolidColorBrush(_accessibilitySettings.HighContrast
            ? _uiSettings.GetColorValue(UIColorType.Foreground)
            : ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
    }

    private void OnAccessibilityLoaded(object sender, RoutedEventArgs e)
    {
        if (_accessibilityLoaded) return;
        _accessibilityLoaded = true;
        ObservePhotoGroups();
        if (ItemsSource is null)
        {
            OnPhotoDataChanged();
            RecalculateLayout();
        }
        _uiSettings.ColorValuesChanged += OnPhotoSystemColorsChanged;
        ActualThemeChanged += OnPhotoThemeChanged;
        RefreshPhotoColors();
    }

    private void OnAccessibilityUnloaded(object sender, RoutedEventArgs e)
    {
        _accessibilityLoaded = false;
        _uiSettings.ColorValuesChanged -= OnPhotoSystemColorsChanged;
        ActualThemeChanged -= OnPhotoThemeChanged;
        foreach (var collection in _observedPhotoCollections)
            collection.CollectionChanged -= OnPhotoItemsChanged;
        _observedPhotoCollections.Clear();
        foreach (var group in _observedPhotoGroups)
            group.PropertyChanged -= OnPhotoGroupChanged;
        _observedPhotoGroups.Clear();
    }

    private void OnPhotoSystemColorsChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(RefreshPhotoColors);
    private void OnPhotoThemeChanged(FrameworkElement sender, object args) => RefreshPhotoColors();

    private void RefreshPhotoColors()
    {
        foreach (var element in _activeElements.Values.Concat(_availablePhotoElements).Concat(_availableHeaderElements))
        {
            if (element is Grid grid) ApplyPhotoColors(grid);
            else if (element is Border border) ApplyHeaderColors(border);
        }
        RefreshAllSelectionVisuals(notifyAutomation: false);
    }

    private static class PhotoAutomationStrings
    {
        private static readonly ResourceLoader Loader = new(
            Path.Combine(AppContext.BaseDirectory, "PhotoLibrarian.pri"), "Resources");
        public static string Get(string key) => Loader.GetString(key);
    }

    private sealed class PhotoGridPeer(VirtualizingPhotoGrid grid) :
        FrameworkElementAutomationPeer(grid), ISelectionProvider
    {
        private object[] _children = [];

        protected override string GetClassNameCore() => nameof(VirtualizingPhotoGrid);
        protected override string GetNameCore() =>
            grid.ItemsSource is null ? PhotoAutomationStrings.Get("PhotoGridName") : base.GetNameCore();
        protected override string GetAutomationIdCore() =>
            grid.ItemsSource is null ? "PhotoGrid" : base.GetAutomationIdCore();
        protected override string GetHelpTextCore() =>
            grid.ItemsSource is null ? PhotoAutomationStrings.Get("PhotoGridHelp") : base.GetHelpTextCore();
        protected override AutomationControlType GetAutomationControlTypeCore() =>
            grid.ItemsSource is null ? AutomationControlType.List : base.GetAutomationControlTypeCore();
        protected override object GetPatternCore(PatternInterface patternInterface)
        {
            if (grid.ItemsSource is null)
            {
                if (patternInterface == PatternInterface.Selection) return this;
                if (patternInterface == PatternInterface.Scroll)
                    return CreatePeerForElement(grid.ScrollContainer)?.GetPattern(patternInterface)!;
            }
            return base.GetPatternCore(patternInterface);
        }

        protected override IList<AutomationPeer> GetChildrenCore()
        {
            if (grid.ItemsSource is not null) return base.GetChildrenCore();
            return grid._activeElements
                .Where(pair => pair.Key is ImageThumbnailViewModel)
                .OrderBy(pair => Canvas.GetTop(pair.Value))
                .ThenBy(pair => Canvas.GetLeft(pair.Value))
                .Select(pair => (AutomationPeer)grid.GetPhotoPeer((ImageThumbnailViewModel)pair.Key))
                .ToList();
        }

        protected override object GetFocusedElementCore() =>
            grid.ItemsSource is null && grid._focusedItem is not null && grid.FocusState != FocusState.Unfocused
                ? grid.GetPhotoPeer(grid._focusedItem) : base.GetFocusedElementCore();

        public bool CanSelectMultiple => true;
        public bool IsSelectionRequired => false;
        public IRawElementProviderSimple[] GetSelection() =>
            grid._selectedItems.Select(item => ProviderFromPeer(grid.GetPhotoPeer(item))).ToArray();

        public void UpdateChildren()
        {
            var children = grid._activeElements.Keys.OfType<ImageThumbnailViewModel>().Cast<object>().ToArray();
            if (_children.SequenceEqual(children)) return;
            _children = children;
            InvalidatePeer();
        }
    }

    private sealed class PhotoItemPeer : FrameworkElementAutomationPeer,
        ISelectionItemProvider, IInvokeProvider, IScrollItemProvider
    {
        private readonly VirtualizingPhotoGrid _grid;
        private readonly ImageThumbnailViewModel _item;
        private readonly int _version;
        private bool _wasSelected;

        public PhotoItemPeer(VirtualizingPhotoGrid grid, ImageThumbnailViewModel item, int version) : base(grid)
        {
            _grid = grid;
            _item = item;
            _version = version;
            _wasSelected = grid._selectedItems.Contains(item);
        }

        private void EnsureAvailable()
        {
            if (_version != _grid._photoDataVersion || _grid.ItemsSource is not null)
                throw new ElementNotAvailableException();
        }

        private void EnsureEnabled()
        {
            EnsureAvailable();
            if (!_grid.IsEnabled) throw new ElementNotEnabledException();
        }

        protected override string GetNameCore() { EnsureAvailable(); return _item.FileName; }
        protected override string GetAutomationIdCore() { EnsureAvailable(); return "Photo:" + _item.Entry.FilePath; }
        protected override string GetClassNameCore() => "PhotoItem";
        protected override string GetHelpTextCore() => PhotoAutomationStrings.Get("PhotoGridItemHelp");
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
        protected override IList<AutomationPeer> GetChildrenCore() => [];
        protected override bool IsKeyboardFocusableCore() => _grid.IsEnabled;
        protected override bool HasKeyboardFocusCore() =>
            ReferenceEquals(_grid._focusedItem, _item) && _grid.FocusState != FocusState.Unfocused;

        protected override object GetPatternCore(PatternInterface patternInterface) => patternInterface switch
        {
            PatternInterface.SelectionItem or PatternInterface.Invoke or PatternInterface.ScrollItem => this,
            _ => null!
        };

        private Rect VisibleBounds()
        {
            EnsureAvailable();
            if (!_grid._activeElements.TryGetValue(_item, out var element)) return new Rect();
            var bounds = new FrameworkElementAutomationPeer(element).GetBoundingRectangle();
            var viewport = new FrameworkElementAutomationPeer(_grid.ScrollContainer).GetBoundingRectangle();
            bounds.Intersect(viewport);
            return bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0 ? new Rect() : bounds;
        }

        protected override Rect GetBoundingRectangleCore() => VisibleBounds();
        protected override bool IsOffscreenCore()
        {
            var bounds = VisibleBounds();
            return bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0;
        }
        protected override Point GetClickablePointCore()
        {
            var bounds = VisibleBounds();
            return bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0
                ? new Point(double.NaN, double.NaN)
                : new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        }
        protected override void SetFocusCore()
        {
            EnsureEnabled();
            _grid.FocusPhoto(_item, FocusState.Programmatic, scrollIntoView: true);
        }

        public bool IsSelected { get { EnsureAvailable(); return _grid._selectedItems.Contains(_item); } }
        public IRawElementProviderSimple SelectionContainer => ProviderFromPeer(CreatePeerForElement(_grid));
        public void Select()
        {
            EnsureEnabled();
            _grid.HandleSelection(_item, ModifierState.None);
        }
        public void AddToSelection()
        {
            EnsureEnabled();
            if (!IsSelected) _grid.HandleSelection(_item, ModifierState.Ctrl);
        }
        public void RemoveFromSelection()
        {
            EnsureEnabled();
            if (IsSelected) _grid.HandleSelection(_item, ModifierState.Ctrl);
        }
        public void Invoke()
        {
            EnsureEnabled();
            _grid.FocusPhoto(_item, FocusState.Programmatic, scrollIntoView: true);
            _grid.HandleSelection(_item, ModifierState.None);
            _grid.ItemDoubleClicked?.Invoke(_grid, _item);
        }
        public void ScrollIntoView()
        {
            EnsureEnabled();
            _grid.ScrollItemIntoView(_item);
        }
        public void NotifySelectionChanged()
        {
            var selected = IsSelected;
            if (_wasSelected == selected) return;
            RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, _wasSelected, selected);
            RaiseAutomationEvent(selected ? AutomationEvents.SelectionItemPatternOnElementAddedToSelection
                : AutomationEvents.SelectionItemPatternOnElementRemovedFromSelection);
            _wasSelected = selected;
        }
    }
}
