// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoLibrarian.Services;
using PhotoLibrarian.ViewModels;
using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;

namespace PhotoLibrarian.Views;

public sealed partial class DuplicateReviewView : UserControl
{
    public Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    private readonly Dictionary<Image, CancellationTokenSource> _previewLoads = [];

    public DuplicateReviewViewModel ViewModel => App.ViewModel.DuplicateReview;

    public DuplicateReviewView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        AutomationProperties.SetName(
            ScanButton,
            DuplicateReviewStrings.Get("DuplicateScanButtonName"));
        AutomationProperties.SetName(
            CancelButton,
            DuplicateReviewStrings.Get("DuplicateCancelButtonName"));
        AutomationProperties.SetName(
            CloseButton,
            DuplicateReviewStrings.Get("DuplicateCloseButtonName"));
        ToolTipService.SetToolTip(CloseButton, DuplicateReviewStrings.Get("DuplicateCloseButtonName"));
        AutomationProperties.SetName(
            RecycleButton,
            DuplicateReviewStrings.Get("DuplicateRecycleButtonName"));
        RefreshDisplay();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        foreach (var cancellation in _previewLoads.Values)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
        _previewLoads.Clear();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewModel.SelectedGroup)
            or nameof(ViewModel.IsBusy)
            or nameof(ViewModel.ProgressValue)
            or nameof(ViewModel.ProgressMaximum)
            or nameof(ViewModel.CanRemoveSelectedGroup))
        {
            DispatcherQueue.TryEnqueue(RefreshDisplay);
        }
    }

    private void RefreshDisplay()
    {
        GroupList.SelectedItem = ViewModel.SelectedGroup;
        CandidateList.ItemsSource = ViewModel.SelectedGroup?.Files;
        MatchKindText.Text = ViewModel.SelectedGroup?.KindText ?? "";
        EmptyState.Visibility = ViewModel.Groups.Count == 0 && !ViewModel.IsBusy
            ? Visibility.Visible
            : Visibility.Collapsed;
        CandidateList.Visibility = ViewModel.SelectedGroup is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        CandidateList.IsEnabled = !ViewModel.IsBusy;
        ScanButton.IsEnabled = !ViewModel.IsBusy;
        CancelButton.IsEnabled = ViewModel.IsBusy;
        GroupList.IsEnabled = !ViewModel.IsBusy;
        RecycleButton.IsEnabled =
            ViewModel.CanRemoveSelectedGroup && !ViewModel.IsBusy;
        ScanProgress.Maximum = Math.Max(1, ViewModel.ProgressMaximum);
        ScanProgress.Value = Math.Min(ViewModel.ProgressValue, ScanProgress.Maximum);
        ScanProgress.IsIndeterminate =
            ViewModel.IsBusy && ViewModel.ProgressMaximum == 0;
    }

    private void OnGroupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SelectedGroup =
            GroupList.SelectedItem as DuplicateReviewGroupViewModel;
        RefreshDisplay();
    }

    private void OnKeepClicked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { DataContext: DuplicateReviewFileViewModel file })
            ViewModel.SetKeeper(file);
    }

    private async void OnRecycleClicked(object sender, RoutedEventArgs e)
    {
        var group = ViewModel.SelectedGroup;
        var keeper = group?.Keeper;
        if (group is null || keeper is null)
            return;

        var count = group.Files.Count - 1;
        var warning = group.Source.Kind == Core.Models.DuplicateMatchKind.Near
            ? DuplicateReviewStrings.Get("DuplicateNearRecycleWarning")
            : DuplicateReviewStrings.Get("DuplicateExactRecycleWarning");
        var dialog = new ContentDialog
        {
            Title = DuplicateReviewStrings.Get("DuplicateRecycleDialogTitle"),
            Content = DuplicateReviewStrings.Format(
                "DuplicateRecycleDialogContent",
                warning,
                keeper.FileName,
                count),
            PrimaryButtonText = DuplicateReviewStrings.Format(
                "DuplicateRecycleDialogPrimary",
                count),
            CloseButtonText = DuplicateReviewStrings.Get("DuplicateRecycleDialogCancel"),
            DefaultButton = ContentDialogButton.None,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        try
        {
            await ViewModel.RemoveNonKeeperFilesAsync();
            await App.ViewModel.ImageGrid.LoadImagesAsync();
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = DuplicateReviewStrings.Format(
                "DuplicateRecycleFailed",
                DuplicateReviewStrings.GetExceptionMessage(ex));
        }
    }

    private async void OnPreviewLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image image)
            await LoadPreviewAsync(image);
    }

    private async void OnPreviewDataContextChanged(
        FrameworkElement sender,
        DataContextChangedEventArgs args)
    {
        if (sender is Image image)
            await LoadPreviewAsync(image);
    }

    private async Task LoadPreviewAsync(Image image)
    {
        if (image.DataContext is not DuplicateReviewFileViewModel file)
            return;

        if (_previewLoads.Remove(image, out var previousLoad))
        {
            previousLoad.Cancel();
            previousLoad.Dispose();
        }

        var cancellation = new CancellationTokenSource();
        _previewLoads[image] = cancellation;
        try
        {
            var bytes = await file.LoadThumbnailAsync(cancellation.Token);
            if (bytes is null)
            {
                ViewModel.StatusMessage = DuplicateReviewStrings.Format(
                    "DuplicatePreviewFailed",
                    file.FileName,
                    DuplicateReviewStrings.Get("DuplicatePreviewMissing"));
                return;
            }
            if (cancellation.IsCancellationRequested ||
                !ReferenceEquals(image.DataContext, file))
                return;

            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (ReferenceEquals(image.DataContext, file))
                image.Source = bitmap;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = DuplicateReviewStrings.Format(
                "DuplicatePreviewFailed",
                file.FileName,
                ex.Message);
        }
        finally
        {
            if (_previewLoads.Remove(image, out var current) &&
                ReferenceEquals(current, cancellation))
                cancellation.Dispose();
        }
    }
}
