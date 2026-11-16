// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using PhotoLibrarian.ViewModels;
using PhotoLibrarian.Views;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PhotoLibrarian.AccessibilityTests;

public partial class App
{
    private async Task RunEditPresetsTestsAsync()
    {
        string? failure = null;
        var directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-presets-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        ImageViewerViewModel? viewModel = null;
        ImageViewerOverlay? viewer = null;
        PhotoPresetStore? store = null;
        try
        {
            var photoPath = Path.Combine(directory, "Synthetic preset preview.png");
            await WriteSyntheticPhotoAsync(photoPath);
            var entry = new ImageEntry
            {
                Id = 1,
                FilePath = photoPath,
                FileName = Path.GetFileName(photoPath),
                FileSize = new FileInfo(photoPath).Length,
                DateModified = File.GetLastWriteTimeUtc(photoPath),
                MediaType = MediaType.Image
            };
            viewModel = new ImageViewerViewModel();
            viewModel.OpenImage(entry, [entry]);
            store = new PhotoPresetStore(Path.Combine(directory, "presets.json"));
            var preset = new PhotoPreset(
                Guid.NewGuid().ToString("N"),
                "Synthetic exposure",
                EditSettingGroup.Tone,
                new EditParameters { Exposure = 0.4 },
                null,
                false);
            store.Save(preset);

            viewer = new ImageViewerOverlay(viewModel, store);
            var anchor = new Button { Content = "Preset UI test anchor" };
            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition());
            layout.Children.Add(anchor);
            layout.Children.Add(viewer);
            Grid.SetRow(viewer, 1);
            _window = new Window { Title = "Synthetic preset acceptance", Content = layout };
            _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 800));
            _window.Activate();
            await UntilAsync(() => viewer.IsLoaded && viewModel.CurrentImage is not null);

            var originalBytes = await File.ReadAllBytesAsync(photoPath);
            var originalIdentity = await FileEditTransaction.ReadIdentityAsync(photoPath);
            await viewer.ShowEffectsAsync(anchor);
            var presetGallery = (ListView)viewer.FindName("PresetGallery");
            var amountSlider = (Slider)viewer.FindName("PresetAmountSlider");
            var amountLabel = (TextBlock)viewer.FindName("PresetAmountValue");
            var previewImage = (Image)viewer.FindName("PresetPreviewImage");
            var previewStatus = (TextBlock)viewer.FindName("PresetsStatus");
            presetGallery.SelectedIndex = 0;
            await UntilAsync(() => previewImage.Source is not null &&
                previewStatus.Text.Contains("Preview", StringComparison.OrdinalIgnoreCase));
            if (Environment.GetCommandLineArgs().Contains("--preview"))
                await Task.Delay(TimeSpan.FromSeconds(10));

            Check(amountSlider.Minimum == 0 && amountSlider.Maximum == 200,
                "Preset Amount slider exposes the full 0–200 percent range");
            var half = await SetAmountAndCaptureAsync(amountSlider, amountLabel, previewImage, 50);
            var oneAndHalf = await SetAmountAndCaptureAsync(amountSlider, amountLabel, previewImage, 150);
            var halfAgain = await SetAmountAndCaptureAsync(amountSlider, amountLabel, previewImage, 50);
            Check(half.SequenceEqual(halfAgain) && !half.SequenceEqual(oneAndHalf),
                "Repeated Amount previews use a stable opening recipe instead of compounding");

            amountSlider.Value = 0;
            await UntilAsync(() => previewImage.Source is null &&
                previewImage.Visibility == Visibility.Collapsed);
            var bytesAfterPreview = await File.ReadAllBytesAsync(photoPath);
            Check(await FileEditTransaction.ReadIdentityAsync(photoPath) == originalIdentity &&
                originalBytes.SequenceEqual(bytesAfterPreview),
                "Zero Amount and preview-only changes leave the source photo unchanged");

            var favoriteId = ImageViewerOverlay.PresetFavoriteAutomationId(preset.Id);
            await UntilAsync(() => FindElement(presetGallery, favoriteId) is not null);
            var favorite = FindControl(presetGallery, favoriteId);
            var favoritePeer = FrameworkElementAutomationPeer.CreatePeerForElement(favorite);
            Check(!string.IsNullOrWhiteSpace(favoritePeer.GetName()) &&
                favoritePeer.GetPattern(PatternInterface.Toggle) is IToggleProvider,
                "Favorite action exposes an accessible Toggle control");
            ((IToggleProvider)favoritePeer.GetPattern(PatternInterface.Toggle)).Toggle();
            await UntilAsync(() => store.Load().Presets.Single().IsFavorite);
            Check(((ToggleButton)favorite).IsChecked == true,
                "Favorite toggle updates and reloads the persistent preset catalog");

            var deleteId = ImageViewerOverlay.PresetDeleteAutomationId(preset.Id);
            await UntilAsync(() => FindElement(presetGallery, deleteId) is not null);
            var delete = FindControl(presetGallery, deleteId);
            var deletePeer = FrameworkElementAutomationPeer.CreatePeerForElement(delete);
            Check(!string.IsNullOrWhiteSpace(deletePeer.GetName()) &&
                deletePeer.GetPattern(PatternInterface.Invoke) is IInvokeProvider,
                "Delete action exposes an accessible Invoke control");
            ((IInvokeProvider)deletePeer.GetPattern(PatternInterface.Invoke)).Invoke();
            await UntilAsync(() => FindOpenContentDialog() is not null);
            var deleteDialog = FindOpenContentDialog()!;
            var confirmDelete = FindButtonByContent(deleteDialog, "Delete preset");
            var confirmPeer = FrameworkElementAutomationPeer.CreatePeerForElement(confirmDelete);
            Check(!string.IsNullOrWhiteSpace(confirmPeer.GetName()),
                "Preset deletion requires a named confirmation action");
            ((IInvokeProvider)confirmPeer.GetPattern(PatternInterface.Invoke)).Invoke();
            await UntilAsync(() => store.Load().Presets.Count == 0);

            await UntilAsync(() => FindDialogControl("EditorSavePreset") is not null);
            var savePreset = FindDialogControl("EditorSavePreset")!;
            var savePresetPeer = FrameworkElementAutomationPeer.CreatePeerForElement(savePreset);
            Check(!string.IsNullOrWhiteSpace(savePresetPeer.GetName()) &&
                savePresetPeer.GetPattern(PatternInterface.Invoke) is IInvokeProvider,
                "Save preset action exposes an accessible Invoke control");
            ((IInvokeProvider)savePresetPeer.GetPattern(PatternInterface.Invoke)).Invoke();
            await UntilAsync(() => FindOpenContentDialog() is not null &&
                FindDialogControl("EditPresetName") is not null);
            var wiredSaveDialog = FindOpenContentDialog()!;
            var wiredNameBox = (TextBox)FindDialogControl("EditPresetName")!;
            Check(!string.IsNullOrWhiteSpace(
                    FrameworkElementAutomationPeer.CreatePeerForElement(wiredNameBox).GetName()),
                "Save preset action opens the named selective-group dialog");
            var cancelSaveDialog = FindButtonByContent(wiredSaveDialog, "Cancel");
            var cancelPeer = FrameworkElementAutomationPeer.CreatePeerForElement(cancelSaveDialog);
            ((IInvokeProvider)cancelPeer.GetPattern(PatternInterface.Invoke)).Invoke();
            await UntilAsync(() => FindOpenContentDialog() is null);

            var groupDialogTask = EditSettingGroupDialog.ShowAsync(
                viewer.XamlRoot, viewer.ActualTheme, "Save synthetic preset",
                EditSettingGroup.All, includeName: true);
            await UntilAsync(() => FindOpenContentDialog() is not null &&
                FindDialogControl("EditPresetName") is not null);
            var groupDialog = FindOpenContentDialog()!;
            var nameBox = (TextBox)FindDialogControl("EditPresetName")!;
            var namePeer = FrameworkElementAutomationPeer.CreatePeerForElement(nameBox);
            Check(!string.IsNullOrWhiteSpace(namePeer.GetName()),
                "Preset name field is accessible");
            nameBox.Text = "Tone only";
            foreach (var group in Enum.GetValues<EditSettingGroup>()
                .Where(group => group is not (EditSettingGroup.None or EditSettingGroup.All)))
            {
                var checkBox = (CheckBox)FindDialogControl($"EditPresetGroup{group}")!;
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(checkBox);
                Check(!string.IsNullOrWhiteSpace(peer.GetName()) &&
                    !string.IsNullOrWhiteSpace(AutomationProperties.GetAutomationId(checkBox)),
                    $"{group} preset group is labeled and addressable");
                checkBox.IsChecked = group == EditSettingGroup.Tone;
            }
            var saveGroupSelection = FindButtonByContent(groupDialog, "Save preset");
            var savePeer = FrameworkElementAutomationPeer.CreatePeerForElement(saveGroupSelection);
            ((IInvokeProvider)savePeer.GetPattern(PatternInterface.Invoke)).Invoke();
            var groupResult = await groupDialogTask;
            Check(groupResult is { Name: "Tone only", Groups: EditSettingGroup.Tone },
                "Selective-group dialog returns only the chosen group and trimmed name");

            var effectsFlyout = (Flyout)viewer.FindName("EffectsFlyout");
            effectsFlyout.Hide();
            await UntilAsync(() => previewImage.Source is null);
            var bytesAfterCancel = await File.ReadAllBytesAsync(photoPath);
            Check(await FileEditTransaction.ReadIdentityAsync(photoPath) == originalIdentity &&
                originalBytes.SequenceEqual(bytesAfterCancel),
                "Closing preview cancels rendering without changing photo data");
        }
        catch (Exception exception)
        {
            failure = exception.ToString();
        }
        finally
        {
            try
            {
                if (viewer is not null && viewer.FindName("EffectsFlyout") is Flyout flyout)
                    flyout.Hide();
                viewModel?.CloseCommand.Execute(null);
                await Task.Delay(150);
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception)
            {
                failure ??= $"The synthetic fixture could not be cleaned up: {exception.Message}";
            }
        }

        await FinishAsync(failure);
    }

    private static async Task<byte[]> SetAmountAndCaptureAsync(
        Slider slider,
        TextBlock label,
        Image preview,
        double amount)
    {
        var previous = preview.Source;
        slider.Value = amount;
        await UntilAsync(() => label.Text == $"{amount:0}%" &&
            preview.Source is not null && !ReferenceEquals(previous, preview.Source));
        var bitmap = (WriteableBitmap)preview.Source;
        return bitmap.PixelBuffer.ToArray();
    }

    private ContentDialog? FindOpenContentDialog()
    {
        if (_window?.Content?.XamlRoot is not { } root) return null;
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
        {
            var dialog = FindContentDialog(popup.Child);
            if (dialog is not null) return dialog;
        }
        return null;
    }

    private static ContentDialog? FindContentDialog(DependencyObject root)
    {
        if (root is ContentDialog dialog) return dialog;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var found = FindContentDialog(VisualTreeHelper.GetChild(root, index));
            if (found is not null) return found;
        }
        return null;
    }

    private Control? FindDialogControl(string id)
    {
        if (_window?.Content?.XamlRoot is not { } root) return null;
        return VisualTreeHelper.GetOpenPopupsForXamlRoot(root)
            .Select(popup => FindElement(popup.Child, id))
            .FirstOrDefault(control => control is not null);
    }

    private static Button FindButtonByContent(DependencyObject root, string text) =>
        FindButton(root, text) ?? throw new InvalidOperationException($"Button '{text}' not found.");

    private static Button? FindButton(DependencyObject root, string text)
    {
        if (root is Button button && string.Equals(button.Content?.ToString(), text, StringComparison.Ordinal))
            return button;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var found = FindButton(VisualTreeHelper.GetChild(root, index), text);
            if (found is not null) return found;
        }
        return null;
    }

    private static async Task WriteSyntheticPhotoAsync(string path)
    {
        const uint width = 96;
        const uint height = 72;
        var pixels = new byte[(int)(width * height * 4)];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = (int)((y * width + x) * 4);
            pixels[offset] = (byte)(x * 255 / width);
            pixels[offset + 1] = (byte)(y * 255 / height);
            pixels[offset + 2] = (byte)((x + y) * 255 / (width + height));
            pixels[offset + 3] = 255;
        }
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            width, height, 96, 96, pixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        using var output = File.Create(path);
        await stream.AsStreamForRead().CopyToAsync(output);
    }
}
