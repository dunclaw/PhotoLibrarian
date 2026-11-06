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
        var path = Path.Combine(directory, "Synthetic smooth-surface blemish.png");
        var pixels = new byte[128 * 128 * 4];
        for (var y = 0; y < 128; y++)
        for (var x = 0; x < 128; x++)
        {
            var i = (y * 128 + x) * 4;
            var blemish = (x - 64) * (x - 64) + (y - 64) * (y - 64) < 16;
            pixels[i] = blemish ? (byte)10 : (byte)90;
            pixels[i + 1] = blemish ? (byte)10 : (byte)140;
            pixels[i + 2] = blemish ? (byte)10 : (byte)190;
            pixels[i + 3] = 255;
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
        var editor = new ImageEditorView(vm);
        var ribbon = new TopRibbon();
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
            using (var savedStream = File.OpenRead(path).AsRandomAccessStream())
            {
                var decoder = await BitmapDecoder.CreateAsync(savedStream);
                var decoded = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    new BitmapTransform(), ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
                var savedPixels = decoded.DetachPixelData();
                var centerOffset = (64 * 128 + 64) * 4;
                centerHealed = savedPixels.AsSpan(centerOffset, 4).SequenceEqual(new byte[] { 90, 140, 190, 255 });
            }
            var result = new
            {
                saved,
                centerHealed,
                backupCreated = Directory.EnumerateFiles(Path.Combine(directory, "originals"), "*", SearchOption.AllDirectories).Any(),
                pendingRetouchCount = ImageEditService.ReadEditParameters(path).RetouchStrokes.Count,
                userLibraryOpened = false
            };
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "retouch-ui-results.json"),
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Directory.Delete(directory, recursive: true);
            application.Exit();
        }
    }
}
