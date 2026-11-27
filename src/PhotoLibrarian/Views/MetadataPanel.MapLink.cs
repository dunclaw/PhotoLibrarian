// Copyright (c) Duncan Lawler. All Rights Reserved.

using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoLibrarian.Services;

namespace PhotoLibrarian.Views;

public sealed partial class MetadataPanel
{
    private bool _isGeotagPasteDialogOpen;

    private async void OnGeotagPasteMapLinkClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { HasImage: true, CanEditGeotag: true } viewModel ||
            XamlRoot is not { } root || _isGeotagPasteDialogOpen)
            return;

        var selectionRevision = _displayedGeotagSelectionRevision;
        if (selectionRevision != viewModel.GeotagSelectionRevision)
        {
            App.ViewModel.StatusText = ResourceStrings.Get("GeotagPasteMapSelectionChanged");
            return;
        }

        if (_isSavingGeotag)
        {
            App.ViewModel.StatusText = ResourceStrings.Get("GeotagPasteMapBusy");
            return;
        }

        var target = viewModel.IsMultiSelect
            ? ResourceStrings.Format("GeotagPasteMapTargetMultiple", viewModel.SelectionCount)
            : ResourceStrings.Format("GeotagPasteMapTargetSingle", viewModel.FileName);

        _isGeotagPasteDialogOpen = true;
        try
        {
            var dialog = new MapGeotagDialog(
                target, MapLinkService.GetBrowseUri(App.ViewModel.Settings.MapProvider), LaunchMapInBrowserAsync)
            {
                XamlRoot = root,
                RequestedTheme = ActualTheme
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || dialog.Point is not { } confirmedPoint)
                return;

            if (_isSavingGeotag)
            {
                App.ViewModel.StatusText = ResourceStrings.Get("GeotagPasteMapBusy");
                return;
            }

            await ApplyGeotagAsync(viewModel, confirmedPoint, selectionRevision, fromMapLink: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            App.ViewModel.StatusText = ResourceStrings.Format("GeotagPasteMapDialogError", exception.Message);
        }
        finally
        {
            _isGeotagPasteDialogOpen = false;
        }
    }

    private static async Task<string?> LaunchMapInBrowserAsync(Uri uri)
    {
        try
        {
            return await Windows.System.Launcher.LaunchUriAsync(uri)
                ? null
                : MapStrings.Get("MapLaunchFailed");
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or COMException)
        {
            PhotoLibrarian.Diagnostics.DebugLog.WriteLine($"[MAP] Could not open map: {ex.Message}");
            return MapStrings.Format("MapLaunchError", ex.Message);
        }
    }
}
