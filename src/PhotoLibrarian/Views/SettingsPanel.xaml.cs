using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using PhotoLibrarian.ML.Services;
using PhotoLibrarian.ViewModels;

namespace PhotoLibrarian.Views;

public sealed partial class SettingsPanel : UserControl
{
    private SettingsViewModel? ViewModel =>
        App.ViewModel?.Settings;
    private bool _isUpdatingDisplay;
    private bool _isBenchmarkVisible;

    public SettingsPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        ModelProfileCombo.ItemsSource = ViewModel.Profiles;
        BenchmarkResultsList.ItemsSource =
            ViewModel.BenchmarkExamples;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateDisplay();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(UpdateDisplay);

    private void UpdateDisplay()
    {
        if (ViewModel is null)
        {
            return;
        }

        _isUpdatingDisplay = true;
        try
        {
            var profile = ViewModel.SelectedProfile;
            var isProfileEditable =
                !ViewModel.IsBenchmarkRunning &&
                !ViewModel.IsModelImportRunning;
            var calibratedThresholdsVisibility =
                profile.UsesCalibratedThresholds
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            var binaryTagMaskVisibility =
                profile.OutputKind == AutoTagOutputKind.BinaryTagMask
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            var isTinyClip =
                profile.Id == AutoTagModelCatalog.TinyClipProfileId;
            CacheSizeText.Text =
                $"Cache size: {ViewModel.ThumbnailCacheSizeMB} MB";
            CachePathText.Text = ViewModel.CacheLocation;
            AutoTaggingToggle.IsOn =
                ViewModel.IsAutoTaggingEnabled;
            AutoTaggingToggle.IsEnabled =
                ViewModel.IsSelectedProfileApproved;
            ModelProfileCombo.SelectedItem =
                ViewModel.Profiles.First(candidate =>
                    candidate.Id == ViewModel.SelectedProfileId);
            ModelProfileCombo.IsEnabled = isProfileEditable;
            ModelPurposeText.Text = ViewModel.ModelPurpose;
            ModelSizeText.Text =
                $"Asset size: {ViewModel.ModelDownloadSize}";
            ModelDownloadStatusText.Text =
                ViewModel.ModelDownloadStatus;
            QualityStatusText.Text = ViewModel.QualityStatus;
            MaximumTagsNumberBox.Maximum =
                profile.MaximumSupportedTags;
            MaximumTagsNumberBox.Value =
                ViewModel.MaximumTags;
            MaximumTagsNumberBox.IsEnabled = isProfileEditable;
            ConfidenceNumberBox.Value =
                ViewModel.ConfidencePercent;
            ConfidenceNumberBox.IsEnabled =
                isProfileEditable && !profile.UsesFixedThresholds;
            ConfidenceNumberBox.Visibility =
                profile.UsesFixedThresholds || isTinyClip
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            TinyClipOpennessPanel.Visibility =
                isTinyClip ? Visibility.Visible : Visibility.Collapsed;
            TinyClipOpennessSlider.Value = ViewModel.TinyClipOpenness;
            TinyClipOpennessSlider.IsEnabled = isProfileEditable;
            ConfidenceNumberBox.Header = isTinyClip
                ? "Minimum cosine similarity (%)"
                : "Minimum confidence (%)";
            AutomationProperties.SetName(
                ConfidenceNumberBox,
                isTinyClip
                    ? "Minimum TinyCLIP cosine similarity percentage"
                    : "Minimum confidence percentage");
            TinyClipLimitationsText.Visibility =
                isTinyClip
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            TaggingBehaviorHelpText.Text =
                profile.UsesFixedThresholds
                    ? "Changing the maximum tag count clears trust. Review and trust the profile again to enable automatic tagging."
                    : "Changing either value clears trust. Review and trust the profile again to enable automatic tagging.";
            CalibratedThresholdsText.Visibility =
                calibratedThresholdsVisibility;
            BenchmarkScoreHelpText.Visibility = calibratedThresholdsVisibility;
            BinaryDetectionThresholdsText.Visibility =
                binaryTagMaskVisibility;
            BinaryDetectionScoreHelpText.Visibility =
                binaryTagMaskVisibility;
            ModelDirectoryText.Text = ViewModel.ModelDirectory;
            AutoTaggingStatusText.Text =
                ViewModel.AutoTaggingStatus;
            AutoTaggingProgress.Visibility =
                ViewModel.IsAutoTaggingRunning
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            AutoTaggingProgress.IsIndeterminate =
                ViewModel.IsAutoTaggingRunning &&
                ViewModel.AutoTaggingTotal == 0;
            if (ViewModel.AutoTaggingTotal > 0)
            {
                AutoTaggingProgress.Value =
                    100d * ViewModel.AutoTaggingProcessed /
                    ViewModel.AutoTaggingTotal;
            }

            RunBenchmarkButton.IsEnabled =
                !ViewModel.IsBenchmarkRunning &&
                !ViewModel.IsModelImportRunning;
            BenchmarkProgressRing.IsActive =
                ViewModel.IsBenchmarkRunning;
            BenchmarkProgressRing.Visibility =
                ViewModel.IsBenchmarkRunning
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            BenchmarkFolderText.Text =
                ViewModel.BenchmarkFolder;
            BenchmarkSummaryText.Text =
                ViewModel.BenchmarkSummary;
            ApproveProfileButton.IsEnabled =
                !ViewModel.IsBenchmarkRunning &&
                !ViewModel.IsModelImportRunning &&
                (!profile.RequiresLocalImport ||
                 ViewModel.ModelDownloadStatus.StartsWith(
                     "Imported", StringComparison.Ordinal));
            var requiresImport = profile.RequiresLocalImport;
            ImportModelAssetsButton.Content =
                $"Import {profile.DisplayName} assets";
            ImportModelAssetsButton.Visibility =
                requiresImport
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            ImportModelAssetsButton.IsEnabled =
                requiresImport &&
                !ViewModel.IsModelImportRunning &&
                !ViewModel.IsBenchmarkRunning &&
                !ViewModel.IsAutoTaggingRunning;
            ModelImportProgressRing.IsActive =
                ViewModel.IsModelImportRunning;
            ModelImportProgressRing.Visibility =
                ViewModel.IsModelImportRunning
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            ModelImportInfoBar.Visibility =
                requiresImport
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            ModelImportInfoBar.IsOpen = requiresImport;
            ModelImportInfoBar.Title =
                $"Local assets — {profile.DisplayName}";
            ModelImportInfoBar.Message =
                ViewModel.ModelImportMessage;
            ModelImportInfoBar.Severity =
                ViewModel.ModelImportHasError
                    ? InfoBarSeverity.Error
                    : InfoBarSeverity.Informational;
            ModelSetupLink.NavigateUri =
                AutoTagModelCatalog.ModelSetupInstructionsUri;
            ModelSourceLink.NavigateUri =
                AutoTagModelCatalog.SourceUriFor(profile.Id);
            DeleteSelectedModelButton.IsEnabled =
                !ViewModel.IsAutoTaggingRunning &&
                !ViewModel.IsBenchmarkRunning &&
                !ViewModel.IsModelImportRunning;
            DeleteAllModelsButton.IsEnabled =
                !ViewModel.IsAutoTaggingRunning &&
                !ViewModel.IsBenchmarkRunning &&
                !ViewModel.IsModelImportRunning;
            RemoveAutoTagsButton.IsEnabled =
                !ViewModel.IsAutoTagCleanupRunning &&
                !ViewModel.IsBenchmarkRunning &&
                !ViewModel.IsModelImportRunning;
        }
        finally
        {
            _isUpdatingDisplay = false;
        }
    }

