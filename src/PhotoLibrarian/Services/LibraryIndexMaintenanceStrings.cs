// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Windows.ApplicationModel.Resources;
using PhotoLibrarian.Core.Services;
using System.Globalization;

namespace PhotoLibrarian.Services;

internal static class LibraryIndexMaintenanceStrings
{
    private static readonly ResourceLoader Loader = new(
        Path.Combine(AppContext.BaseDirectory, "PhotoLibrarian.pri"),
        "Resources");

    public static string Get(string key) => Loader.GetString(key);

    public static string Format(string key, params object?[] values) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), values);

    public static string GetStageName(LibraryIndexMaintenanceStage stage) =>
        Get(stage switch
        {
            LibraryIndexMaintenanceStage.ScanningFolders =>
                "IndexMaintenanceStageScanning",
            LibraryIndexMaintenanceStage.VerifyingMoves =>
                "IndexMaintenanceStageVerifyingMoves",
            LibraryIndexMaintenanceStage.IndexingFiles =>
                "IndexMaintenanceStageIndexing",
            LibraryIndexMaintenanceStage.Complete =>
                "IndexMaintenanceStageComplete",
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null)
        });

    public static string FormatIssue(LibraryIndexMaintenanceIssue issue) =>
        Format(
            "IndexMaintenanceIssue",
            GetIssueKindName(issue.Kind),
            issue.Path,
            issue.Message);

    private static string GetIssueKindName(
        LibraryIndexMaintenanceIssueKind kind) =>
        Get(kind switch
        {
            LibraryIndexMaintenanceIssueKind.NoWatchedFolders =>
                "IndexMaintenanceIssueNoWatchedFolders",
            LibraryIndexMaintenanceIssueKind.FolderUnavailable =>
                "IndexMaintenanceIssueFolderUnavailable",
            LibraryIndexMaintenanceIssueKind.AccessDenied =>
                "IndexMaintenanceIssueAccessDenied",
            LibraryIndexMaintenanceIssueKind.FolderScanFailed =>
                "IndexMaintenanceIssueFolderScanFailed",
            LibraryIndexMaintenanceIssueKind.FileReadFailed =>
                "IndexMaintenanceIssueFileReadFailed",
            LibraryIndexMaintenanceIssueKind.FileIndexFailed =>
                "IndexMaintenanceIssueFileIndexFailed",
            LibraryIndexMaintenanceIssueKind.PendingMetadataExport =>
                "IndexMaintenanceIssuePendingMetadataExport",
            LibraryIndexMaintenanceIssueKind.ReparsePointSkipped =>
                "IndexMaintenanceIssueReparsePointSkipped",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        });
}
