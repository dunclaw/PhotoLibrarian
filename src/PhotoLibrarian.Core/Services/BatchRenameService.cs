using System.Globalization;
using System.Text.RegularExpressions;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Core.Services;

public sealed record RenamePreview(
    ImageEntry Entry, string SourcePath, string DestinationPath,
    string SourceSidecar, string DestinationSidecar, string? Error)
{
    public string OriginalName => Path.GetFileName(SourcePath);
    public string NewName => Path.GetFileName(DestinationPath);
}

public sealed class BatchRenameService(ImageRepository images)
{
    private static readonly Regex Tokens = new(@"\{([^{}]*)\}", RegexOptions.CultureInvariant);

    public static IReadOnlyList<RenamePreview> PreviewSingle(ImageEntry entry, string name)
    {
        string? error = null;
        try
        {
            ValidateName(name);
            if (!Path.HasExtension(name))
                name += Path.GetExtension(entry.FilePath);
            ValidateName(name);
        }
        catch (ArgumentException ex) { error = ex.Message; }
        var destination = error is null
            ? Path.Combine(Path.GetDirectoryName(entry.FilePath)!, name)
            : entry.FilePath;
        var sidecar = FaceMetadataStore.GetSidecarPathForImage(entry.FilePath);
        var newSidecar = FaceMetadataStore.GetSidecarPathForDestinationImage(destination);
        return Validate([new(entry, entry.FilePath, destination, sidecar, newSidecar, error)]);
    }

    public static IReadOnlyList<RenamePreview> Preview(
        IReadOnlyList<ImageEntry> entries, string pattern)
    {
        var previews = new List<RenamePreview>();
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var destination = entry.FilePath;
            string? error = null;
            try
            {
                var literal = Tokens.Replace(pattern, "");
                if (literal.Contains('{') || literal.Contains('}'))
                    throw new ArgumentException("Unmatched braces in pattern.");
                var name = Tokens.Replace(pattern, match =>
                {
                    var token = match.Groups[1].Value;
                    var date = entry.DateTaken ?? entry.DateModified;
                    return token switch
                    {
                        "original" => Path.GetFileNameWithoutExtension(entry.FilePath),
                        "yyyy" or "MM" or "dd" or "HH" or "mm" or "ss" =>
                            date.ToString(token, CultureInfo.InvariantCulture),
                        _ when token.Length is > 0 and <= 9 && token.All(c => c == '#') =>
                            (index + 1).ToString(new string('0', token.Length), CultureInfo.InvariantCulture),
                        _ => throw new ArgumentException($"Unknown token: {{{token}}}.")
                    };
                });
                ValidateName(name);
                destination = Path.Combine(
                    Path.GetDirectoryName(entry.FilePath)!, name + Path.GetExtension(entry.FilePath));
                ValidateName(Path.GetFileName(destination));
            }
            catch (ArgumentException ex)
            {
                error = ex.Message;
            }

            var sidecar = FaceMetadataStore.GetSidecarPathForImage(entry.FilePath);
            var newSidecar = FaceMetadataStore.GetSidecarPathForDestinationImage(destination);
            previews.Add(new(entry, entry.FilePath, destination, sidecar, newSidecar, error));
        }

        return Validate(previews);
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.EndsWith('.') || name.EndsWith(' ') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Length > 255)
            throw new ArgumentException("Names must be valid filenames, without trailing spaces or dots.");

        var stem = name.Split('.')[0];
        if (Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase))
            throw new ArgumentException("Windows reserved filenames cannot be used.");
    }

    private static IReadOnlyList<RenamePreview> Validate(IReadOnlyList<RenamePreview> previews)
    {
        var targets = previews.SelectMany(p => new[] { p.DestinationPath, p.DestinationSidecar })
            .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sharedSidecars = previews.Where(p => File.Exists(p.SourceSidecar))
            .GroupBy(p => p.SourceSidecar, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return previews.Select(p =>
        {
            var error = p.Error;
            if (error is null)
            {
                if (!string.Equals(p.Entry.FilePath, p.SourcePath, StringComparison.Ordinal))
                    error = "The photo path changed. Reopen the rename dialog.";
                else if (!File.Exists(p.SourcePath))
                    error = "Source file no longer exists.";
                else if (targets.Contains(p.DestinationPath) || targets.Contains(p.DestinationSidecar))
                    error = "Multiple photos would use the same photo or XMP name.";
                else if (sharedSidecars.Contains(p.SourceSidecar))
                    error = "Selected photos share an XMP sidecar and cannot be renamed together.";
                else if (Occupied(p.SourcePath, p.DestinationPath) ||
                         Occupied(p.SourceSidecar, p.DestinationSidecar))
                    error = "A destination photo, XMP sidecar, or folder already exists.";
            }
            return p with { Error = error };
        }).ToArray();
    }

    private static bool Occupied(string source, string destination) =>
        !string.Equals(source, destination, StringComparison.OrdinalIgnoreCase) &&
        (File.Exists(destination) || Directory.Exists(destination));

    public async Task RenameAsync(IReadOnlyList<RenamePreview> previews)
    {
        var checkedPreviews = Validate(previews);
        var invalid = checkedPreviews.FirstOrDefault(p => p.Error is not null);
        if (invalid is not null)
            throw new IOException($"{invalid.OriginalName}: {invalid.Error}");

        var completed = 0;
        foreach (var preview in checkedPreviews)
        {
            if (string.Equals(preview.SourcePath, preview.DestinationPath, StringComparison.OrdinalIgnoreCase))
                continue;

            var movedPhoto = false;
            var movedSidecar = false;
            try
            {
                if (Occupied(preview.SourcePath, preview.DestinationPath) ||
                    Occupied(preview.SourceSidecar, preview.DestinationSidecar))
                    throw new IOException("A destination photo, XMP sidecar, or folder already exists.");
                File.Move(preview.SourcePath, preview.DestinationPath);
                movedPhoto = true;
                if (File.Exists(preview.SourceSidecar))
                {
                    File.Move(preview.SourceSidecar, preview.DestinationSidecar);
                    movedSidecar = true;
                }
                if (preview.Entry.Id > 0)
                    await images.UpdatePathAsync(
                        preview.Entry.Id, preview.DestinationPath,
                        movedSidecar ? preview.DestinationSidecar : null);

                preview.Entry.FilePath = preview.DestinationPath;
                preview.Entry.FileName = preview.NewName;
                preview.Entry.FaceSidecarPath = movedSidecar ? preview.DestinationSidecar : null;
                completed++;
            }
            catch (Exception ex)
            {
                try
                {
                    if (movedSidecar) File.Move(preview.DestinationSidecar, preview.SourceSidecar);
                    if (movedPhoto) File.Move(preview.DestinationPath, preview.SourcePath);
                }
                catch (Exception rollbackError)
                {
                    throw new AggregateException(
                        $"Rename stopped after {completed} files. Restoring {preview.OriginalName} also failed; check its folder.",
                        ex, rollbackError);
                }
                throw new IOException(
                    $"Rename stopped after {completed} files. {preview.OriginalName}: {ex.Message}", ex);
            }
        }
    }
}
