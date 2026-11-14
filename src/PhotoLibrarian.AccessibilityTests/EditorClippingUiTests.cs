// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using PhotoLibrarian.ViewModels;
using PhotoLibrarian.Views;
using Windows.Graphics;
using Windows.Graphics.DirectX;

namespace PhotoLibrarian.AccessibilityTests;

public partial class App
{
    private async Task RunEditorClippingTestsAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-clipping-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var preview = Environment.GetCommandLineArgs().Contains("--preview");
        try
        {
            const int width = 320, height = 200;
            var path = Path.Combine(directory, "Synthetic SDR endpoints.png");
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var value = (byte)(x < 80 ? 0 : x < 160 ? 1 : x < 240 ? 254 : 255);
                pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
                pixels[i + 3] = y < 100 ? (byte)255 : (byte)128;
                if (y >= 100)
                    for (var c = 0; c < 3; c++) pixels[i + c] = (byte)Math.Round(value * 128d / 255);
            }
            using (var device = new CanvasDevice())
            using (var image = CanvasBitmap.CreateFromBytes(device, pixels, width, height,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied))
                await image.SaveAsync(path, CanvasBitmapFileFormat.Png);
            var vm = new ImageEditorViewModel(new OriginalBackupService(Path.Combine(directory, "originals")));
            await vm.OpenForEditAsync(new ImageEntry { FilePath = path, FileName = Path.GetFileName(path) });
            var editor = new ImageEditorView(vm);
            _window = new Window { Title = "Isolated clipping regression", Content = editor };
            _window.AppWindow.Resize(new SizeInt32(1280, 900));
            _window.Activate();
            var clipping = (Button)editor.FindName("ClippingButton");
            await WaitForAsync(() => clipping.IsEnabled, "Clipping enabled only after preview loads");
            var shadows = (ToggleMenuFlyoutItem)editor.FindName("ClippedShadowsItem");
            var highlights = (ToggleMenuFlyoutItem)editor.FindName("ClippedHighlightsItem");
            Check(!shadows.IsChecked && !highlights.IsChecked, "Clipping defaults off");
            Check(AutomationProperties.GetAutomationId(shadows) == "EditorClippedShadows" &&
                AutomationProperties.GetName(clipping) == "Clipping overlays", "Clipping controls have accessible identities");
            var initial = editor.GetPreviewAnalysis()!;
            Check(initial.HasShadows && initial.HasHighlights, "Loaded rendered histogram detects SDR endpoints");
            var identity = await FileEditTransaction.ReadIdentityAsync(path);
            var notifications = 0;
            vm.ParametersChanged += (_, _) => notifications++;
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                editor.RequestedTheme = theme;
                await ToggleAsync(shadows);
                await ToggleAsync(highlights);
                Check(shadows.IsChecked && highlights.IsChecked, $"{theme} accessible toggles enable both diagnostics");
                var marked = RenderEditor();
                Check(marked[0] == 255 && marked[2] == 0 &&
                    marked[319 * 4] == 0 && marked[319 * 4 + 2] == 255, $"{theme} image shows blue black and red white");
                Check(marked[(150 * width) * 4 + 3] == 128, $"{theme} image overlay preserves partial alpha");
                await ToggleAsync(shadows);
                await ToggleAsync(highlights);
            }
            Check(notifications == 0 && !vm.HasChanges && !vm.CanUndo && !vm.CanRedo &&
                ReferenceEquals(initial, editor.GetPreviewAnalysis()), "Toggles do not change history, recipe or histogram cache");
            Check(identity == await FileEditTransaction.ReadIdentityAsync(path), "Toggles leave photo and portable metadata unchanged");
            await ToggleAsync(shadows);
            await ToggleAsync(highlights);
            var curves = vm.GetCurrentParameters().ToneCurves;
            curves.Rgb = [new(0, .1), new(1, .9)];
            vm.SetToneCurves(curves);
            await WaitForAsync(() => editor.GetPreviewAnalysis() is { HasShadows: false, HasHighlights: false },
                "Curve edit invalidates analysis and removes clipping");
            vm.UndoCommand.Execute(null);
            await WaitForAsync(() => editor.GetPreviewAnalysis() is { HasShadows: true, HasHighlights: true },
                "Undo refreshes analysis and restores clipping");
            vm.RedoCommand.Execute(null);
            await WaitForAsync(() => editor.GetPreviewAnalysis() is { HasShadows: false, HasHighlights: false },
                "Redo refreshes analysis");
            vm.UndoCommand.Execute(null);
            vm.Exposure = .3;
            await Task.Delay(150);
            Check(vm.GetCurrentParameters().ToneCurves.IsDefault, "Save fixture has returned to the baking path");
            var canvas = (Microsoft.Graphics.Canvas.UI.Xaml.CanvasControl)editor.FindName("EditCanvas");
            var referencePath = Path.Combine(directory, "No diagnostic control.png");
            File.Copy(path, referencePath);
            await new ImageEditService(new OriginalBackupService(Path.Combine(directory, "control-originals")))
                .ApplyEditsAsync(referencePath, vm.GetCurrentParameters(),
                    (copy, parameters) => ImageEditRenderer.RenderToFileAsync(copy, parameters, canvas.Device));
            var save = (Button)editor.FindName("SaveBtn");
            ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(save)
                .GetPattern(PatternInterface.Invoke)).Invoke();
            await WaitForAsync(() => !vm.IsBusy && !vm.HasChanges && clipping.IsEnabled, "Save with overlays enabled completes");
            using (var saved = await ImageEditRenderer.LoadOrientedAsync(canvas.Device, path))
            using (var reference = await ImageEditRenderer.LoadOrientedAsync(canvas.Device, referencePath))
            {
                Check(saved.GetPixelBytes().SequenceEqual(reference.GetPixelBytes()),
                    "Baked PNG with overlays exactly matches independently saved no-diagnostic control");
            }
            Check(shadows.IsChecked && highlights.IsChecked, "Save keeps only temporary view toggles");
            Check(!ImageEditService.ReadEditParameters(path).HasAdjustments, "No diagnostic state persisted in recipe");
            if (preview)
            {
                ((Expander)editor.FindName("LevelsPanel")).IsExpanded = true;
                vm.ToggleComparison(EditorComparisonMode.SideBySide);
                _window.Closed += (_, _) => Directory.Delete(directory, recursive: true);
                return;
            }

            byte[] RenderEditor()
            {
                var canvas = (Microsoft.Graphics.Canvas.UI.Xaml.CanvasControl)editor.FindName("EditCanvas");
                using var target = new CanvasRenderTarget(canvas, width, height, 96);
                using (var session = target.CreateDrawingSession())
                {
                    session.Clear(Microsoft.UI.Colors.Transparent);
                    Check(editor.TryDrawComparisonPreview(session, new(width, height)), "Real editor draws diagnostic preview");
                }
                return target.GetPixelBytes();
            }

            async Task ToggleAsync(ToggleMenuFlyoutItem item)
            {
                clipping.Flyout.ShowAt(clipping);
                await Task.Delay(80);
                ((IToggleProvider)FrameworkElementAutomationPeer.CreatePeerForElement(item)
                    .GetPattern(PatternInterface.Toggle)).Toggle();
                clipping.Flyout.Hide();
                await Task.Delay(80);
            }
        }
        finally
        {
            if (!preview)
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
            for (var i = 0; i < 100 && !ready(); i++) await Task.Delay(100);
            Check(ready(), name);
        }
    }
}
