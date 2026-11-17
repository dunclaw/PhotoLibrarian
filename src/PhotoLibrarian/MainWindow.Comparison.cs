// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Services;
using PhotoLibrarian.Views;

namespace PhotoLibrarian;

public sealed partial class MainWindow
{
    private PhotoComparisonView? _comparison;
    private bool IsComparisonOpen => _comparison?.IsOpen == true;

    private void OnComparisonRequested(object? sender, IReadOnlyList<ImageEntry> entries)
    {
        if (_isClosing || IsComparisonOpen || ViewModel.ImageViewer.IsOpen ||
            ViewModel.ImageEditor.IsOpen || ViewModel.Settings.IsOpen ||
            ViewModel.PeopleReview.IsOpen || ViewModel.DuplicateReview.IsOpen ||
            ViewModel.EditHistory.IsBusy || !PhotoComparisonSession.CanOpen(entries)) return;

        _comparison = new PhotoComparisonView();
        _comparison.Closed += OnComparisonClosed;
        ComparisonOverlay.Content = _comparison;
        _comparison.Open(entries);
        UpdateSurfaceNavigation();
    }

    private void OnComparisonClosed(object? sender, EventArgs args)
    {
        if (_comparison is null) return;
        _comparison.Closed -= OnComparisonClosed;
        _comparison = null;
        ComparisonOverlay.Content = null;
        if (!_isClosing)
            UpdateSurfaceNavigation();
    }
}
