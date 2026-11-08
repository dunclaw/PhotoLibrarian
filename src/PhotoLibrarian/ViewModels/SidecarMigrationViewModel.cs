// Copyright (c) Duncan Lawler. All Rights Reserved.
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using System.Collections.ObjectModel;

namespace PhotoLibrarian.ViewModels;

public partial class SidecarMigrationViewModel : ObservableObject
{
    private readonly SidecarMigrationService _service = new();
    private IReadOnlyList<SidecarMigrationItem> _preview = [];
    private CancellationTokenSource? _cancellation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMigrate))]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMigrate))]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    public partial bool HasPreview { get; set; }

    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    public partial bool Consent { get; set; }

    public ObservableCollection<string> Results { get; } = [];
    public bool CanMigrate => !IsBusy && HasPreview &&
        _preview.Any(item => item.State == SidecarMigrationState.Ready);
    public bool CanApply => CanMigrate && Consent;

    public async Task ScanAsync(string folder)
    {
        IsBusy = IsScanning = true;
        Status = Text("MigrationScanning");
        Results.Clear();
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        try
        {
            _preview = await _service.PreviewAsync(folder,
                new Progress<SidecarMigrationItem>(item => Results.Add(Describe(item))),
                cancellation.Token);
            HasPreview = true;
            Status = LibraryIndexMaintenanceStrings.Format("MigrationPreviewSummary",
                _preview.Count(item => item.State == SidecarMigrationState.Ready), _preview.Count);
        }
        catch (OperationCanceledException)
        {
            _preview = [];
            Status = Text("MigrationCanceled");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = LibraryIndexMaintenanceStrings.Format("MigrationScanFailed", ex.Message);
        }
        finally
        {
            _cancellation = null;
            IsScanning = IsBusy = false;
        }
    }

    public async Task MigrateAsync()
    {
        if (!CanMigrate) return;
        IsBusy = true;
        HasPreview = false;
        Results.Clear();
        Status = Text("MigrationRunning");
        Progress = 0;
        var completed = 0;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        try
        {
            var results = await _service.MigrateAsync(_preview,
                new Progress<SidecarMigrationItem>(item =>
                {
                    Results.Add(Describe(item));
                    Progress = 100d * ++completed / _preview.Count;
                }), cancellation.Token);
            Status = LibraryIndexMaintenanceStrings.Format("MigrationResultSummary",
                results.Count(item => item.State == SidecarMigrationState.Migrated),
                results.Count(item => item.State == SidecarMigrationState.Failed),
                results.Count(item => item.State == SidecarMigrationState.Skipped),
                results.Count(item => item.State == SidecarMigrationState.Canceled));
        }
        finally
        {
            _cancellation = null;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _cancellation?.Cancel();

    internal static string Text(string key) => LibraryIndexMaintenanceStrings.Get(key);

    private static string Describe(SidecarMigrationItem item) =>
        LibraryIndexMaintenanceStrings.Format("MigrationItem",
            item.SidecarPath, item.ImagePath ?? "", Text(item.Reason),
            item.BackupDirectory ?? "", item.Error ?? "");
}
