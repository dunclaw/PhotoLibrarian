// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Services;

/// <summary>Session-only candidates in displayed order; never mutates entries or grid selection.</summary>
internal sealed class PhotoComparisonSession
{
    private readonly ImageEntry[] _original;
    private readonly List<ImageEntry> _candidates;
    internal IReadOnlyList<ImageEntry> Candidates => _candidates;
    internal int OriginalCount => _original.Length;

    internal static bool CanOpen(IReadOnlyList<ImageEntry> entries, bool survey) =>
        (survey ? entries.Count >= 2 : entries.Count == 2) &&
        entries.All(entry => entry.MediaType == MediaType.Image) &&
        entries.Select(entry => entry.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == entries.Count;

    internal PhotoComparisonSession(IEnumerable<ImageEntry> entries)
    {
        _original = entries.ToArray();
        _candidates = [.. _original];
    }

    internal bool Remove(ImageEntry entry) => _candidates.Remove(entry);

    internal void Restore()
    {
        _candidates.Clear();
        _candidates.AddRange(_original);
    }
}

/// <summary>Same source-pixel scale and relative image center, independent of image dimensions.</summary>
internal readonly record struct ComparisonViewport(
    double Scale, double CenterX, double CenterY, bool IsFit = false, bool IsActualSize = false)
{
    internal static ComparisonViewport Fit => new(1, 0.5, 0.5, IsFit: true);

    internal static (double Left, double Top) Position(
        double width, double height, double viewportWidth, double viewportHeight,
        double scale, double centerX, double centerY) =>
        (viewportWidth / 2 - width * scale * centerX,
         viewportHeight / 2 - height * scale * centerY);
}
