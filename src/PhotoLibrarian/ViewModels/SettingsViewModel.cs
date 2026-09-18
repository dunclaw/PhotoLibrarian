using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.ML.Services;
using System.Collections.ObjectModel;

namespace PhotoLibrarian.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly CacheDatabase _db;
    private readonly AutoTaggingSettingsStore _autoTaggingSettingsStore;
    private readonly AutoTagModelManager _autoTagModelManager;
    private readonly AutoTagBenchmarkProcessor _benchmarkProcessor;
    private Dictionary<string, AutoTagQualityStatus> _qualityStatuses = [];
    private bool _isInitializing;

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial int ThumbnailCacheSizeMB { get; set; }

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
        AutoTagBenchmarkProcessor benchmarkProcessor)
    {
        _db = db;
        _autoTaggingSettingsStore = autoTaggingSettingsStore;
        _autoTagModelManager = autoTagModelManager;
        _benchmarkProcessor = benchmarkProcessor;

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
    private async Task RebuildCacheAsync()
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM thumbnails";
        await cmd.ExecuteNonQueryAsync();

        using var vacuum = conn.CreateCommand();
        vacuum.CommandText = "VACUUM";
        await vacuum.ExecuteNonQueryAsync();

        LoadSettings();
    }

    public void SelectProfile(string profileId)
    {
        if (IsBenchmarkRunning ||
            IsModelImportRunning ||
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
        if (!SelectedProfile.RequiresLocalImport)
        {
            ModelImportHasError = true;
            ModelImportMessage =
                "The selected profile downloads its assets automatically.";
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
        ModelImportMessage = SelectedProfile.RequiresLocalImport
            ? $"{SelectedProfile.DisplayName} is not distributed by PhotoLibrarian. Select a folder " +
              $"containing '{SelectedProfile.ModelAsset.FileName}' and " +
              $"'{SelectedProfile.LabelsAsset.FileName}'. Source files are " +
              "verified, copied, and never modified."
            : string.Empty;
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
