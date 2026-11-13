// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Services;

namespace PhotoLibrarian.Views;

public sealed partial class ImageGridView
{
    internal event EventHandler<IReadOnlyList<ImageEntry>>? ComparisonRequested;
    internal void FocusGrid() => PhotoGrid.Focus(FocusState.Programmatic);

    private ImageEntry[] GetComparisonSelection()
    {
        var paths = GetSelectedItems().Select(item => item.Entry.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ViewModel?.GetViewerImages().Where(entry => paths.Contains(entry.FilePath)).ToArray() ?? [];
    }

    private void OnCompareClick(object sender, RoutedEventArgs e) => OpenComparison();

    private void OpenComparison()
    {
        var entries = GetComparisonSelection();
        if (!PhotoComparisonSession.CanOpen(entries))
        {
            App.ViewModel.StatusText = ComparisonStrings.Get("ComparisonSelectionHelp");
            return;
        }
        ComparisonRequested?.Invoke(this, entries);
    }
}
