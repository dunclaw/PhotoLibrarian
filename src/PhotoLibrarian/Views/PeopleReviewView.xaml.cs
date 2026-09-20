using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Diagnostics;
using PhotoLibrarian.ViewModels;
using System.ComponentModel;
using System.Linq;
using Windows.ApplicationModel.DataTransfer;

namespace PhotoLibrarian.Views;

public sealed partial class PeopleReviewView : UserControl
{
    private int _previewRequest;
    private List<FaceSuggestionItemViewModel> _draggedFaces = [];
    private GridView? _dragSourceGrid;
    private long? _dragSourcePersonId;

    public PeopleReviewViewModel ViewModel => App.ViewModel.PeopleReview;

    public PeopleReviewView()
    {
        InitializeComponent();
        ModeSelector.SelectedItem = ReviewModeItem;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        RefreshDisplay();
        if (!IsReviewMode)
        {
            await LoadPeopleManagementAsync();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewModel.SelectedGroup)
            or nameof(ViewModel.SelectedPerson)
            or nameof(ViewModel.IsBusy)
            or nameof(ViewModel.CanUndoLastTag)
            or nameof(ViewModel.StatusMessage))
        {
            DispatcherQueue.TryEnqueue(RefreshDisplay);
        }
        else if (e.PropertyName == nameof(ViewModel.IsOpen) &&
                 ViewModel.IsOpen &&
                 !IsReviewMode)
        {
            DispatcherQueue.TryEnqueue(async () => await LoadPeopleManagementAsync());
        }
    }

    private bool IsReviewMode => ModeSelector.SelectedItem == ReviewModeItem;
    private bool IsPeopleMode => ModeSelector.SelectedItem == PeopleModeItem;

    private async void OnModeSelectionChanged(
        SelectorBar sender,
        SelectorBarSelectionChangedEventArgs args)
    {
        ReviewPanel.Visibility = IsReviewMode ? Visibility.Visible : Visibility.Collapsed;
        PeoplePanel.Visibility = IsPeopleMode ? Visibility.Visible : Visibility.Collapsed;
        ExcludedPanel.Visibility =
            !IsReviewMode && !IsPeopleMode ? Visibility.Visible : Visibility.Collapsed;
        ReviewFooter.Visibility = ReviewPanel.Visibility;
        PeopleFooter.Visibility = PeoplePanel.Visibility;
        ExcludedFooter.Visibility = ExcludedPanel.Visibility;
        ClearContextPreview();

        if (!IsLoaded) return;

        if (IsReviewMode)
        {
            await RunActionAsync(() => ViewModel.RefreshAsync());
        }
        else
        {
            await LoadPeopleManagementAsync();
        }
        RefreshDisplay();
    }

    private async Task LoadPeopleManagementAsync()
    {
        try
        {
            await ViewModel.EnsurePeopleManagementLoadedAsync();
            RefreshDisplay();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("People couldn't be loaded", ex);
        }
    }

    private void RefreshDisplay()
    {
        ModeSelector.IsEnabled = !ViewModel.IsBusy;
        GroupList.SelectedItem = ViewModel.SelectedGroup;
        PeopleList.SelectedItem = ViewModel.SelectedPerson;

        var suggestions = ViewModel.SelectedGroup?.Suggestions;
        if (!ReferenceEquals(SuggestionGrid.ItemsSource, suggestions))
        {
            SuggestionGrid.ItemsSource = suggestions;
            ClearContextPreview();
        }

        var assignedFaces = ViewModel.SelectedPerson?.Faces;
        if (!ReferenceEquals(PersonFaceGrid.ItemsSource, assignedFaces))
        {
            PersonFaceGrid.ItemsSource = assignedFaces;
            ClearContextPreview();
        }

        ReviewEmptyState.Visibility = ViewModel.Groups.Count == 0 && !ViewModel.IsBusy
            ? Visibility.Visible
            : Visibility.Collapsed;
        PeopleEmptyState.Visibility =
            (ViewModel.People.Count == 0 ||
             ViewModel.SelectedPerson?.Faces.Count == 0) &&
            !ViewModel.IsBusy
                ? Visibility.Visible
                : Visibility.Collapsed;
        PeopleEmptyText.Text = ViewModel.People.Count == 0
            ? "No named people."
            : ViewModel.SelectedPerson is null
                ? "Select a person to see their faces."
                : $"No faces are assigned to {ViewModel.SelectedPerson.Name}.";
        ExcludedEmptyState.Visibility =
            ViewModel.HiddenFaces.Count == 0 && !ViewModel.IsBusy
                ? Visibility.Visible
                : Visibility.Collapsed;
        UpdateActions();
    }

    private void OnGroupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel.IsApplyingSuggestionRefresh) return;

        var selectedGroup = GroupList.SelectedItem as FaceSuggestionGroupViewModel;
        if (ReferenceEquals(ViewModel.SelectedGroup, selectedGroup)) return;

        ViewModel.SelectedGroup = selectedGroup;
        SuggestionGrid.ItemsSource = selectedGroup?.Suggestions;
        ClearContextPreview();
        UpdateActions();
    }

    private void OnPersonSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedPerson = PeopleList.SelectedItem as PersonManagementItemViewModel;
        if (ReferenceEquals(ViewModel.SelectedPerson, selectedPerson)) return;

        ViewModel.SelectedPerson = selectedPerson;
        PersonFaceGrid.ItemsSource = selectedPerson?.Faces;
        ClearContextPreview();
        UpdateActions();
    }

    private async void OnFaceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateActions();
        if (sender is GridView grid &&
            grid.SelectedItems.LastOrDefault() is FaceSuggestionItemViewModel face)
        {
            await ShowContextPreviewAsync(face);
        }
    }

    private void UpdateActions()
    {
        var selectedCount = SuggestionGrid.SelectedItems.Count;
        var totalCount = ViewModel.SelectedGroup?.Suggestions.Count ?? 0;
        var actionCount = selectedCount > 0 ? selectedCount : totalCount;
        var hasPerson = ViewModel.SelectedGroup?.HasSuggestedPerson == true;
        ConfirmButton.IsEnabled = actionCount > 0 && hasPerson && !ViewModel.IsBusy;
        RejectButton.IsEnabled = actionCount > 0 && hasPerson && !ViewModel.IsBusy;
        TagAsButton.IsEnabled = actionCount > 0 && !ViewModel.IsBusy;
        HideFacesButton.IsEnabled = actionCount > 0 && !ViewModel.IsBusy;
        HidePersonButton.IsEnabled = hasPerson && !ViewModel.IsBusy;
        ReviewUndoTagButton.IsEnabled =
            ViewModel.CanUndoLastTag && !ViewModel.IsBusy;
        ConfirmButton.Label = selectedCount > 0
            ? $"Confirm ({selectedCount})"
            : $"Confirm all ({totalCount})";
        RejectButton.Label = selectedCount > 0
            ? $"Not this person ({selectedCount})"
            : $"Not this person for all ({totalCount})";
        TagAsButton.Label = selectedCount > 0
            ? $"Tag as... ({selectedCount})"
            : $"Tag all as... ({totalCount})";
        HideFacesButton.Label = selectedCount > 0
            ? $"Don't show again ({selectedCount})"
            : $"Don't show again ({totalCount})";
        SelectionStatus.Text = selectedCount > 0
            ? $"{selectedCount} selected"
            : totalCount > 0
                ? $"No thumbnails selected; actions apply to all {totalCount} suggestions"
                : ViewModel.StatusMessage;

        var selectedPerson = ViewModel.SelectedPerson;
        var selectedAssignedCount = PersonFaceGrid.SelectedItems.Count;
        RenamePersonButton.IsEnabled = selectedPerson is not null && !ViewModel.IsBusy;
        MergePersonButton.IsEnabled =
            selectedPerson is not null && ViewModel.People.Count > 1 && !ViewModel.IsBusy;
        UnassignFacesButton.IsEnabled =
            selectedAssignedCount > 0 && !ViewModel.IsBusy;
        SetPersonPictureButton.IsEnabled =
            selectedAssignedCount == 1 && !ViewModel.IsBusy;
        PeopleUndoTagButton.IsEnabled =
            ViewModel.CanUndoLastTag && !ViewModel.IsBusy;
        DeletePersonButton.IsEnabled = selectedPerson is not null && !ViewModel.IsBusy;
        UnassignFacesButton.Label = selectedAssignedCount > 0
            ? $"Remove from person ({selectedAssignedCount})"
            : "Remove from person";

        var selectedHiddenCount = ExcludedFaceGrid.SelectedItems.Count;
        RestoreFacesButton.IsEnabled = selectedHiddenCount > 0 && !ViewModel.IsBusy;
        ExcludedUndoTagButton.IsEnabled =
            ViewModel.CanUndoLastTag && !ViewModel.IsBusy;
        RestoreFacesButton.Label = selectedHiddenCount > 0
            ? $"Show again ({selectedHiddenCount})"
            : "Show again";
    }

    private async void OnPersonTargetContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue ||
            args.Item is not PersonDropTargetViewModel target)
        {
            return;
        }

        try
        {
            await target.LoadProfilePictureAsync();
        }
        catch (Exception ex)
        {
            DebugLog.WriteLine(
                $"PeopleReviewView: Could not load picture for {target.Name}: {ex.Message}");
        }
    }

    private async void OnFaceContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue ||
            args.Item is not FaceSuggestionItemViewModel suggestion)
        {
            return;
        }

        try
        {
            await suggestion.LoadThumbnailAsync();
        }
        catch (Exception ex)
        {
            DebugLog.WriteLine(
                $"PeopleReviewView: Could not load {suggestion.FileName}: {ex.Message}");
        }
    }

    private async void OnSuggestionPointerEntered(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement
            {
                DataContext: FaceSuggestionItemViewModel suggestion
            })
        {
            return;
        }

        await ShowContextPreviewAsync(suggestion);
    }

    private async void OnConfirmClick(object sender, RoutedEventArgs e) =>
        await RunGridActionAsync(
            SuggestionGrid,
            () => ViewModel.ConfirmAsync(GetReviewActionFaces()));

    private async void OnRejectClick(object sender, RoutedEventArgs e) =>
        await RunGridActionAsync(
            SuggestionGrid,
            () => ViewModel.RejectAsync(GetReviewActionFaces()));

    private async void OnHideFacesClick(object sender, RoutedEventArgs e) =>
        await RunGridActionAsync(
            SuggestionGrid,
            () => ViewModel.HideFacesAsync(GetReviewActionFaces()));

    private void OnSuggestionDragItemsStarting(
        object sender,
        DragItemsStartingEventArgs e)
    {
        _draggedFaces = e.Items
            .OfType<FaceSuggestionItemViewModel>()
            .ToList();
        _dragSourceGrid = sender as GridView;
        _dragSourcePersonId = ReferenceEquals(_dragSourceGrid, PersonFaceGrid)
            ? ViewModel.SelectedPerson?.Id
            : null;
        if (_draggedFaces.Count == 0)
        {
            e.Cancel = true;
            ClearDragState();
            return;
        }

        e.Data.RequestedOperation = DataPackageOperation.Move;
        e.Data.SetText(string.Join(
            ",",
            _draggedFaces.Select(face => face.FaceRegionId)));
    }

    private void OnSuggestionDragItemsCompleted(
        ListViewBase sender,
        DragItemsCompletedEventArgs args)
    {
        ClearDragState();
    }

    private void OnPersonTargetDragOver(object sender, DragEventArgs e)
    {
        if (ViewModel.IsBusy ||
            _draggedFaces.Count == 0 ||
            sender is not FrameworkElement
            {
                DataContext: PersonDropTargetViewModel target
            } ||
            _dragSourcePersonId == target.Id)
        {
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.Caption =
            $"Tag {_draggedFaces.Count} face{(_draggedFaces.Count == 1 ? "" : "s")} as {target.Name}";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
    }

    private async void OnPersonTargetDrop(object sender, DragEventArgs e)
    {
        if (ViewModel.IsBusy ||
            _draggedFaces.Count == 0 ||
            sender is not FrameworkElement
            {
                DataContext: PersonDropTargetViewModel target
            })
        {
            return;
        }

        var faces = _draggedFaces.ToList();
        var sourceGrid = _dragSourceGrid;
        var sourcePersonId = _dragSourcePersonId;
        ClearDragState();
        if (sourceGrid is null || sourcePersonId == target.Id)
        {
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Move;
        await RunGridActionAsync(
            sourceGrid,
            () => sourcePersonId.HasValue
                ? ViewModel.ReassignFacesAsync(faces, target.Source)
                : ViewModel.TagAsAsync(faces, target.Source, null));
    }

    private void ClearDragState()
    {
        _draggedFaces = [];
        _dragSourceGrid = null;
        _dragSourcePersonId = null;
    }

    private async void OnTagAsClick(object sender, RoutedEventArgs e)
    {
        var selected = GetReviewActionFaces();
        if (selected.Count == 0) return;

        var people = await ViewModel.GetPeopleAsync();
        var existingPeople = new ComboBox
        {
            Header = "Existing person",
            PlaceholderText = "Choose a person",
            ItemsSource = people,
            DisplayMemberPath = nameof(Person.Name),
            MinWidth = 320
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
            existingPeople,
            "ExistingPersonPicker");
        var newName = new TextBox
        {
            Header = "Or add a new person",
            PlaceholderText = "Name",
            MinWidth = 320
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
            newName,
            "NewPersonName");
        var content = new StackPanel { Spacing = 12 };
        var recentPeople = ViewModel.RecentPeople
            .Select(recent => people.FirstOrDefault(person => person.Id == recent.Id) ?? recent)
            .ToList();
        if (recentPeople.Count > 0)
        {
            var recentLabel = new TextBlock
            {
                Text = "Recent",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"]
            };
            var recentRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var recentPerson in recentPeople)
            {
                var recentButton = new Button { Content = recentPerson.Name, Tag = recentPerson };
                recentButton.Click += (_, _) =>
                {
                    existingPeople.SelectedItem = recentPerson;
                    newName.Text = "";
                };
                recentRow.Children.Add(recentButton);
            }
            content.Children.Add(recentLabel);
            content.Children.Add(recentRow);
        }
        content.Children.Add(existingPeople);
        content.Children.Add(newName);

        var dialog = new ContentDialog
        {
            Title = "Tag selected faces",
            Content = content,
            PrimaryButtonText = "Tag",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            XamlRoot = XamlRoot
        };
        void UpdateDialogState() =>
            dialog.IsPrimaryButtonEnabled =
                existingPeople.SelectedItem is Person || !string.IsNullOrWhiteSpace(newName.Text);
        existingPeople.SelectionChanged += (_, _) =>
        {
            if (existingPeople.SelectedItem is Person) newName.Text = "";
            UpdateDialogState();
        };
        newName.TextChanged += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(newName.Text)) existingPeople.SelectedItem = null;
            UpdateDialogState();
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await RunGridActionAsync(
            SuggestionGrid,
            () => ViewModel.TagAsAsync(
                selected,
                existingPeople.SelectedItem as Person,
                newName.Text));
    }

    private async void OnHidePersonClick(object sender, RoutedEventArgs e)
    {
        var personName = ViewModel.SelectedGroup?.DisplayName;
        if (personName is null) return;

        var dialog = new ContentDialog
        {
            Title = $"Hide suggestions for {personName}?",
            Content = "This person will no longer appear in people review.",
            PrimaryButtonText = "Hide suggestions",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await RunGridActionAsync(
            SuggestionGrid,
            () => ViewModel.HideCurrentPersonAsync());
    }

    private async void OnRenamePersonClick(object sender, RoutedEventArgs e)
    {
        var person = ViewModel.SelectedPerson;
        if (person is null) return;

        var name = new TextBox
        {
            Header = "Name",
            Text = person.Name,
            MinWidth = 320,
            SelectionStart = 0,
            SelectionLength = person.Name.Length
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
            name,
            "RenamePersonName");
        var dialog = new ContentDialog
        {
            Title = $"Rename {person.Name}",
            Content = name,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(name.Text),
            XamlRoot = XamlRoot
        };
        name.TextChanged += (_, _) =>
            dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(name.Text);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await RunActionAsync(() => ViewModel.RenamePersonAsync(person, name.Text));
    }

    private async void OnMergePersonClick(object sender, RoutedEventArgs e)
    {
        var source = ViewModel.SelectedPerson;
        if (source is null) return;

        var targets = ViewModel.People
            .Where(person => person.Id != source.Id)
            .ToList();
        var targetPicker = new ComboBox
        {
            Header = $"Merge {source.Name} into",
            PlaceholderText = "Choose the person to keep",
            ItemsSource = targets,
            DisplayMemberPath = nameof(PersonManagementItemViewModel.Name),
            MinWidth = 320
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
            targetPicker,
            "MergePersonTarget");
        var dialog = new ContentDialog
        {
            Title = "Merge people",
            Content = targetPicker,
            PrimaryButtonText = "Merge",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
            XamlRoot = XamlRoot
        };
        targetPicker.SelectionChanged += (_, _) =>
            dialog.IsPrimaryButtonEnabled =
                targetPicker.SelectedItem is PersonManagementItemViewModel;

        if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
            targetPicker.SelectedItem is not PersonManagementItemViewModel target)
        {
            return;
        }
        await RunActionAsync(() => ViewModel.MergePersonAsync(source, target));
    }

    private async void OnUnassignFacesClick(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedFaces(PersonFaceGrid);
        if (selected.Count == 0) return;

        await RunGridActionAsync(
            PersonFaceGrid,
            () => ViewModel.UnassignFacesAsync(selected));
    }

    private async void OnSetPersonPictureClick(object sender, RoutedEventArgs e)
    {
        var person = ViewModel.SelectedPerson;
        var face = GetSelectedFaces(PersonFaceGrid).SingleOrDefault();
        if (person is null || face is null) return;

        await RunActionAsync(
            () => ViewModel.SetRepresentativeFaceAsync(person, face));
    }

    private void OnAssignedFaceRightTapped(
        object sender,
        RightTappedRoutedEventArgs e)
    {
        if (ViewModel.IsBusy ||
            ViewModel.SelectedPerson is null ||
            e.OriginalSource is not FrameworkElement element ||
            element.DataContext is not FaceSuggestionItemViewModel face)
        {
            return;
        }

        PersonFaceGrid.SelectedItem = face;
        var setPicture = new MenuFlyoutItem
        {
            Text = "Set as person's picture",
            Icon = new SymbolIcon(Symbol.Contact)
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
            setPicture,
            "SetPersonPictureMenuItem");
        setPicture.Click += async (_, _) =>
        {
            var person = ViewModel.SelectedPerson;
            if (person is not null)
            {
                await RunActionAsync(
                    () => ViewModel.SetRepresentativeFaceAsync(person, face));
            }
        };
        var menu = new MenuFlyout();
        menu.Items.Add(setPicture);
        menu.ShowAt(element, e.GetPosition(element));
        e.Handled = true;
    }

    private async void OnDeletePersonClick(object sender, RoutedEventArgs e)
    {
        var person = ViewModel.SelectedPerson;
        if (person is null) return;

        var dialog = new ContentDialog
        {
            Title = $"Delete {person.Name}?",
            Content =
                $"The person will be deleted and {person.Faces.Count} assigned faces " +
                "will return to face matching.",
            PrimaryButtonText = "Delete person",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await RunActionAsync(() => ViewModel.DeletePersonAsync(person));
    }

    private async void OnRestoreFacesClick(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedFaces(ExcludedFaceGrid);
        if (selected.Count == 0) return;

        await RunGridActionAsync(
            ExcludedFaceGrid,
            () => ViewModel.RestoreHiddenFacesAsync(selected));
    }

    private async void OnUndoTagClick(object sender, RoutedEventArgs e) =>
        await RunActionAsync(() => ViewModel.UndoLastTagAsync());

    private async void OnUndoTagInvoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!ViewModel.CanUndoLastTag || ViewModel.IsBusy)
        {
            return;
        }

        args.Handled = true;
        await RunActionAsync(() => ViewModel.UndoLastTagAsync());
    }

    private static List<FaceSuggestionItemViewModel> GetSelectedFaces(GridView grid) =>
        grid.SelectedItems.OfType<FaceSuggestionItemViewModel>().ToList();

    private List<FaceSuggestionItemViewModel> GetReviewActionFaces()
    {
        var selected = GetSelectedFaces(SuggestionGrid);
        return selected.Count > 0
            ? selected
            : ViewModel.SelectedGroup?.Suggestions.ToList() ?? [];
    }

    private GridView GetActiveFaceGrid() =>
        IsReviewMode
            ? SuggestionGrid
            : IsPeopleMode
                ? PersonFaceGrid
                : ExcludedFaceGrid;

    private void OnSelectAllInvoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox or RichEditBox)
        {
            return;
        }

        var grid = GetActiveFaceGrid();
        if (grid.Items.Count == 0) return;

        grid.SelectAll();
        grid.Focus(FocusState.Programmatic);
        args.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            ViewModel.Close();
            e.Handled = true;
        }
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        if (ViewModel.IsBusy) return;

        try
        {
            await action();
            RefreshDisplay();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("People couldn't complete that action", ex);
        }
    }

    private async Task RunGridActionAsync(
        GridView grid,
        Func<Task> action)
    {
        if (ViewModel.IsBusy) return;

        var selectedIndexes = grid.SelectedItems
            .OfType<FaceSuggestionItemViewModel>()
            .Select(item => grid.Items.IndexOf(item))
            .Where(index => index >= 0)
            .ToList();
        var restoreIndex = selectedIndexes.DefaultIfEmpty(
            Math.Max(0, grid.SelectedIndex)).Min();
        try
        {
            await action();
            RefreshDisplay();
            RestoreGridPosition(grid, restoreIndex);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("People couldn't complete that action", ex);
        }
    }

    private static void RestoreGridPosition(GridView grid, int preferredIndex)
    {
        if (grid.Items.Count == 0) return;

        var index = Math.Clamp(preferredIndex, 0, grid.Items.Count - 1);
        grid.SelectedIndex = index;
        grid.ScrollIntoView(grid.Items[index]);
        grid.Focus(FocusState.Programmatic);
    }

    private async Task ShowErrorAsync(string title, Exception exception)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = exception.Message,
            CloseButtonText = "Close",
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }

    private async Task ShowContextPreviewAsync(
        FaceSuggestionItemViewModel suggestion)
    {
        var request = Interlocked.Increment(ref _previewRequest);
        ContextPreviewImage.Source = null;
        ContextPreviewName.Text = suggestion.FileName;
        ContextPreviewPlaceholder.Text = "Loading photo...";
        ContextPreviewPlaceholder.Visibility = Visibility.Visible;
        ContextPreviewProgress.IsActive = true;
        try
        {
            await suggestion.LoadContextImageAsync();
            if (request != Volatile.Read(ref _previewRequest)) return;

            ContextPreviewImage.Source = suggestion.ContextImage;
            ContextPreviewPlaceholder.Visibility = suggestion.ContextImage is null
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            if (request != Volatile.Read(ref _previewRequest)) return;

            ContextPreviewPlaceholder.Text = "Preview unavailable";
            DebugLog.WriteLine(
                $"PeopleReviewView: Could not load context for {suggestion.FileName}: {ex.Message}");
        }
        finally
        {
            if (request == Volatile.Read(ref _previewRequest))
            {
                ContextPreviewProgress.IsActive = false;
            }
        }
    }

    private void ClearContextPreview()
    {
        Interlocked.Increment(ref _previewRequest);
        ContextPreviewImage.Source = null;
        ContextPreviewName.Text = "";
        ContextPreviewProgress.IsActive = false;
        ContextPreviewPlaceholder.Text = "Select or point to a face to preview its photo.";
        ContextPreviewPlaceholder.Visibility = Visibility.Visible;
    }
}
