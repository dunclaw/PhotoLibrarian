using CommunityToolkit.Mvvm.ComponentModel;
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.ViewModels;

public partial class MetadataPanelViewModel : ObservableObject
{
    [ObservableProperty]
    public partial bool HasImage { get; set; }

    [ObservableProperty]
    public partial string FileName { get; set; }

    [ObservableProperty]
    public partial string FilePath { get; set; }

    [ObservableProperty]
    public partial string FileSize { get; set; }

    [ObservableProperty]
    public partial string Dimensions { get; set; }

    [ObservableProperty]
    public partial string DateTaken { get; set; }

    [ObservableProperty]
    public partial string Camera { get; set; }

    [ObservableProperty]
    public partial string Lens { get; set; }

    [ObservableProperty]
    public partial string Exposure { get; set; }

    [ObservableProperty]
    public partial string FocalLength { get; set; }

    [ObservableProperty]
    public partial string Aperture { get; set; }

    [ObservableProperty]
    public partial string Iso { get; set; }

    [ObservableProperty]
    public partial string GpsLocation { get; set; }

    [ObservableProperty]
    public partial int Rating { get; set; }

    [ObservableProperty]
    public partial string MediaType { get; set; }

    public MetadataPanelViewModel()
    {
        FileName = "";
        FilePath = "";
        FileSize = "";
        Dimensions = "";
        DateTaken = "";
        Camera = "";
        Lens = "";
        Exposure = "";
        FocalLength = "";
        Aperture = "";
        Iso = "";
        GpsLocation = "";
        MediaType = "";
    }

    public void ShowMetadata(ImageEntry entry)
    {
        HasImage = true;
        FileName = entry.FileName;
        FilePath = entry.FilePath;
        FileSize = FormatFileSize(entry.FileSize);
        Dimensions = entry.Width > 0 ? $"{entry.Width} × {entry.Height}" : "Unknown";
        DateTaken = entry.DateTaken?.ToString("MMMM d, yyyy h:mm tt") ?? "Unknown";
        Camera = FormatCamera(entry.CameraMake, entry.CameraModel);
        Lens = entry.LensModel ?? "";
        Exposure = entry.ExposureTime ?? "";
        FocalLength = entry.FocalLength.HasValue ? $"{entry.FocalLength:F0}mm" : "";
        Aperture = entry.Aperture.HasValue ? $"f/{entry.Aperture:F1}" : "";
        Iso = entry.Iso.HasValue ? $"ISO {entry.Iso}" : "";
        GpsLocation = FormatGps(entry.GpsLatitude, entry.GpsLongitude);
        Rating = entry.Rating ?? 0;
        MediaType = entry.MediaType == Core.Models.MediaType.Video ? "Video" : "Photo";
    }

    public void Clear()
    {
        HasImage = false;
        FileName = "";
    }

    private static string FormatFileSize(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
            _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
        };
    }

    private static string FormatCamera(string? make, string? model)
    {
        if (string.IsNullOrEmpty(make) && string.IsNullOrEmpty(model))
            return "";
        if (string.IsNullOrEmpty(make))
            return model!;
        if (model?.StartsWith(make, StringComparison.OrdinalIgnoreCase) == true)
            return model;
        return $"{make} {model}";
    }

    private static string FormatGps(double? lat, double? lon)
    {
        if (!lat.HasValue || !lon.HasValue)
            return "";
        return $"{lat:F6}°, {lon:F6}°";
    }
}
