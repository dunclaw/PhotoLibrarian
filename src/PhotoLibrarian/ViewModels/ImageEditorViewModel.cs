using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;

namespace PhotoLibrarian.ViewModels;

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

    private sealed record HistoryEntry(string Label, EditParameters Before, EditParameters After);

    public IReadOnlyList<string> UndoHistory => _undoHistory.AsEnumerable().Reverse().Select(x => x.Label).ToArray();
    public IReadOnlyList<string> RedoHistory => _redoHistory.AsEnumerable().Reverse().Select(x => x.Label).ToArray();
    public bool CanUndo => IsOpen && !IsSaving && _undoHistory.Count > 0;
    public bool CanRedo => IsOpen && !IsSaving && _redoHistory.Count > 0;

    partial void OnIsSavingChanged(bool value) => NotifyHistoryChanged();
    partial void OnIsOpenChanged(bool value) => NotifyHistoryChanged();

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string? ImagePath { get; set; }

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
        Midtones = 0.5;
        ClearHistory();
    }

    public async Task OpenForEditAsync(ImageEntry entry)
    {
        _currentEntry = entry;
        Title = entry.FileName;
        ImagePath = entry.FilePath;
        IsOpen = true;

        RestoreParameters(ImageEditService.ReadEditParameters(entry.FilePath));
        ClearHistory();

        // Backups are keyed by the content hash of the file they were taken from, so remember
        // the hash we opened with — after a save the file's own hash no longer finds it.
        _backupHash = await OriginalBackupService.ComputeFileHashAsync(entry.FilePath);
        HasBackup = await _backupService.HasBackupAsync(entry.FilePath);
        HasChanges = false;
    }

    /// <summary>
    /// Bakes the current adjustments into the image file on disk. The original is backed up
    /// first, then the sliders reset because the values now live in the pixels.
    /// Returns true when the file was written.
    /// </summary>
    public async Task<bool> SaveAsync(ImageEditService.PixelRenderer renderer)
    {
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
        Highlights = Highlights,
        Shadows = Shadows,
        Saturation = Saturation,
        Temperature = Temperature,
        Tint = Tint,
        Clarity = Clarity,
        Sharpness = Sharpness,
        NoiseReduction = NoiseReduction,
        BlackPoint = BlackPoint,
        WhitePoint = WhitePoint,
        Midtones = Midtones,
        RotationAngle = RotationAngle
    };

    [RelayCommand]
    private void ResetAll()
    {
        if (IsSaving) return;
        BeginEdit("Reset adjustments");
        ApplyParametersToSliders(new EditParameters());
        EndEdit();
    }

    [RelayCommand]
    private async Task RevertToOriginalAsync()
    {
        if (_currentEntry is null || IsSaving) return;
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
        if (IsSaving) return;
        IsOpen = false;
        ImagePath = null;
        _currentEntry = null;
        ClearHistory();
    }

    public void ApplyAutoEnhance(EditParameters adjustments)
    {
        ArgumentNullException.ThrowIfNull(adjustments);
        if (IsSaving) return;
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
        Brightness = p.Brightness;
        Contrast = p.Contrast;
        Exposure = p.Exposure;
        Highlights = p.Highlights;
        Shadows = p.Shadows;
        Saturation = p.Saturation;
        Temperature = p.Temperature;
        Tint = p.Tint;
        Clarity = p.Clarity;
        Sharpness = p.Sharpness;
        NoiseReduction = p.NoiseReduction;
        BlackPoint = p.BlackPoint;
        WhitePoint = p.WhitePoint;
        Midtones = p.Midtones;
        RotationAngle = p.RotationAngle;
    }

    // Called when any slider value changes to trigger re-render
    partial void OnBrightnessChanged(double value) => OnParameterChanged("Brightness");
    partial void OnContrastChanged(double value) => OnParameterChanged("Contrast");
    partial void OnExposureChanged(double value) => OnParameterChanged("Exposure");
    partial void OnHighlightsChanged(double value) => OnParameterChanged("Highlights");
    partial void OnShadowsChanged(double value) => OnParameterChanged("Shadows");
    partial void OnSaturationChanged(double value) => OnParameterChanged("Saturation");
    partial void OnTemperatureChanged(double value) => OnParameterChanged("Temperature");
    partial void OnTintChanged(double value) => OnParameterChanged("Tint");
    partial void OnClarityChanged(double value) => OnParameterChanged("Clarity");
    partial void OnSharpnessChanged(double value) => OnParameterChanged("Sharpness");
    partial void OnNoiseReductionChanged(double value) => OnParameterChanged("Noise reduction");
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
        if (!IsOpen || IsSaving) return;
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
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private static bool ParametersEqual(EditParameters a, EditParameters b) =>
        a.Brightness == b.Brightness && a.Contrast == b.Contrast && a.Exposure == b.Exposure &&
        a.Highlights == b.Highlights && a.Shadows == b.Shadows && a.Saturation == b.Saturation &&
        a.Temperature == b.Temperature && a.Tint == b.Tint && a.Clarity == b.Clarity &&
        a.Sharpness == b.Sharpness && a.NoiseReduction == b.NoiseReduction &&
        a.BlackPoint == b.BlackPoint && a.WhitePoint == b.WhitePoint &&
        a.Midtones == b.Midtones && a.RotationAngle == b.RotationAngle;
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
