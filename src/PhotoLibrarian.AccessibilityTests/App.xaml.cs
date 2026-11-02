// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PhotoLibrarian.Controls;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.ViewModels;
using System.Collections.ObjectModel;
using System.Text.Json;
using Windows.Graphics;
using Windows.System;

namespace PhotoLibrarian.AccessibilityTests;

/// <summary>Isolated UI-thread tests: synthetic entries, no library, files, or thumbnail loading.</summary>
public partial class App : Application
{
    private Window _window = null!;
    private readonly List<string> _passed = [];
    private VirtualizingPhotoGrid _grid = null!;
    private readonly ObservableCollection<PhotoGroup> _groups = [];

    public App()
    {
        UnhandledException += (_, args) =>
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "accessibility-results.json"),
                JsonSerializer.Serialize(new { passed = _passed, failure = args.Exception.ToString() }));
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        string? failure = null;
        try
        {
            _grid = new VirtualizingPhotoGrid { ItemSize = 120 };
            var group = new PhotoGroup { Header = "Synthetic photos" };
            for (int i = 0; i < 10_000; i++)
                group.Items.Add(new ImageThumbnailViewModel(new ImageEntry
                {
                    Id = i + 1,
                    FileName = $"Photo-{i:D5}.jpg",
                    FilePath = $@"C:\SyntheticPhotos\Photo-{i:D5}.jpg"
                }) { IsLoading = false });
            _groups.Add(group);
            _grid.SetGroups(_groups);
            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition());
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var before = new Button { Content = "Before grid" };
            var after = new Button { Content = "After grid" };
            layout.Children.Add(before);
            layout.Children.Add(_grid);
            layout.Children.Add(after);
            Grid.SetRow(_grid, 1);
            Grid.SetRow(after, 2);
            _window = new Window { Title = "Photo grid accessibility regression", Content = layout };
            _window.AppWindow.Resize(new SizeInt32(920, 700));
            _window.Activate();
            await Task.Delay(500);
            if (Environment.GetCommandLineArgs().Contains("--preview")) return;
            await RunTestsAsync(group, before, after);
        }
        catch (Exception exception)
        {
            failure = exception.ToString();
        }
        var result = new { passed = _passed, failure };
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "accessibility-results.json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        _window?.Close();
        Exit();
    }

    private async Task RunTestsAsync(PhotoGroup group, Button before, Button after)
    {
        var root = FrameworkElementAutomationPeer.CreatePeerForElement(_grid);
        Check(root.GetAutomationControlType() == AutomationControlType.List, "Grid has list semantics");
        Check(root.GetAutomationId() == "PhotoGrid" && root.GetName() == "Photos", "Localized grid name and stable ID");
        Check(!string.IsNullOrWhiteSpace(root.GetHelpText()), "Keyboard help is exposed");
        var children = root.GetChildren();
        Check(children.Count is > 0 and < 150, "10,000 photos expose fewer than 150 realized peers");
        Check(children.Select(peer => peer.GetName()).SequenceEqual(
            children.Select(peer => peer.GetName()).Order()), "Peers follow visual photo order");
        Check(children.All(peer => peer.GetAutomationControlType() == AutomationControlType.ListItem &&
            peer.GetChildren().Count == 0), "No pooled image/loading/label descendants leak into UIA");
        var first = children[0];
        var second = children[1];
        var firstSelection = (ISelectionItemProvider)first.GetPattern(PatternInterface.SelectionItem);
        var secondSelection = (ISelectionItemProvider)second.GetPattern(PatternInterface.SelectionItem);
        var selection = (ISelectionProvider)root.GetPattern(PatternInterface.Selection);
        firstSelection.Select();
        secondSelection.AddToSelection();
        Check(selection.CanSelectMultiple && !selection.IsSelectionRequired &&
            selection.GetSelection().Length == 2, "Selection and additive UIA selection match grid");
        firstSelection.RemoveFromSelection();
        Check(!firstSelection.IsSelected && secondSelection.IsSelected &&
            _grid.SelectedItems.Count == 1, "UIA removal preserves other selections");
        ImageThumbnailViewModel? opened = null;
        _grid.ItemDoubleClicked += (_, item) => opened = item;
        ((IInvokeProvider)first.GetPattern(PatternInterface.Invoke)).Invoke();
        Check(ReferenceEquals(opened, group.Items[0]) && firstSelection.IsSelected, "Invoke opens the identified photo");

        before.Focus(FocusState.Keyboard);
        await PressAsync(VirtualKey.Tab);
        Check(first.HasKeyboardFocus(), "Tab enters grid at logical photo");
        await PressAsync(VirtualKey.Control, VirtualKey.Right);
        Check(second.HasKeyboardFocus() && firstSelection.IsSelected && !secondSelection.IsSelected,
            "Ctrl+Right moves focus independently of selection");
        await PressAsync(VirtualKey.Control, VirtualKey.Space);
        Check(firstSelection.IsSelected && secondSelection.IsSelected, "Ctrl+Space toggles focused photo");
        await PressAsync(VirtualKey.Shift, VirtualKey.Right);
        Check(_grid.SelectedItems.Count == 2 && _grid.SelectedItems.Contains(group.Items[2]),
            "Shift+Right extends range from anchor");
        ContextMenuRequestedEventArgs? context = null;
        _grid.ContextMenuRequested += (_, value) => context = value;
        await PressAsync(VirtualKey.Shift, VirtualKey.F10);
        Check(ReferenceEquals(context?.PrimaryItem, group.Items[2]), "Shift+F10 requests focused photo actions");
        await PressAsync(VirtualKey.Application);
        Check(ReferenceEquals(context?.PrimaryItem, group.Items[2]), "Menu key requests photo actions");
        await PressAsync(VirtualKey.Tab);
        Check(ReferenceEquals(FocusManager.GetFocusedElement(_grid.XamlRoot), after),
            "Tab leaves grid without traversing pooled visuals");
        _grid.Focus(FocusState.Keyboard);
        await PressAsync(VirtualKey.End);
        await Task.Delay(200);
        Check(_grid.PrimaryItem == group.Items[^1], "End reaches last photo across virtualized rows");
        Check(root.GetChildren().Count < 150 && first.GetName() == "Photo-00000.jpg" && first.IsOffscreen(),
            "Recycling stays bounded and old peer never adopts recycled label");
        Check(first.GetBoundingRectangle().IsEmpty || first.GetBoundingRectangle().Height <= 0,
            "Unrealized photo has no visible bounds");
        Check(selection.GetSelection().Length == 1, "Selection remains available at end of library");
        ((IScrollItemProvider)first.GetPattern(PatternInterface.ScrollItem)).ScrollIntoView();
        await Task.Delay(200);
        Check(!first.IsOffscreen(), "ScrollItem realizes original photo without changing its identity");
        first.SetFocus();
        await PressAsync(VirtualKey.Control, VirtualKey.A);
        Check(_grid.SelectedItems.Count == 10_000 && selection.GetSelection().Length == 10_000 &&
            root.GetChildren().Count < 150, "Select all does not realize the library");
        await PressAsync(VirtualKey.Escape);
        Check(selection.GetSelection().Length == 0 && first.HasKeyboardFocus(),
            "Escape clears selection without losing logical focus");
        _grid.IsEnabled = false;
        bool disabledRejected = false;
        try { firstSelection.Select(); }
        catch (ElementNotEnabledException) { disabledRejected = true; }
        Check(disabledRejected, "Disabled grid rejects UIA actions");
        _grid.IsEnabled = true;
        group.Items.RemoveAt(0);
        bool staleRejected = false;
        try { first.GetName(); }
        catch (ElementNotAvailableException) { staleRejected = true; }
        Check(staleRejected && root.GetChildren()[0].GetName() == "Photo-00001.jpg",
            "Data mutation invalidates old peers and refreshes current labels");
        var oldPeer = root.GetChildren()[0];
        group.Items = [new ImageThumbnailViewModel(new ImageEntry
        {
            Id = 20_001, FileName = "Replacement.jpg", FilePath = @"C:\SyntheticPhotos\Replacement.jpg"
        }) { IsLoading = false }];
        bool replacementRejected = false;
        try { oldPeer.GetName(); }
        catch (ElementNotAvailableException) { replacementRejected = true; }
        Check(replacementRejected && root.GetChildren()[0].GetName() == "Replacement.jpg",
            "Replacing a group's item collection invalidates prior identities");
        var next = new ImageThumbnailViewModel(new ImageEntry
        {
            Id = 20_002, FileName = "Next-group.jpg", FilePath = @"C:\SyntheticPhotos\Next-group.jpg"
        }) { IsLoading = false };
        _groups.Add(new PhotoGroup { Header = "Next group", Items = [next] });
        await PressAsync(VirtualKey.Home);
        await PressAsync(VirtualKey.Right);
        Check(ReferenceEquals(_grid.PrimaryItem, next) &&
            root.GetChildren().Select(peer => peer.GetName()).SequenceEqual(["Replacement.jpg", "Next-group.jpg"]),
            "Keyboard navigation and peer order cross group headers correctly");
        _groups.Clear();
        Check(root.GetChildren().Count == 0 && selection.GetSelection().Length == 0,
            "Empty results expose no stale photos or selection");
        _grid.ItemsSource = new[] { "Flat item" };
        Check(root.GetPattern(PatternInterface.Selection) is null && root.GetAutomationId() != "PhotoGrid",
            "Flat-template surfaces do not inherit grouped-photo patterns or IDs");
    }

    private void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        _passed.Add(name);
    }

    private async Task PressAsync(params VirtualKey[] keys)
    {
        if (keys[^1] == VirtualKey.Tab)
        {
            if (!FocusManager.TryMoveFocus(FocusNavigationDirection.Next,
                new FindNextElementOptions { SearchRoot = _window.Content }))
                throw new InvalidOperationException("WinUI could not move tab focus.");
        }
        else
        {
            var mods = VirtualizingPhotoGrid.ModifierState.None;
            if (keys.Contains(VirtualKey.Control)) mods |= VirtualizingPhotoGrid.ModifierState.Ctrl;
            if (keys.Contains(VirtualKey.Shift)) mods |= VirtualizingPhotoGrid.ModifierState.Shift;
            if (!_grid.HandlePhotoKey(keys[^1], mods))
                throw new InvalidOperationException($"Grid did not handle {keys[^1]}.");
        }
        await Task.Delay(100);
    }
}
