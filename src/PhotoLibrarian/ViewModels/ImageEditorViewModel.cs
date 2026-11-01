// Copyright (c) Duncan Lawler. All Rights Reserved.
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;

namespace PhotoLibrarian.ViewModels;

public enum PendingEditDecision { Cancel, Save, Discard }

/// <summary>
/// ViewModel for the image editor with real-time adjustment sliders.
/// </summary>
public partial class ImageEditorViewModel : ObservableObject
{
    private readonly OriginalBackupService _backupService;
    private readonly ImageEditService _editService;
    private ImageEntry? _currentEntry;
    private string? _backupHash;
    private readonly List<HistoryEntry> _undoHistory = [];
    private readonly List<HistoryEntry> _redoHistory = [];
    private EditParameters _lastParameters = new();
    private EditParameters _baseline = new();
    private EditParameters? _editStart;
    private string _editLabel = "";
    private bool _restoring;
    private CropRect? _crop;
    private CancellationTokenSource? _rawExport;
    private List<ImageEntry> _images = [];
    private int _openVersion;

    public ImageEntry? CurrentEntry => _currentEntry;
    public event Action<ImageEntry?>? CurrentEntryChanged;
    public bool IsBusy => IsSaving || IsTransitioning;
    public bool CanNavigate => IsOpen && !IsBusy && FindNavigationTarget(1) is not null;

    [ObservableProperty]
    public partial bool IsTransitioning { get; set; }

    partial void OnIsTransitioningChanged(bool value) => NotifyHistoryChanged();

    public void UpdateLibraryImages(IEnumerable<ImageEntry> images)
    {
        _images = images.Where(entry => entry.MediaType == MediaType.Image &&
            (ImageEditRenderer.IsSupported(entry.FilePath) || RawToneMappingService.IsRaw(entry.FilePath))).ToList();
        OnPropertyChanged(nameof(CanNavigate));
    }

