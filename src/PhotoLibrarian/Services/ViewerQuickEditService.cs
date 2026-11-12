// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace PhotoLibrarian.Services;

internal enum ViewerQuickEdit { RotateLeft, RotateRight, AutoBrightness, AutoColor }

/// <summary>Prepares a portable recipe on the history transaction's private copy.</summary>
internal static class ViewerQuickEditService
{
    internal static Task PrepareAsync(string path, ViewerQuickEdit action) => Task.Run(async () =>
    {
        // ReadEditParameters tolerates bad sidecars; an explicit edit must not erase one.
        var sidecar = Path.ChangeExtension(path, ".xmp");
        if (File.Exists(sidecar))
        {
            using var stream = File.OpenRead(sidecar);
            _ = XmpCore.XmpMetaFactory.Parse(stream);
        }
        var parameters = ImageEditService.ReadEditParameters(path);
        byte[] pixels = [];
        if (action is ViewerQuickEdit.AutoBrightness or ViewerQuickEdit.AutoColor)
            pixels = await LoadAnalysisPixelsAsync(path);
        if (!Apply(parameters, action, pixels)) return;
        _ = await ViewerEffectService.RenderAsync(path, parameters);
        await ImageEditService.WritePendingRecipeAsync(path, parameters);
    });

    internal static bool Apply(EditParameters parameters, ViewerQuickEdit action, byte[] pixels)
    {
        if (action is ViewerQuickEdit.RotateLeft or ViewerQuickEdit.RotateRight)
        {
            var angle = parameters.RotationAngle + (action == ViewerQuickEdit.RotateRight ? 90 : -90);
            parameters.RotationAngle = ((angle + 180) % 360 + 360) % 360 - 180;
            return true;
        }

        if (action == ViewerQuickEdit.AutoBrightness)
        {
            var tone = AutoEnhanceAnalyzer.Analyze(pixels);
            var changed = parameters.Brightness != 0 || parameters.Exposure != tone.Exposure ||
                parameters.Contrast != tone.Contrast || parameters.Highlights != tone.Highlights ||
                parameters.Shadows != tone.Shadows;
            parameters.Brightness = 0;
            parameters.Exposure = tone.Exposure;
            parameters.Contrast = tone.Contrast;
            parameters.Highlights = tone.Highlights;
            parameters.Shadows = tone.Shadows;
            return changed;
        }

        var color = AutoEnhanceAnalyzer.AnalyzeColor(pixels);
        var colorChanged = parameters.Saturation != color.Saturation ||
            parameters.Temperature != color.Temperature || parameters.Tint != color.Tint;
        parameters.Saturation = color.Saturation;
        parameters.Temperature = color.Temperature;
        parameters.Tint = color.Tint;
        return colorChanged;
    }

    private static async Task<byte[]> LoadAnalysisPixelsAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        // This is a statistics sample, not a limit on the source or rendered recipe.
        var scale = Math.Min(1d, 1024d / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
                ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale))
            },
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        return data.DetachPixelData();
    }
}
