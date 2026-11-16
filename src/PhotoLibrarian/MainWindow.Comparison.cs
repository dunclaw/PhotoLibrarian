// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
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
        ComparisonOverlay.Visibility = Visibility.Visible;
        MainLayout.Visibility = Visibility.Collapsed;
        TopRibbon.Visibility = Visibility.Collapsed;
        FooterNotifications.Visibility = Visibility.Collapsed;
        StatusBar.Visibility = Visibility.Collapsed;
        _comparison.Open(entries);
        _comparison.FocusWorkspace();
    }

    private void OnComparisonClosed(object? sender, EventArgs args)
    {
        if (_comparison is null) return;
        _comparison.Closed -= OnComparisonClosed;
        _comparison = null;
        ComparisonOverlay.Content = null;
        ComparisonOverlay.Visibility = Visibility.Collapsed;
        if (_isClosing) return;
        MainLayout.Visibility = Visibility.Visible;
        FooterNotifications.Visibility = Visibility.Visible;
        StatusBar.Visibility = Visibility.Visible;
        UpdateRibbonVisibility();
        ImageGridPanel.FocusGrid();
    }
}
