// Copyright (c) Duncan Lawler. All Rights Reserved.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PhotoLibrarian.Services;

public sealed class DialogService : IDialogService
{
    public static IDialogService Instance { get; } = new DialogService();

    public Task<ContentDialogResult> ConfirmAsync(
        FrameworkElement owner,
        string title,
        object content,
        string primaryButtonText,
        string cancelButtonText,
        ContentDialogButton defaultButton = ContentDialogButton.Primary) =>
        ShowAsync(owner, title, content, primaryButtonText, cancelButtonText, defaultButton);

    public Task<ContentDialogResult> ConfirmDestructiveAsync(
        FrameworkElement owner,
        string title,
        object content,
        string primaryButtonText,
        string cancelButtonText) =>
        ShowAsync(owner, title, content, primaryButtonText, cancelButtonText, ContentDialogButton.Close);

    public Task<ContentDialogResult> ErrorAsync(
        FrameworkElement owner,
        string title,
        object content,
        string closeButtonText) =>
        ShowAsync(owner, title, content, "", closeButtonText, ContentDialogButton.Close);

    private static async Task<ContentDialogResult> ShowAsync(
        FrameworkElement owner,
        string title,
        object content,
        string primaryButtonText,
        string closeButtonText,
        ContentDialogButton defaultButton)
    {
        // Resolve for each invocation, not from App.MainWindow or a cached window/root.
        var root = owner.XamlRoot
            ?? throw new InvalidOperationException("The dialog owner is not attached to a window.");
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = owner.ActualTheme,
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = closeButtonText,
            DefaultButton = defaultButton
        };
        return await dialog.ShowAsync();
    }
}
