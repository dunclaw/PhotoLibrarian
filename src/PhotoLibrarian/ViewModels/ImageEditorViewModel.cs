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
    }

    public async Task OpenForEditAsync(ImageEntry entry)
    {
        _currentEntry = entry;
        Title = entry.FileName;
        ImagePath = entry.FilePath;
        IsOpen = true;

        // Load existing edit parameters from XMP if any
        try
        {
            ApplyParametersToSliders(ImageEditService.ReadEditParameters(entry.FilePath));
        }
        catch { /* No existing edits */ }

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

        var parameters = GetCurrentParameters();
        if (!parameters.HasAdjustments) return false;

        IsSaving = true;
        try
        {
            var (width, height) = await _editService.ApplyEditsAsync(
                _currentEntry.FilePath, parameters, renderer);

            ResetAll();
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
        BlackPoint = BlackPoint,
        WhitePoint = WhitePoint,
        Midtones = Midtones,
        RotationAngle = RotationAngle
    };

    [RelayCommand]
    private void ResetAll()
    {
        Brightness = Contrast = Exposure = 0;
        Highlights = Shadows = 0;
        Saturation = Temperature = Tint = 0;
        Clarity = Sharpness = 0;
        BlackPoint = 0;
        WhitePoint = 1.0;
        Midtones = 0.5;
        RotationAngle = 0;
        HasChanges = false;
        ParametersChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task RevertToOriginalAsync()
    {
        if (_currentEntry is null) return;
        var hash = _backupHash ?? await OriginalBackupService.ComputeFileHashAsync(_currentEntry.FilePath);
        if (await _backupService.RestoreOriginalAsync(_currentEntry.FilePath, hash))
        {
            ResetAll();
            Reverted?.Invoke(this, _currentEntry.FilePath);
        }
    }

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
        ImagePath = null;
        _currentEntry = null;
    }

    [RelayCommand]
    private void AutoEnhance()
    {
        // Simple auto-enhance: boost contrast and saturation slightly
        Contrast = 0.15;
        Saturation = 0.1;
        Clarity = 0.1;
        Shadows = 0.1;
        Highlights = -0.05;
        HasChanges = true;
        ParametersChanged?.Invoke(this, EventArgs.Empty);
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
        BlackPoint = p.BlackPoint;
        WhitePoint = p.WhitePoint;
        Midtones = p.Midtones;
        RotationAngle = p.RotationAngle;
    }

    // Called when any slider value changes to trigger re-render
    partial void OnBrightnessChanged(double value) => OnParameterChanged();
    partial void OnContrastChanged(double value) => OnParameterChanged();
    partial void OnExposureChanged(double value) => OnParameterChanged();
    partial void OnHighlightsChanged(double value) => OnParameterChanged();
    partial void OnShadowsChanged(double value) => OnParameterChanged();
    partial void OnSaturationChanged(double value) => OnParameterChanged();
    partial void OnTemperatureChanged(double value) => OnParameterChanged();
    partial void OnTintChanged(double value) => OnParameterChanged();
    partial void OnClarityChanged(double value) => OnParameterChanged();
    partial void OnSharpnessChanged(double value) => OnParameterChanged();
    partial void OnBlackPointChanged(double value) => OnParameterChanged();
    partial void OnWhitePointChanged(double value) => OnParameterChanged();
    partial void OnMidtonesChanged(double value) => OnParameterChanged();
    partial void OnRotationAngleChanged(double value) => OnParameterChanged();

    private void OnParameterChanged()
    {
        HasChanges = true;
        ParametersChanged?.Invoke(this, EventArgs.Empty);
    }
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
