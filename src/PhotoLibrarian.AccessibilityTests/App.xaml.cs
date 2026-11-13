// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PhotoLibrarian.Controls;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.ViewModels;
using PhotoLibrarian.Views;
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
        if (Environment.GetCommandLineArgs().Contains("--motion-photo"))
        {
            await RunMotionPhotoSuiteAsync();
            return;
        }
        string? failure = null;
        try
        {
            if (Environment.GetCommandLineArgs().Contains("--comparison"))
            {
                await RunComparisonTestsAsync();
                if (Environment.GetCommandLineArgs().Contains("--preview")) return;
                await FinishAsync(null);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--edit-history"))
            {
                await RunEditHistoryTestsAsync();
                if (Environment.GetCommandLineArgs().Contains("--preview")) return;
                await FinishAsync(null);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--photo-fuse"))
            {
                var fuseLayout = new Grid();
                _window = new Window { Title = "Photo Fuse synthetic UI regression", Content = fuseLayout };
                _window.AppWindow.Resize(new SizeInt32(1200, 900));
                _window.Activate();
                await Task.Delay(300);
                await PhotoFuseUiTests.RunAsync(fuseLayout, _passed,
                    Environment.GetCommandLineArgs().Contains("--preview"));
                await FinishAsync(null);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--slideshow"))
            {
                await RunSlideshowTestsAsync();
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--retouch"))
            {
                _window = await RetouchEditorFixture.OpenAsync(this);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--viewer-context-menu"))
            {
                await RunViewerContextMenuTestsAsync();
            }
            else
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
                await RunViewerCommandTestsAsync();
                if (Environment.GetCommandLineArgs().Contains("--viewer-preview"))
                {
                    var preview = (Grid)_window.Content;
                    var themeToggle = new Button
                    {
                        Content = "Switch preview theme",
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(12)
                    };
                    AutomationProperties.SetAutomationId(themeToggle, "TestPreviewTheme");
                    themeToggle.Click += (_, _) =>
                        preview.RequestedTheme = preview.RequestedTheme == ElementTheme.Dark
                            ? ElementTheme.Light : ElementTheme.Dark;
                    Grid.SetRow(themeToggle, 1);
                    preview.Children.Add(themeToggle);
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            failure = exception.ToString();
        }
        await FinishAsync(failure);
    }

    private async Task FinishAsync(string? failure)
    {
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

    private async Task RunViewerCommandTestsAsync()
    {
        var ribbon = new TopRibbon();
        ribbon.SetPhotoContext(hasPhoto: true, editorActive: false);
        var viewer = new ImageViewerOverlay();
        var layout = new Grid { Style = (Style)Resources["TestSurfaceStyle"] };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.Children.Add(ribbon);
        layout.Children.Add(viewer);
        ((TextBlock)viewer.FindName("TitleText")).Text = "Synthetic preview";
        ((TextBlock)viewer.FindName("IndexText")).Text = "No library loaded";
        Grid.SetRow(viewer, 1);
        _window.Content = layout;
        await Task.Delay(200);
        var viewerPeer = FrameworkElementAutomationPeer.CreatePeerForElement(viewer);
        Check(viewerPeer.GetName() == "Full photo viewer" && !string.IsNullOrWhiteSpace(viewerPeer.GetHelpText()),
            "Viewer focus stop has a name and keyboard help");

        foreach (var id in new[] { "ViewerZoomOut", "ViewerZoomFit", "ViewerZoomIn",
            "ViewerActualSize", "ViewerClose", "ViewerPrevious", "ViewerNext" })
        {
            var control = FindControl(viewer, id);
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(control);
            Check(peer.GetAutomationControlType() == AutomationControlType.Button &&
                !string.IsNullOrWhiteSpace(peer.GetName()) &&
                peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider,
                $"{id} exposes named Button/Invoke semantics");
        }
        var information = FrameworkElementAutomationPeer.CreatePeerForElement(
            FindControl(viewer, "ViewerInformationPane"));
        Check(!string.IsNullOrWhiteSpace(information.GetName()) &&
            information.GetPattern(PatternInterface.Toggle) is IToggleProvider,
            "Viewer information pane exposes named Toggle semantics");
        var calls = new List<string>();
        ribbon.MakeCopyClicked += (_, _) => calls.Add("RibbonMakeCopy");
        ribbon.CropClicked += (_, _) => calls.Add("RibbonCrop");
        ribbon.StraightenClicked += (_, _) => calls.Add("RibbonStraighten");
        ribbon.AdjustClicked += (_, _) => calls.Add("RibbonAdjust");
        ribbon.RetouchClicked += (_, _) => calls.Add("RibbonRetouch");
        ribbon.RedEyeClicked += (_, _) => calls.Add("RibbonRedEye");
        ribbon.SlideshowClicked += (_, _) => calls.Add("RibbonSlideshow");
        ribbon.ApplyCropClicked += (_, _) => calls.Add("RibbonCropApply");
        ribbon.CancelCropClicked += (_, _) => calls.Add("RibbonCropCancel");
        ribbon.ApplyStraightenClicked += (_, _) => calls.Add("RibbonStraightenApply");
        ribbon.CancelStraightenClicked += (_, _) => calls.Add("RibbonStraightenCancel");
        foreach (var id in new[] { "RibbonMakeCopy", "RibbonCrop", "RibbonStraighten",
            "RibbonAdjust", "RibbonRetouch", "RibbonRedEye", "RibbonSlideshow" })
        {
            await VerifyCommandAsync(ribbon, id, calls);
        }

        viewer.Focus(FocusState.Keyboard);
        foreach (var id in new[] { "RibbonSlideshow", "RibbonRedEye", "RibbonRetouch", "RibbonAdjust",
            "RibbonStraighten", "RibbonCrop", "RibbonMakeCopy" })
        {
            MoveFocus(FocusNavigationDirection.Previous);
            Check(FocusedId() == id, $"Shift+Tab from viewer reaches {id}");
        }
        foreach (var id in new[] { "RibbonCrop", "RibbonStraighten", "RibbonAdjust",
            "RibbonRetouch", "RibbonRedEye", "RibbonSlideshow", "ViewerPhotoSurface" })
        {
            MoveFocus(FocusNavigationDirection.Next);
            Check(FocusedId() == id, $"Tab from ribbon reaches {id}");
        }
        MoveFocus(FocusNavigationDirection.Next);
        Check(FocusedId() == "ViewerZoomOut", "Tab from viewer reaches first zoom control");
        foreach (var id in new[] { "ViewerZoomFit", "ViewerZoomIn", "ViewerInformationPane",
            "ViewerClose", "ViewerPrevious", "ViewerNext" })
        {
            MoveFocus(FocusNavigationDirection.Next);
            Check(FocusedId() == id, $"Tab reaches {id} and skips unavailable controls");
        }
        FindControl(ribbon, "RibbonCrop").Focus(FocusState.Keyboard);
        ribbon.EnterCropMode();
        Check(FocusedId() == "RibbonCropAspect", "Entering crop transfers focus to proportion selector");
        var aspectPeer = FrameworkElementAutomationPeer.CreatePeerForElement(
            FindControl(ribbon, "RibbonCropAspect"));
        Check(aspectPeer.GetName() == "Crop proportion" &&
            aspectPeer.GetPattern(PatternInterface.ExpandCollapse) is IExpandCollapseProvider,
            "Crop proportion exposes standard ComboBox expansion");
        MoveFocus(FocusNavigationDirection.Next);
        Check(FocusedId() == "RibbonCropApply", "Tab reaches apply crop");
        await VerifyCommandAsync(ribbon, "RibbonCropApply", calls);
        MoveFocus(FocusNavigationDirection.Next);
        Check(FocusedId() == "RibbonCropCancel", "Tab reaches cancel crop");
        await VerifyCommandAsync(ribbon, "RibbonCropCancel", calls);
        ribbon.ExitCropMode();
        Check(FocusedId() == "RibbonCrop", "Leaving crop restores its ribbon command focus");
        FindControl(ribbon, "RibbonStraighten").Focus(FocusState.Keyboard);
        ribbon.EnterStraightenMode();
        Check(FocusedId() == "RibbonStraightenApply", "Entering straighten transfers focus to apply");
        await VerifyCommandAsync(ribbon, "RibbonStraightenApply", calls);
        MoveFocus(FocusNavigationDirection.Next);
        Check(FocusedId() == "RibbonStraightenCancel", "Tab reaches cancel straighten");
        await VerifyCommandAsync(ribbon, "RibbonStraightenCancel", calls);
        ribbon.ExitStraightenMode();
        Check(FocusedId() == "RibbonStraighten", "Leaving straighten restores its command focus");
        viewer.Focus(FocusState.Keyboard);
        ribbon.EnterCropMode();
        Check(ReferenceEquals(FocusManager.GetFocusedElement(layout.XamlRoot), viewer),
            "Ribbon mode changes do not steal focus from the image viewport");
        ribbon.ExitCropMode();

        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            layout.RequestedTheme = theme;
            await Task.Delay(100);
            var zoom = FindControl(viewer, "ViewerZoomOut");
            zoom.Focus(FocusState.Keyboard);
            Check(zoom.ActualTheme == theme && FocusedId() == "ViewerZoomOut",
                $"{theme} viewer retains theme-aware, focusable controls");
        }
    }

    private void MoveFocus(FocusNavigationDirection direction)
    {
        if (!FocusManager.TryMoveFocus(direction, new FindNextElementOptions { SearchRoot = _window.Content }))
            throw new InvalidOperationException($"WinUI could not move focus {direction}.");
    }

    private async Task VerifyCommandAsync(DependencyObject root, string id, List<string> calls)
    {
        var control = FindControl(root, id);
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(control);
        Check(!string.IsNullOrWhiteSpace(peer.GetName()) &&
            peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider,
            $"{id} exposes a named Invoke command");
        control.Focus(FocusState.Keyboard);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        await Task.Delay(50);
        Check(calls.LastOrDefault() == id, $"{id} invokes its production command event");
    }

    private string? FocusedId() =>
        FocusManager.GetFocusedElement(_window.Content.XamlRoot) is DependencyObject focused
            ? AutomationProperties.GetAutomationId(focused) : null;

    private static Control FindControl(DependencyObject root, string id) =>
        FindElement(root, id) ?? throw new InvalidOperationException($"Control {id} not found.");

    private static Control? FindElement(DependencyObject root, string id)
    {
        if (root is Control control && AutomationProperties.GetAutomationId(control) == id)
            return control;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindElement(VisualTreeHelper.GetChild(root, i), id);
            if (found is not null) return found;
        }
        return null;
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
