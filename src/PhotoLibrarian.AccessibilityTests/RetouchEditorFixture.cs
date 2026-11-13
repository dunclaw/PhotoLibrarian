// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.ViewModels;
using PhotoLibrarian.Views;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PhotoLibrarian.AccessibilityTests;

/// <summary>Opt-in real-editor UI fixture. No MainViewModel, user preferences, library or personal photos.</summary>
internal static class RetouchEditorFixture
{
    internal static async Task<Window> OpenAsync(Application application)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-retouch-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Synthetic stoplight on noisy sky.png");
        var pixels = new byte[128 * 128 * 4];
        var reference = new byte[pixels.Length];
        var random = new Random(23175);
        for (var y = 0; y < 128; y++)
        for (var x = 0; x < 128; x++)
        {
            var i = (y * 128 + x) * 4;
            var blemish = x >= 62 && x <= 66 && y >= 60 && y <= 68;
            for (var channel = 0; channel < 3; channel++)
            {
                var noise = random.Next(-7, 8) + ((x + y) % 19 == 0 ? 16 : 0);
                var value = 110 + channel * 30 + 8 * Math.Sin(x * 0.1) + y * 0.2 + noise;
                reference[i + channel] = (byte)Math.Clamp(Math.Round(value), 0, 255);
                pixels[i + channel] = blemish ? (channel == 2 && y < 64 ? (byte)240 : (byte)10)
                    : reference[i + channel];
            }
            pixels[i + 3] = 255;
            reference[i + 3] = 255;
        }
        using (var stream = new InMemoryRandomAccessStream())
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, 128, 128, 96, 96, pixels);
            await encoder.FlushAsync();
            stream.Seek(0);
            using var output = File.Create(path);
            await stream.AsStreamForRead().CopyToAsync(output);
        }
        var original = await File.ReadAllBytesAsync(path);
        var vm = new ImageEditorViewModel(new OriginalBackupService(Path.Combine(directory, "originals")));
        await vm.OpenForEditAsync(new ImageEntry { FilePath = path, FileName = Path.GetFileName(path) });
        List<RetouchStroke> lastPaintedRecipe = [];
        vm.ParametersChanged += (_, _) =>
        {
            var recipe = vm.GetCurrentParameters().RetouchStrokes;
            if (recipe.Count > 0) lastPaintedRecipe = recipe;
        };
        var editor = new ImageEditorView(vm);
        var ribbon = new TopRibbon();
        ribbon.SetPhotoContext(hasPhoto: true, editorActive: false);
        ribbon.RetouchClicked += (_, _) => editor.SetRetouchWorkspace(true);
        ribbon.AdjustClicked += (_, _) => editor.SetRetouchWorkspace(false);
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.Children.Add(ribbon);
        layout.Children.Add(editor);
        Grid.SetRow(editor, 1);
        var window = new Window { Title = "Isolated spot retouch fixture", Content = layout };
        window.AppWindow.Resize(new SizeInt32(1920, 1200));
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.IsOpen) && !vm.IsOpen)
                editor.DispatcherQueue.TryEnqueue(window.Close);
        };
        window.Closed += OnWindowClosed;
        window.Activate();
        return window;

        async void OnWindowClosed(object sender, WindowEventArgs args)
        {
            var saved = !original.SequenceEqual(File.ReadAllBytes(path));
            bool centerHealed;
            double healedAreaRms;
            bool outsideMaskUnchanged;
            using (var savedStream = File.OpenRead(path).AsRandomAccessStream())
            {
                var decoder = await BitmapDecoder.CreateAsync(savedStream);
                var decoded = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    new BitmapTransform(), ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
                var savedPixels = decoded.DetachPixelData();
                double squaredError = 0;
                var sampleCount = 0;
                outsideMaskUnchanged = true;
                for (var y = 0; y < 128; y++)
                for (var x = 0; x < 128; x++)
                {
                    var inside = IsPainted(x, y);
                    for (var channel = 0; channel < 4; channel++)
                    {
                        var offset = (y * 128 + x) * 4 + channel;
                        if (x >= 62 && x <= 66 && y >= 60 && y <= 68 && channel < 3)
                        {
                            squaredError += Math.Pow(savedPixels[offset] - reference[offset], 2);
                            sampleCount++;
                        }
                        if (!inside)
                            outsideMaskUnchanged &= savedPixels[offset] == pixels[offset];
                    }
                }
                healedAreaRms = Math.Sqrt(squaredError / sampleCount);
                centerHealed = healedAreaRms < 10;
            }
            var result = new
            {
                saved,
                centerHealed,
                healedAreaRms,
                outsideMaskUnchanged,
                strokeCount = lastPaintedRecipe.Count,
                backupCreated = Directory.EnumerateFiles(Path.Combine(directory, "originals"), "*", SearchOption.AllDirectories).Any(),
                pendingRetouchCount = ImageEditService.ReadEditParameters(path).RetouchStrokes.Count,
                userLibraryOpened = false
            };
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "retouch-ui-results.json"),
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Directory.Delete(directory, recursive: true);
            application.Exit();
        }

        bool IsPainted(int x, int y)
        {
            foreach (var stroke in lastPaintedRecipe)
            for (var i = 0; i < stroke.Points.Length; i++)
            {
                var a = stroke.Points[Math.Max(0, i - 1)];
                var b = stroke.Points[i];
                var dx = (double)b.X - a.X;
                var dy = (double)b.Y - a.Y;
                var lengthSquared = dx * dx + dy * dy;
                var t = lengthSquared == 0 ? 0 : Math.Clamp(
                    ((x - a.X) * dx + (y - a.Y) * dy) / lengthSquared, 0, 1);
                var ex = x - a.X - t * dx;
                var ey = y - a.Y - t * dy;
                if (ex * ex + ey * ey <= stroke.Radius * stroke.Radius) return true;
            }
            return false;
        }
    }
}
