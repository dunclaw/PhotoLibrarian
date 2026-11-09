// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using PhotoLibrarian.Views;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.ViewModels;
using Windows.Graphics.Imaging;

namespace PhotoLibrarian.AccessibilityTests;

/// <summary>Exercises the real viewer workspace with synthetic faces; no live library or models.</summary>
internal static class PhotoFuseUiTests
{
    internal static async Task RunAsync(Grid root, List<string> passed, bool previewOnly)
    {
        root.RequestedTheme = Environment.GetCommandLineArgs().Contains("--dark") ? ElementTheme.Dark : ElementTheme.Light;
        // Root-only theme overrides need an opaque backing independent of the Window's system theme.
        root.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            root.RequestedTheme == ElementTheme.Dark ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White);
        string folder = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-FuseUi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            string[] paths = [Path.Combine(folder, "base.png"), Path.Combine(folder, "donor.png")];
            for (int frame = 0; frame < 2; frame++) await WriteFixtureAsync(paths[frame], frame);
            var photos = paths.Select((path, i) => new ImageEntry
            {
                Id = i + 1, FilePath = path, FileName = Path.GetFileName(path),
                FileSize = new FileInfo(path).Length, DateModified = File.GetLastWriteTimeUtc(path)
            }).ToArray();
            var faces = photos.ToDictionary(photo => photo.Id, photo => new List<FaceRegion>
            {
                new() { Id = photo.Id, ImageId = photo.Id, X = 80.0 / 384, Y = 80.0 / 384,
                    Width = 24.0 / 384, Height = 24.0 / 384 }
            });
            using var workspace = new PhotoFuseViewModel(
                (_, _) => Task.FromResult(new List<ImageEntry> { photos[1] }),
                (photo, _) => Task.FromResult<IReadOnlyList<FaceRegion>>(faces[photo.Id]),
                photo => Task.FromResult(faces[photo.Id]));
            var viewer = new ImageViewerOverlay();
            var ribbon = new TopRibbon();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(ribbon);
            Grid.SetRow(viewer, 1);
            root.Children.Add(viewer);
            viewer.SetPhotoFuseWorkspace(workspace);
            ribbon.SetPhotoFuseMode(true);
            await workspace.StartAsync(photos[0]);
            await Task.Delay(150);
            if (previewOnly)
            {
                var exited = new TaskCompletionSource();
                ribbon.PhotoFuseClicked += async (_, _) => { await workspace.FinishAsync(); exited.TrySetResult(); };
                viewer.PhotoFuseDiscardRequested += async (_, _) => { await workspace.DiscardAsync(); exited.TrySetResult(); };
                await exited.Task;
                return;
            }
            try
            {
                Check(workspace.IsActive && !workspace.IsBusy && !workspace.HasChanges,
                    "Single-photo entry discovers a nearby donor without preselection", passed);
                var overlay = (Canvas)viewer.FindName("FuseFaceOverlay");
                Check(overlay.Children.Count == 1, "Detected face is highlighted in the full viewer", passed);
                Check(((Button)viewer.FindName("NextButton")).Visibility == Visibility.Collapsed,
                    "Navigation does not discard active Fuse changes", passed);
                Check(!((Button)ribbon.FindName("CropButton")).IsEnabled &&
                    ((Button)ribbon.FindName("PhotoFuseButton")).IsEnabled,
                    "Top ribbon keeps Fuse exit available and excludes conflicting edits", passed);
                Invoke((Button)overlay.Children[0]);
                await Task.Delay(150);
                var options = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot)
                    .Select(popup => TryFindList(popup.Child, "FuseReplacements")).FirstOrDefault(list => list is not null);
                Check(options?.Items.Count == 1, "Face click opens a nearby replacement thumbnail flyout", passed);
                options!.SelectedIndex = 0;
                await WaitAsync(() => !workspace.IsBusy && workspace.HasChanges);
                Check(workspace.HasChanges && workspace.DisplayPixels is not null,
                    "Face selection updates the full-view composite", passed);
                await workspace.SelectAsync(workspace.Faces[0], null);
                Check(!workspace.HasChanges, "Original face can be restored", passed);
                await workspace.SelectAsync(workspace.Faces[0], workspace.GetReplacements(workspace.Faces[0])[0]);
                string? saved = await workspace.FinishAsync();
                Check(saved is not null && File.Exists(saved) && !workspace.IsActive,
                    "Exit saves a new copy and ends Fuse mode", passed);
            }
            finally { viewer.SetPhotoFuseWorkspace(null); root.Children.Clear(); root.RowDefinitions.Clear(); }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static ListView? TryFindList(DependencyObject parent, string name)
    {
        if (parent is ListView list && Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(list) == name)
            return list;
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var found = TryFindList(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i), name);
            if (found is not null) return found;
        }
        return null;
    }

    private static void Invoke(Button button) =>
        ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(button)
            .GetPattern(PatternInterface.Invoke)).Invoke();

    private static async Task WaitAsync(Func<bool> ready)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (ready()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("The Photo Fuse viewer did not reach its expected state.");
    }

    private static void Check(bool condition, string message, List<string> passed)
    {
        if (!condition) throw new InvalidOperationException(message);
        passed.Add(message);
    }

    private static async Task WriteFixtureAsync(string path, int frame)
    {
        const int size = 384;
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            double value = 125 + 28 * Math.Sin(x * 0.18 + Math.Sin(y * 0.044) * 4) +
                24 * Math.Cos(y * 0.23 + Math.Cos(x * 0.021) * 3);
            int index = (y * size + x) * 4;
            bool patch = frame == 1 && x is >= 90 and < 96 && y is >= 90 and < 94;
            pixels[index] = (byte)(value + (patch ? 55 : -12));
            pixels[index + 1] = (byte)(value + (patch ? -35 : 8));
            pixels[index + 2] = (byte)(value + (patch ? 40 : 16));
            pixels[index + 3] = 255;
        }
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, file.AsRandomAccessStream());
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, size, size, 96, 96, pixels);
        await encoder.FlushAsync();
    }
}
