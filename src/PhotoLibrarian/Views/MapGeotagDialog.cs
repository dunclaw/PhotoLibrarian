// Copyright (c) Duncan Lawler. All Rights Reserved.

using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;

namespace PhotoLibrarian.Views;

internal sealed class MapGeotagDialog : ContentDialog
{
    public GeoCoordinate? Point { get; private set; }

    public MapGeotagDialog(string target, Uri? browseUri, Func<Uri, Task<string?>> launchMapAsync)
    {
        Title = ResourceStrings.Get("GeotagPasteMapTitle");
        PrimaryButtonText = ResourceStrings.Get("GeotagPasteMapApply");
        CloseButtonText = ResourceStrings.Get("GeotagPasteMapCancel");
        DefaultButton = ContentDialogButton.None;
        IsPrimaryButtonEnabled = false;
        AutomationProperties.SetAutomationId(this, "GeotagPasteMapDialog");
        Resources["ContentDialogMinWidth"] = 0d;
        Resources["ContentDialogButtonMinWidth"] = 0d;
        Resources["ContentDialogPadding"] = new Thickness(12);

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = target, TextWrapping = TextWrapping.Wrap });

        var input = new TextBox { Header = ResourceStrings.Get("GeotagPasteMapInput") };
        AutomationProperties.SetAutomationId(input, "GeotagPasteMapInput");
        var help = ResourceStrings.Get("GeotagPasteMapHelp");
        ToolTipService.SetToolTip(input, help);
        AutomationProperties.SetHelpText(input, help);
        var preview = CreateFeedbackText();
        AutomationProperties.SetAutomationId(preview, "GeotagPasteMapPreview");
        content.Children.Add(input);
        content.Children.Add(preview);

        if (browseUri is not null)
        {
            var browse = new HyperlinkButton { Content = ResourceStrings.Get("GeotagPasteMapBrowse") };
            AutomationProperties.SetAutomationId(browse, "GeotagPasteMapBrowse");
            var browseError = CreateFeedbackText();
            browse.Click += async (_, _) => SetFeedback(browseError, await launchMapAsync(browseUri) ?? "");
            content.Children.Add(browse);
            content.Children.Add(browseError);
        }

        Content = content;

        Opened += (_, _) => input.Focus(FocusState.Programmatic);
        input.TextChanged += (_, _) => PreviewPoint(input.Text, preview);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("ContentScrollViewer") is ScrollViewer scroll)
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
    }

    private void PreviewPoint(string text, TextBlock preview)
    {
        var valid = MapLinkService.TryParsePoint(text, out var coordinate);
        Point = valid ? coordinate : null;
        IsPrimaryButtonEnabled = valid;
        var message = valid
            ? ResourceStrings.Format("GeotagPasteMapCoordinates",
                coordinate.Latitude.ToString("R", CultureInfo.InvariantCulture),
                coordinate.Longitude.ToString("R", CultureInfo.InvariantCulture))
            : string.IsNullOrWhiteSpace(text) ? "" : ResourceStrings.Get("GeotagPasteMapInvalid");
        SetFeedback(preview, message);
    }

    private static TextBlock CreateFeedbackText()
    {
        var textBlock = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        AutomationProperties.SetLiveSetting(textBlock, AutomationLiveSetting.Polite);
        return textBlock;
    }

    private static void SetFeedback(TextBlock textBlock, string message)
    {
        textBlock.Text = message;
        textBlock.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (message.Length != 0)
        {
            var peer = FrameworkElementAutomationPeer.FromElement(textBlock) ??
                FrameworkElementAutomationPeer.CreatePeerForElement(textBlock);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }
}
