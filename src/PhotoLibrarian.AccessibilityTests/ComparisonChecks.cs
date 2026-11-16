// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using PhotoLibrarian.Views;
using System.Security.Cryptography;
using Windows.Graphics;
using Windows.System;

namespace PhotoLibrarian.AccessibilityTests;

public partial class App
{
    private async Task RunComparisonTestsAsync()
    {
        var shell = new Grid();
        var library = new Button { Content = "Synthetic library selection" };
        shell.Children.Add(library);
        _window = new Window { Title = "Isolated in-window comparison", Content = shell };
        _window.AppWindow.Resize(new SizeInt32(1600, 1000));
        _window.Activate();
        var windowId = _window.AppWindow.Id;
        var folder = Path.Combine(AppContext.BaseDirectory, "comparison-synthetic");
        Directory.CreateDirectory(folder);
        var landscape = Path.Combine(folder, "landscape.png");
        var portrait = Path.Combine(folder, "portrait.png");
        var wide = Path.Combine(folder, "wide.png");
        await CreateTestPhotoAsync(landscape, 960, 640);
        await CreateTestPhotoAsync(portrait, 640, 960);
        await CreateTestPhotoAsync(wide, 1440, 960);
        await ImageEditService.WritePendingRecipeAsync(landscape, new EditParameters { RotationAngle = 90 });
        var before = Directory.GetFiles(folder).ToDictionary(path => path,
            path => SHA256.HashData(File.ReadAllBytes(path)));
        var first = TestPhoto(landscape);
        var second = TestPhoto(portrait);
        var comparison = new PhotoComparisonView();
        shell.Children.Add(comparison);
        library.Visibility = Visibility.Collapsed;
        comparison.Closed += (_, _) =>
        {
            comparison.Visibility = Visibility.Collapsed;
            library.Visibility = Visibility.Visible;
            library.Focus(FocusState.Programmatic);
        };
        comparison.Open([first, second]);
        comparison.FocusWorkspace();
        var root = (FrameworkElement)comparison;
        var grid = (Grid)root.FindName("PanesGrid");
        await WaitForComparisonAsync(() => grid.Children.OfType<PhotoComparisonPane>()
            .All(pane => ((Image)pane.FindName("Photo")).Source is not null), "compare load");
        var panes = grid.Children.Cast<PhotoComparisonPane>().ToArray();
        Check(panes.Length == 2 && Grid.GetRow(panes[0]) == Grid.GetRow(panes[1]) &&
            Grid.GetColumn(panes[0]) != Grid.GetColumn(panes[1]), "Compare has two side-by-side panes");
        var rendered = (BitmapImage)((Image)panes[0].FindName("Photo")).Source;
        Check(rendered.PixelWidth == 640 && rendered.PixelHeight == 960,
            "Compare uses the full-resolution pending rotation recipe, not source thumbnails");
        Check(panes.All(pane => pane.ActualWidth > 200 && pane.ActualHeight > 200),
            "Both comparison photos fill usable pane space");
        Check(_window.AppWindow.Id == windowId &&
            Math.Abs(comparison.ActualWidth - shell.ActualWidth) < 1 &&
            Math.Abs(comparison.ActualHeight - shell.ActualHeight) < 1,
            "Comparison fills the existing window without creating or resizing another window");
        Check(ComparisonStrings.Get("ComparisonCompareMenu/Text") == "Compare selected photos",
            "One localized comparison command covers every selection size");
        foreach (var pane in panes)
        {
            var viewport = (ScrollViewer)pane.FindName("Viewport");
            Check(AutomationProperties.GetName(viewport) == pane.Entry.FileName &&
                !string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(viewport)),
                "Photo viewport exposes filename and keyboard help");
        }
        InvokeComparison(root, "ComparisonActual");
        await Task.Delay(100);
        var host = (Grid)panes[0].FindName("ImageHost");
        Check(Math.Abs(host.ActualWidth * root.XamlRoot.RasterizationScale - 640) < 1,
            "100% maps one decoded pixel to one physical display pixel");
        var firstViewport = (ScrollViewer)panes[0].FindName("Viewport");
        firstViewport.Focus(FocusState.Keyboard);
        Check(firstViewport.Focus(FocusState.Keyboard) &&
            panes[0].HandlePhotoKey((VirtualKey)0xBB) && panes[0].HandlePhotoKey(VirtualKey.Right),
            "Focused photo accepts keyboard zoom and pan");
        await Task.Delay(100);
        foreach (var pane in panes)
        {
            var viewport = (ScrollViewer)pane.FindName("Viewport");
            var imageHost = (Grid)pane.FindName("ImageHost");
            Check(Math.Abs(imageHost.Width - host.Width) < 0.01 &&
                Math.Abs(Canvas.GetLeft(imageHost) - Canvas.GetLeft(host)) < 0.01,
                "Keyboard zoom and pan stay synchronized across equal decoded sizes");
        }
        InvokeComparison(root, "ComparisonFit");
        root.RequestedTheme = ElementTheme.Dark;
        await Task.Delay(150);
        await CaptureSlideshowAsync(root, "comparison-dark.png");
        root.RequestedTheme = ElementTheme.Light;
        var oldWidth = root.ActualWidth;
        _window.AppWindow.Resize(new SizeInt32(1300, 900));
        await WaitForComparisonAsync(() => root.ActualWidth < oldWidth, "resize layout");
        await Task.Delay(300);
        await CaptureSlideshowAsync(root, "comparison-light.png");
        InvokeComparison(panes[0], "ComparisonRemove");
        Check(grid.Children.Count == 1 && ((Image)panes[0].FindName("Photo")).Source is null,
            "Remove releases pixels and removes only the workspace candidate");
        await Task.Delay(150);
        var remaining = grid.Children.Cast<PhotoComparisonPane>().Single();
        Check(Math.Abs(remaining.ActualWidth - grid.ActualWidth) < 1,
            "Removing down to one candidate expands it across the workspace");
        InvokeComparison(root, "ComparisonRestore");
        await WaitForComparisonAsync(() => grid.Children.Count == 2 &&
            grid.Children.OfType<PhotoComparisonPane>().All(pane => ((Image)pane.FindName("Photo")).Source is not null),
            "restore load");
        Check(grid.Children.OfType<PhotoComparisonPane>().Select(pane => pane.Entry).SequenceEqual(new[] { first, second }),
            "Restore all returns the original candidate order");
        Check(before.Count == Directory.GetFiles(folder).Length &&
            before.All(pair => pair.Value.SequenceEqual(SHA256.HashData(File.ReadAllBytes(pair.Key)))),
            "Compare, zoom, remove and restore never write photos or recipes");
        if (Environment.GetCommandLineArgs().Contains("--preview")) return;
        InvokeComparison(root, "ComparisonClose");
        Check(!comparison.IsOpen && comparison.Visibility == Visibility.Collapsed &&
            ReferenceEquals(FocusManager.GetFocusedElement(shell.XamlRoot), library),
            "Back to library closes only comparison and restores focus in the same window");
        shell.Children.Remove(comparison);

