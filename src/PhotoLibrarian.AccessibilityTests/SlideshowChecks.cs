// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Views;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI.ViewManagement;

namespace PhotoLibrarian.AccessibilityTests;

public partial class App
{
    private async Task RunSlideshowTestsAsync()
    {
        string? failure = null;
        var landscapePath = Path.Combine(AppContext.BaseDirectory, "slideshow-synthetic-landscape.png");
        var portraitPath = Path.Combine(AppContext.BaseDirectory, "slideshow-synthetic-portrait.png");
        try
        {
            await CreateTestPhotoAsync(landscapePath, 960, 640);
            await CreateTestPhotoAsync(portraitPath, 640, 960);
            var before = SHA256.HashData(await File.ReadAllBytesAsync(landscapePath));
            var sequence = new SlideshowSequence(
                [TestPhoto(landscapePath), TestPhoto(portraitPath)], []);
            var slideshow = new SlideshowWindow(sequence, 60);
            _window = slideshow;
            slideshow.Activate();
            var vm = slideshow.ViewModel;
            await WaitForPhotoAsync(slideshow);
            var root = (FrameworkElement)slideshow.Content;
            var picker = (ComboBox)root.FindName("ThemePicker");
            var viewport = (Grid)root.FindName("PhotoViewport");
            var layer = (Grid)root.FindName("CurrentPhotoLayer");
            var image = (Image)root.FindName("CurrentPhotoImage");
            var oldImage = (Image)root.FindName("PreviousPhotoImage");
            var visual = ElementCompositionPreview.GetElementVisual(layer);
            Check(picker.ActualWidth >= 168 && picker.Items.Count == 6 &&
                AutomationProperties.GetAutomationId(picker) == "SlideshowTheme" &&
                ((ComboBoxItem)picker.SelectedItem).Tag.ToString() == "Contemporary",
                "Visible slideshow selector exposes all six themes and the current selection");
            CheckCenteredPhoto(viewport, layer, image);
            var offset = visual.Offset;
            await Task.Delay(900);
            Check(visual.Offset == offset && visual.Scale == Vector3.One,
                "Contemporary stays centered and stationary during its dwell");
            await CaptureSlideshowAsync(root, "slideshow-contemporary.png");

            vm.IsPaused = true;
            await vm.NextCommand.ExecuteAsync(null);
            await WaitForPhotoAsync(slideshow);
            CheckCenteredPhoto(viewport, layer, image);
            Check(sequence.Index == 1 && vm.IsPaused && vm.IntervalSeconds == 60 &&
                oldImage.Source is null, "Portrait navigation preserves pause/timing and releases the outgoing image");

            foreach (var theme in Enum.GetValues<SlideshowTheme>())
            {
                picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().Single(
                    item => item.Tag.ToString() == theme.ToString());
                await WaitForPhotoAsync(slideshow);
                Check(vm.Theme == theme && sequence.Index == 1 && vm.IsPaused &&
                    vm.IntervalSeconds == 60 && image.Source == vm.CurrentImage,
                    $"Live {theme} selection reloads the same photo without changing playback state");
            }

            await vm.ChangeThemeAsync(SlideshowTheme.Contemporary);
            await WaitForPhotoAsync(slideshow);
            await vm.PreviousCommand.ExecuteAsync(null);
            await WaitForPhotoAsync(slideshow);
            CheckCenteredPhoto(viewport, layer, image);
            Check(visual.Offset == offset && visual.Scale == Vector3.One,
                "Returning to Contemporary restores the centered neutral pose");

            vm.IntervalSeconds = 1;
            vm.IsPaused = false;
            var advanceDeadline = DateTime.UtcNow.AddSeconds(10);
            while (sequence.Index == 0)
            {
                if (DateTime.UtcNow >= advanceDeadline)
                    throw new TimeoutException("Automatic advancement did not select the next photo.");
                await Task.Delay(20);
            }
            vm.IsPaused = true;
            await WaitForPhotoAsync(slideshow);
            CheckCenteredPhoto(viewport, layer, image);
            Check(sequence.Index == 1 && oldImage.Source is null,
                "Automatic Contemporary transition leaves the next photo centered and releases the previous frame");
            await vm.PreviousCommand.ExecuteAsync(null);
            await WaitForPhotoAsync(slideshow);

            if (new UISettings().AnimationsEnabled)
            {
                await vm.ChangeThemeAsync(SlideshowTheme.PanAndZoom);
                await WaitForPhotoAsync(slideshow);
                vm.IntervalSeconds = 5;
                vm.IsPaused = false;
                await Task.Delay(500);
                var controller = visual.TryGetAnimationController("Scale");
                Check(controller is not null && visual.Offset == offset &&
                    visual.CenterPoint == new Vector3((float)viewport.ActualWidth / 2,
                        (float)viewport.ActualHeight / 2, 0),
                    "Pan and Zoom runs around the viewport center without changing layout Offset");
                vm.IsPaused = true;
                await Task.Delay(100);
                var pausedFrame = await ReadRenderedPixelsAsync(viewport);
                await Task.Delay(300);
                var heldFrame = await ReadRenderedPixelsAsync(viewport);
                Check(pausedFrame.SequenceEqual(heldFrame),
                    "Pause freezes motion instead of resetting the photo position");
                vm.IsPaused = false;
                await Task.Delay(300);
                var resumedFrame = await ReadRenderedPixelsAsync(viewport);
                Check(!heldFrame.SequenceEqual(resumedFrame),
                    "Resume continues motion from the paused frame");
                vm.IsPaused = true;
            }
            else
            {
                Check(visual.Scale == Vector3.One, "Reduced-motion setting leaves the photo stationary");
            }

            var superseded = vm.ChangeThemeAsync(SlideshowTheme.Sepia);
            var latest = vm.ChangeThemeAsync(SlideshowTheme.Fade);
            await Task.WhenAll(superseded, latest);
            await WaitForPhotoAsync(slideshow);
            Check(vm.Theme == SlideshowTheme.Fade && !vm.IsLoading && image.Source == vm.CurrentImage,
                "Rapid theme changes publish only the latest render");
            var after = SHA256.HashData(await File.ReadAllBytesAsync(landscapePath));
            Check(before.SequenceEqual(after),
                "Rendering and theme changes leave source bytes unchanged");
            var pending = vm.LoadAsync();
            slideshow.Close();
            await pending;
            Check(vm.CurrentImage is null && image.Source is null && oldImage.Source is null &&
                !vm.IsLoading, "Close cancels pending loads and releases both photo layers");
            _window = null!;
        }
        catch (Exception exception)
        {
            failure = exception.ToString();
        }
        finally
        {
            _window?.Close();
            File.Delete(landscapePath);
            File.Delete(portraitPath);
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "slideshow-results.json"),
                JsonSerializer.Serialize(new { passed = _passed, failure },
                    new JsonSerializerOptions { WriteIndented = true }));
            Exit();
        }
    }

    private static ImageEntry TestPhoto(string path) =>
        new() { FilePath = path, FileName = Path.GetFileName(path), MediaType = MediaType.Image };

    private void CheckCenteredPhoto(Grid viewport, Grid layer, Image image)
    {
        var origin = image.TransformToVisual(viewport).TransformPoint(new Windows.Foundation.Point());
        var centerX = origin.X + image.ActualWidth / 2;
        var centerY = origin.Y + image.ActualHeight / 2;
        var visual = ElementCompositionPreview.GetElementVisual(layer);
        Check(Math.Abs(centerX - viewport.ActualWidth / 2) < 1 &&
            Math.Abs(centerY - viewport.ActualHeight / 2) < 1 &&
            visual.Offset.X == 0 && visual.Offset.Y == 0 && visual.Scale == Vector3.One,
            "Rendered photo is centered in the viewport without a Composition layout override");
    }

    private static async Task WaitForPhotoAsync(SlideshowWindow window)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (window.ViewModel.IsLoading || window.ViewModel.CurrentImage is null)
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"Slideshow did not render: {window.ViewModel.Status}");
            await Task.Delay(50);
        }
        await Task.Delay(500);
    }

    private static async Task CreateTestPhotoAsync(string path, uint width, uint height)
    {
        var pixels = new byte[width * height * 4];
        for (uint y = 0; y < height; y++)
        {
            for (uint x = 0; x < width; x++)
            {
                var index = (y * width + x) * 4;
                var center = Math.Abs((int)x - (int)width / 2) < 4 ||
                    Math.Abs((int)y - (int)height / 2) < 4;
                pixels[index] = center ? (byte)255 : (byte)96;
                pixels[index + 1] = center ? (byte)255 : (byte)(x < width / 2 ? 160 : 80);
                pixels[index + 2] = center ? (byte)255 : (byte)(y < height / 2 ? 48 : 176);
                pixels[index + 3] = 255;
            }
        }
        using var stream = File.Create(path).AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            width, height, 96, 96, pixels);
        await encoder.FlushAsync();
    }

    private static async Task CaptureSlideshowAsync(FrameworkElement root, string name)
    {
        var capture = new RenderTargetBitmap();
        await capture.RenderAsync(root);
        var pixels = await capture.GetPixelsAsync();
        using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, name)).AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        using var reader = DataReader.FromBuffer(pixels);
        var bytes = new byte[pixels.Length];
        reader.ReadBytes(bytes);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)capture.PixelWidth, (uint)capture.PixelHeight, 96, 96, bytes);
        await encoder.FlushAsync();
    }

    private static async Task<byte[]> ReadRenderedPixelsAsync(FrameworkElement root)
    {
        var capture = new RenderTargetBitmap();
        await capture.RenderAsync(root);
        var pixels = await capture.GetPixelsAsync();
        using var reader = DataReader.FromBuffer(pixels);
        var bytes = new byte[pixels.Length];
        reader.ReadBytes(bytes);
        return bytes;
    }
}