    private async void OnRebuildCache(
        object sender,
        RoutedEventArgs e)
    {
        if (ViewModel?.RebuildCacheCommand.CanExecute(null) == true)
        {
            await ViewModel.RebuildCacheCommand.ExecuteAsync(null);
        }
    }

    private async void OnClose(object sender, RoutedEventArgs e)
    {
        await App.ViewModel.RefreshTagsTreeAsync();
        ViewModel?.CloseCommand.Execute(null);
    }

    private void OnAutoTaggingToggled(
        object sender,
        RoutedEventArgs e)
    {
        if (!_isUpdatingDisplay && ViewModel is not null)
        {
            ViewModel.IsAutoTaggingEnabled =
                AutoTaggingToggle.IsOn;
        }
    }

    private void OnModelProfileChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_isUpdatingDisplay &&
            ViewModel is not null &&
            ModelProfileCombo.SelectedItem is
                AutoTagModelDefinition profile)
        {
            ViewModel.SelectProfile(profile.Id);
        }
    }

    private void OnMaximumTagsChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args)
    {
        if (!_isUpdatingDisplay &&
            ViewModel is not null &&
            !ViewModel.IsBenchmarkRunning &&
            !ViewModel.IsModelImportRunning &&
            !double.IsNaN(args.NewValue))
        {
            ViewModel.MaximumTags = (int)args.NewValue;
        }
    }

    private void OnConfidenceChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args)
    {
        if (!_isUpdatingDisplay &&
            ViewModel is not null &&
            !ViewModel.IsBenchmarkRunning &&
            !ViewModel.IsModelImportRunning &&
            !ViewModel.SelectedProfile.UsesFixedThresholds &&
            !double.IsNaN(args.NewValue))
        {
            ViewModel.ConfidencePercent = args.NewValue;
        }
    }

    private void OnTinyClipOpennessChanged(
        object sender,
        RangeBaseValueChangedEventArgs args)
    {
        if (!_isUpdatingDisplay &&
            ViewModel is not null &&
            !ViewModel.IsBenchmarkRunning &&
            !ViewModel.IsModelImportRunning)
        {
            ViewModel.TinyClipOpenness = args.NewValue;
        }
    }

    private void OnToggleBenchmark(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        _isBenchmarkVisible = !_isBenchmarkVisible;
        BenchmarkSection.Visibility = _isBenchmarkVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        BenchmarkDivider.Visibility = BenchmarkSection.Visibility;
        args.Handled = true;
    }

    private async void OnChooseModelDirectory(
        object sender,
        RoutedEventArgs e)
    {
        var folder = await PickFolderAsync();
        if (folder is not null)
        {
            ViewModel?.SetModelDirectory(folder.Path);
        }
    }

    private void OnResetModelDirectory(
        object sender,
        RoutedEventArgs e)
    {
        ViewModel?.ResetModelDirectory();
    }

    private async void OnImportModelAssets(
        object sender,
        RoutedEventArgs e)
    {
        var folder = await PickFolderAsync(
            Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary);
        if (folder is not null && ViewModel is not null)
        {
            await ViewModel.ImportSelectedModelAssetsAsync(folder.Path);
        }
    }

    private async void OnDeleteSelectedModel(
        object sender,
        RoutedEventArgs e)
    {
        if (ViewModel is null ||
            !await ConfirmAsync(
                "Delete selected model?",
                "The selected profile's managed model and vocabulary files will be deleted. Imported source files are not changed.",
                "Delete"))
        {
            return;
        }

        await ViewModel.DeleteSelectedModelAssetsAsync();
    }

    private async void OnRemoveAutoTags(
        object sender,
        RoutedEventArgs e)
    {
        if (ViewModel is null ||
            !await ConfirmAsync(
                "Remove all automatic tags?",
                "Automatic tagging will be turned off and every generated Auto tag will be removed from the library. Manual, imported, and metadata tags will not be changed.",
                "Remove automatic tags"))
        {
            return;
        }

        await App.ViewModel.RemoveAllAutomaticTagsAsync();
    }

    private async void OnDeleteAllModels(
        object sender,
        RoutedEventArgs e)
    {
        if (ViewModel is null ||
            !await ConfirmAsync(
                "Delete all model downloads?",
                "All automatic-tagging model assets in the current model location will be deleted. Imported source files, other files, and previous model locations are not changed.",
                "Delete all"))
        {
            return;
        }

        await ViewModel.DeleteAllModelAssetsAsync();
    }

    private async void OnRunBenchmark(
        object sender,
        RoutedEventArgs e)
    {
        var folder = await PickFolderAsync();
        if (folder is not null && ViewModel is not null)
        {
            await ViewModel.RunBenchmarkAsync(folder.Path);
        }
    }

    private void OnApproveProfile(
        object sender,
        RoutedEventArgs e)
    {
        ViewModel?.ApproveSelectedProfile();
    }

    private static async Task<Windows.Storage.StorageFolder?> PickFolderAsync(
        Windows.Storage.Pickers.PickerLocationId startLocation =
            Windows.Storage.Pickers.PickerLocationId.PicturesLibrary)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = startLocation
        };
        picker.FileTypeFilter.Add("*");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(
            App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        return await picker.PickSingleFolderAsync();
    }

    private async Task<bool> ConfirmAsync(
        string title,
        string message,
        string primaryButtonText)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() ==
            ContentDialogResult.Primary;
    }
}
