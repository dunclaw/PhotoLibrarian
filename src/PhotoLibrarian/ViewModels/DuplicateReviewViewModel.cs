// Copyright (c) Duncan Lawler. All Rights Reserved.
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using System.Collections.ObjectModel;
using System.Globalization;

namespace PhotoLibrarian.ViewModels;

public partial class DuplicateReviewFileViewModel : ObservableObject
{
    public DuplicateFileSnapshot Snapshot { get; }
    public string FileName => Snapshot.Entry.FileName;
    public string FilePath => Snapshot.Entry.FilePath;
    public string FileDetails => DuplicateReviewStrings.Format(
        "DuplicateFileDetails",
        Snapshot.FileSize,
        Snapshot.Entry.Width,
        Snapshot.Entry.Height,
        Snapshot.LastWriteTimeUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
    public string KeepAutomationName =>
        DuplicateReviewStrings.Format("DuplicateKeepAutomationName", FileName);

    [ObservableProperty]
    public partial bool IsKeeper { get; set; }

    public DuplicateReviewFileViewModel(DuplicateFileSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    public Task<byte[]?> LoadThumbnailAsync(CancellationToken cancellationToken) =>
        WindowsThumbnailService.GetThumbnailStreamAsync(
            FilePath,
            160,
            cancellationToken);
}

public sealed class DuplicateReviewGroupViewModel
{
    public DuplicateCandidateGroup Source { get; }
    public ObservableCollection<DuplicateReviewFileViewModel> Files { get; }
    public string Header { get; }
    public string Summary { get; }
    public string KindText => Source.Kind == DuplicateMatchKind.Exact
        ? DuplicateReviewStrings.Get("DuplicateExactKind")
        : DuplicateReviewStrings.Format(
            "DuplicateNearKind",
            Source.HammingDistance ?? 0);

    public DuplicateReviewFileViewModel? Keeper =>
        Files.FirstOrDefault(file => file.IsKeeper);

    public DuplicateReviewGroupViewModel(DuplicateCandidateGroup source)
    {
        Source = source;
        Files = new ObservableCollection<DuplicateReviewFileViewModel>(
            source.Files.Select(file => new DuplicateReviewFileViewModel(file)));
        Header = source.Kind == DuplicateMatchKind.Exact
            ? DuplicateReviewStrings.Format("DuplicateExactHeader", Files.Count)
            : DuplicateReviewStrings.Get("DuplicateNearHeader");
        Summary = source.Kind == DuplicateMatchKind.Exact
            ? string.Join(CultureInfo.CurrentCulture.TextInfo.ListSeparator + " ",
                Files.Select(file => file.FileName))
            : DuplicateReviewStrings.Format(
                "DuplicateNearSummary",
                Files[0].FileName,
                Files[1].FileName);
    }

    public void SetKeeper(DuplicateReviewFileViewModel keeper)
    {
        foreach (var file in Files)
            file.IsKeeper = ReferenceEquals(file, keeper);
    }
}

public partial class DuplicateReviewViewModel : ObservableObject
{
    private readonly DuplicateDetectionService _service;
    private readonly Services.PhotoOperationsService _photoOperations;
    private CancellationTokenSource? _scanCancellation;
    private IReadOnlySet<string> _seedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public Func<Task>? BeforeScanAsync { get; set; }
    public Action? AfterScan { get; set; }

    public ObservableCollection<DuplicateReviewGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    public partial DuplicateReviewGroupViewModel? SelectedGroup { get; set; }

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial int ProgressValue { get; set; }

    [ObservableProperty]
    public partial int ProgressMaximum { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } =
        DuplicateReviewStrings.Get("DuplicateInitialStatus");

    [ObservableProperty]
    public partial string DiagnosticsMessage { get; set; } = "";

    [ObservableProperty]
    public partial bool HasDiagnostics { get; set; }

    [ObservableProperty]
    public partial bool CanRemoveSelectedGroup { get; set; }

    public DuplicateReviewViewModel(
        DuplicateDetectionService service,
        Services.PhotoOperationsService photoOperations)
    {
        _service = service;
        _photoOperations = photoOperations;
    }

    public async Task OpenForImagesAsync(IReadOnlyList<ImageEntry> images)
    {
        if (IsBusy)
        {
            IsOpen = true;
            return;
        }

        _seedPaths = images
            .Where(image => image.MediaType == MediaType.Image)
            .Select(image => image.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        IsOpen = true;
        await ScanAsync();
    }

    [RelayCommand]
    public async Task ScanAsync()
    {
        if (IsBusy)
            return;

        _scanCancellation = new CancellationTokenSource();
        var cancellationToken = _scanCancellation.Token;
        IsBusy = true;
        ProgressValue = 0;
        ProgressMaximum = 0;
        StatusMessage = DuplicateReviewStrings.Get("DuplicateScanningStatus");
        DiagnosticsMessage = "";
        HasDiagnostics = false;
        CanRemoveSelectedGroup = false;

        try
        {
            if (BeforeScanAsync is not null)
                await BeforeScanAsync();
            var progress = new Progress<DuplicateScanProgress>(UpdateProgress);
            var result = await Task.Run(
                () => _service.ScanAsync(progress, cancellationToken, _seedPaths),
                cancellationToken);
            SelectedGroup = null;
            Groups.Clear();
            var added = 0;
            foreach (var group in result.Groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Groups.Add(new DuplicateReviewGroupViewModel(group));
                if (++added % 128 == 0)
                    await Task.Yield();
            }
            SelectedGroup = Groups.FirstOrDefault();

            var exactCount = result.Groups.Count(group => group.Kind == DuplicateMatchKind.Exact);
            var nearCount = result.Groups.Count - exactCount;
            StatusMessage = result.Groups.Count == 0
                ? DuplicateReviewStrings.Get("DuplicateNoCandidates")
                : DuplicateReviewStrings.Format(
                    "DuplicateScanSummary",
                    exactCount,
                    nearCount);
            if (result.NearMatchLimitReached)
                StatusMessage += " " + DuplicateReviewStrings.Get("DuplicateLimitWarning");

            if (result.DiagnosticCount > 0)
            {
                var sampleCount = Math.Min(10, result.Diagnostics.Count);
                var samples = result.Diagnostics
                    .Take(sampleCount)
                    .Select(diagnostic => DuplicateReviewStrings.Format(
                        "DuplicateDiagnosticItem",
                        Path.GetFileName(diagnostic.FilePath),
                        diagnostic.Message));
                var additionalCount = result.DiagnosticCount - sampleCount;
                var additional = additionalCount > 0
                    ? Environment.NewLine + DuplicateReviewStrings.Format(
                        "DuplicateAdditionalDiagnostics",
                        additionalCount)
                    : "";
                DiagnosticsMessage =
                    DuplicateReviewStrings.Format(
                        "DuplicateDiagnosticsSummary",
                        result.DiagnosticCount,
                        string.Join(Environment.NewLine, samples)) + additional;
                HasDiagnostics = true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = DuplicateReviewStrings.Get("DuplicateScanCanceled");
        }
        catch (Exception ex)
        {
            StatusMessage = DuplicateReviewStrings.Format("DuplicateScanFailed", ex.Message);
        }
        finally
        {
            _scanCancellation?.Dispose();
            _scanCancellation = null;
            IsBusy = false;
            AfterScan?.Invoke();
        }
    }

    [RelayCommand]
    public void CancelScan() => _scanCancellation?.Cancel();

    [RelayCommand]
    public void Close()
    {
        _scanCancellation?.Cancel();
        IsOpen = false;
    }

    public void SetKeeper(DuplicateReviewFileViewModel file)
    {
        SelectedGroup?.SetKeeper(file);
        CanRemoveSelectedGroup = SelectedGroup?.Keeper is not null && !IsBusy;
    }

    public async Task RemoveNonKeeperFilesAsync(
        CancellationToken cancellationToken = default)
    {
        var group = SelectedGroup
            ?? throw new InvalidOperationException(
                DuplicateReviewStrings.Get("DuplicateSelectionRequired"));
        var keeper = group.Keeper
            ?? throw new InvalidOperationException(
                DuplicateReviewStrings.Get("DuplicateKeeperRequired"));
        var filesToRemove = group.Files.Where(file => !ReferenceEquals(file, keeper)).ToArray();
        IsBusy = true;
        CanRemoveSelectedGroup = false;

        try
        {
            var result = await _photoOperations.DeleteDuplicateCandidatesToRecycleBinAsync(
                keeper.Snapshot,
                filesToRemove.Select(file => file.Snapshot).ToArray(),
                cancellationToken);

            foreach (var recycled in result.RecycledFiles)
            {
                var file = filesToRemove.FirstOrDefault(candidate =>
                    string.Equals(
                        candidate.FilePath,
                        recycled.Entry.FilePath,
                        StringComparison.OrdinalIgnoreCase));
                if (file is not null)
                    group.Files.Remove(file);
            }

            if (result.WasCanceled)
            {
                StatusMessage = DuplicateReviewStrings.Format(
                    "DuplicateRecycleInterrupted",
                    result.RecycledFiles.Count,
                    filesToRemove.Length);
            }
            else if (group.Files.Count <= 1)
            {
                Groups.Remove(group);
                SelectedGroup = Groups.FirstOrDefault();
                StatusMessage = DuplicateReviewStrings.Format(
                    "DuplicateRecycleComplete",
                    result.RecycledFiles.Count);
            }
            else
            {
                StatusMessage = DuplicateReviewStrings.Format(
                    "DuplicateRecyclePartial",
                    result.RecycledFiles.Count,
                    filesToRemove.Length);
            }

            if (result.Failures.Count > 0)
            {
                DiagnosticsMessage = string.Join(
                    Environment.NewLine,
                    result.Failures.Select(failure =>
                        DuplicateReviewStrings.Format(
                            "DuplicateRecycleErrorItem",
                            Path.GetFileName(failure.FilePath),
                            failure.ResourceKey is not null
                                ? DuplicateReviewStrings.Get(failure.ResourceKey)
                                : failure.Message ?? "")));
                HasDiagnostics = true;
            }
        }
        catch (DuplicateCandidateValidationException ex)
        {
            StatusMessage = DuplicateReviewStrings.GetExceptionMessage(ex);
            return;
        }
        finally
        {
            IsBusy = false;
            CanRemoveSelectedGroup = SelectedGroup?.Keeper is not null;
        }
    }

    private void UpdateProgress(DuplicateScanProgress progress)
    {
        if (!IsBusy)
            return;
        ProgressMaximum = progress.TotalFiles;
        ProgressValue = progress.CompletedFiles;
        StatusMessage =
            progress.TotalFiles == 0
                ? DuplicateReviewStrings.Get($"DuplicateStage{progress.Stage}")
                : DuplicateReviewStrings.Format(
                "DuplicateProgressStatus",
                DuplicateReviewStrings.Get($"DuplicateStage{progress.Stage}"),
                progress.CompletedFiles,
                progress.TotalFiles,
                Path.GetFileName(progress.FilePath));
    }

    partial void OnSelectedGroupChanged(DuplicateReviewGroupViewModel? value)
    {
        CanRemoveSelectedGroup = value?.Keeper is not null && !IsBusy;
    }
}
