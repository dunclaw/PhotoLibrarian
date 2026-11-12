// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Services;

namespace PhotoLibrarian.Views;

public sealed partial class ImageGridView
{
    private PhotoComparisonWindow? _comparison;
    private bool _closingComparison;
    internal void CloseComparison()
    {
        _closingComparison = true;
        _comparison?.Close();
    }

    private ImageEntry[] GetComparisonSelection()
    {
        var paths = GetSelectedItems().Select(item => item.Entry.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ViewModel?.GetViewerImages().Where(entry => paths.Contains(entry.FilePath)).ToArray() ?? [];
    }

    private void OnComparisonMenuOpening(object sender, object e)
    {
        var entries = GetComparisonSelection();
        CompareMenuItem.IsEnabled = PhotoComparisonSession.CanOpen(entries, survey: false);
        SurveyMenuItem.IsEnabled = PhotoComparisonSession.CanOpen(entries, survey: true);
    }

    private void OnCompareClick(object sender, RoutedEventArgs e) => OpenComparison(survey: false);
    private void OnSurveyClick(object sender, RoutedEventArgs e) => OpenComparison(survey: true);

    private void OpenComparison(bool survey)
    {
        var entries = GetComparisonSelection();
        if (!PhotoComparisonSession.CanOpen(entries, survey))
        {
            App.ViewModel.StatusText = ComparisonStrings.Get("ComparisonSelectionHelp");
            return;
        }
        if (_comparison is not null)
        {
            _comparison.Activate();
            return;
        }
        _comparison = new PhotoComparisonWindow(entries, survey);
        _closingComparison = false;
        _comparison.Closed += (_, _) =>
        {
            _comparison = null;
            if (_closingComparison) return;
            App.MainWindow?.Activate();
            PhotoGrid.Focus(FocusState.Keyboard);
        };
        _comparison.Activate();
    }
}
