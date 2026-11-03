// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using PhotoLibrarian.Views;
using Windows.Graphics.Imaging;

namespace PhotoLibrarian.AccessibilityTests;

/// <summary>Exercises the real dialog in isolation: no library initialization or user-photo access.</summary>
internal static class PhotoFuseUiTests
{
    internal static async Task RunAsync(Grid root, List<string> passed, bool previewOnly)
    {
        string folder = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-FuseUi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            string[] paths = [Path.Combine(folder, "base.png"), Path.Combine(folder, "donor.png")];
            for (int frame = 0; frame < 2; frame++) await WriteFixtureAsync(paths[frame], frame);
            var dialog = new PhotoFuseDialog(paths) { XamlRoot = root.XamlRoot };
            if (Environment.GetCommandLineArgs().Contains("--dark"))
                dialog.RequestedTheme = ElementTheme.Dark;
            var shown = dialog.ShowAsync();
            if (previewOnly) { await shown; return; }
            try
            {
                var workspace = Element<ContentControl>(dialog, "Workspace");
                await WaitAsync(() => workspace.IsEnabled);
                await Task.Delay(100);
                var surface = Element<Grid>(dialog, "PreviewSurface");
                var scroll = Element<ScrollViewer>(dialog, "PreviewScroll");
                Check(surface.ActualHeight <= scroll.ActualHeight + 1 &&
                    surface.ActualWidth <= scroll.ActualWidth + 1,
                    "Fit preview stays fully inside its viewport at current DPI", passed);
                Check(!dialog.IsPrimaryButtonEnabled, "Save is disabled before a composite exists", passed);
                var confirm = Element<CheckBox>(dialog, "ConfirmBox");
                Check(!confirm.IsEnabled, "Confirmation is disabled before actual-pixel review", passed);
                Element<NumberBox>(dialog, "LeftBox").Value = 56;
                Element<NumberBox>(dialog, "TopBox").Value = 56;
                Element<NumberBox>(dialog, "WidthBox").Value = 72;
                Element<NumberBox>(dialog, "HeightBox").Value = 72;
                Invoke(FindButton(dialog, "Add region"));
                var regions = Element<ListView>(dialog, "RegionsList");
                await WaitAsync(() => regions.Items.Count == 1 && workspace.IsEnabled);
                Check(regions.Items.Count == 1, "Manual donor region accepted through real dialog", passed);
                Invoke(FindButton(dialog, "Build composite preview"));
                await WaitAsync(() => Element<ComboBox>(dialog, "DisplayBox").SelectedIndex == 2 && workspace.IsEnabled);
                Check(!confirm.IsEnabled && !dialog.IsPrimaryButtonEnabled,
                    "Building preview does not authorize export", passed);
                Invoke(FindButton(dialog, "1:1"));
                await WaitAsync(() => confirm.IsEnabled);
                Check(!dialog.IsPrimaryButtonEnabled, "Actual-pixel view still requires explicit confirmation", passed);
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(confirm);
                ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)).Toggle();
                Check(dialog.IsPrimaryButtonEnabled, "Reviewed preview enables new-copy save", passed);
                Element<NumberBox>(dialog, "LeftBox").Value = 57;
                Check(!dialog.IsPrimaryButtonEnabled && confirm.IsChecked == false,
                    "Changing a region invalidates the reviewed preview", passed);
            }
            finally { dialog.Hide(); await shown; }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static T Element<T>(PhotoFuseDialog dialog, string name) where T : FrameworkElement =>
        (T)dialog.FindName(name);

    private static Button FindButton(DependencyObject parent, string content)
    {
        return TryFindButton(parent, content) ??
            throw new InvalidOperationException($"Button not found: {content}");
    }

    private static Button? TryFindButton(DependencyObject parent, string content)
    {
        if (parent is Button button && button.Content as string == content) return button;
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var found = TryFindButton(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i), content);
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
        throw new TimeoutException("The Photo Fuse dialog did not reach its expected state.");
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
            bool patch = frame == 1 && x is > 80 and < 104 && y is > 80 and < 104;
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