        int active = 0, maximumActive = 0;
        bool fail = true;
        async Task<BitmapImage> LoadForSurvey(string path, CancellationToken token)
        {
            active++;
            maximumActive = Math.Max(maximumActive, active);
            try
            {
                await Task.Delay(50, token);
                if (path == "unavailable" && fail) throw new IOException("Synthetic offline photo");
                if (path == landscape)
                    return await ViewerImageLoader.LoadPhotoAsync(wide, token);
                return await ViewerImageLoader.LoadPhotoAsync(portrait, token);
            }
            finally { active--; }
        }
        var unavailable = TestPhoto("unavailable");
        var survey = new PhotoComparisonView(LoadForSurvey);
        shell.Children.Add(survey);
        library.Visibility = Visibility.Collapsed;
        survey.Open([first, second, unavailable]);
        root = survey;
        grid = (Grid)root.FindName("PanesGrid");
        var failed = grid.Children.OfType<PhotoComparisonPane>().Last();
        await WaitForComparisonAsync(() => ((Button)failed.FindName("RetryButton")).Visibility == Visibility.Visible,
            "offline error");
        Check(maximumActive == 1 && grid.Children.Count == 3, "Survey serializes decoding and keeps failed candidates visible");
        Check(grid.ColumnDefinitions.Count > 1 &&
            Math.Abs(survey.ActualWidth - shell.ActualWidth) < 1,
            "The same in-window view automatically lays out three candidates");
        Check(((TextBlock)failed.FindName("StatusText")).Text.Contains("Synthetic offline photo"),
            "Per-photo resource and codec errors are explicit");
        var surveyPanes = grid.Children.Cast<PhotoComparisonPane>().ToArray();
        InvokeComparison(root, "ComparisonActual");
        surveyPanes[0].HandlePhotoKey((VirtualKey)0xBB);
        surveyPanes[0].HandlePhotoKey(VirtualKey.Right);
        var firstHost = (Grid)surveyPanes[0].FindName("ImageHost");
        var secondHost = (Grid)surveyPanes[1].FindName("ImageHost");
        var firstView = (ScrollViewer)surveyPanes[0].FindName("Viewport");
        var secondView = (ScrollViewer)surveyPanes[1].FindName("Viewport");
        var firstCenter = (firstView.ActualWidth / 2 - Canvas.GetLeft(firstHost)) / firstHost.Width;
        var secondCenter = (secondView.ActualWidth / 2 - Canvas.GetLeft(secondHost)) / secondHost.Width;
        Check(Math.Abs(firstHost.Width / 1440 - secondHost.Width / 640) < 0.001 &&
            Math.Abs(firstCenter - secondCenter) < 0.001,
            "Different image dimensions share source-pixel scale and normalized pan center");
        fail = false;
        InvokeComparison(failed, "ComparisonRetry");
        await WaitForComparisonAsync(() => ((Image)failed.FindName("Photo")).Source is not null, "retry load");
        await Task.Delay(150);
        var lateHost = (Grid)failed.FindName("ImageHost");
        var lateView = (ScrollViewer)failed.FindName("Viewport");
        var lateCenter = (lateView.ActualWidth / 2 - Canvas.GetLeft(lateHost)) / lateHost.Width;
        Check(Math.Abs(lateHost.Width - secondHost.Width) < 0.001 &&
            Math.Abs(lateCenter - secondCenter) < 0.001,
            $"A late or retried photo adopts the current linked zoom and pan (scale widths {lateHost.Width}/{secondHost.Width}, centers {lateCenter}/{secondCenter})");
        InvokeComparison(root, "ComparisonFit");
        await Task.Delay(200);
        await CaptureSlideshowAsync(root, "survey-light.png");
        foreach (var pane in grid.Children.Cast<PhotoComparisonPane>().ToArray())
            InvokeComparison(pane, "ComparisonRemove");
        Check(grid.Children.Count == 0, "Survey can temporarily remove every candidate without deleting a file");
        survey.Close();
        survey.Open([first, second]);
        Check(grid.Children.Count == 2 && grid.ColumnDefinitions.Count == 2,
            "Reopening the same comparison view with two candidates adapts back to side-by-side");
        survey.Close();
        shell.Children.Remove(survey);

        var started = new TaskCompletionSource();
        var cancelled = new TaskCompletionSource();
        async Task<BitmapImage> WaitForClose(string path, CancellationToken token)
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cancelled.TrySetResult();
                throw;
            }
            return new BitmapImage();
        }
        var closing = new PhotoComparisonView(WaitForClose);
        shell.Children.Add(closing);
        closing.Open([first, second]);
        await started.Task;
        closing.Close();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(true, "Closing cancels in-flight loads and queued candidates");
        shell.Children.Remove(closing);
    }

    private static void InvokeComparison(FrameworkElement root, string id)
    {
        var element = FindComparisonElement(root, id) ??
            throw new InvalidOperationException($"Missing comparison command {id}");
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(element);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }

    private static FrameworkElement? FindComparisonElement(DependencyObject root, string id)
    {
        if (root is FrameworkElement element && AutomationProperties.GetAutomationId(element) == id)
            return element;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindComparisonElement(VisualTreeHelper.GetChild(root, i), id);
            if (found is not null) return found;
        }
        return null;
    }

    private static async Task WaitForComparisonAsync(Func<bool> ready, string operation)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(50);
        if (!ready()) throw new TimeoutException($"Comparison test timed out: {operation}");
    }
}
