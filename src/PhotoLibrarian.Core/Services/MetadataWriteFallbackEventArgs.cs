namespace PhotoLibrarian.Core.Services;

/// <summary>An edit saved to a sidecar because writing a supported image failed.</summary>
public sealed class MetadataWriteFallbackEventArgs(
    string imagePath, string propertyName, Exception error) : EventArgs
{
    public string ImagePath { get; } = imagePath;
    public string SidecarPath => Path.ChangeExtension(ImagePath, ".xmp");
    public string PropertyName { get; } = propertyName;
    public Exception Error { get; } = error;
}
