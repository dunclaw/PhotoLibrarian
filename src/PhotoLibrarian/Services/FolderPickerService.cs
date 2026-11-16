// Copyright (c) Duncan Lawler. All Rights Reserved.

using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace PhotoLibrarian.Services;

public sealed class FolderPickerService(Func<Window?> ownerProvider)
{
    private readonly Func<Window?> _ownerProvider = ownerProvider;

    public async Task<StorageFolder?> PickFolderAsync(
        PickerLocationId startLocation = PickerLocationId.PicturesLibrary)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = startLocation
        };
        picker.FileTypeFilter.Add("*");

        var owner = _ownerProvider() ??
            throw new InvalidOperationException(
                "The folder picker requires an active window.");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(owner);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        return await picker.PickSingleFolderAsync();
    }
}