    private ImageEntry? FindNavigationTarget(int direction)
    {
        if (_currentEntry is null || _images.Count == 0) return null;
        var index = _images.FindIndex(entry =>
            string.Equals(entry.FilePath, _currentEntry.FilePath, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return direction > 0 ? _images[0] : _images[^1];
        if (_images.Count < 2) return null;
        return _images[(index + direction + _images.Count) % _images.Count];
    }

    public async Task<bool> NavigateAsync(int direction, Func<Task<PendingEditDecision>> confirm,
        ImageEditService.PixelRenderer renderer)
    {
        if (direction is not (1 or -1)) throw new ArgumentOutOfRangeException(nameof(direction));
        if (!CanNavigate) return false;
        IsTransitioning = true;
        try
        {
            EndEdit();
            var decision = HasChanges ? await confirm() : PendingEditDecision.Discard;
            if (decision == PendingEditDecision.Cancel) return false;
            // Resolve against the latest filtered order, not the order when the prompt opened.
            var target = FindNavigationTarget(direction);
            if (target is null) return false;
            Task SaveBeforeMovingAsync() => decision == PendingEditDecision.Save
                ? SavePendingEditsAsync(renderer) : Task.CompletedTask;
            bool IsTargetCurrent() => string.Equals(FindNavigationTarget(direction)?.FilePath,
                target.FilePath, StringComparison.OrdinalIgnoreCase);
            return await OpenEntryAsync(target, SaveBeforeMovingAsync, IsTargetCurrent);
        }
        finally { IsTransitioning = false; }
    }

    public async Task<bool> TryCloseAsync(Func<Task<PendingEditDecision>> confirm,
        ImageEditService.PixelRenderer renderer)
    {
        if (!IsOpen || IsBusy) return false;
        IsTransitioning = true;
        try
        {
            EndEdit();
            if (HasChanges)
            {
                var decision = await confirm();
                if (decision == PendingEditDecision.Cancel) return false;
                if (decision == PendingEditDecision.Save)
                    await SavePendingEditsAsync(renderer);
            }
            CloseCore();
            return true;
        }
        finally { IsTransitioning = false; }
    }

    private async Task SavePendingEditsAsync(ImageEditService.PixelRenderer renderer)
    {
        if (IsRawPreview)
        {
            await KeepRawPreviewRecipeAsync();
            return;
        }
        if (!await SaveAsync(renderer))
            throw new InvalidOperationException("No raster adjustments could be saved. Choose Discard to leave this photo without keeping the pending changes.");
    }

    private sealed record HistoryEntry(string Label, EditParameters Before, EditParameters After);

    public IReadOnlyList<string> UndoHistory => _undoHistory.AsEnumerable().Reverse().Select(x => x.Label).ToArray();
    public IReadOnlyList<string> RedoHistory => _redoHistory.AsEnumerable().Reverse().Select(x => x.Label).ToArray();
    public bool CanUndo => IsOpen && !IsBusy && _undoHistory.Count > 0;
    public bool CanRedo => IsOpen && !IsBusy && _redoHistory.Count > 0;

    partial void OnIsSavingChanged(bool value) => NotifyHistoryChanged();
    partial void OnIsOpenChanged(bool value) => NotifyHistoryChanged();

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string? ImagePath { get; set; }

    public bool IsRawPreview => ImagePath is not null && RawToneMappingService.IsRaw(ImagePath);
    public bool CanSavePixels => IsOpen && !IsBusy && !IsRawPreview;
    public bool CanExportRawDerivative => IsOpen && !IsBusy && IsRawPreview;
    public string SaveDisabledReason => IsRawPreview
        ? "RAW originals cannot be overwritten. Use Export SDR derivative to create a separate JPEG or PNG."
        : "";

    partial void OnImagePathChanged(string? value)
    {
        _rawExport?.Cancel();
        OnPropertyChanged(nameof(IsRawPreview));
        OnPropertyChanged(nameof(CanSavePixels));
        OnPropertyChanged(nameof(CanExportRawDerivative));
        OnPropertyChanged(nameof(SaveDisabledReason));
    }

    [ObservableProperty]
    public partial double RawExposureEv { get; set; }

    [ObservableProperty]
    public partial double RawWhitePoint { get; set; }

    // Tone
    [ObservableProperty]
    public partial double Brightness { get; set; }

    [ObservableProperty]
    public partial double Contrast { get; set; }

    [ObservableProperty]
    public partial double Exposure { get; set; }

    [ObservableProperty]
    public partial double Highlights { get; set; }

    [ObservableProperty]
    public partial double Shadows { get; set; }

    // Color
    [ObservableProperty]
    public partial double Saturation { get; set; }

    [ObservableProperty]
    public partial double Temperature { get; set; }

    [ObservableProperty]
    public partial double Tint { get; set; }

    // Detail
    [ObservableProperty]
    public partial double Clarity { get; set; }

    [ObservableProperty]
    public partial double Sharpness { get; set; }

    [ObservableProperty]
    public partial double NoiseReduction { get; set; }

    [ObservableProperty]
    public partial SharpeningMode SharpeningMode { get; set; }

    [ObservableProperty]
    public partial double UnsharpMaskAmount { get; set; }

    [ObservableProperty]
    public partial double UnsharpMaskRadius { get; set; }

    [ObservableProperty]
    public partial double UnsharpMaskThreshold { get; set; }

    // Levels
    [ObservableProperty]
    public partial double BlackPoint { get; set; }

    [ObservableProperty]
    public partial double WhitePoint { get; set; }

    [ObservableProperty]
    public partial double Midtones { get; set; }

    [ObservableProperty]
    public partial double RotationAngle { get; set; }

    [ObservableProperty]
    public partial bool HasBackup { get; set; }

    [ObservableProperty]
    public partial bool HasChanges { get; set; }

    [ObservableProperty]
    public partial bool IsSaving { get; set; }

    /// <summary>
    /// Raised when any parameter changes so the Win2D canvas can re-render.
    /// </summary>
    public event EventHandler? ParametersChanged;

    /// <summary>
    /// Raised after adjustments have been baked into the file on disk, so the rest of the app
    /// (grid thumbnail, viewer, database dimensions) can refresh.
    /// </summary>
    public event EventHandler<EditsAppliedEventArgs>? EditsApplied;

    /// <summary>Raised after the file has been restored from its backup.</summary>
    public event EventHandler<string>? Reverted;

    public ImageEditorViewModel(OriginalBackupService backupService)
    {
        _backupService = backupService;
        _editService = new ImageEditService(backupService);
        Title = "";
        WhitePoint = 1.0;
        RawWhitePoint = RawToneMappingService.DefaultWhitePoint;
        Midtones = 0.5;
        UnsharpMaskRadius = 1.0;
        UnsharpMaskThreshold = 0.02;
        ClearHistory();
    }

    public async Task OpenForEditAsync(ImageEntry entry)
    {
        if (IsTransitioning || (IsSaving && _rawExport is null))
            throw new InvalidOperationException("An editor operation is already in progress.");
        // A programmatic replacement must still cancel a non-cooperative RAW export.
        _rawExport?.Cancel();
        IsTransitioning = true;
        try { await OpenEntryAsync(entry); }
        finally { IsTransitioning = false; }
    }

    private async Task<bool> OpenEntryAsync(ImageEntry entry, Func<Task>? beforeCommit = null,
        Func<bool>? stillCurrent = null)
    {
        var version = ++_openVersion;
        var parameters = ImageEditService.ReadEditParameters(entry.FilePath);
        var isRaw = RawToneMappingService.IsRaw(entry.FilePath);
        if (isRaw) File.GetAttributes(entry.FilePath);
        // Complete fallible I/O before replacing the current photo or its unsaved history.
        var backupHash = isRaw ? null : await OriginalBackupService.ComputeFileHashAsync(entry.FilePath);
        var hasBackup = !isRaw && await _backupService.HasBackupAsync(entry.FilePath);
        if (version != _openVersion || stillCurrent?.Invoke() == false) return false;
        if (beforeCommit is not null) await beforeCommit();
        if (version != _openVersion || stillCurrent?.Invoke() == false) return false;

        _currentEntry = entry;
        _backupHash = backupHash;
        HasBackup = hasBackup;
        Title = entry.FileName;
        ImagePath = entry.FilePath;
        IsOpen = true;

        RestoreParameters(parameters);
        ClearHistory();

        OnPropertyChanged(nameof(CurrentEntry));
        CurrentEntryChanged?.Invoke(entry);
        return true;
    }

    /// <summary>
    /// Bakes the current adjustments into the image file on disk. The original is backed up
    /// first, then the sliders reset because the values now live in the pixels.
    /// Returns true when the file was written.
    /// </summary>
    public async Task<bool> SaveAsync(ImageEditService.PixelRenderer renderer)
    {
        if (IsRawPreview) throw new NotSupportedException(SaveDisabledReason);
        if (_currentEntry is null || IsSaving) return false;

        EndEdit();
        var parameters = GetCurrentParameters();
        if (!parameters.HasAdjustments) return false;

        IsSaving = true;
        try
        {
            var (width, height) = await _editService.ApplyEditsAsync(
                _currentEntry.FilePath, parameters, renderer);

            RestoreParameters(new EditParameters());
            ClearHistory();
            HasBackup = true;
            EditsApplied?.Invoke(this, new EditsAppliedEventArgs(_currentEntry.FilePath, width, height));
            return true;
        }
        finally
        {
            IsSaving = false;
        }
    }

    public EditParameters GetCurrentParameters() => new()
    {
        Brightness = Brightness,
        Contrast = Contrast,
        Exposure = Exposure,
        RawExposureEv = RawExposureEv,
        RawWhitePoint = RawWhitePoint,
        Highlights = Highlights,
        Shadows = Shadows,
        Saturation = Saturation,
        Temperature = Temperature,
        Tint = Tint,
        Clarity = Clarity,
        Sharpness = Sharpness,
        NoiseReduction = NoiseReduction,
        SharpeningMode = SharpeningMode,
        UnsharpMaskAmount = UnsharpMaskAmount,
        UnsharpMaskRadius = UnsharpMaskRadius,
        UnsharpMaskThreshold = UnsharpMaskThreshold,
        BlackPoint = BlackPoint,
        WhitePoint = WhitePoint,
        Midtones = Midtones,
        RotationAngle = RotationAngle,
        Crop = _crop?.Clone()
    };

    public async Task KeepRawPreviewRecipeAsync()
    {
        if (!IsRawPreview || _currentEntry is null)
            throw new InvalidOperationException("No RAW preview is open.");
        if (IsSaving) throw new InvalidOperationException("An editor operation is already in progress.");
        EndEdit();
        var parameters = GetCurrentParameters();
        IsSaving = true;
        try
        {
            await ImageEditService.SaveRawPreviewRecipeAsync(_currentEntry.FilePath, parameters);
            _baseline = parameters.Clone();
            HasChanges = false;
        }
        finally { IsSaving = false; }
    }

    public async Task<RawDerivativeResult> ExportRawDerivativeAsync(string folder, RawDerivativeFormat format,
        Func<string, EditParameters, CancellationToken, Task<RawSdrImage>> render,
        CancellationToken cancellationToken = default)
    {
        if (!CanExportRawDerivative || _currentEntry is null)
            throw new InvalidOperationException("Open a RAW photo and finish the current operation before exporting.");
        EndEdit();
        var entry = _currentEntry;
        var snapshot = GetCurrentParameters();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _rawExport = request;
        IsSaving = true;
        try
        {
            var result = await RawDerivativeExportService.ExportAsync(
                entry.FilePath, folder, format, snapshot, render, request.Token);
            if (ReferenceEquals(entry, _currentEntry))
            {
                _baseline = snapshot.Clone();
                HasChanges = !ParametersEqual(GetCurrentParameters(), _baseline);
            }
            return result;
        }
        finally
        {
            if (_rawExport == request) _rawExport = null;
            IsSaving = false;
        }
    }

    public void CancelRawExport() => _rawExport?.Cancel();

    [RelayCommand]
    private void ResetAll()
    {
        if (IsBusy) return;
        BeginEdit("Reset adjustments");
        var parameters = IsRawPreview ? GetCurrentParameters() : new EditParameters();
        parameters.RawExposureEv = 0;
        parameters.RawWhitePoint = RawToneMappingService.DefaultWhitePoint;
        ApplyParametersToSliders(parameters);
        EndEdit();
    }

    [RelayCommand]
    private async Task RevertToOriginalAsync()
    {
        if (IsRawPreview)
            throw new NotSupportedException("RAW originals are immutable in the preview workflow. Reset the preview recipe instead.");
        if (_currentEntry is null || IsBusy) return;
        EndEdit();
        IsSaving = true;
        try
        {
            var hash = _backupHash ?? await OriginalBackupService.ComputeFileHashAsync(_currentEntry.FilePath);
            if (await _backupService.RestoreOriginalAsync(_currentEntry.FilePath, hash))
            {
                RestoreParameters(new EditParameters());
                ClearHistory();
                Reverted?.Invoke(this, _currentEntry.FilePath);
            }
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private void Close()
    {
        if (IsBusy) return;
        CloseCore();
    }

    private void CloseCore()
    {
        _openVersion++;
        IsOpen = false;
        ImagePath = null;
        _currentEntry = null;
        ClearHistory();
        OnPropertyChanged(nameof(CurrentEntry));
        CurrentEntryChanged?.Invoke(null);
    }

    public void ApplyAutoEnhance(EditParameters adjustments)
    {
        ArgumentNullException.ThrowIfNull(adjustments);
        if (IsBusy) return;
        BeginEdit("Auto enhance");
        Exposure = adjustments.Exposure;
        Contrast = adjustments.Contrast;
        Highlights = adjustments.Highlights;
        Shadows = adjustments.Shadows;
        Saturation = adjustments.Saturation;
        Clarity = adjustments.Clarity;
        EndEdit();
    }

    private void ApplyParametersToSliders(EditParameters p)
    {
        var cropChanged = !CropEqual(_crop, p.Crop);
        _crop = p.Crop?.Clone();
        Brightness = p.Brightness;
        Contrast = p.Contrast;
        Exposure = p.Exposure;
        RawExposureEv = p.RawExposureEv;
        RawWhitePoint = p.RawWhitePoint;
        Highlights = p.Highlights;
        Shadows = p.Shadows;
        Saturation = p.Saturation;
        Temperature = p.Temperature;
        Tint = p.Tint;
        Clarity = p.Clarity;
        Sharpness = p.Sharpness;
        NoiseReduction = p.NoiseReduction;
        SharpeningMode = p.SharpeningMode;
        UnsharpMaskAmount = p.UnsharpMaskAmount;
        UnsharpMaskRadius = p.UnsharpMaskRadius;
        UnsharpMaskThreshold = p.UnsharpMaskThreshold;
        BlackPoint = p.BlackPoint;
        WhitePoint = p.WhitePoint;
        Midtones = p.Midtones;
        RotationAngle = p.RotationAngle;
        if (cropChanged) OnParameterChanged("Crop");
    }

    // Called when any slider value changes to trigger re-render
    partial void OnBrightnessChanged(double value) => OnParameterChanged("Brightness");
    partial void OnContrastChanged(double value) => OnParameterChanged("Contrast");
    partial void OnExposureChanged(double value) => OnParameterChanged("Exposure");
    partial void OnRawExposureEvChanged(double value) => OnParameterChanged("RAW exposure");
    partial void OnRawWhitePointChanged(double value) => OnParameterChanged("RAW white point");
    partial void OnHighlightsChanged(double value) => OnParameterChanged("Highlights");
    partial void OnShadowsChanged(double value) => OnParameterChanged("Shadows");
    partial void OnSaturationChanged(double value) => OnParameterChanged("Saturation");
    partial void OnTemperatureChanged(double value) => OnParameterChanged("Temperature");
    partial void OnTintChanged(double value) => OnParameterChanged("Tint");
    partial void OnClarityChanged(double value) => OnParameterChanged("Clarity");
    partial void OnSharpnessChanged(double value) => OnParameterChanged("Sharpness");
    partial void OnNoiseReductionChanged(double value) => OnParameterChanged("Noise reduction");
    partial void OnSharpeningModeChanged(SharpeningMode value) => OnParameterChanged("Sharpening mode");
    partial void OnUnsharpMaskAmountChanged(double value) => OnParameterChanged("Unsharp-mask amount");
    partial void OnUnsharpMaskRadiusChanged(double value) => OnParameterChanged("Unsharp-mask radius");
    partial void OnUnsharpMaskThresholdChanged(double value) => OnParameterChanged("Unsharp-mask threshold");
    partial void OnBlackPointChanged(double value) => OnParameterChanged("Black point");
    partial void OnWhitePointChanged(double value) => OnParameterChanged("White point");
    partial void OnMidtonesChanged(double value) => OnParameterChanged("Midtones");
    partial void OnRotationAngleChanged(double value) => OnParameterChanged("Rotation");

    private void OnParameterChanged(string label)
    {
        if (_restoring) return;
        var current = GetCurrentParameters();
        if (IsOpen && _editStart is null)
            RecordEdit(label, _lastParameters, current);
        _lastParameters = current;
        HasChanges = !ParametersEqual(current, _baseline);
        ParametersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void BeginEdit(string label)
    {
        EndEdit();
        _editStart = GetCurrentParameters();
        _editLabel = label;
    }

    public void EndEdit()
    {
        if (_editStart is not { } before) return;
        _editStart = null;
        if (IsOpen) RecordEdit(_editLabel, before, GetCurrentParameters());
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => UndoSteps(1);

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => RedoSteps(1);

    public void UndoSteps(int count) => MoveHistory(_undoHistory, _redoHistory, count, undo: true);
    public void RedoSteps(int count) => MoveHistory(_redoHistory, _undoHistory, count, undo: false);

    private void MoveHistory(List<HistoryEntry> source, List<HistoryEntry> target, int count, bool undo)
    {
        if (!IsOpen || IsBusy) return;
        EndEdit();
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, source.Count);
        var parameters = undo ? source[^count].Before : source[^count].After;
        for (var i = 0; i < count; i++)
        {
            var entry = source[^1];
            source.RemoveAt(source.Count - 1);
            target.Add(entry);
        }
        RestoreParameters(parameters);
        NotifyHistoryChanged();
    }

    private void RecordEdit(string label, EditParameters before, EditParameters after)
    {
        if (ParametersEqual(before, after)) return;
        _undoHistory.Add(new HistoryEntry(label, before.Clone(), after.Clone()));
        _redoHistory.Clear();
        NotifyHistoryChanged();
    }

    private void RestoreParameters(EditParameters parameters)
    {
        _restoring = true;
        try { ApplyParametersToSliders(parameters); }
        finally { _restoring = false; }
        _lastParameters = GetCurrentParameters();
        HasChanges = !ParametersEqual(_lastParameters, _baseline);
        ParametersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearHistory()
    {
        _editStart = null;
        _undoHistory.Clear();
        _redoHistory.Clear();
        _baseline = _lastParameters = GetCurrentParameters();
        HasChanges = false;
        NotifyHistoryChanged();
    }

    private void NotifyHistoryChanged()
    {
        OnPropertyChanged(nameof(UndoHistory));
        OnPropertyChanged(nameof(RedoHistory));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(CanSavePixels));
        OnPropertyChanged(nameof(CanExportRawDerivative));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanNavigate));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private static bool ParametersEqual(EditParameters a, EditParameters b) =>
        a.Brightness == b.Brightness && a.Contrast == b.Contrast && a.Exposure == b.Exposure &&
        a.RawExposureEv == b.RawExposureEv && a.RawWhitePoint == b.RawWhitePoint &&
        a.Highlights == b.Highlights && a.Shadows == b.Shadows && a.Saturation == b.Saturation &&
        a.Temperature == b.Temperature && a.Tint == b.Tint && a.Clarity == b.Clarity &&
        a.Sharpness == b.Sharpness && a.NoiseReduction == b.NoiseReduction &&
        a.SharpeningMode == b.SharpeningMode && a.UnsharpMaskAmount == b.UnsharpMaskAmount &&
        a.UnsharpMaskRadius == b.UnsharpMaskRadius &&
        a.UnsharpMaskThreshold == b.UnsharpMaskThreshold &&
        a.BlackPoint == b.BlackPoint && a.WhitePoint == b.WhitePoint &&
        a.Midtones == b.Midtones && a.RotationAngle == b.RotationAngle &&
        CropEqual(a.Crop, b.Crop);

    private static bool CropEqual(CropRect? a, CropRect? b) =>
        a is null || b is null ? a is null && b is null :
        a.Left == b.Left && a.Top == b.Top && a.Width == b.Width && a.Height == b.Height;
}

/// <summary>
/// Describes an image whose adjustments have just been baked into the file on disk.
/// </summary>
public sealed class EditsAppliedEventArgs : EventArgs
{
    public EditsAppliedEventArgs(string filePath, uint pixelWidth, uint pixelHeight)
    {
        FilePath = filePath;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
    }

    public string FilePath { get; }
    public uint PixelWidth { get; }
    public uint PixelHeight { get; }
}
