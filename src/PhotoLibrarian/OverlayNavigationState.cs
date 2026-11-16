// Copyright (c) Duncan Lawler. All Rights Reserved.
namespace PhotoLibrarian;

internal enum OverlaySurface
{
    Library,
    Viewer,
    Editor,
    Settings,
    PeopleReview,
    DuplicateReview,
    Comparison
}

internal static class OverlayNavigationState
{
    internal static OverlaySurface Resolve(
        bool isComparisonOpen,
        bool isDuplicateReviewOpen,
        bool isPeopleReviewOpen,
        bool isSettingsOpen,
        bool isEditorOpen,
        bool isViewerOpen)
    {
        // Keep the host's active surface aligned with the XAML's overlay z-order.
        if (isComparisonOpen) return OverlaySurface.Comparison;
        if (isDuplicateReviewOpen) return OverlaySurface.DuplicateReview;
        if (isPeopleReviewOpen) return OverlaySurface.PeopleReview;
        if (isSettingsOpen) return OverlaySurface.Settings;
        if (isEditorOpen) return OverlaySurface.Editor;
        if (isViewerOpen) return OverlaySurface.Viewer;
        return OverlaySurface.Library;
    }
}
