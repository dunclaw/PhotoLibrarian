// Copyright (c) Duncan Lawler. All Rights Reserved.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using PhotoLibrarian.Views;
using Windows.Graphics;

namespace PhotoLibrarian.AccessibilityTests;

public partial class App
{
    private async Task<bool> RunMapGeotagDialogTestsAsync()
    {
        var args = Environment.GetCommandLineArgs();
        bool dark = args.Contains("--dark");
        var root = new Grid { RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light };
        _window = new Window { Title = "Map geotag dialog test", Content = root };
        _window.AppWindow.Resize(dark ? new SizeInt32(400, 460) : new SizeInt32(640, 600));
        _window.Activate();
        await UntilAsync(() => root.IsLoaded);

        int launchCount = 0;
        var dialog = new MapGeotagDialog(
            ResourceStrings.Format("GeotagPasteMapTargetMultiple", 2),
            MapLinkService.GetBrowseUri(MapProvider.Google),
            _ =>
            {
                launchCount++;
                return Task.FromResult<string?>(null);
            })
        {
            XamlRoot = root.XamlRoot,
            RequestedTheme = root.RequestedTheme
        };
        var task = dialog.ShowAsync().AsTask();
        var content = (StackPanel)dialog.Content;
        var input = content.Children.OfType<TextBox>().Single();
        var preview = content.Children.OfType<TextBlock>().Single(
            text => AutomationProperties.GetAutomationId(text) == "GeotagPasteMapPreview");
        await UntilAsync(() => Equals(FocusManager.GetFocusedElement(root.XamlRoot), input));
        const string pin = "https://www.openstreetmap.org/?mlat=-33.875&mlon=151.25#map=9/-34/152";

        Check(!dialog.IsPrimaryButtonEnabled && dialog.Point is null, "Blank link cannot apply a geotag");
        input.Text = pin;
        await UntilAsync(() => dialog.IsPrimaryButtonEnabled);
        Check(dialog.Point == new GeoCoordinate(-33.875, 151.25) &&
            preview.Text.Contains("-33.875") && preview.Text.Contains("151.25"),
            "The live preview shows the pin coordinates rather than the viewport");
        Check(launchCount == 0, "Opening the dialog and pasting never open a browser");
        if (args.Contains("--preview")) return false;

        var browse = content.Children.OfType<HyperlinkButton>().Single();
        var browsePeer = FrameworkElementAutomationPeer.CreatePeerForElement(browse);
        ((IInvokeProvider)browsePeer.GetPattern(PatternInterface.Invoke)).Invoke();
        await UntilAsync(() => launchCount == 1);
        Check(dialog.Point == new GeoCoordinate(-33.875, 151.25), "The optional browser action does not change the preview");

        input.Text = "https://www.openstreetmap.org/#map=9/-34/152";
        await UntilAsync(() => !dialog.IsPrimaryButtonEnabled);
        Check(dialog.Point is null && preview.Text == ResourceStrings.Get("GeotagPasteMapInvalid"),
            "An invalid edit clears the prior preview and disables Apply");
        input.Text = pin;
        await UntilAsync(() => dialog.IsPrimaryButtonEnabled);
        Check(dialog.DefaultButton == ContentDialogButton.None, "Apply is not the dialog default");
        if (dark)
        {
            InvokeDialogButton(dialog, dialog.PrimaryButtonText);
            Check(await task == ContentDialogResult.Primary, "Explicit Apply confirms the pin");
        }
        else
        {
            InvokeDialogButton(dialog, dialog.CloseButtonText);
            Check(await task == ContentDialogResult.None, "Cancel dismisses an otherwise valid preview");
        }
        return true;
    }
}
