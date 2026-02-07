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
    private ImageEntry? _currentEntry;

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

    /// <summary>
    /// Raised when any parameter changes so the Win2D canvas can re-render.
    /// </summary>
    public event EventHandler? ParametersChanged;

    public ImageEditorViewModel(OriginalBackupService backupService)
    {
        _backupService = backupService;
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
            var dirs = MetadataExtractor.ImageMetadataReader.ReadMetadata(entry.FilePath);
            var xmpDir = dirs.OfType<MetadataExtractor.Formats.Xmp.XmpDirectory>().FirstOrDefault();
            if (xmpDir?.XmpMeta is not null)
            {
                var p = EditParametersSerializer.ReadFromXmp(xmpDir.XmpMeta);
                ApplyParametersToSliders(p);
            }
        }
        catch { /* No existing edits */ }

        HasBackup = await _backupService.HasBackupAsync(entry.FilePath);
        HasChanges = false;
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
        var hash = await OriginalBackupService.ComputeFileHashAsync(_currentEntry.FilePath);
        if (await _backupService.RestoreOriginalAsync(_currentEntry.FilePath, hash))
        {
            ResetAll();
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
