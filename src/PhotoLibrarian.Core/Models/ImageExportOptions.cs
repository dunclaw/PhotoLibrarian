namespace PhotoLibrarian.Core.Models;

public enum ExportResizeMode
{
    MaximumDimension,
    Percentage
}

public sealed record ImageExportOptions
{
    public ExportResizeMode ResizeMode { get; init; }
    public double Size { get; init; } = 1600;
    public int Quality { get; init; } = 90;
    public bool StripMetadata { get; init; }

    public void Validate()
    {
        if (!Enum.IsDefined(ResizeMode))
            throw new ArgumentOutOfRangeException(nameof(ResizeMode));
        var maximum = ResizeMode == ExportResizeMode.Percentage ? 100 : 65535;
        if (!double.IsFinite(Size) || Size < 1 || Size > maximum || Size != Math.Truncate(Size))
            throw new ArgumentOutOfRangeException(nameof(Size), $"Enter a whole number from 1 to {maximum}.");
        if (Quality is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(Quality), "Quality must be from 1 to 100.");
    }

    public (uint Width, uint Height) GetDimensions(uint width, uint height)
    {
        Validate();
        if (width == 0 || height == 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");

        var scale = ResizeMode == ExportResizeMode.Percentage
            ? Size / 100
            : Math.Min(1, Size / Math.Max(width, height));
        return (
            Math.Max(1, (uint)Math.Round(width * scale)),
            Math.Max(1, (uint)Math.Round(height * scale)));
    }
}
