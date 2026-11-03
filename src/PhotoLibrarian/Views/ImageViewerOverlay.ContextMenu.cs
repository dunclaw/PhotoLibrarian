// Copyright (c) Duncan Lawler. All Rights Reserved.

using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using PhotoLibrarian.Services;

namespace PhotoLibrarian.Views;

public sealed partial class ImageViewerOverlay
{
    private bool _isPhotoContextMenuOpen;

    public event Action<MenuFlyout, PhotoContextMenuTarget>? PhotoContextMenuOpening;

    private void OnPhotoContextMenuOpening(object sender, object e)
    {
        StopFocusGuard();
        _isPhotoContextMenuOpen = true;
        PhotoContextMenu.Items.Clear();
        var target = PhotoContextMenuTarget.Create(
            ViewModel?.CurrentEntry,
            IsCropping || IsStraightening || IsRedEyeRemoving || IsManualFaceTagging ||
            App.ViewModel?.ImageEditor.IsOpen == true);
        if (target is not null)
            PhotoContextMenuOpening?.Invoke(PhotoContextMenu, target);

        if (PhotoContextMenu.Items.Count == 0)
        {
            var unavailable = new MenuFlyoutItem
            {
                Text = PhotoContextMenuStrings.Get("PhotoContextUnavailable"),
                IsEnabled = false
            };
            AutomationProperties.SetAutomationId(unavailable, "ViewerNoFileActions");
            PhotoContextMenu.Items.Add(unavailable);
        }
    }

    private void OnPhotoContextMenuClosed(object sender, object e) =>
        _isPhotoContextMenuOpen = false;
}
