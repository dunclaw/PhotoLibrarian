// Copyright (c) Duncan Lawler. All Rights Reserved.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoLibrarian.Views;

namespace PhotoLibrarian.AccessibilityTests;

public partial class App
{
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, nuint extraInfo);

    private async Task RunViewerContextMenuTestsAsync()
    {
        // No production App or library is initialized. Exercise the real viewer's unavailable
        // state to verify native input and focus routing without touching any photo files.
        var viewer = new ImageViewerOverlay();
        _window = new Window { Title = "Viewer context menu regression", Content = viewer };
        _window.Activate();
        await Task.Delay(500);
        var menu = (MenuFlyout)viewer.ContextFlyout;
        var openCount = 0;
        var isOpen = false;
        menu.Opening += (_, _) => { openCount++; isOpen = true; };
        menu.Closed += (_, _) => isOpen = false;

        Check(viewer.Focus(FocusState.Keyboard), "Viewer accepts keyboard focus");
        await PressViewerKeysAsync(0x10, 0x79); // Shift+F10
        Check(isOpen && openCount == 1, "Shift+F10 opens the existing viewer context flyout");
        Check(menu.Items.Count == 1 && menu.Items[0] is MenuFlyoutItem { IsEnabled: false },
            "Empty viewer exposes no actionable file commands");
        await PressViewerKeysAsync(0x1B); // Escape
        Check(!isOpen && viewer.HasFocusWithin(), "Escape dismisses menu and returns focus to viewer");

        await PressViewerKeysAsync(0x5D); // Menu key
        Check(isOpen && openCount == 2, "Menu key opens the same viewer context flyout");
        await PressViewerKeysAsync(0x1B);

        await RunViewerUiCommandAsync("click", "ViewerPhotoSurface", "--right");
        await Task.Delay(200);
        Check(isOpen && openCount == 3, "Right mouse click opens the same viewer context flyout");
        await RunViewerUiCommandAsync("screenshot", "--output",
            Path.Combine(AppContext.BaseDirectory, "viewer-context-menu.png"));
        await PressViewerKeysAsync(0x1B);
        Check(!isOpen && viewer.HasFocusWithin(), "Mouse-opened menu dismisses without losing viewer focus");
    }

    private async Task PressViewerKeysAsync(params byte[] keys)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        if (GetForegroundWindow() != hwnd)
            throw new InvalidOperationException("Test window is not foreground; refusing to send keyboard input.");
        foreach (var key in keys) keybd_event(key, 0, 0, 0);
        for (int i = keys.Length - 1; i >= 0; i--) keybd_event(keys[i], 0, 2, 0);
        await Task.Delay(200);
    }

    private static async Task RunViewerUiCommandAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("winapp")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("ui");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--app");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        using var process = Process.Start(start) ??
            throw new InvalidOperationException("Could not start winapp UI command.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"winapp UI command failed: {await output}\n{await errors}");
    }
}
