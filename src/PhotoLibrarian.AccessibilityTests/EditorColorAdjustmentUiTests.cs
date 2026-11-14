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
    private async Task RunColorAdjustmentControlTestsAsync(
        ImageEditorViewModel viewModel, ImageEditorView editor, string directory, Window window)
    {
        var path = Path.Combine(directory, "Synthetic color photo.png");
        byte[] pixels = [40, 100, 220, 255, 40, 100, 220, 255];
        using (var device = new CanvasDevice())
        using (var image = CanvasBitmap.CreateFromBytes(device, pixels, 2, 1,
            DirectXPixelFormat.B8G8R8A8UIntNormalized))
            await image.SaveAsync(path, CanvasBitmapFileFormat.Png);
        await viewModel.OpenForEditAsync(new ImageEntry
        {
            FilePath = path,
            FileName = Path.GetFileName(path)
        });
        await WaitForAsync(() => ((Button)editor.FindName("ComparisonButton")).IsEnabled,
            "Color editor source loaded");

        ((Expander)editor.FindName("ColorMixerExpander")).IsExpanded = true;
        ((Expander)editor.FindName("ColorGradingExpander")).IsExpanded = true;
        await Task.Delay(100);

        var band = (ComboBox)editor.FindName("ColorMixerBand");
        var hue = (Slider)editor.FindName("ColorMixerHueSlider");
        var saturation = (Slider)editor.FindName("ColorMixerSaturationSlider");
        var luminance = (Slider)editor.FindName("ColorMixerLuminanceSlider");
        var shadowAmount = (Slider)editor.FindName("ShadowAmountSlider");
        var midtoneAmount = (Slider)editor.FindName("MidtoneAmountSlider");
        var highlightAmount = (Slider)editor.FindName("HighlightAmountSlider");
        var blending = (Slider)editor.FindName("ColorGradingBlendingSlider");
        var balance = (Slider)editor.FindName("ColorGradingBalanceSlider");
        var shadowWheel = (ColorPicker)editor.FindName("ShadowColorPicker");
        var reset = (Button)editor.FindName("ResetBtn");

        Check(AutomationProperties.GetAutomationId(band) == "EditorColorMixerBand" &&
            AutomationProperties.GetAutomationId(hue) == "EditorColorMixerHue" &&
            AutomationProperties.GetName(hue).Contains("hue shift"),
            "Mixer band and hue control expose accessible names and IDs");
        Check(AutomationProperties.GetAutomationId(shadowWheel) == "EditorShadowColorWheel" &&
            AutomationProperties.GetAutomationId(blending) == "EditorColorGradingBlending" &&
            AutomationProperties.GetAutomationId(balance) == "EditorColorGradingBalance",
            "Grading wheel, blending, and balance expose accessible IDs");

        band.SelectedIndex = 3;
        Check(band.SelectedIndex == 3, "Color mixer selects its green hue band");
        viewModel.BeginEdit("Color controls");
        SetSliderValue(hue, 45);
        SetSliderValue(saturation, 0.4);
        SetSliderValue(luminance, 0.2);
        SetSliderValue(shadowAmount, 0.65);
        SetSliderValue(midtoneAmount, 0.4);
        SetSliderValue(highlightAmount, 0.3);
        SetSliderValue(blending, 0.75);
        SetSliderValue(balance, -0.3);
        shadowWheel.Color = Windows.UI.Color.FromArgb(255, 255, 192, 192);
        viewModel.EndEdit();

        var edited = viewModel.GetCurrentParameters().ColorAdjustments;
        Check(edited.MixerBands[3].HueShiftDegrees == 45 &&
            edited.MixerBands[3].Saturation == 0.4 &&
            edited.MixerBands[3].Luminance == 0.2,
            "Mixer sliders update the selected recipe band");
        Check(edited.Shadows.Amount > 0.24 && edited.Shadows.Amount < 0.25 &&
            edited.Shadows.HueDegrees == 0 &&
            edited.Midtones.Amount == 0.4 && edited.Highlights.Amount == 0.3 &&
            edited.Blending == 0.75 && edited.Balance == -0.3,
            "Color wheels and tonal sliders update grading settings");
        Check(viewModel.CanUndo, "Color controls create one undoable edit gesture");

        viewModel.UndoCommand.Execute(null);
        Check(viewModel.GetCurrentParameters().ColorAdjustments.IsDefault,
            "Undo restores neutral color controls");
        viewModel.RedoCommand.Execute(null);
        Check(viewModel.GetCurrentParameters().ColorAdjustments.ContentEquals(edited),
            "Redo restores mixer and grading controls");
        var adjustmentsPanel = (ScrollViewer)editor.FindName("AdjustmentsPanel");
        adjustmentsPanel.ChangeView(null, 520, null, true);
        await Task.Delay(150);
        await CaptureColorWindowAsync(window, "color-mixer-ui.png");
        foreach (var (offset, screenshot) in new[]
        {
            (880d, "shadow-grading-ui.png"),
            (1240d, "midtone-grading-ui.png"),
            (1600d, "highlight-grading-ui.png")
        })
        {
            adjustmentsPanel.ChangeView(null, offset, null, true);
            await Task.Delay(150);
            await CaptureColorWindowAsync(window, screenshot);
        }

        var identity = await FileEditTransaction.ReadIdentityAsync(path);
        var resetPeer = FrameworkElementAutomationPeer.CreatePeerForElement(reset);
        ((IInvokeProvider)resetPeer.GetPattern(PatternInterface.Invoke)).Invoke();
        await Task.Delay(100);
        Check(viewModel.GetCurrentParameters().ColorAdjustments.IsDefault &&
            hue.Value == 0 && saturation.Value == 0 && luminance.Value == 0 &&
            blending.Value == 0.5 && balance.Value == 0,
            "Reset clears color edits and restores neutral control values");
        Check(identity == await FileEditTransaction.ReadIdentityAsync(path) &&
            ImageEditService.ReadEditParameters(path).ColorAdjustments.IsDefault,
            "Color-control gestures and Reset leave the photo and recipe untouched");

        adjustmentsPanel.ChangeView(null, 520, null, true);
        await Task.Delay(150);
        await CaptureColorWindowAsync(window, "color-adjustments-reset-ui.png");
        _passed.Add("Color mixer and grading UI screenshots captured");

        void Check(bool success, string name)
        {
            if (!success) throw new InvalidOperationException(name);
            _passed.Add(name);
        }

        static void SetSliderValue(Slider slider, double value)
        {
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(slider);
            var range = (IRangeValueProvider)peer.GetPattern(PatternInterface.RangeValue);
            range.SetValue(value);
        }

        static Task CaptureColorWindowAsync(Window window, string fileName)
        {
            var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window).ToInt64().ToString();
            return RunViewerUiCommandAsync("screenshot", "--window", windowHandle, "--output",
                Path.Combine(AppContext.BaseDirectory, fileName));
        }

        async Task WaitForAsync(Func<bool> ready, string name)
        {
            for (var attempt = 0; attempt < 100 && !ready(); attempt++) await Task.Delay(100);
            Check(ready(), name);
        }
    }
}
