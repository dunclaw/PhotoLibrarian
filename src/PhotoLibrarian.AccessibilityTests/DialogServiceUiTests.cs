// Copyright (c) Duncan Lawler. All Rights Reserved.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PhotoLibrarian.Services;
using PhotoLibrarian.Views;
using Windows.System;

namespace PhotoLibrarian.AccessibilityTests;

public partial class App
{
    private async Task RunDialogServiceTestsAsync()
    {
        var owner = new Button { Content = "Dialog owner" };
        var firstLayout = new Grid { RequestedTheme = ElementTheme.Light };
        firstLayout.Children.Add(owner);
        _window = new Window { Title = "Synthetic dialog regression", Content = firstLayout };
        _window.Activate();
        await UntilAsync(() => owner.IsLoaded);
        var service = DialogService.Instance;

        var folders = new ManageFoldersDialog { XamlRoot = owner.XamlRoot };
        var foldersTask = folders.ShowAsync().AsTask();
        await UntilAsync(() => FindOpenContentDialog() is not null);
        var maintenanceButton = (Button)folders.FindName("IndexMaintenanceButton");
        var maintenancePeer = FrameworkElementAutomationPeer.CreatePeerForElement(maintenanceButton);
        ((IInvokeProvider)maintenancePeer.GetPattern(PatternInterface.Invoke)).Invoke();
        await foldersTask;
        Check(folders.IndexMaintenanceRequested && FindOpenContentDialog() is null,
            "Manage Folders closes before requesting index maintenance");

        folders = new ManageFoldersDialog { XamlRoot = owner.XamlRoot };
        foldersTask = folders.ShowAsync().AsTask();
        await UntilAsync(() => FindOpenContentDialog() is not null);
        InvokeDialogButton(folders, "Close");
        await foldersTask;
        Check(!folders.IndexMaintenanceRequested,
            "Closing Manage Folders does not request index maintenance");

        var neutralTask = service.ConfirmAsync(
            owner, "Keep changes?", "Synthetic confirmation", "Keep changes", "Cancel",
            ContentDialogButton.None);
        await UntilAsync(() => FindOpenContentDialog() is not null);
        var neutral = FindOpenContentDialog()!;
        Check(neutral.XamlRoot == owner.XamlRoot && neutral.RequestedTheme == ElementTheme.Light &&
            neutral.DefaultButton == ContentDialogButton.None,
            "Neutral confirmation preserves its explicit default and invoking window/theme");
        InvokeDialogButton(neutral, "Keep changes");
        Check(await neutralTask == ContentDialogResult.Primary,
            "Neutral confirmation returns the primary result without remapping it");

        var printMessage = "Unsupported photos:" + Environment.NewLine + "one.raw" + Environment.NewLine + "two.raw";
        var printTask = service.ErrorAsync(owner, "Printing unavailable", printMessage, "Close");
        await UntilAsync(() => FindOpenContentDialog() is not null);
        var printError = FindOpenContentDialog()!;
        Check(printError.Title?.ToString() == "Printing unavailable" &&
            Equals(printError.Content, printMessage) && printError.CloseButtonText == "Close" &&
            string.IsNullOrEmpty(printError.PrimaryButtonText),
            "Error dialog preserves multiline print details and exposes only Close");
        InvokeDialogButton(printError, "Close");
        Check(await printTask == ContentDialogResult.None,
            "Closing the print error dismisses it");

        var destructiveTask = service.ConfirmDestructiveAsync(
            owner, "Recycle duplicates?", "Synthetic photos only", "Recycle 2 photos", "Cancel");
        await UntilAsync(() => FindOpenContentDialog() is not null);
        var destructive = FindOpenContentDialog()!;
        Check(destructive.DefaultButton == ContentDialogButton.Close &&
            destructive.PrimaryButtonText == "Recycle 2 photos" && destructive.CloseButtonText == "Cancel" &&
            string.IsNullOrEmpty(destructive.SecondaryButtonText),
            "Destructive confirmation names the action and defaults only to Cancel");
        await PressViewerKeysAsync((byte)VirtualKey.Enter);
        await UntilAsync(() => destructiveTask.IsCompleted);
        Check(await destructiveTask == ContentDialogResult.None,
            "Enter with the destructive confirmation's initial focus does not approve the action");

        destructiveTask = service.ConfirmDestructiveAsync(
            owner, "Delete preset?", "Synthetic preset only", "Delete preset", "Cancel");
        await UntilAsync(() => FindOpenContentDialog() is not null);
        await PressViewerKeysAsync((byte)VirtualKey.Escape);
        await UntilAsync(() => destructiveTask.IsCompleted);
        Check(await destructiveTask == ContentDialogResult.None,
            "Escape preserves the cancelled result for destructive confirmation");

        destructiveTask = service.ConfirmDestructiveAsync(
            owner, "Delete preset?", "Synthetic preset only", "Delete preset", "Cancel");
        await UntilAsync(() => FindOpenContentDialog() is not null);
        InvokeDialogButton(FindOpenContentDialog()!, "Delete preset");
        Check(await destructiveTask == ContentDialogResult.Primary,
            "Destructive confirmation approves only the explicit primary action");

        var firstRoot = owner.XamlRoot;
        firstLayout.Children.Remove(owner);
        var secondLayout = new Grid { RequestedTheme = ElementTheme.Dark };
        secondLayout.Children.Add(owner);
        var secondWindow = new Window { Title = "Second synthetic dialog window", Content = secondLayout };
        secondWindow.Activate();
        try
        {
            await UntilAsync(() => owner.IsLoaded && owner.XamlRoot != firstRoot);
            var failureContent = new ScrollViewer
            {
                MaxHeight = 320,
                Content = new TextBlock { Text = "Synthetic per-file failure", TextWrapping = TextWrapping.Wrap }
            };
            var errorTask = service.ErrorAsync(owner, "Some geotags need attention", failureContent, "Close");
            await UntilAsync(() => FindDialogInRoot(owner.XamlRoot) is not null);
            var error = FindDialogInRoot(owner.XamlRoot)!;
            Check(error.XamlRoot == owner.XamlRoot && error.XamlRoot != firstRoot &&
                error.RequestedTheme == ElementTheme.Dark && FindDialogInRoot(firstRoot) is null,
                "Reusing the service and owner after moving windows resolves the new root and theme");
            Check(ReferenceEquals(error.Content, failureContent) &&
                string.IsNullOrEmpty(error.PrimaryButtonText) && error.DefaultButton == ContentDialogButton.Close,
                "Error summary preserves scrollable content and has only a close action");
            InvokeDialogButton(error, "Close");
            Check(await errorTask == ContentDialogResult.None,
                "Closing an error summary returns the native dismissed result");

            var exception = new InvalidOperationException("Synthetic operation failure");
            errorTask = service.ErrorAsync(owner, "People couldn't be loaded", exception, "Close");
            await UntilAsync(() => FindDialogInRoot(owner.XamlRoot) is not null);
            error = FindDialogInRoot(owner.XamlRoot)!;
            Check(error.Title?.ToString() == "People couldn't be loaded" &&
                error.Content is string message && message == exception.Message &&
                error.CloseButtonText == "Close",
                "Exception error presentation preserves its title, message, and close label");
            InvokeDialogButton(error, "Close");
            Check(await errorTask == ContentDialogResult.None,
                "Closing an exception error summary returns the native dismissed result");
        }
        finally
        {
            FindDialogInRoot(owner.XamlRoot)?.Hide();
            secondWindow.Close();
        }
    }

    private static ContentDialog? FindDialogInRoot(XamlRoot root)
    {
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
        {
            var dialog = FindContentDialog(popup.Child);
            if (dialog is not null) return dialog;
        }
        return null;
    }

    private static void InvokeDialogButton(ContentDialog dialog, string text)
    {
        var button = FindButtonByContent(dialog, text);
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }
}
