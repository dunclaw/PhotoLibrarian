// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using PhotoLibrarian.ViewModels;
using PhotoLibrarian.Views;
using System.Numerics;
using Windows.Graphics;
using Windows.Graphics.DirectX;

namespace PhotoLibrarian.AccessibilityTests;

public partial class App
{
    private async Task RunEditorComparisonTestsAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-editor-comparison-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "Synthetic gradient.png");
            var pixels = new byte[320 * 200 * 4];
            for (var y = 0; y < 200; y++)
            for (var x = 0; x < 320; x++)
            {
                var i = (y * 320 + x) * 4;
                pixels[i] = (byte)(x * 255 / 319);
                pixels[i + 1] = (byte)(y * 255 / 199);
                pixels[i + 2] = 90;
                pixels[i + 3] = 255;
            }
            using (var device = new CanvasDevice())
            using (var image = CanvasBitmap.CreateFromBytes(device, pixels, 320, 200,
                DirectXPixelFormat.B8G8R8A8UIntNormalized))
                await image.SaveAsync(path, CanvasBitmapFileFormat.Png);
            var backups = new OriginalBackupService(Path.Combine(directory, "originals"));
            await new ImageEditService(backups).SaveEditParametersAsync(path, new EditParameters { Exposure = .2 });
            var vm = new ImageEditorViewModel(backups);
            var entry = new ImageEntry { FilePath = path, FileName = Path.GetFileName(path) };
            await vm.OpenForEditAsync(entry);
            var editor = new ImageEditorView(vm);
            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition());
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.Children.Add(editor);
            var typing = new TextBox { PlaceholderText = "Isolated native keyboard input fixture", Margin = new Thickness(12) };
            AutomationProperties.SetAutomationId(typing, "EditorComparisonTypingFixture");
            layout.Children.Add(typing);
            Grid.SetRow(typing, 1);
            layout.KeyDown += async (_, args) =>
                await editor.HandleEditorKeyAsync(args, Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(layout.XamlRoot) as DependencyObject);
            _window = new Window { Title = "Isolated editor comparison regression", Content = layout };
            _window.AppWindow.Resize(new SizeInt32(1280, 900));
            _window.Activate();
            var compare = (Button)editor.FindName("ComparisonButton");
            await WaitForAsync(() => compare.IsEnabled, "Real editor source loaded");
            vm.Exposure = .6;
            vm.Contrast = .2;
            vm.UndoCommand.Execute(null);
            var identity = await FileEditTransaction.ReadIdentityAsync(path);
            var history = vm.UndoHistory.ToArray();
            var redo = vm.RedoHistory.ToArray();
            var parametersChanged = 0;
            vm.ParametersChanged += (_, _) => parametersChanged++;
            var slider = (Slider)editor.FindName("SplitPositionSlider");
            var label = (TextBlock)editor.FindName("ComparisonLabelText");
            var retouch = (ToggleButton)editor.FindName("RetouchToggle");
            foreach (var (name, mode) in new[]
            {
                ("BeforeViewItem", EditorComparisonMode.Before),
                ("SideBySideViewItem", EditorComparisonMode.SideBySide),
                ("SplitViewItem", EditorComparisonMode.Split)
            })
            {
                await ChooseModeAsync(name);
                Check(vm.ComparisonMode == mode, $"Menu selects {mode}");
                Check(!retouch.IsEnabled, $"Painting disabled in {mode}");
                Check(label.Text.Contains("Before recipes"), $"{mode} labels recipe boundary");
            }
            Check(slider.Visibility == Visibility.Visible &&
                AutomationProperties.GetName(slider).Contains("percent before"), "Accessible split slider is visible");
            var sliderPeer = FrameworkElementAutomationPeer.CreatePeerForElement(slider);
            ((IRangeValueProvider)sliderPeer.GetPattern(PatternInterface.RangeValue)).SetValue(30);
            Check(slider.Value == 30, "Split position supports accessible range input");
            Check(parametersChanged == 0 && history.SequenceEqual(vm.UndoHistory) &&
                redo.SequenceEqual(vm.RedoHistory) && vm.HasChanges, "Views and split position preserve pending history");
            Check(identity == await FileEditTransaction.ReadIdentityAsync(path), "Comparison never writes source or sidecar");
            await ChooseModeAsync("SplitViewItem");
            Check(vm.ComparisonMode == EditorComparisonMode.After && retouch.IsEnabled &&
                slider.Visibility == Visibility.Collapsed, "Selecting active mode returns to After and restores painting");
            await ChooseModeAsync("BeforeViewItem");
            var save = (Button)editor.FindName("SaveBtn");
            ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(save)
                .GetPattern(PatternInterface.Invoke)).Invoke();
            await WaitForAsync(() => !vm.IsBusy && !vm.HasChanges && compare.IsEnabled, "Save while comparing completes");
            Check(vm.ComparisonMode == EditorComparisonMode.Before, "Save does not change comparison mode");
            using (var target = new CanvasRenderTarget(
                (Microsoft.Graphics.Canvas.UI.Xaml.CanvasControl)editor.FindName("EditCanvas"), 320, 200, 96))
            {
                using (var session = target.CreateDrawingSession())
                    Check(editor.TryDrawComparisonPreview(session, new Vector2(320, 200)), "Opening source can still render after baking");
                Check(pixels.SequenceEqual(target.GetPixelBytes()), "Before retains exact opening pixels after baked save");
            }
            var canvas = (Microsoft.Graphics.Canvas.UI.Xaml.CanvasControl)editor.FindName("EditCanvas");
            var oldDevice = canvas.Device;
            var recreated = false;
            canvas.CreateResources += (_, args) =>
                recreated = args.Reason == Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesReason.NewDevice;
            var replacementDevice = new CanvasDevice();
            _window.Closed += (_, _) => replacementDevice.Dispose();
            canvas.CustomDevice = replacementDevice;
            await WaitForAsync(() => recreated && !ReferenceEquals(canvas.Device, oldDevice) && compare.IsEnabled,
                "Preview device replacement recreates comparison resources");
            using (var target = new CanvasRenderTarget(canvas, 320, 200, 96))
            {
                using (var session = target.CreateDrawingSession())
                    Check(editor.TryDrawComparisonPreview(session, new Vector2(320, 200)), "Recovered device can draw Before");
                Check(pixels.SequenceEqual(target.GetPixelBytes()), "Device recovery preserves exact pre-save opening pixels");
            }
            await ChooseModeAsync("AfterViewItem");
            Check(vm.Exposure == 0, "Baked save resets only the recipe, not view state");
            await vm.OpenForEditAsync(entry);
            await WaitForAsync(() => compare.IsEnabled && vm.ComparisonMode == EditorComparisonMode.After,
                "Reopening starts with After");
            var raw = Path.Combine(directory, "Unsupported comparison.dng");
            await File.WriteAllBytesAsync(raw, [1, 2, 3]);
            await vm.OpenForEditAsync(new ImageEntry { FilePath = raw, FileName = Path.GetFileName(raw) });
            await WaitForAsync(() => !compare.IsEnabled, "RAW comparison is disabled");
            Check(ToolTipService.GetToolTip(compare).ToString()!.Contains("not available for RAW"),
                "RAW limitation has an explicit tooltip");

            if (Environment.GetCommandLineArgs().Contains("--preview"))
            {
                _window.Closed += (_, _) => Directory.Delete(directory, recursive: true);
                await vm.OpenForEditAsync(entry);
                await WaitForAsync(() => compare.IsEnabled, "Preview fixture reloaded");
                vm.Exposure = .35;
                vm.ToggleComparison(EditorComparisonMode.Split);
                editor.FocusEditor();
                return;
            }

            async Task ChooseModeAsync(string name)
            {
                compare.Flyout.ShowAt(compare);
                await Task.Delay(100);
                var item = (ToggleMenuFlyoutItem)editor.FindName(name);
                ((IToggleProvider)FrameworkElementAutomationPeer.CreatePeerForElement(item)
                    .GetPattern(PatternInterface.Toggle)).Toggle();
                compare.Flyout.Hide();
                await Task.Delay(100);
            }
        }
        finally
        {
            if (!Environment.GetCommandLineArgs().Contains("--preview"))
            {
                _window?.Close();
                Directory.Delete(directory, recursive: true);
            }
        }

        void Check(bool success, string name)
        {
            if (!success) throw new InvalidOperationException(name);
            _passed.Add(name);
        }

        async Task WaitForAsync(Func<bool> ready, string name)
        {
            for (var attempt = 0; attempt < 100 && !ready(); attempt++) await Task.Delay(100);
            Check(ready(), name);
        }
    }
}
