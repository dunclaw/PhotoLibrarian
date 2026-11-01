// Copyright (c) Duncan Lawler. All Rights Reserved.
namespace PhotoLibrarian.Core.Models;

public enum DuplicateMatchKind
{
    Exact,
    Near
}

public sealed record DuplicateFileSnapshot(
    ImageEntry Entry,
    long FileSize,
    DateTime LastWriteTimeUtc,
    string? Sha256,
    ulong? PerceptualHash);

public sealed record DuplicateCandidateGroup(
    DuplicateMatchKind Kind,
    IReadOnlyList<DuplicateFileSnapshot> Files,
    int? HammingDistance);

public sealed record DuplicateScanDiagnostic(string FilePath, string Message);

public sealed record DuplicateScanProgress(int CompletedFiles, int TotalFiles, string FilePath);

public sealed record DuplicateScanResult(
    IReadOnlyList<DuplicateCandidateGroup> Groups,
    IReadOnlyList<DuplicateScanDiagnostic> Diagnostics,
    int DiagnosticCount,
    bool NearMatchLimitReached);
