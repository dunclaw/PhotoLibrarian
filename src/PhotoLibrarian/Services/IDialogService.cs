// Copyright (c) Duncan Lawler. All Rights Reserved.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PhotoLibrarian.Services;

/// <summary>
/// Shows dialogs on the UI thread using the invoking control's current window.
/// Callers supply localized text and must act only on <see cref="ContentDialogResult.Primary"/>.
/// Primary text names the action; confirmation close text is Cancel.
/// The owner must be attached to a window. Presentation failures propagate to the caller.
/// Do not use these templates for nested dialogs, progress, or save/discard/cancel forms.
/// </summary>
public interface IDialogService
{
    /// <summary>Preserves the caller's neutral default; destructive actions use ConfirmDestructiveAsync.</summary>
    Task<ContentDialogResult> ConfirmAsync(
        FrameworkElement owner,
        string title,
        object content,
        string primaryButtonText,
        string cancelButtonText,
        ContentDialogButton defaultButton = ContentDialogButton.Primary);

    /// <summary>Always defaults to Cancel, never to the destructive action.</summary>
    Task<ContentDialogResult> ConfirmDestructiveAsync(
        FrameworkElement owner,
        string title,
        object content,
        string primaryButtonText,
        string cancelButtonText);

    Task<ContentDialogResult> ErrorAsync(
        FrameworkElement owner,
        string title,
        object content,
        string closeButtonText);
}
