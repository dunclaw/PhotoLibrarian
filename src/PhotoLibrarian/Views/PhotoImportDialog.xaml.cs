// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using PhotoLibrarian.ViewModels;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace PhotoLibrarian.Views;

public sealed partial class PhotoImportDialog : ContentDialog
{
    private readonly MainViewModel _viewModel;
    private readonly ObservableCollection<PhotoImportListItem> _rows = [];
    private CancellationTokenSource? _operationCancellation;
    private bool _isBusy;
    private bool _hasScanned;
    private bool _finished;
    private bool _templateIsValid = true;

    public string DefaultNamingTemplate => "{name}";
    public PhotoImportResult? Outcome { get; private set; }
    public bool DeleteSourceRequested { get; private set; }

    public PhotoImportDialog(
        MainViewModel viewModel,
        string? sourceRoot = null,
        string? destinationRoot = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        Title = PhotoImportStrings.Get("PhotoImportDialogTitle");
        PrimaryButtonText = PhotoImportStrings.Get("PhotoImportStart");
        CloseButtonText = PhotoImportStrings.Get("PhotoImportCancel");
        DefaultButton = ContentDialogButton.Primary;
        PhotoList.ItemsSource = _rows;
        if (!string.IsNullOrWhiteSpace(sourceRoot))
            SourceFolderBox.Text = sourceRoot;
        if (!string.IsNullOrWhiteSpace(destinationRoot))
            DestinationFolderBox.Text = destinationRoot;
        UpdateSelectionStatus();
    }

