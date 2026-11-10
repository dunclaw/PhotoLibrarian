// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Views;
using Windows.Graphics;
using Windows.System;

namespace PhotoLibrarian.AccessibilityTests;

public partial class App
{
    private async Task RunEditHistoryTestsAsync()
    {
        var history = new EditHistoryService();
        var ribbon = new TopRibbon();
        var text = new TextBox { Header = "Native text undo", Margin = new Thickness(16), Width = 400 };
        AutomationProperties.SetAutomationId(text, "HistoryText");
        var layout = new StackPanel();
        layout.Children.Add(ribbon);
        layout.Children.Add(text);
        _window = new Window { Title = "Isolated unified edit history", Content = layout };
        _window.AppWindow.Resize(new SizeInt32(1600, 500));
        _window.Activate();
        await Task.Delay(350);

        var undo = FindHistoryButton(ribbon, "RibbonUndo")!;
        var redo = FindHistoryButton(ribbon, "RibbonRedo")!;
        history.Changed += (_, _) => Update();
        ribbon.UndoClicked += async (_, _) => await history.UndoAsync();
        ribbon.RedoClicked += async (_, _) => await history.RedoAsync();
        layout.PreviewKeyDown += async (_, args) =>
        {
            var focus = FocusManager.GetFocusedElement(layout.XamlRoot) as DependencyObject;
            var control = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            var modifiers = control ? VirtualKeyModifiers.Control : VirtualKeyModifiers.None;
            if (shift) modifiers |= VirtualKeyModifiers.Shift;
            if (!MainWindow.TryGetEditorHistoryShortcut(args.Key, modifiers,
                    MainWindow.HasNativeHistoryOrDialog(focus), out var isUndo)) return;
            args.Handled = true;
            if (isUndo) await history.UndoAsync();
            else await history.RedoAsync();
        };
        Update();
        Check(undo.ActualWidth > 0 && redo.ActualWidth > 0 && !undo.IsEnabled && !redo.IsEnabled,
            "Browsing exposes disabled shared Undo and Redo");
        Check(AutomationProperties.GetHelpText(undo).Contains("file moves/deletes"),
            "History boundaries are exposed accessibly");

        var value = 0;
        await AddAsync("rating - first.png", 1, 0);
        await AddAsync("crop - second.png", 2, 1);
        Check(undo.IsEnabled && AutomationProperties.GetName(undo) == "Undo crop - second.png",
            "Toolbar labels identify the chronological edit and photo");
        ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(undo)
            .GetPattern(PatternInterface.Invoke)).Invoke();
        await Task.Delay(100);
        Check(value == 1 && redo.IsEnabled &&
            AutomationProperties.GetName(redo) == "Redo crop - second.png",
            "Toolbar Undo restores the latest edit and enables labeled Redo");

        ribbon.SetPhotoContext(hasPhoto: true, editorActive: true);
        await Task.Delay(100);
        Check(undo.ActualWidth > 0 && redo.ActualWidth > 0, "Editor context keeps the shared toolbar");
        text.Focus(FocusState.Keyboard);
        await PressViewerKeysAsync(0x41);
        Check(text.Text == "a", "Synthetic typing reaches the native text control");
        await PressViewerKeysAsync(0x11, 0x5A);
        Check(text.Text.Length == 0 && value == 1 && history.CanRedo,
            "Native text Undo does not consume application history");

        undo.Focus(FocusState.Keyboard);
        await PressViewerKeysAsync(0x11, 0x5A);
        Check(value == 0, "Ctrl+Z uses shared history outside text controls");
        redo.Focus(FocusState.Keyboard);
        await PressViewerKeysAsync(0x11, 0x10, 0x5A);
        Check(value == 1, "Ctrl+Shift+Z redoes the preceding application edit");
        await PressViewerKeysAsync(0x11, 0x59);
        Check(value == 2, "Ctrl+Y shares the same redo stack");

        var pending = new TaskCompletionSource();
        var operation = history.ExecuteAsync("caption", async () =>
        {
            await pending.Task;
            return new(EditHistoryStatus.Applied);
        }, _ => Task.FromResult(new EditHistoryResult(EditHistoryStatus.Applied)));
        Check(!undo.IsEnabled && !redo.IsEnabled, "Busy history disables both toolbar actions");
        pending.SetResult();
        await operation;
        Check(undo.IsEnabled && !redo.IsEnabled, "Completed edit restores toolbar availability");
        history.Clear();
        if (Environment.GetCommandLineArgs().Contains("--preview"))
        {
            ribbon.SetPhotoContext(hasPhoto: true, editorActive: false);
            ribbon.SetContextLabel("Synthetic photo - no user library");
            await AddAsync("rating - Synthetic photo.png", 1, 0);
        }

        void Update()
        {
            ribbon.SetHistoryAvailability(history.CanUndo, history.CanRedo, history.IsBusy);
            ribbon.SetHistoryLabels(history.UndoLabel, history.RedoLabel);
        }

        Task<EditHistoryResult> AddAsync(string label, int after, int before) =>
            history.ExecuteAsync(label, () =>
            {
                value = after;
                return Task.FromResult(new EditHistoryResult(EditHistoryStatus.Applied));
            }, undoing =>
            {
                value = undoing ? before : after;
                return Task.FromResult(new EditHistoryResult(EditHistoryStatus.Applied));
            });
    }

    private static Button? FindHistoryButton(DependencyObject root, string id)
    {
        if (root is Button button && AutomationProperties.GetAutomationId(button) == id) return button;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var match = FindHistoryButton(VisualTreeHelper.GetChild(root, index), id);
            if (match is not null) return match;
        }
        return null;
    }
}
