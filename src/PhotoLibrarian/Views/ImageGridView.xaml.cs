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

        // Right-click context menu
        PhotoGrid.ContextMenuRequested += OnContextMenuRequested;

        // F key → toggle flag on the current selection
        PhotoGrid.FlagToggleRequested += OnFlagToggleRequested;
        
        // Listen for GroupedImages changes — the inner grid already self-subscribes to the same
        // ObservableCollection for layout, so we do NOT re-call PhotoGrid.SetGroups here.
        // (SetGroups clears _selectedItems, which would wipe the user's selection on every
        //  in-place re-sort/re-group after a metadata edit.)
        
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

    private void OnFlagToggleRequested(object? sender, EventArgs e)
    {
        ViewModel?.ToggleFlagCommand.Execute(null);
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

    // =================================================================
    //  Right-click context menu
    // =================================================================

    private void OnContextMenuRequested(object? sender, Controls.ContextMenuRequestedEventArgs e)
    {
        if (ViewModel is null) return;

        var primary = e.PrimaryItem;
        var selected = ViewModel.SelectedImages.Count > 0
            ? ViewModel.SelectedImages.ToList()
            : new List<ImageThumbnailViewModel> { primary };
        bool isMulti = selected.Count > 1;
        var ops = App.ViewModel.PhotoOps;

        var menu = new MenuFlyout();

        // View file (default action) — only enabled for single selection
        var viewItem = new MenuFlyoutItem { Text = "View file" };
        viewItem.Click += (_, _) =>
        {
            ViewModel.SelectedImage = primary;
            ViewModel.OpenViewerCommand.Execute(null);
        };
        viewItem.IsEnabled = !isMulti;
        menu.Items.Add(viewItem);

        // Open with default
        var openWith = new MenuFlyoutItem { Text = "Open with default app" };
        openWith.Click += async (_, _) =>
        {
            foreach (var vm in selected) await Services.PhotoOperationsService.OpenWithDefaultAsync(vm.Entry.FilePath);
        };
        menu.Items.Add(openWith);

        // Open with → submenu of registered handlers for this extension
        BuildOpenWithSubMenu(menu, primary, selected);

        // Open file location
        var reveal = new MenuFlyoutItem { Text = "Open file location" };
        reveal.Click += (_, _) => Services.PhotoOperationsService.RevealInExplorer(primary.Entry.FilePath);
        menu.Items.Add(reveal);

        menu.Items.Add(new MenuFlyoutSeparator());

        // Set as desktop background — single only
        var wallpaper = new MenuFlyoutItem { Text = "Set as desktop background" };
        wallpaper.Click += (_, _) => Services.PhotoOperationsService.SetAsDesktopBackground(primary.Entry.FilePath);
        wallpaper.IsEnabled = !isMulti;
        menu.Items.Add(wallpaper);

        var rotateRight = new MenuFlyoutItem { Text = "Rotate right" };
        rotateRight.Click += async (_, _) =>
        {
            foreach (var vm in selected) await ops.RotateAsync(vm.Entry, clockwise: true);
        };
        menu.Items.Add(rotateRight);

        var rotateLeft = new MenuFlyoutItem { Text = "Rotate left" };
        rotateLeft.Click += async (_, _) =>
        {
            foreach (var vm in selected) await ops.RotateAsync(vm.Entry, clockwise: false);
        };
        menu.Items.Add(rotateLeft);

        menu.Items.Add(new MenuFlyoutSeparator());

        // Flag / Unflag — mirrors the F shortcut. A mixed selection is flagged first.
        bool allFlagged = selected.All(vm => vm.Entry.IsFlagged);
        var flagItem = new MenuFlyoutItem
        {
            Text = allFlagged
                ? (isMulti ? $"Unflag ({selected.Count})" : "Unflag")
                : (isMulti ? $"Flag ({selected.Count})" : "Flag"),
            Icon = new FontIcon { Glyph = "\uE129" },
            KeyboardAcceleratorTextOverride = "F"
        };
        flagItem.Click += async (_, _) => await ViewModel.SetFlagAsync(selected, !allFlagged);
        menu.Items.Add(flagItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        // Copy
        var copy = new MenuFlyoutItem { Text = isMulti ? $"Copy ({selected.Count} files)" : "Copy" };
        copy.Click += async (_, _) =>
        {
            await Services.PhotoOperationsService.CopyFilesToClipboardAsync(selected.Select(vm => vm.Entry.FilePath));
        };
        menu.Items.Add(copy);

        // Delete
        var delete = new MenuFlyoutItem { Text = isMulti ? $"Delete ({selected.Count})" : "Delete" };
        delete.Click += async (_, _) =>
        {
            var deleted = await ops.DeleteToRecycleBinAsync(selected.Select(vm => vm.Entry));
            if (deleted.Count > 0)
            {
                // Refresh the grid; deleted IDs are gone from DB already
                await ViewModel.LoadImagesAsync();
            }
        };
        menu.Items.Add(delete);

        // Rename — single only
        var rename = new MenuFlyoutItem { Text = "Rename…" };
        rename.Click += async (_, _) => await ShowRenameDialogAsync(primary.Entry);
        rename.IsEnabled = !isMulti;
        menu.Items.Add(rename);

        menu.Items.Add(new MenuFlyoutSeparator());

        // Properties — single only (shell dialog is one-file-at-a-time)
        var props = new MenuFlyoutItem { Text = "Properties" };
        props.Click += (_, _) => Services.PhotoOperationsService.ShowPropertiesDialog(primary.Entry.FilePath);
        props.IsEnabled = !isMulti;
        menu.Items.Add(props);

        menu.ShowAt(e.Source, e.Position);
    }

    private static void BuildOpenWithSubMenu(
        MenuFlyout menu,
        ViewModels.ImageThumbnailViewModel primary,
        List<ViewModels.ImageThumbnailViewModel> selected)
    {
        var ext = System.IO.Path.GetExtension(primary.Entry.FilePath);
        var subFlyout = new MenuFlyoutSubItem { Text = "Open with" };

        try
        {
            var handlers = Services.OpenWithHelper.EnumerateHandlers(ext);
            // Deduplicate by UI name to avoid showing the same app multiple times
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in handlers)
            {
                if (!seen.Add(h.UIName)) continue;
                var item = new MenuFlyoutItem { Text = h.UIName };
                item.Click += (_, _) =>
                {
                    var paths = selected.Select(vm => vm.Entry.FilePath).ToList();
                    Services.OpenWithHelper.Invoke(h, paths);
                };
                subFlyout.Items.Add(item);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OPS] OpenWith enumeration failed: {ex.Message}");
        }

        if (subFlyout.Items.Count > 0)
            subFlyout.Items.Add(new MenuFlyoutSeparator());

        var chooseAnother = new MenuFlyoutItem { Text = "Choose another app…" };
        chooseAnother.Click += (_, _) =>
        {
            // Multi-file → the dialog only takes one file, use the primary
            Services.OpenWithHelper.ShowOpenWithDialog(primary.Entry.FilePath);
        };
        subFlyout.Items.Add(chooseAnother);

        menu.Items.Add(subFlyout);
    }

    private async Task ShowRenameDialogAsync(Core.Models.ImageEntry entry)
    {
        if (ViewModel is null) return;

        var box = new TextBox
        {
            Text = System.IO.Path.GetFileNameWithoutExtension(entry.FileName),
            SelectionStart = 0,
            SelectionLength = System.IO.Path.GetFileNameWithoutExtension(entry.FileName).Length
        };

        var dialog = new ContentDialog
        {
            Title = "Rename file",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = $"Current: {entry.FileName}", Opacity = 0.7 },
                    box
                }
            },
            PrimaryButtonText = "Rename",
            SecondaryButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var newName = box.Text?.Trim();
        if (string.IsNullOrWhiteSpace(newName)) return;

        var ops = App.ViewModel.PhotoOps;
        var newPath = await ops.RenameAsync(entry, newName);
        if (newPath == null)
        {
            var err = new ContentDialog
            {
                Title = "Rename failed",
                Content = "Couldn't rename file. The name may be invalid or a file with that name already exists.",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await err.ShowAsync();
            return;
        }

        // Refresh the metadata panel for the renamed entry
        if (ViewModel.SelectedImage?.Entry == entry)
        {
            App.ViewModel.MetadataPanel.ShowMetadata(entry);
        }
    }
}
