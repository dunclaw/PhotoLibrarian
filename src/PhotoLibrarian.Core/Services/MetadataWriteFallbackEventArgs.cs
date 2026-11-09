// Copyright (c) Duncan Lawler. All Rights Reserved.
namespace PhotoLibrarian.Core.Services;

/// <summary>An edit saved to a sidecar because writing a supported image failed.</summary>
public sealed class MetadataWriteFallbackEventArgs(
    string imagePath, string propertyName, Exception error, string? sidecarPath = null) : EventArgs
{
    private const int InvalidArgumentHResult = unchecked((int)0x80070057);

    public string ImagePath { get; } = imagePath;
    public string SidecarPath { get; } = sidecarPath ?? Path.ChangeExtension(imagePath, ".xmp");
    public string PropertyName { get; } = propertyName;
    public Exception Error { get; } = error;

    /// <summary>
    /// A user-facing explanation of why the photo could not be updated, including the
    /// Windows error code so the failure can be matched with the diagnostic log.
    /// </summary>
    public string Reason => Error.HResult == InvalidArgumentHResult
        ? "Windows could not rewrite the metadata already stored in this photo; it may be " +
          $"malformed or in a layout Windows does not support (0x{Error.HResult:X8}: {Error.Message}). " +
          "The photo itself was not changed."
        : $"{Error.Message} (0x{Error.HResult:X8})";
}
