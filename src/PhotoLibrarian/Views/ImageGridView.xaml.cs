// Copyright (c) Duncan Lawler. All Rights Reserved.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Diagnostics;
using PhotoLibrarian.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PhotoLibrarian.Views;

public sealed partial class ImageGridView : UserControl
{
    private ImageGridViewModel? ViewModel => App.ViewModel?.ImageGrid;
    private bool _isInitialized;
    private bool _isExporting;

    public ImageGridView()
    {
        this.InitializeComponent();
        this.Loaded += OnLoaded;
    }

    /// <summary>Highlights the given face's rectangle over its image's grid thumbnail, if
    /// currently rendered. Pass null to clear. Forwarded from the Browse panel on hover.</summary>
    public void SetFaceHighlight(ImageThumbnailViewModel? item, Core.Models.FaceRegion? region) =>
        PhotoGrid.SetFaceHighlight(item, region);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || _isInitialized) return;
        _isInitialized = true;

        DebugLog.WriteDebugLine($"[GRIDVIEW] OnLoaded - Images.Count={ViewModel.Images.Count}, GroupedImages.Count={ViewModel.GroupedImages.Count}");

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
            DebugLog.WriteDebugLine($"[GRIDVIEW] Images changed, EmptyState visibility={shouldShow}");
            EmptyState.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
        };
        
        EmptyState.Visibility = ViewModel.Images.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
            
        // Initialize UI controls to match the restored layout.
        UpdateGroupByCombo();
        UpdateSortByCombo();
        UpdateSortOrderIcon();
        ViewModel.ResultsChanged += OnResultsChanged;
        ViewModel.SelectionRestoreRequested += OnSelectionRestoreRequested;
        InitializeRefinementControls(ViewModel.Refinement);
        await RefreshPeopleAsync();
        UpdateActiveFilterChips();
    }

    public async System.Threading.Tasks.Task RefreshPeopleAsync()
    {
        if (ViewModel is null) return;

        try
        {
            var selectedPersonId = ViewModel.Refinement.PersonId;
            var people = await ViewModel.GetAvailablePeopleAsync();
            while (PersonCombo.Items.Count > 1)
                PersonCombo.Items.RemoveAt(PersonCombo.Items.Count - 1);

            ComboBoxItem? selectedItem = null;
            foreach (var person in people)
            {
                var item = new ComboBoxItem
                {
                    Content = $"{person.Name} ({person.FaceCount:N0})",
                    Tag = person.Id
                };
                PersonCombo.Items.Add(item);
                if (person.Id == selectedPersonId)
                    selectedItem = item;
            }

            if (selectedItem is not null)
            {
                PersonCombo.SelectedItem = selectedItem;
            }
            else if (selectedPersonId.HasValue)
            {
                PersonCombo.SelectedIndex = 0;
                await ViewModel.ApplyRefinementAsync(
                    ViewModel.Refinement with { PersonId = null });
            }

            if (PersonCombo.SelectedIndex < 0)
                PersonCombo.SelectedIndex = 0;

            if (_isInitialized)
                UpdateActiveFilterChips();
        }
        catch (Exception ex)
        {
            App.ViewModel.StatusText = $"Could not load people filters: {ex.Message}";
        }
    }

    private void OnResultsChanged(object? sender, EventArgs e)
    {
        RestoreGridSelection(scrollIntoView: false);
    }

    private void OnSelectionRestoreRequested(object? sender, EventArgs e)
    {
        RestoreGridSelection(scrollIntoView: true);
    }

    private void RestoreGridSelection(bool scrollIntoView)
    {
        if (ViewModel is null) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            PhotoGrid.RestoreSelection(
                ViewModel.SelectedImages,
                ViewModel.SelectedImage,
                scrollIntoView);
        });
    }

    private void OnDatePresetChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectedIndex is applied while InitializeComponent is still creating later elements.
        if (CustomDatePanel is null || sender is not ComboBox datePresetCombo)
            return;

        CustomDatePanel.Visibility =
            GetSelectedTag(datePresetCombo) == "Custom"
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private async void OnApplyFilters(object sender, RoutedEventArgs e)
    {
        await ApplyRefinementFromControlsAsync(hideFlyout: true);
    }

    private async void OnClearAllFilters(object sender, RoutedEventArgs e)
    {
        ResetRefinementControls();
        await ApplyRefinementFromControlsAsync(hideFlyout: false);
    }

    private async System.Threading.Tasks.Task ApplyRefinementFromControlsAsync(bool hideFlyout)
    {
        if (ViewModel is null) return;

        try
        {
            var refinement = BuildRefinementFromControls();
            await ViewModel.ApplyRefinementAsync(refinement);
            UpdateActiveFilterChips();
            if (hideFlyout)
                FilterFlyout.Hide();
        }
        catch (Exception ex)
        {
            App.ViewModel.StatusText = $"Could not apply filters: {ex.Message}";
        }
    }

    private ImageRefinementFilter BuildRefinementFromControls()
    {
        int? rating = int.TryParse(GetSelectedTag(RatingCombo), out var parsedRating)
            ? parsedRating
            : null;
        _ = Enum.TryParse<RatingFilterMode>(
            GetSelectedTag(RatingModeCombo),
            out var ratingMode);

        var (dateFrom, dateTo) = GetDateRange();
        _ = Enum.TryParse<FlagFilterMode>(
            GetSelectedTag(FlagCombo),
            out var flag);
        _ = Enum.TryParse<MediaKindFilter>(
            GetSelectedTag(MediaKindCombo),
            out var mediaKind);
        _ = Enum.TryParse<MissingMetadataFilter>(
            GetSelectedTag(MissingMetadataCombo),
            out var missingMetadata);

        return new ImageRefinementFilter
        {
            Rating = rating,
            RatingMode = ratingMode,
            DateFrom = dateFrom,
            DateTo = dateTo,
            IncludedTags = ParseTokens(IncludeTagsBox.Text),
            ExcludedTags = ParseTokens(ExcludeTagsBox.Text),
            PersonId = (PersonCombo.SelectedItem as ComboBoxItem)?.Tag as long?,
            Flag = flag,
            MediaKind = mediaKind,
            Extensions = ParseTokens(ExtensionsBox.Text)
                .Select(ImageRefinementFilter.NormalizeExtension)
                .Where(extension => extension.Length > 1)
                .ToList(),
            MissingMetadata = missingMetadata
        };
    }

    private (DateTime? From, DateTime? To) GetDateRange()
    {
        var today = DateTime.Today;
        return GetSelectedTag(DatePresetCombo) switch
        {
            "ThisYear" => (new DateTime(today.Year, 1, 1), today),
            "Last12Months" => (today.AddMonths(-12), today),
            "Custom" => (
                DateFromPicker.Date?.DateTime.Date,
                DateToPicker.Date?.DateTime.Date),
            _ => (null, null)
        };
    }

    private void InitializeRefinementControls(ImageRefinementFilter refinement)
    {
        SelectByTag(
            RatingCombo,
            refinement.Rating?.ToString() ?? string.Empty);
        SelectByTag(RatingModeCombo, refinement.RatingMode.ToString());

        if (refinement.DateFrom.HasValue || refinement.DateTo.HasValue)
        {
            SelectByTag(DatePresetCombo, "Custom");
            DateFromPicker.Date = refinement.DateFrom.HasValue
                ? new DateTimeOffset(refinement.DateFrom.Value)
                : null;
            DateToPicker.Date = refinement.DateTo.HasValue
                ? new DateTimeOffset(refinement.DateTo.Value)
                : null;
        }

        IncludeTagsBox.Text = string.Join(", ", refinement.IncludedTags);
        ExcludeTagsBox.Text = string.Join(", ", refinement.ExcludedTags);
        SelectByTag(FlagCombo, refinement.Flag.ToString());
        SelectByTag(MediaKindCombo, refinement.MediaKind.ToString());
        ExtensionsBox.Text = string.Join(
            ", ",
            refinement.Extensions.Select(extension => extension.TrimStart('.')));
        SelectByTag(
            MissingMetadataCombo,
            refinement.MissingMetadata.ToString());
    }

    private void ResetRefinementControls()
    {
        RatingCombo.SelectedIndex = 0;
        RatingModeCombo.SelectedIndex = 0;
        DatePresetCombo.SelectedIndex = 0;
        DateFromPicker.Date = null;
        DateToPicker.Date = null;
        IncludeTagsBox.Text = string.Empty;
        ExcludeTagsBox.Text = string.Empty;
        PersonCombo.SelectedIndex = 0;
        FlagCombo.SelectedIndex = 0;
        MediaKindCombo.SelectedIndex = 0;
        ExtensionsBox.Text = string.Empty;
        MissingMetadataCombo.SelectedIndex = 0;
    }

    private void UpdateActiveFilterChips()
    {
        if (ViewModel is null) return;

        var refinement = ViewModel.Refinement;
        ActiveFiltersPanel.Children.Clear();

        if (refinement.Rating is int rating)
        {
            var qualifier = refinement.RatingMode switch
            {
                RatingFilterMode.Exact => "exactly",
                RatingFilterMode.AndLower => "and lower",
                _ => "and higher"
            };
            AddFilterChip("rating", $"{rating} star {qualifier}");
        }

        if (refinement.DateFrom.HasValue || refinement.DateTo.HasValue)
        {
            AddFilterChip(
                "date",
                $"Date: {refinement.DateFrom?.ToString("d") ?? "any"} - " +
                $"{refinement.DateTo?.ToString("d") ?? "any"}");
        }

        foreach (var tag in refinement.IncludedTags)
            AddFilterChip($"include:{tag}", $"Tag: {tag}");
        foreach (var tag in refinement.ExcludedTags)
            AddFilterChip($"exclude:{tag}", $"Not tag: {tag}");

        if (refinement.PersonId.HasValue)
        {
            var personName = (PersonCombo.SelectedItem as ComboBoxItem)?.Content?.ToString()
                ?? "Selected person";
            AddFilterChip("person", $"Person: {personName}");
        }

        if (refinement.Flag != FlagFilterMode.Any)
            AddFilterChip("flag", refinement.Flag.ToString());

        if (refinement.MediaKind != MediaKindFilter.Any)
            AddFilterChip("media", refinement.MediaKind.ToString());

        foreach (var extension in refinement.Extensions)
            AddFilterChip($"extension:{extension}", extension.ToUpperInvariant());

        if (refinement.MissingMetadata != MissingMetadataFilter.None)
            AddFilterChip("missing", $"Missing {refinement.MissingMetadata}");

        if (ActiveFiltersPanel.Children.Count > 0)
        {
            var clearAll = new Button
            {
                Content = "Clear all",
                Padding = new Thickness(8, 4, 8, 4)
            };
            AutomationProperties.SetAutomationId(clearAll, "ActiveFiltersClearAll");
            clearAll.Click += OnClearAllFilters;
            ActiveFiltersPanel.Children.Add(clearAll);
        }

        ActiveFiltersScroller.Visibility =
            ActiveFiltersPanel.Children.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void AddFilterChip(string key, string label)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4
        };
        content.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center
        });
        content.Children.Add(new FontIcon
        {
            Glyph = "\uE711",
            FontSize = 12
        });

        var button = new Button
        {
            Content = content,
            Tag = key,
            Padding = new Thickness(8, 4, 8, 4)
        };
        AutomationProperties.SetAutomationId(button, $"ActiveFilter_{key}");
        AutomationProperties.SetName(button, $"Remove filter: {label}");
        button.Click += OnRemoveFilterChip;
        ActiveFiltersPanel.Children.Add(button);
    }

    private async void OnRemoveFilterChip(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;

        if (key == "rating")
            RatingCombo.SelectedIndex = 0;
        else if (key == "date")
            DatePresetCombo.SelectedIndex = 0;
        else if (key == "person")
            PersonCombo.SelectedIndex = 0;
        else if (key == "flag")
            FlagCombo.SelectedIndex = 0;
        else if (key == "media")
            MediaKindCombo.SelectedIndex = 0;
        else if (key == "missing")
            MissingMetadataCombo.SelectedIndex = 0;
        else if (key.StartsWith("include:", StringComparison.Ordinal))
            IncludeTagsBox.Text = RemoveToken(IncludeTagsBox.Text, key["include:".Length..]);
        else if (key.StartsWith("exclude:", StringComparison.Ordinal))
            ExcludeTagsBox.Text = RemoveToken(ExcludeTagsBox.Text, key["exclude:".Length..]);
        else if (key.StartsWith("extension:", StringComparison.Ordinal))
            ExtensionsBox.Text = RemoveToken(
                ExtensionsBox.Text,
                key["extension:".Length..].TrimStart('.'));

        await ApplyRefinementFromControlsAsync(hideFlyout: false);
    }

    private static List<string> ParseTokens(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string RemoveToken(string text, string token) =>
        string.Join(
            ", ",
            ParseTokens(text).Where(value =>
                !string.Equals(value, token, StringComparison.OrdinalIgnoreCase)));

    private static string GetSelectedTag(ComboBox comboBox) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;

    private static void SelectByTag(ComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item =>
                string.Equals(
                    item.Tag?.ToString(),
                    tag,
                    StringComparison.OrdinalIgnoreCase));
        comboBox.SelectedIndex = Math.Max(comboBox.SelectedIndex, 0);
    }
    
    private void OnVisibleItemsChanged(object? sender, List<ImageThumbnailViewModel> visibleItems)
    {
        // Request thumbnails for visible items
        DebugLog.WriteDebugLine($"[GRIDVIEW] OnVisibleItemsChanged: {visibleItems.Count} items");
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
        if (!_isInitialized || ViewModel is null || GroupByCombo.SelectedItem is not ComboBoxItem item) return;
        
        if (Enum.TryParse<GroupByOption>(item.Tag?.ToString(), out var groupBy))
        {
            ViewModel.GroupBy = groupBy;
        }
    }
    
    private void OnSortByChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || ViewModel is null || SortByCombo.SelectedItem is not ComboBoxItem item) return;
        
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

    /// <summary>Raised when the user asks for a slideshow. The argument is the photo selection to
    /// use, or null to use the grid's current selection.</summary>
    public event EventHandler<IReadOnlyList<Core.Models.ImageEntry>?>? SlideshowRequested;

    private void OnSlideshowClick(object sender, RoutedEventArgs e) =>
        SlideshowRequested?.Invoke(this, null);

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

        var slideshowItem = new MenuFlyoutItem
        {
            Text = isMulti ? $"Slideshow ({selected.Count})" : "Slideshow from here",
            Icon = new FontIcon { Glyph = "\uE786" },
            KeyboardAcceleratorTextOverride = "F11"
        };
        AutomationProperties.SetAutomationId(slideshowItem, "GridContextSlideshow");
        var selectedEntries = selected.Select(vm => vm.Entry).ToArray();
        slideshowItem.Click += (_, _) => SlideshowRequested?.Invoke(this, selectedEntries);
        menu.Items.Add(slideshowItem);

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
            foreach (var vm in selected)
            {
                var size = await ops.RotateAsync(vm.Entry, clockwise: true);
                await App.ViewModel!.RefreshAfterPixelEditAsync(
                    vm.Entry.FilePath, size.Width, size.Height, "Rotated");
            }
        };
        menu.Items.Add(rotateRight);

        var rotateLeft = new MenuFlyoutItem { Text = "Rotate left" };
        rotateLeft.Click += async (_, _) =>
        {
            foreach (var vm in selected)
            {
                var size = await ops.RotateAsync(vm.Entry, clockwise: false);
                await App.ViewModel!.RefreshAfterPixelEditAsync(
                    vm.Entry.FilePath, size.Width, size.Height, "Rotated");
            }
        };
        menu.Items.Add(rotateLeft);

        var resize = new MenuFlyoutItem
        {
            Text = isMulti ? $"Resize and export ({selected.Count})..." : "Resize and export...",
            IsEnabled = !_isExporting && selected.All(vm => vm.Entry.MediaType == MediaType.Image)
        };
        AutomationProperties.SetAutomationId(resize, "ResizeExportSelection");
        resize.Click += async (_, _) => await ShowResizeExportAsync(selected);
        menu.Items.Add(resize);

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

        var rename = new MenuFlyoutItem { Text = isMulti ? $"Rename ({selected.Count})…" : "Rename…" };
        rename.Click += async (_, _) => await RenameEntriesAsync(selected.Select(vm => vm.Entry).ToArray());
        menu.Items.Add(rename);

        menu.Items.Add(new MenuFlyoutSeparator());

        // Properties — single only (shell dialog is one-file-at-a-time)
        var props = new MenuFlyoutItem { Text = "Properties" };
        props.Click += (_, _) => Services.PhotoOperationsService.ShowPropertiesDialog(primary.Entry.FilePath);
        props.IsEnabled = !isMulti;
        menu.Items.Add(props);

        menu.ShowAt(e.Source, e.Position);
    }

    private async Task ShowResizeExportAsync(IReadOnlyList<ImageThumbnailViewModel> selected)
    {
        if (_isExporting) return;
        _isExporting = true;
        try
        {
            // Snapshot paths before opening a dialog; navigation or selection changes must not
            // change which photos belong to this batch.
            var paths = selected.Select(vm => vm.Entry.FilePath).ToArray();
            var optionsDialog = new ResizeExportDialog(paths.Length)
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme
            };
            if (await optionsDialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            var outputFolder = optionsDialog.OutputFolder;
            var options = optionsDialog.Options;
            using var cancellation = new System.Threading.CancellationTokenSource();
            var bar = new ProgressBar { Minimum = 0, Maximum = paths.Length, Value = 0 };
            AutomationProperties.SetAutomationId(bar, "ExportProgress");
            var label = new TextBlock { Text = $"Exporting 0 of {paths.Length:N0}", TextWrapping = TextWrapping.Wrap };
            var progressDialog = new ContentDialog
            {
                Title = "Exporting resized copies",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Content = new StackPanel { Spacing = 16, Children = { label, bar } }
            };
            var exportFinished = false;
            progressDialog.Closing += (_, args) =>
            {
                if (exportFinished) return;
                args.Cancel = true;
                cancellation.Cancel();
                label.Text = "Cancelling after the current operation...";
            };
            var progress = new Progress<Core.Services.ImageExportProgress>(update =>
            {
                bar.Value = update.Completed;
                if (!cancellation.IsCancellationRequested)
                    label.Text = $"Exporting {update.Completed:N0} of {update.Total:N0}: {update.FileName}";
            });
            var showingProgress = progressDialog.ShowAsync();
            Core.Services.ImageExportResult result;
            try
            {
                result = await Task.Run(() => Core.Services.ImageExportService.ExportAsync(
                    paths, outputFolder, options, progress, cancellation.Token));
            }
            finally
            {
                exportFinished = true;
                progressDialog.Hide();
                await showingProgress;
            }

            var summary = $"{(result.IsCancelled ? "Export cancelled." : "Export complete.")} " +
                $"{result.ExportedPaths.Count:N0} exported, {result.Failures.Count:N0} failed.";
            App.ViewModel.StatusText = summary;
            var details = summary;
            if (result.Failures.Count > 0)
                details += "\n\n" + string.Join("\n", result.Failures.Select(failure =>
                    $"{failure.SourcePath}: {failure.Message}"));
            await new ContentDialog
            {
                Title = result.Failures.Count > 0 ? "Export finished with errors" : "Resize and export",
                Content = new ScrollViewer
                {
                    Content = new TextBlock { Text = details, TextWrapping = TextWrapping.Wrap },
                    MaxHeight = 400
                },
                CloseButtonText = "OK",
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme
            }.ShowAsync();
        }
        catch (Exception ex)
        {
            DebugLog.WriteDebugLine($"[EXPORT] {ex}");
            App.ViewModel.StatusText = $"Resize and export failed: {ex.Message}";
        }
        finally
        {
            _isExporting = false;
        }
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
            DebugLog.WriteDebugLine($"[OPS] OpenWith enumeration failed: {ex.Message}");
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

    public async Task RenameSelectedAsync()
    {
        if (ViewModel is null) return;
        var entries = ViewModel.SelectedImages.Select(vm => vm.Entry).ToArray();
        if (entries.Length == 0 && ViewModel.SelectedImage is { } primary)
            entries = [primary.Entry];
        await RenameEntriesAsync(entries);
    }

    private async Task RenameEntriesAsync(IReadOnlyList<Core.Models.ImageEntry> entries)
    {
        if (ViewModel is null || entries.Count == 0) return;
        if (entries.Count == 1)
        {
            await ShowRenameDialogAsync(entries[0]);
            return;
        }

        var dialog = new BatchRenameDialog(entries) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await App.ViewModel!.PhotoOps.RenameBatchAsync(dialog.Preview);
        }
        catch (Exception ex)
        {
            DebugLog.WriteDebugLine($"[OPS] Batch rename failed: {ex}");
            await new ContentDialog
            {
                Title = "Rename failed",
                Content = ex.Message,
                CloseButtonText = "OK",
                XamlRoot = XamlRoot
            }.ShowAsync();
        }
        finally
        {
            ViewModel.UpdateSelection(ViewModel.SelectedImages.ToArray(), ViewModel.SelectedImage);
            await ViewModel.LoadImagesAsync();
        }
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
        ViewModel.UpdateSelection(ViewModel.SelectedImages.ToArray(), ViewModel.SelectedImage);
        await ViewModel.LoadImagesAsync();
    }
}
