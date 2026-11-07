// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Views;
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
            var scale = (ScaleTransform)root.FindName("CurrentPhotoScale");
            var toolbar = (Grid)root.FindName("SlideshowToolbar");
            Check(picker.ActualWidth >= 168 && picker.Items.Count == 6 &&
                AutomationProperties.GetAutomationId(picker) == "SlideshowTheme" &&
                ((ComboBoxItem)picker.SelectedItem).Tag.ToString() == "Contemporary",
                "Visible slideshow selector exposes all six themes and the current selection");
            Check(toolbar.ActualHeight <= 48 && string.IsNullOrEmpty(picker.Header as string) &&
                viewport.ActualHeight >= root.ActualHeight - 48,
                $"Controls occupy a single compact row of at most 48 DIPs (toolbar={toolbar.ActualHeight}, viewport={viewport.ActualHeight}, root={root.ActualHeight})");
            CheckCenteredPhoto(viewport, layer, image);
            var initialFrame = await ReadRenderedPixelsAsync(viewport);
            await Task.Delay(900);
            var contemporaryFrame = await ReadRenderedPixelsAsync(viewport);
            if (new UISettings().AnimationsEnabled)
            {
                Check(!initialFrame.SequenceEqual(contemporaryFrame) && scale.ScaleX > 1 &&
                    scale.ScaleX <= 1.04, "Contemporary visibly zooms gently during its dwell");
            }
            CheckCenteredPhoto(viewport, layer, image);
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
            Check(scale.ScaleX == 1 && scale.ScaleY == 1,
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
                Check(scale.ScaleX > 1 && scale.ScaleX <= 1.12 &&
                    layer.RenderTransformOrigin == new Windows.Foundation.Point(0.5, 0.5),
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
                Check(scale.ScaleX == 1, "Reduced-motion setting leaves the photo stationary");
            }

            await vm.ChangeThemeAsync(SlideshowTheme.PanAndZoom);
            await WaitForPhotoAsync(slideshow);
            vm.IntervalSeconds = 1;
            vm.IsPaused = false;
            var automaticPhotos = 0;
            vm.PropertyChanged += CountAutomaticPhoto;
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (automaticPhotos < 6)
                {
                    if (DateTime.UtcNow >= deadline)
                        throw new TimeoutException($"Pan and Zoom completed only {automaticPhotos} transitions.");
                    await Task.Delay(50);
                }
            }
            finally
            {
                vm.PropertyChanged -= CountAutomaticPhoto;
            }
            vm.IsPaused = true;
            await WaitForPhotoAsync(slideshow);
            Check(image.Source == vm.CurrentImage && oldImage.Source is null,
                "Pan and Zoom survives six automatic transitions after full motion completion");

            vm.IntervalSeconds = 5;
            vm.IsPaused = false;
            automaticPhotos = 0;
            vm.PropertyChanged += CountAutomaticPhoto;
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(25);
                while (automaticPhotos < 3)
                {
                    if (DateTime.UtcNow >= deadline)
                        throw new TimeoutException($"Five-second Pan and Zoom completed only {automaticPhotos} transitions.");
                    await Task.Delay(50);
                }
            }
            finally
            {
                vm.PropertyChanged -= CountAutomaticPhoto;
            }
            vm.IsPaused = true;
            await WaitForPhotoAsync(slideshow);
            Check(image.Source == vm.CurrentImage && oldImage.Source is null,
                "Pan and Zoom survives three full five-second automatic transitions");

            void CountAutomaticPhoto(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(vm.CurrentImage)) automaticPhotos++;
            }

            vm.IsPaused = false;
            var superseded = vm.ChangeThemeAsync(SlideshowTheme.Sepia);
            var latest = vm.ChangeThemeAsync(SlideshowTheme.Fade);
            await Task.WhenAll(superseded, latest);
            await WaitForPhotoAsync(slideshow);
            Check(vm.Theme == SlideshowTheme.Fade && !vm.IsLoading && image.Source == vm.CurrentImage,
                "Rapid theme changes during playback publish only the latest render");
            vm.IsPaused = true;
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
        var transform = image.TransformToVisual(viewport);
        var origin = transform.TransformPoint(new Windows.Foundation.Point());
        var opposite = transform.TransformPoint(
            new Windows.Foundation.Point(image.ActualWidth, image.ActualHeight));
        var centerX = (origin.X + opposite.X) / 2;
        var centerY = (origin.Y + opposite.Y) / 2;
        Check(Math.Abs(centerX - viewport.ActualWidth / 2) < 1 &&
            Math.Abs(centerY - viewport.ActualHeight / 2) < 1 &&
            layer.RenderTransformOrigin == new Windows.Foundation.Point(0.5, 0.5),
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
