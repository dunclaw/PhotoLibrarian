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
using PhotoLibrarian.Services;
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
        var gridFocus = new Button
        {
            Content = "Grid keyboard focus",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(16)
        };
        AutomationProperties.SetAutomationId(gridFocus, "HistoryGridFocus");
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(ribbon);
        Grid.SetRow(text, 1);
        layout.Children.Add(text);
        Grid.SetRow(gridFocus, 1);
        layout.Children.Add(gridFocus);
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
        Check(ribbon.Visibility == Visibility.Collapsed && layout.RowDefinitions[0].ActualHeight == 0,
            "Grid browsing removes the entire Undo/Redo ribbon row");
        Check(!undo.IsEnabled && !redo.IsEnabled, "Empty history keeps both actions disabled");
        Check(AutomationProperties.GetHelpText(undo).Contains("file moves/deletes"),
            "History boundaries are exposed accessibly");
        await RunViewerUiCommandAsync("screenshot", "--output",
            Path.Combine(AppContext.BaseDirectory, "history-grid.png"));

        var value = 0;
        await AddAsync("rating - first.png", 1, 0);
        await AddAsync("crop - second.png", 2, 1);
        Check(ribbon.Visibility == Visibility.Collapsed && layout.RowDefinitions[0].ActualHeight == 0 &&
            history.CanUndo, "Populated history does not restore the grid ribbon row");
        ribbon.SetPhotoContext(hasPhoto: true, editorActive: false);
        await Task.Delay(100);
        Check(ribbon.Visibility == Visibility.Visible && layout.RowDefinitions[0].ActualHeight > 0 &&
            undo.ActualWidth > 0 && redo.ActualWidth > 0,
            "Opening the viewer restores the shared Undo/Redo toolbar");
        Check(undo.IsEnabled && AutomationProperties.GetName(undo) == "Undo crop - second.png",
            "Toolbar labels identify the chronological edit and photo");
        await RunViewerUiCommandAsync("screenshot", "--output",
            Path.Combine(AppContext.BaseDirectory, "history-viewer.png"));
        ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(undo)
            .GetPattern(PatternInterface.Invoke)).Invoke();
        await Task.Delay(100);
        Check(value == 1 && redo.IsEnabled &&
            AutomationProperties.GetName(redo) == "Redo crop - second.png",
            "Toolbar Undo restores the latest edit and enables labeled Redo");

        ribbon.SetPhotoContext(hasPhoto: false, editorActive: true);
        await Task.Delay(100);
        Check(ribbon.Visibility == Visibility.Visible && undo.ActualWidth > 0 && redo.ActualWidth > 0,
            "Editor context keeps the shared toolbar without an open viewer");
        await RunViewerUiCommandAsync("focus", "HistoryText");
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

        ribbon.SetPhotoContext(hasPhoto: false, editorActive: false);
        await Task.Delay(100);
        Check(ribbon.Visibility == Visibility.Collapsed && layout.RowDefinitions[0].ActualHeight == 0 &&
            history.CanUndo, "Returning to the grid removes the ribbon without clearing history");
        Check(gridFocus.Focus(FocusState.Keyboard), "Grid history shortcut target receives keyboard focus");
        await PressViewerKeysAsync(0x11, 0x5A);
        Check(value == 1 && history.CanRedo, "Grid Ctrl+Z still uses shared history without a visible toolbar");
        await PressViewerKeysAsync(0x11, 0x59);
        Check(value == 2, "Grid Ctrl+Y preserves shared redo without a visible toolbar");

        ribbon.SetPhotoContext(hasPhoto: true, editorActive: false);
        ribbon.SetQuickEditAvailability(true);
        var pending = new TaskCompletionSource();
        var operation = history.ExecuteAsync("caption", async () =>
        {
            await pending.Task;
            return new(EditHistoryStatus.Applied);
        }, _ => Task.FromResult(new EditHistoryResult(EditHistoryStatus.Applied)));
        Check(!undo.IsEnabled && !redo.IsEnabled, "Busy history disables both toolbar actions");
        Check(Enum.GetValues<ViewerQuickEdit>().All(action =>
                !FindHistoryButton(ribbon, $"ViewerQuick{action}")!.IsEnabled),
            "Busy history disables quick edits");
        pending.SetResult();
        await operation;
        Check(undo.IsEnabled && !redo.IsEnabled, "Completed edit restores toolbar availability");
        history.Clear();
        ribbon.SetPhotoContext(hasPhoto: true, editorActive: false);
        ribbon.SetQuickEditAvailability(true);
        var scale = layout.XamlRoot.RasterizationScale;
        _window.AppWindow.Resize(new SizeInt32((int)(1280 * scale), (int)(500 * scale)));
        await Task.Delay(150);
        var actions = new List<ViewerQuickEdit>();
        ribbon.QuickEditClicked += (_, action) => actions.Add(action);
        foreach (var action in Enum.GetValues<ViewerQuickEdit>())
        {
            var button = FindHistoryButton(ribbon, $"ViewerQuick{action}")!;
            var position = button.TransformToVisual(ribbon).TransformPoint(new Windows.Foundation.Point());
            Check(button.IsEnabled && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)) &&
                position.X >= 0 && position.X + button.ActualWidth <= ribbon.ActualWidth &&
                position.Y >= 0 && position.Y + button.ActualHeight <= ribbon.ActualHeight,
                $"{action} is labeled, enabled and fully visible at 1280 DIPs");
            ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(button)
                .GetPattern(PatternInterface.Invoke)).Invoke();
            await Task.Delay(50);
            Check(actions.LastOrDefault() == action && actions.Count == (int)action + 1,
                $"{action} invokes its own quick edit without opening Adjust");
        }
        var slideshow = (FrameworkElement)ribbon.FindName("SlideshowButton");
        var rightEdge = slideshow.TransformToVisual(ribbon).TransformPoint(new Windows.Foundation.Point()).X +
            slideshow.ActualWidth;
        Check(rightEdge <= ribbon.ActualWidth, "Quick edits do not clip the ribbon's rightmost command at 1280 DIPs");
        ribbon.SetPhotoFuseMode(true);
        Check(Enum.GetValues<ViewerQuickEdit>().All(action =>
                !FindHistoryButton(ribbon, $"ViewerQuick{action}")!.IsEnabled),
            "Photo Fuse disables quick edits");
        ribbon.SetPhotoFuseMode(false);
        ribbon.SetQuickEditAvailability(false);
        Check(Enum.GetValues<ViewerQuickEdit>().All(action =>
                !FindHistoryButton(ribbon, $"ViewerQuick{action}")!.IsEnabled),
            "Unsupported photo context disables quick edits");
        if (Environment.GetCommandLineArgs().Contains("--preview"))
        {
            ribbon.SetPhotoContext(hasPhoto: true, editorActive: false);
            ribbon.SetQuickEditAvailability(true);
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
