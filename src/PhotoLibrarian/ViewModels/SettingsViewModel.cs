// Copyright (c) Duncan Lawler. All Rights Reserved.

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.ML.Services;
using PhotoLibrarian.Services;
using System.Collections.ObjectModel;

namespace PhotoLibrarian.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly CacheDatabase _db;
    private readonly AutoTaggingSettingsStore _autoTaggingSettingsStore;
    private readonly AutoTagModelManager _autoTagModelManager;
    private readonly AutoTagBenchmarkProcessor _benchmarkProcessor;
    private readonly LibraryIndexMaintenanceService _indexMaintenanceService;
    private Dictionary<string, AutoTagQualityStatus> _qualityStatuses = [];
    private bool _isInitializing;
    private CancellationTokenSource? _modelDownloadCancellation;
    private CancellationTokenSource? _indexMaintenanceCancellation;

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial int ThumbnailCacheSizeMB { get; set; }

    [ObservableProperty]
    public partial bool IsIndexMaintenanceRunning { get; set; }

    [ObservableProperty]
    public partial bool IsIndexMaintenanceProgressIndeterminate { get; set; }

    [ObservableProperty]
    public partial double IndexMaintenanceProgress { get; set; }

    [ObservableProperty]
    public partial string IndexMaintenanceStatus { get; set; } = "";

    [ObservableProperty]
    public partial string IndexMaintenanceDiagnostics { get; set; } = "";

    [ObservableProperty]
    public partial string CacheLocation { get; set; } = "";

    [ObservableProperty]
    public partial bool IsAutoTaggingEnabled { get; set; }

    [ObservableProperty]
    public partial string SelectedProfileId { get; set; } = "";

    [ObservableProperty]
    public partial int MaximumTags { get; set; }

    [ObservableProperty]
    public partial double ConfidencePercent { get; set; }

    [ObservableProperty]
    public partial double TinyClipOpenness { get; set; }

    [ObservableProperty]
    public partial string AutoTaggingStatus { get; set; } = "";

    [ObservableProperty]
    public partial bool IsAutoTaggingRunning { get; set; }

    [ObservableProperty]
    public partial int AutoTaggingProcessed { get; set; }

    [ObservableProperty]
    public partial int AutoTaggingTotal { get; set; }

    [ObservableProperty]
    public partial string ModelPurpose { get; set; } = "";

    [ObservableProperty]
    public partial string ModelDownloadSize { get; set; } = "";

    [ObservableProperty]
    public partial string ModelDownloadStatus { get; set; } = "";

    [ObservableProperty]
    public partial string ModelDirectory { get; set; } = "";

    [ObservableProperty]
    public partial string QualityStatus { get; set; } = "";

    [ObservableProperty]
    public partial bool IsSelectedProfileApproved { get; set; }

    [ObservableProperty]
    public partial bool IsBenchmarkRunning { get; set; }

    [ObservableProperty]
    public partial bool IsModelImportRunning { get; set; }

    [ObservableProperty]
    public partial bool IsModelDownloadRunning { get; set; }

    [ObservableProperty]
    public partial double ModelDownloadProgress { get; set; }

    [ObservableProperty]
    public partial string ModelDownloadMessage { get; set; } = "";

    [ObservableProperty]
    public partial bool IsAutoTagCleanupRunning { get; set; }

    [ObservableProperty]
    public partial string ModelImportMessage { get; set; } = "";

    [ObservableProperty]
    public partial bool ModelImportHasError { get; set; }

    [ObservableProperty]
    public partial bool HasBenchmarkResults { get; set; }

    [ObservableProperty]
    public partial string BenchmarkFolder { get; set; } = "";

    [ObservableProperty]
    public partial string BenchmarkSummary { get; set; } =
        "Choose a sample folder to evaluate up to 50 images. Results are not saved to your library.";

    public IReadOnlyList<AutoTagModelDefinition> Profiles =>
        AutoTagModelCatalog.Profiles;

    public AutoTagModelDefinition SelectedProfile =>
        AutoTagModelCatalog.ForId(SelectedProfileId);

    public ObservableCollection<string> BenchmarkExamples { get; } = [];

    public event EventHandler<AutoTaggingSettingsChangedEventArgs>?
        AutoTaggingSettingsChanged;

    public SettingsViewModel(
        CacheDatabase db,
        AutoTaggingSettingsStore autoTaggingSettingsStore,
        AutoTagModelManager autoTagModelManager,
        AutoTagBenchmarkProcessor benchmarkProcessor,
        LibraryIndexMaintenanceService indexMaintenanceService)
    {
        _db = db;
        _autoTaggingSettingsStore = autoTaggingSettingsStore;
        _autoTagModelManager = autoTagModelManager;
        _benchmarkProcessor = benchmarkProcessor;
        _indexMaintenanceService = indexMaintenanceService;

        _isInitializing = true;
        var settings = _autoTaggingSettingsStore.Load();
        _qualityStatuses =
            settings.ProfileQualityStatuses is null
                ? []
                : new Dictionary<string, AutoTagQualityStatus>(
                    settings.ProfileQualityStatuses,
                    StringComparer.Ordinal);
        IsAutoTaggingEnabled = settings.CanRun;
        SelectedProfileId = settings.ProfileId;
        MaximumTags = settings.MaximumTags;
        ConfidencePercent = settings.ConfidenceThreshold * 100;
        TinyClipOpenness =
            AutoTagModelCatalog.TinyClipConfidenceToOpenness(
                ConfidencePercent);
        ModelDirectory = _autoTagModelManager.ResolveRootDirectory(
            settings.ModelDirectory);
        AutoTaggingStatus = settings.CanRun
            ? "Waiting for the app to be idle"
            : "Automatic tagging is off";
        _isInitializing = false;
        RefreshSelectedProfileDetails();
        SetModelImportGuidance();
    }

    partial void OnTinyClipOpennessChanged(double value)
    {
        if (!_isInitializing &&
            SelectedProfile.Id == AutoTagModelCatalog.TinyClipProfileId)
        {
            ConfidencePercent =
                AutoTagModelCatalog.TinyClipOpennessToConfidence(value);
        }
    }

    public AutoTaggingSettings CurrentAutoTaggingSettings =>
        CreateSettings();

    [RelayCommand]
    private void Open()
    {
        IsOpen = true;
        LoadSettings();
        RefreshSelectedProfileDetails();
    }

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
    }

    private void LoadSettings()
    {
        try
        {
            var dbPath = GetDatabasePath();
            CacheLocation = dbPath;
            if (File.Exists(dbPath))
            {
                var info = new FileInfo(dbPath);
                ThumbnailCacheSizeMB =
                    (int)(info.Length / (1024 * 1024));
            }
        }
        catch
        {
        }
    }

    [RelayCommand]
    private async Task RepairLibraryIndexAsync()
    {
        if (IsIndexMaintenanceRunning)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _indexMaintenanceCancellation = cancellation;
        IsIndexMaintenanceRunning = true;
        IsIndexMaintenanceProgressIndeterminate = true;
        IndexMaintenanceProgress = 0;
        IndexMaintenanceDiagnostics = "";
        IndexMaintenanceStatus =
            LibraryIndexMaintenanceStrings.Get("IndexMaintenanceStarting");
        try
        {
            var progress = new Progress<LibraryIndexMaintenanceProgress>(
                update =>
                {
                    IsIndexMaintenanceProgressIndeterminate =
                        update.Stage is
                            LibraryIndexMaintenanceStage.ScanningFolders or
                            LibraryIndexMaintenanceStage.VerifyingMoves;
                    IndexMaintenanceProgress = update.Total > 0
                        ? 100d * update.Current / update.Total
                        : 0;
                    IndexMaintenanceStatus =
                        LibraryIndexMaintenanceStrings.Format(
                            "IndexMaintenanceProgress",
                            LibraryIndexMaintenanceStrings.GetStageName(
                                update.Stage),
                            update.Current,
                            update.Total,
                            update.Path);
                });
            var result = await Task.Run(
                () => _indexMaintenanceService.ReconcileAsync(
                    progress,
                    cancellation.Token));
            var diagnostics = result.Issues.Take(10).Select(
                LibraryIndexMaintenanceStrings.FormatIssue).ToList();
            if (result.Issues.Count > diagnostics.Count)
            {
                diagnostics.Add(
                    LibraryIndexMaintenanceStrings.Format(
                        "IndexMaintenanceAdditionalDiagnostics",
                        result.Issues.Count - diagnostics.Count));
            }
            IndexMaintenanceDiagnostics = string.Join(
                Environment.NewLine,
                diagnostics);

            var resultKey = result.Cancelled
                ? "IndexMaintenanceCancelled"
                : result.Succeeded
                    ? "IndexMaintenanceCompleted"
                    : "IndexMaintenanceCompletedWithIssues";
            IndexMaintenanceStatus =
                LibraryIndexMaintenanceStrings.Format(
                    resultKey,
                    result.UpdatedFiles,
                    result.RepairedMoves,
                    result.ConfirmedMissingFiles,
                    result.Issues.Count);
        }
        catch (Exception exception)
        {
            PhotoLibrarian.Core.Diagnostics.DebugLog.WriteLine(
                $"Library index maintenance failed: {exception}");
            IndexMaintenanceStatus =
                LibraryIndexMaintenanceStrings.Format(
                    "IndexMaintenanceFailed",
                    exception.Message);
        }
        finally
        {
            _indexMaintenanceCancellation = null;
            IsIndexMaintenanceRunning = false;
            IsIndexMaintenanceProgressIndeterminate = false;
            LoadSettings();
        }
    }

    [RelayCommand]
    private void CancelIndexMaintenance()
    {
        _indexMaintenanceCancellation?.Cancel();
    }

    public void SelectProfile(string profileId)
    {
        if (IsBenchmarkRunning ||
            IsModelImportRunning ||
            IsModelDownloadRunning ||
            profileId == SelectedProfileId ||
            !AutoTagModelCatalog.TryGet(profileId, out var definition))
        {
            return;
        }

        _isInitializing = true;
        SelectedProfileId = profileId;
        MaximumTags = definition.DefaultMaximumTags;
        ConfidencePercent =
            definition.DefaultConfidenceThreshold * 100;
        TinyClipOpenness =
            AutoTagModelCatalog.TinyClipConfidenceToOpenness(
                ConfidencePercent);
        IsAutoTaggingEnabled = false;
        AutoTaggingStatus = "Automatic tagging is off";
        HasBenchmarkResults = false;
        BenchmarkFolder = string.Empty;
        BenchmarkExamples.Clear();
        BenchmarkSummary =
            "Developer benchmark has not been run for this profile.";
        _isInitializing = false;
        RefreshSelectedProfileDetails();
        SetModelImportGuidance();
        SaveAutoTaggingSettings();
    }

    public void SetModelDirectory(string directory)
    {
        try
        {
            var preparedDirectory =
                _autoTagModelManager.PrepareCustomDirectory(directory);
            _isInitializing = true;
            ModelDirectory = preparedDirectory;
            _isInitializing = false;
            ModelDownloadStatus =
                "Location changed. Existing files were left in their previous location.";
            SaveAutoTaggingSettings();
            RefreshSelectedProfileDetails();
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
        {
            ModelDownloadStatus =
                $"The model folder could not be used: {exception.Message}";
        }
    }

    public void ResetModelDirectory()
    {
        _isInitializing = true;
        ModelDirectory = _autoTagModelManager.DefaultDirectory;
        _isInitializing = false;
        SaveAutoTaggingSettings();
        RefreshSelectedProfileDetails();
    }

    public async Task<bool> ImportSelectedModelAssetsAsync(
        string sourceDirectory,
        CancellationToken cancellationToken = default)
    {
        if (IsModelImportRunning ||
            IsModelDownloadRunning ||
            IsBenchmarkRunning)
        {
            return false;
        }

        IsModelImportRunning = true;
        ModelImportHasError = false;
        ModelImportMessage =
            $"Importing and verifying {SelectedProfile.DisplayName}…";
        try
        {
            await _autoTagModelManager.ImportProfileAssetsAsync(
                SelectedProfileId,
                sourceDirectory,
                GetCustomModelDirectory(),
                cancellationToken);
            RefreshSelectedProfileDetails();
            ModelImportMessage =
                $"{SelectedProfile.DisplayName} model and label assets were verified and copied " +
                "into managed storage.";
            return true;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            ModelImportHasError = true;
            ModelImportMessage = "Model import was canceled.";
            return false;
        }
        catch (Exception exception)
            when (exception is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                ArgumentException)
        {
            ModelImportHasError = true;
            ModelImportMessage = exception.Message;
            return false;
        }
        finally
        {
            IsModelImportRunning = false;
        }
    }

    public async Task<bool> DownloadSelectedModelAssetsAsync()
    {
        if (IsModelDownloadRunning ||
            IsModelImportRunning ||
            IsBenchmarkRunning)
        {
            return false;
        }

        using var cancellation = new CancellationTokenSource();
        _modelDownloadCancellation = cancellation;
        IsModelDownloadRunning = true;
        ModelImportHasError = false;
        ModelDownloadProgress = 0;
        ModelDownloadMessage =
            $"Preparing {SelectedProfile.DisplayName} download…";

        var progress = new Progress<AutoTagModelDownloadProgress>(
            UpdateModelDownloadProgress);
        try
        {
            await _autoTagModelManager.DownloadProfileAssetsAsync(
                SelectedProfileId,
                GetCustomModelDirectory(),
                progress,
                cancellation.Token);
            ModelDownloadProgress = 100;
            RefreshSelectedProfileDetails();
            ModelDownloadMessage =
                $"{SelectedProfile.DisplayName} assets were downloaded and SHA-256 verified.";
            return true;
        }
        catch (OperationCanceledException)
            when (cancellation.IsCancellationRequested)
        {
            RefreshSelectedProfileDetails();
            ModelDownloadMessage =
                "Download canceled. The partial file was kept so you can resume later.";
            return false;
        }
        catch (OperationCanceledException)
        {
            RefreshSelectedProfileDetails();
            ModelImportHasError = true;
            ModelDownloadMessage =
                "Model download timed out after two hours. The partial file was kept so you can retry.";
            ModelImportMessage = ModelDownloadMessage;
            return false;
        }
        catch (Exception exception)
            when (exception is HttpRequestException or
                IOException or
                InvalidDataException or
                InvalidOperationException or
                UnauthorizedAccessException or
                ArgumentException)
        {
            RefreshSelectedProfileDetails();
            ModelImportHasError = true;
            ModelDownloadMessage =
                $"Model download failed: {exception.Message} You can retry or import compatible files instead.";
            ModelImportMessage = ModelDownloadMessage;
            return false;
        }
        finally
        {
            _modelDownloadCancellation = null;
            IsModelDownloadRunning = false;
        }
    }

    public void CancelModelDownload() =>
        _modelDownloadCancellation?.Cancel();

    public async Task DeleteSelectedModelAssetsAsync()
    {
        try
        {
            await _autoTagModelManager.DeleteProfileAssetsAsync(
                SelectedProfileId,
                GetCustomModelDirectory());
            RefreshSelectedProfileDetails();
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            ModelDownloadStatus =
                $"Model files could not be deleted: {exception.Message}";
        }
    }

    public async Task DeleteAllModelAssetsAsync()
    {
        try
        {
            await _autoTagModelManager.DeleteAllProfileAssetsAsync(
                GetCustomModelDirectory());
            RefreshSelectedProfileDetails();
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            ModelDownloadStatus =
                $"Model files could not be deleted: {exception.Message}";
        }
    }

    public async Task RunBenchmarkAsync(
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        if (IsBenchmarkRunning ||
            IsModelImportRunning ||
            IsModelDownloadRunning)
        {
            return;
        }

        var definition = SelectedProfile;
        IsBenchmarkRunning = true;
        HasBenchmarkResults = false;
        BenchmarkFolder = folderPath;
        BenchmarkExamples.Clear();
        BenchmarkSummary = definition.RequiresLocalImport
            ? "Loading the imported model, then evaluating up to 50 images…"
            : "Downloading or loading the model, then evaluating up to 50 images…";
        try
        {
            var result = await _benchmarkProcessor.RunAsync(
                folderPath,
                CreateSettings() with { IsEnabled = false },
                50,
                cancellationToken);
            foreach (var example in result.Examples)
            {
                var value = example.Error is not null
                    ? $"{example.FileName} — failed: {example.Error}"
                    : example.Predictions.Count == 0
                        ? $"{example.FileName} — no safe labels"
                        : $"{example.FileName} — {string.Join(
                            ", ",
                            example.Predictions.Select(prediction =>
                                definition.OutputKind == AutoTagOutputKind.BinaryTagMask
                                    ? $"{prediction.Tag} (detected)"
                                    : definition.UsesCalibratedThresholds
                                        ? $"{prediction.Tag} ({prediction.Confidence:F3} cosine similarity)"
                                        : $"{prediction.Tag} ({prediction.Confidence:P0})"))}";
                BenchmarkExamples.Add(value);
            }

            BenchmarkSummary =
                $"{result.Processed:N0} processed, {result.Failed:N0} failed in {result.Elapsed.TotalSeconds:N1} seconds. Review the examples before approving.";
            HasBenchmarkResults = result.Processed > 0;
            RefreshSelectedProfileDetails();
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            BenchmarkSummary = "Benchmark canceled.";
        }
        catch (Exception exception)
        {
            BenchmarkSummary = $"Benchmark failed: {exception.Message}";
        }
        finally
        {
            IsBenchmarkRunning = false;
        }
    }

    public void ApproveSelectedProfile()
    {
        var definition =
            AutoTagModelCatalog.ForId(SelectedProfileId);
        if (!definition.CanBeApproved)
        {
            BenchmarkSummary =
                "This profile uses an unreviewed vocabulary and cannot be approved.";
            return;
        }

        SetQualityStatus(AutoTagQualityStatus.Approved);
        AutoTaggingStatus =
            "Profile approved. Waiting for the app to be idle.";
    }

    public void RejectSelectedProfile()
    {
        SetQualityStatus(AutoTagQualityStatus.Rejected);
        AutoTaggingStatus =
            "Profile rejected. Automatic tagging is off.";
    }

    partial void OnIsAutoTaggingEnabledChanged(bool value)
    {
        if (_isInitializing)
        {
            return;
        }

        if (value && !IsSelectedProfileApproved)
        {
            _isInitializing = true;
            IsAutoTaggingEnabled = false;
            _isInitializing = false;
            AutoTaggingStatus =
                "Trust the selected profile before enabling automatic tagging.";
            return;
        }

        SaveAutoTaggingSettings();
    }

    partial void OnMaximumTagsChanged(int value)
    {
        if (!_isInitializing)
        {
            InvalidateBenchmarkAndApproval(
                "Tag-count behavior changed. Review and trust this profile again to enable automatic tagging.");
        }
    }

    partial void OnConfidencePercentChanged(double value)
    {
        if (!_isInitializing && !SelectedProfile.UsesFixedThresholds)
        {
            InvalidateBenchmarkAndApproval(
                "Suggestion behavior changed. Review and trust this profile again to enable automatic tagging.");
        }
    }

    public void SetAutoTaggingProgress(
        string status,
        bool isRunning,
        int processed = 0,
        int total = 0)
    {
        AutoTaggingStatus = status;
        IsAutoTaggingRunning = isRunning;
        AutoTaggingProcessed = processed;
        AutoTaggingTotal = total;
    }

    private void SetQualityStatus(AutoTagQualityStatus status)
    {
        var definition =
            AutoTagModelCatalog.ForId(SelectedProfileId);
        _qualityStatuses[definition.ApprovalKey] = status;
        _isInitializing = true;
        IsSelectedProfileApproved =
            status == AutoTagQualityStatus.Approved;
        IsAutoTaggingEnabled = IsSelectedProfileApproved;
        _isInitializing = false;
        QualityStatus = FormatQualityStatus(status);
        SaveAutoTaggingSettings();
    }

    private void RefreshSelectedProfileDetails()
    {
        var definition =
            AutoTagModelCatalog.ForId(SelectedProfileId);
        ModelPurpose = definition.Purpose;
        ModelDownloadSize =
            FormatByteSize(definition.DownloadSizeBytes);
        var qualityStatus = _qualityStatuses.GetValueOrDefault(
            definition.ApprovalKey,
            AutoTagQualityStatus.Untested);
        QualityStatus = FormatQualityStatus(qualityStatus);
        IsSelectedProfileApproved =
            qualityStatus == AutoTagQualityStatus.Approved;
        try
        {
            var downloadStatus =
                _autoTagModelManager.GetDownloadStatus(
                    SelectedProfileId,
                    GetCustomModelDirectory());
            ModelDownloadStatus = downloadStatus.State switch
            {
                AutoTagDownloadState.Downloaded
                    when definition.RequiresLocalImport =>
                    $"Imported ({FormatByteSize(downloadStatus.BytesOnDisk)} on disk)",
                AutoTagDownloadState.PartiallyDownloaded
                    when definition.RequiresLocalImport =>
                    $"Partially imported ({FormatByteSize(downloadStatus.BytesOnDisk)} on disk)",
                AutoTagDownloadState.NotDownloaded
                    when definition.RequiresLocalImport =>
                    "Not imported",
                AutoTagDownloadState.Downloaded =>
                    $"Downloaded ({FormatByteSize(downloadStatus.BytesOnDisk)} on disk)",
                AutoTagDownloadState.PartiallyDownloaded =>
                    $"Partially downloaded ({FormatByteSize(downloadStatus.BytesOnDisk)} on disk)",
                _ => "Not downloaded"
            };
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            ModelDownloadStatus =
                $"Model status is unavailable: {exception.Message}";
        }
    }

    private void SetModelImportGuidance()
    {
        ModelImportHasError = false;
        ModelDownloadMessage = string.Empty;
        ModelDownloadProgress = 0;
        var profile = SelectedProfile;
        if (profile.ManifestUri is not null)
        {
            ModelImportMessage =
                $"Download the verified {profile.DisplayName} bundle or select a folder containing " +
                $"'{profile.ModelAsset.FileName}' and '{profile.LabelsAsset.FileName}'. " +
                "Imported source files are verified, copied, and never modified.";
        }
        else if (profile.RequiresLocalImport)
        {
            ModelImportMessage =
                $"{profile.DisplayName} is not distributed by PhotoLibrarian yet. Select a folder " +
                $"containing '{profile.ModelAsset.FileName}' and " +
                $"'{profile.LabelsAsset.FileName}'. Source files are verified, copied, and never modified.";
        }
        else
        {
            ModelImportMessage = string.Empty;
        }
    }

    private void UpdateModelDownloadProgress(
        AutoTagModelDownloadProgress progress)
    {
        if (progress.TotalBytes > 0)
        {
            ModelDownloadProgress = Math.Clamp(
                100d * progress.BytesDownloaded / progress.TotalBytes,
                0,
                100);
        }

        ModelDownloadMessage =
            $"Downloading {progress.FileName}: " +
            $"{FormatByteSize(progress.BytesDownloaded)} of " +
            $"{FormatByteSize(progress.TotalBytes)}";
    }

    private AutoTaggingSettings CreateSettings() => new(
        IsAutoTaggingEnabled,
        SelectedProfileId,
        MaximumTags,
        SelectedProfile.UsesFixedThresholds
            ? SelectedProfile.DefaultConfidenceThreshold
            : (float)(ConfidencePercent / 100),
        GetCustomModelDirectory(),
        new Dictionary<string, AutoTagQualityStatus>(
            _qualityStatuses,
            StringComparer.Ordinal));

    private void InvalidateBenchmarkAndApproval(string message)
    {
        var definition =
            AutoTagModelCatalog.ForId(SelectedProfileId);
        _qualityStatuses[definition.ApprovalKey] =
            AutoTagQualityStatus.Untested;
        _isInitializing = true;
        IsSelectedProfileApproved = false;
        IsAutoTaggingEnabled = false;
        _isInitializing = false;
        HasBenchmarkResults = false;
        BenchmarkExamples.Clear();
        BenchmarkSummary = message;
        QualityStatus = FormatQualityStatus(
            AutoTagQualityStatus.Untested);
        AutoTaggingStatus = "Automatic tagging is off";
        SaveAutoTaggingSettings();
    }

    private string? GetCustomModelDirectory() =>
        Path.GetFullPath(ModelDirectory)
            .Equals(
                Path.GetFullPath(_autoTagModelManager.DefaultDirectory),
                StringComparison.OrdinalIgnoreCase)
            ? null
            : ModelDirectory;

    private void SaveAutoTaggingSettings()
    {
        if (_isInitializing)
        {
            return;
        }

        var settings = CreateSettings();
        try
        {
            _autoTaggingSettingsStore.Save(settings);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            AutoTaggingStatus =
                $"The setting could not be saved: {exception.Message}";
        }

        AutoTaggingSettingsChanged?.Invoke(
            this,
            new AutoTaggingSettingsChangedEventArgs(settings));
    }

    private static string GetDatabasePath()
    {
        var appData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(
            appData,
            "PhotoLibrarian",
            "cache.db");
    }

    private static string FormatQualityStatus(
        AutoTagQualityStatus status) =>
        status switch
        {
            AutoTagQualityStatus.Approved =>
                "Approved for automatic tagging",
            AutoTagQualityStatus.Rejected =>
                "Rejected — automatic tagging is blocked",
            _ => "Not trusted — review before enabling"
        };

    private static string FormatByteSize(long bytes) =>
        bytes >= 1024 * 1024
            ? $"{bytes / (1024d * 1024):N0} MB"
            : $"{bytes / 1024d:N0} KB";
}

public sealed record AutoTaggingSettingsChangedEventArgs(
    AutoTaggingSettings Settings);
