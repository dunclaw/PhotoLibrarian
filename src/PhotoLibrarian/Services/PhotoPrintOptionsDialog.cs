// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using System.Runtime.InteropServices.WindowsRuntime;

namespace PhotoLibrarian.Services;

internal sealed class PhotoPrintOptionsDialog
{
    private readonly Window _window;
    private readonly IReadOnlyList<ImageEntry> _photos;

    public PhotoPrintOptionsDialog(Window window, IReadOnlyList<ImageEntry> photos)
    {
        _window = window;
        _photos = photos;
    }

    public async Task<(PhotoPrintLayout Layout, PhotoPrintPlacement Placement)?> ShowAsync(
        CancellationToken cancellationToken)
    {
        var sample = await PhotoPrintImageDecoder.DecodeAsync(
            _photos[0].FilePath,
            480,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var layoutChoices = new[]
        {
            new LayoutChoice(PhotoPrintLayout.FullPage, PhotoPrintStrings.Get("PrintLayoutFullPage")),
            new LayoutChoice(PhotoPrintLayout.FourBySix, PhotoPrintStrings.Get("PrintLayoutFourBySix")),
            new LayoutChoice(PhotoPrintLayout.FiveBySeven, PhotoPrintStrings.Get("PrintLayoutFiveBySeven")),
            new LayoutChoice(PhotoPrintLayout.EightByTen, PhotoPrintStrings.Get("PrintLayoutEightByTen")),
            new LayoutChoice(PhotoPrintLayout.Wallet, PhotoPrintStrings.Get("PrintLayoutWallet")),
            new LayoutChoice(PhotoPrintLayout.ContactSheet, PhotoPrintStrings.Get("PrintLayoutContactSheet"))
        };
        var layoutSelector = new ComboBox
        {
            Header = PhotoPrintStrings.Get("PrintLayoutHeader"),
            ItemsSource = layoutChoices,
            DisplayMemberPath = nameof(LayoutChoice.Name),
            SelectedIndex = 0,
            MinWidth = 280
        };
        AutomationProperties.SetName(layoutSelector, PhotoPrintStrings.Get("PrintLayoutHeader"));
        AutomationProperties.SetAutomationId(layoutSelector, "PrintLayoutSelector");

        var placementSelector = new RadioButtons
        {
            Header = PhotoPrintStrings.Get("PrintPlacementHeader"),
            ItemsSource = new[]
            {
                PhotoPrintStrings.Get("PrintPlacementFit"),
                PhotoPrintStrings.Get("PrintPlacementFill")
            },
            SelectedIndex = 0
        };
        AutomationProperties.SetName(placementSelector, PhotoPrintStrings.Get("PrintPlacementHeader"));
        AutomationProperties.SetAutomationId(placementSelector, "PrintPlacementSelector");

        var preview = new WriteableBitmap(checked((int)sample.Width), checked((int)sample.Height));
        using (var stream = preview.PixelBuffer.AsStream())
            await stream.WriteAsync(sample.Pixels.AsMemory(), cancellationToken);
        preview.Invalidate();

        var image = new Image
        {
            Source = preview,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            MaxWidth = 320,
            MaxHeight = 220
        };
        AutomationProperties.SetName(image, _photos[0].FileName);

        var sampleName = new TextBlock
        {
            Text = PhotoPrintStrings.Format(
                _photos.Count == 1 ? "PrintSamplePhotoSingle" : "PrintSamplePhotoMultiple",
                _photos[0].FileName,
                _photos.Count),
            TextWrapping = TextWrapping.Wrap
        };
        var policy = new TextBlock
        {
            Text = PhotoPrintStrings.Get("PrintPlacementPolicy"),
            TextWrapping = TextWrapping.Wrap
        };
        var note = new TextBlock
        {
            Text = PhotoPrintStrings.Get("PrintPreviewNote"),
            TextWrapping = TextWrapping.Wrap
        };

        var content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                sampleName,
                image,
                layoutSelector,
                placementSelector,
                policy,
                note
            }
        };
        var dialog = new ContentDialog
        {
            XamlRoot = _window.Content.XamlRoot,
            Title = PhotoPrintStrings.Get("PrintTitle"),
            Content = content,
            PrimaryButtonText = PhotoPrintStrings.Get("PrintContinue"),
            CloseButtonText = PhotoPrintStrings.Get("PrintCancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary ||
            layoutSelector.SelectedItem is not LayoutChoice choice)
            return null;

        var placement = placementSelector.SelectedIndex == 1
            ? PhotoPrintPlacement.Fill
            : PhotoPrintPlacement.Fit;
        return (choice.Layout, placement);
    }

    private sealed record LayoutChoice(PhotoPrintLayout Layout, string Name);
}