    private async void OnBrowseSourceClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary
            };
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
                SourceFolderBox.Text = folder.Path;
        }
        catch (Exception exception) when (IsPickerFailure(exception))
        {
            SetStatus(PhotoImportStrings.Format("PhotoImportPickerFailed", exception.Message));
        }
    }

    private async void OnBrowseDestinationClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary
            };
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
                DestinationFolderBox.Text = folder.Path;
        }
        catch (Exception exception) when (IsPickerFailure(exception))
        {
            SetStatus(PhotoImportStrings.Format("PhotoImportPickerFailed", exception.Message));
        }
    }

    private async void OnScanClick(object sender, RoutedEventArgs e)
    {
        var sourcePath = SourceFolderBox.Text.Trim();
        if (sourcePath.Length == 0)
        {
            SetStatus(PhotoImportStrings.Get("PhotoImportNoSource"));
            return;
        }

        SetBusy(true);
        SetStatus(PhotoImportStrings.Get("PhotoImportScanning"));
        WarningInfoBar.IsOpen = false;
        _operationCancellation = new CancellationTokenSource();
        try
        {
            var scan = await Task.Run(
                () => _viewModel.PhotoImport.ScanSourceAsync(
                    sourcePath, _operationCancellation.Token),
                _operationCancellation.Token);
            _rows.Clear();
            for (var index = 0; index < scan.Candidates.Count; index++)
            {
                var row = new PhotoImportListItem(scan.Candidates[index], index + 1);
                row.UpdateDestinationPreview(
                    DestinationFolderBox.Text.Trim(),
                    NamingTemplateBox.Text);
                _rows.Add(row);
            }

            _hasScanned = true;
            PhotoList.SelectAll();
            if (scan.Warnings.Count > 0)
            {
                WarningInfoBar.Message = PhotoImportStrings.Format(
                    "PhotoImportScanWarnings",
                    scan.Warnings.Count,
                    string.Join(Environment.NewLine, scan.Warnings));
                WarningInfoBar.IsOpen = true;
            }

            SetStatus(scan.Candidates.Count == 0
                ? PhotoImportStrings.Get("PhotoImportScanEmpty")
                : PhotoImportStrings.Format("PhotoImportScanFound", scan.Candidates.Count));
            UpdateSelectionStatus();
        }
        catch (OperationCanceledException) when (
            _operationCancellation?.IsCancellationRequested == true)
        {
            SetStatus(PhotoImportStrings.Format("PhotoImportCanceledSummary", 0));
        }
        catch (Exception exception) when (IsScanFailure(exception))
        {
            _rows.Clear();
            _hasScanned = false;
            SetStatus(PhotoImportStrings.Format("PhotoImportScanFailed", exception.Message));
            UpdateSelectionStatus();
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            SetBusy(false);
        }
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e) => PhotoList.SelectAll();

    private void OnSelectNoneClick(object sender, RoutedEventArgs e) => PhotoList.SelectedItems.Clear();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateSelectionStatus();

    private void OnSourceFolderChanged(object sender, TextChangedEventArgs e)
    {
        if (_hasScanned)
        {
            _hasScanned = false;
            _rows.Clear();
        }
        UpdateSelectionStatus();
    }

    private void OnDestinationChanged(object sender, TextChangedEventArgs e) =>
        UpdateDestinationPreviews();

    private void OnNamingTemplateChanged(object sender, TextChangedEventArgs e) =>
        UpdateDestinationPreviews();

    private void UpdateDestinationPreviews()
    {
        if (NamingTemplateBox is null)
            return;

        _templateIsValid = true;
        foreach (var row in _rows)
        {
            try
            {
                row.UpdateDestinationPreview(
                    DestinationFolderBox.Text.Trim(),
                    NamingTemplateBox.Text);
            }
            catch (ArgumentException)
            {
                _templateIsValid = false;
                row.UpdateDestinationPreview(
                    PhotoImportStrings.Get("PhotoImportTemplateInvalid"));
            }
        }

        if (!_templateIsValid)
            SetStatus(PhotoImportStrings.Get("PhotoImportTemplateInvalid"));
        UpdateSelectionStatus();
    }

    private async void OnPrimaryButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        if (_finished)
            return;

        args.Cancel = true;
        if (_isBusy)
            return;
        if (!_hasScanned)
        {
            SetStatus(PhotoImportStrings.Get("PhotoImportNoScan"));
            return;
        }

        var selectedRows = PhotoList.SelectedItems
            .OfType<PhotoImportListItem>()
            .ToList();
        if (selectedRows.Count == 0)
        {
            SetStatus(PhotoImportStrings.Get("PhotoImportNoSelection"));
            return;
        }

        var destinationPath = DestinationFolderBox.Text.Trim();
        if (!Directory.Exists(destinationPath))
        {
            SetStatus(PhotoImportStrings.Get("PhotoImportNoDestination"));
            return;
        }

        if (!_templateIsValid)
        {
            SetStatus(PhotoImportStrings.Get("PhotoImportTemplateInvalid"));
            return;
        }

        SetBusy(true);
        _operationCancellation = new CancellationTokenSource();
        DeleteSourceRequested = DeleteSourceCheckBox.IsChecked == true;
        try
        {
            var progress = new Progress<PhotoImportProgress>(UpdateImportProgress);
            var result = await Task.Run(
                () => _viewModel.PhotoImport.ImportAsync(
                    selectedRows.Select(row => row.Candidate),
                    destinationPath,
                    NamingTemplateBox.Text,
                    ParseTags(ImportTagsBox.Text),
                    progress,
                    _operationCancellation.Token));
            Outcome = result;

            var resultBySource = result.Items.ToDictionary(
                item => item.SourcePath,
                StringComparer.OrdinalIgnoreCase);
            foreach (var row in selectedRows)
            {
                if (!resultBySource.TryGetValue(row.Candidate.SourcePath, out var item))
                    continue;
                row.SetResult(item.Succeeded
                    ? PhotoImportStrings.Format("PhotoImportItemSucceeded", item.DestinationPath)
                    : PhotoImportStrings.Format("PhotoImportItemFailed", item.Error));
            }

            var successfulPaths = result.SuccessfulItems
                .Where(item => item.DestinationPath is not null)
                .Select(item => item.DestinationPath!)
                .ToList();
            if (successfulPaths.Count > 0)
            {
                var refreshFailed = false;
                try
                {
                    await _viewModel.EnsureImportDestinationWatchedAsync(destinationPath);
                    await _viewModel.RefreshAfterPhotoImportAsync(successfulPaths);
                }
                catch (Exception exception) when (IsScanFailure(exception))
                {
                    refreshFailed = true;
                    SetStatus(PhotoImportStrings.Format(
                        "PhotoImportRefreshFailed", exception.Message));
                }
                if (!refreshFailed)
                    SetFinalStatus(result, successfulPaths.Count);
            }
            else
                SetFinalStatus(result, successfulPaths.Count);

            _finished = true;
            PrimaryButtonText = PhotoImportStrings.Get("PhotoImportDone");
            CloseButtonText = PhotoImportStrings.Get("PhotoImportClose");
        }
        catch (OperationCanceledException) when (
            _operationCancellation?.IsCancellationRequested == true)
        {
            Outcome = new PhotoImportResult([], true);
            _finished = true;
            SetStatus(PhotoImportStrings.Format("PhotoImportCanceledSummary", 0));
            PrimaryButtonText = PhotoImportStrings.Get("PhotoImportDone");
            CloseButtonText = PhotoImportStrings.Get("PhotoImportClose");
        }
        catch (Exception exception) when (IsScanFailure(exception))
        {
            SetStatus(PhotoImportStrings.Format("PhotoImportOperationFailed", exception.Message));
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            SetBusy(false);
        }
    }

    private void OnCloseButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        if (!_isBusy)
            return;

        args.Cancel = true;
        _operationCancellation?.Cancel();
        SetStatus(PhotoImportStrings.Get("PhotoImportCanceling"));
    }

    private void UpdateImportProgress(PhotoImportProgress progress)
    {
        if (progress.TotalBytes > 0)
        {
            ImportProgressBar.Value = Math.Clamp(
                progress.BytesCopied * 100d / progress.TotalBytes,
                0,
                100);
        }

        if (progress.Stage == PhotoImportStage.VerifyingDeletion)
        {
            SetStatus(PhotoImportStrings.Format(
                "PhotoImportProgressVerifying",
                progress.CurrentFileName,
                progress.FilesCompleted + 1,
                progress.FilesTotal));
        }
        else
        {
            SetStatus(PhotoImportStrings.Format(
                "PhotoImportProgress",
                progress.CurrentFileName,
                ImportProgressBar.Value,
                progress.FilesCompleted + 1,
                progress.FilesTotal));
        }
    }

    private void UpdateSelectionStatus()
    {
        if (SelectionText is null)
            return;

        SelectionText.Text = PhotoImportStrings.Format(
            "PhotoImportSelectionCount",
            PhotoList.SelectedItems.Count,
            _rows.Count);
        IsPrimaryButtonEnabled =
            !_isBusy && _hasScanned && PhotoList.SelectedItems.Count > 0 && _templateIsValid;
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        SourceFolderBox.IsEnabled = !busy;
        DestinationFolderBox.IsEnabled = !busy;
        BrowseSourceButton.IsEnabled = !busy;
        BrowseDestinationButton.IsEnabled = !busy;
        NamingTemplateBox.IsEnabled = !busy;
        ImportTagsBox.IsEnabled = !busy;
        DeleteSourceCheckBox.IsEnabled = !busy;
        ScanButton.IsEnabled = !busy;
        SelectAllButton.IsEnabled = !busy;
        SelectNoneButton.IsEnabled = !busy;
        PhotoList.IsEnabled = !busy;
        ImportProgressBar.IsIndeterminate = busy && !_hasScanned;
        if (!busy)
            ImportProgressBar.Value = 0;
        UpdateSelectionStatus();
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void SetFinalStatus(PhotoImportResult result, int successfulCount) =>
        SetStatus(result.Canceled
            ? PhotoImportStrings.Format("PhotoImportCanceledSummary", successfulCount)
            : PhotoImportStrings.Format(
                "PhotoImportSummary",
                successfulCount,
                result.Items.Count(item => !item.Succeeded)));

    private static IReadOnlyList<string> ParseTags(string value) =>
        value.Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(tag => tag.Trim())
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsPickerFailure(Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            ArgumentException or
            InvalidOperationException or
            NotSupportedException or
            System.Security.SecurityException or
            COMException;

    private static bool IsScanFailure(Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            ArgumentException or
            InvalidOperationException or
            NotSupportedException or
            System.Security.SecurityException or
            Microsoft.Data.Sqlite.SqliteException or
            System.Security.Cryptography.CryptographicException or
            XmpCore.XmpException;
}

public sealed class PhotoImportListItem : INotifyPropertyChanged
{
    private string _destinationPreview = "";
    private string _resultText = "";

    public PhotoImportCandidate Candidate { get; }
    public string FileDetails { get; }
    public string DateDetails { get; }
    public string DestinationPreview
    {
        get => _destinationPreview;
        private set
        {
            if (_destinationPreview == value)
                return;
            _destinationPreview = value;
            OnPropertyChanged();
        }
    }

    public string ResultText
    {
        get => _resultText;
        private set
        {
            if (_resultText == value)
                return;
            _resultText = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public PhotoImportListItem(PhotoImportCandidate candidate, int sequence)
    {
        Candidate = candidate;
        Sequence = sequence;
        FileDetails = PhotoImportStrings.Format(
            "PhotoImportListItemName",
            candidate.FileName,
            candidate.FileSize);
        DateDetails = candidate.UsesFileDateFallback
            ? PhotoImportStrings.Format("PhotoImportFileDate", candidate.DateTaken.ToString("g"))
            : PhotoImportStrings.Format("PhotoImportCaptureDate", candidate.DateTaken.ToString("g"));
    }

    public int Sequence { get; }

    public void UpdateDestinationPreview(string destinationRoot, string template)
    {
        var fileName = PhotoImportService.GetOutputFileName(Candidate, template, Sequence);
        var relativePath = Path.Combine(PhotoImportService.GetDateFolder(Candidate), fileName);
        DestinationPreview = PhotoImportStrings.Format(
            "PhotoImportDestinationPreview",
            string.IsNullOrWhiteSpace(destinationRoot)
                ? relativePath
                : Path.Combine(destinationRoot, relativePath));
    }

    public void UpdateDestinationPreview(string text) => DestinationPreview = text;

    public void SetResult(string text) => ResultText = text;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
