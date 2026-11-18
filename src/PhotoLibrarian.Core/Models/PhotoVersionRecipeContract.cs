// Copyright (c) Duncan Lawler. All Rights Reserved.
namespace PhotoLibrarian.Core.Models;

/// <summary>
/// Identifies a version within one physical photo instance. The path identifies the
/// instance, while the portable IDs survive a move or rename of that instance.
/// </summary>
public sealed record PhotoVersionTarget(string SourcePath, Guid SourceId, Guid VariantId);

/// <summary>
/// An optimistic-concurrency snapshot of a version's complete XMP recipe packet.
/// </summary>
public sealed record PhotoVersionSnapshot(
    PhotoVersionTarget Target,
    Guid DocumentRevision,
    string InputIdentity,
    string RecipeXmp);

public enum PhotoVersionRecipeStatus
{
    Success,
    SourceMissing,
    VersionMissing,
    Unsupported,
    InvalidMetadata,
    ReadOnly,
    Busy,
    Conflict
}

public sealed record PhotoVersionRecipeResult(
    PhotoVersionRecipeStatus Status,
    PhotoVersionSnapshot? Snapshot = null,
    string? Message = null);

internal sealed record PhotoVersionFamilyRecord(
    PhotoVersionTarget Target, string Name, bool IsMain,
    PhotoVersionRecipeStatus Status, PhotoVersionSnapshot? Snapshot);

internal sealed record PhotoVersionFamilyResult(
    PhotoVersionRecipeStatus Status,
    IReadOnlyList<PhotoVersionFamilyRecord>? Records = null,
    string? Message = null,
    Guid? DocumentRevision = null,
    string? InputIdentity = null);

/// <summary>
/// Accesses recipes in portable version-family metadata. Implementations must not
/// substitute the main recipe when a target is missing or stale.
/// </summary>
public interface IPhotoVersionRecipeStore
{
    Task<PhotoVersionRecipeResult> ReadAsync(
        PhotoVersionTarget target,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates only the target recipe if the complete family revision and the
    /// photo/sidecar input identity still match the supplied snapshot. The updated
    /// packet must preserve unknown recipe properties.
    /// </summary>
    Task<PhotoVersionRecipeResult> WriteRecipeAsync(
        PhotoVersionSnapshot expected,
        string updatedRecipeXmp,
        CancellationToken cancellationToken = default);
}
