// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Runtime.InteropServices;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;

namespace PhotoLibrarian.ViewModels;

internal sealed record AutoAdjustProgress(int Examined, int Total, bool IsSaving = false);
internal sealed record AutoAdjustIssue(string Path, string Reason, bool IsFailure);
internal sealed record AutoAdjustResult(
    int Applied, int Unchanged, IReadOnlyList<AutoAdjustIssue> Issues,
    bool IsCancelled = false, int NotSaved = 0, string? SaveMessage = null);

public partial class MainViewModel
{
    internal async Task<AutoAdjustResult> AutoAdjustSelectionAsync(
        IReadOnlyCollection<ImageEntry> selection,
        IProgress<AutoAdjustProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var targets = selection.DistinctBy(image => Path.GetFullPath(image.FilePath), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var recipes = new Dictionary<string, EditParameters>(StringComparer.OrdinalIgnoreCase);
        var identities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<AutoAdjustIssue>();
        var unchanged = 0;
        try
        {
            for (var index = 0; index < targets.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = targets[index];
                var path = Path.GetFullPath(target.FilePath);
                if (!RasterAutoAdjustService.Supports(target))
                    issues.Add(new(path, PhotoContextMenuStrings.Get("BatchAutoAdjustUnsupported"), IsFailure: false));
                else
                {
                    try
                    {
                        var identity = await FileEditTransaction.ReadIdentityAsync(path, cancellationToken);
                        var (recipe, skipReason) = await Task.Run(
                            () => RasterAutoAdjustService.AnalyzeAsync(path, cancellationToken), cancellationToken);
                        if (skipReason is not null)
                            issues.Add(new(path, skipReason, IsFailure: false));
                        else if (recipe is null)
                            unchanged++;
                        else
                        {
                            recipes.Add(path, recipe);
                            identities.Add(path, identity);
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                        COMException or InvalidDataException or NotSupportedException or XmpCore.XmpException or
                        MetadataExtractor.ImageProcessingException)
                    {
                        Diagnostics.DebugLog.WriteLine($"[AUTO-ADJUST] {path}: {exception}");
                        issues.Add(new(path, exception.Message, IsFailure: true));
                    }
                }
                progress?.Report(new(index + 1, targets.Length));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (recipes.Count == 0) return new(0, unchanged, issues);

            progress?.Report(new(targets.Length, targets.Length, IsSaving: true));
            var commit = await RunPhotoEditWithResultAsync(
                PhotoContextMenuStrings.Get("BatchAutoAdjustAction"), recipes.Keys, async copies =>
                {
                    foreach (var (path, recipe) in recipes)
                    {
                        try
                        {
                            await ImageEditService.WritePendingRecipeAsync(copies[path], recipe, cancellationToken);
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                            InvalidDataException or NotSupportedException or XmpCore.XmpException)
                        {
                            throw new IOException(PhotoContextMenuStrings.Format(
                                "BatchAutoAdjustFailedItem", path, exception.Message), exception);
                        }
                    }
                }, expectedIdentities: identities, cancellationToken: cancellationToken);

            return commit.Status switch
            {
                EditHistoryStatus.Applied => new(recipes.Count, unchanged, issues, SaveMessage: commit.Message),
                EditHistoryStatus.Empty => new(0, unchanged + recipes.Count, issues),
                _ => new(0, unchanged, issues, NotSaved: recipes.Count, SaveMessage: commit.Message)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(0, unchanged, issues, IsCancelled: true);
        }
    }
}
