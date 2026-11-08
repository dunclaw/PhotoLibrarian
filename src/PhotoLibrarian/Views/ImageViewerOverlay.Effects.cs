// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using System.Runtime.InteropServices.WindowsRuntime;

namespace PhotoLibrarian.Views;

public sealed partial class ImageViewerOverlay
{
    private readonly EffectThumbnailSession _effectThumbnails = new();
    private readonly List<EffectGalleryItem> _effects = [];
    private bool _syncingEffect;
    private int _effectsVersion;
    private ImageEntry? _effectsEntry;

    public async Task ShowEffectsAsync(FrameworkElement anchor)
    {
        var entry = ViewModel?.CurrentEntry;
        if (entry?.MediaType != MediaType.Image) return;
        StopFocusGuard();
        _effectsEntry = entry;
        if (_effects.Count == 0)
        {
            foreach (var effect in Enum.GetValues<PhotoEffect>())
                _effects.Add(new EffectGalleryItem(effect, EditorEffectStrings.Get($"EditorEffect{effect}")));
            EffectsGallery.ItemsSource = _effects;
        }
        var version = ++_effectsVersion;
        _effectThumbnails.Invalidate();
        foreach (var item in _effects) item.Thumbnail = null;
        EffectsGallery.IsEnabled = false;
        _syncingEffect = true;
        try { EffectsFlyout.ShowAt(anchor); }
        finally { _syncingEffect = false; }
        UndoEffectButton.IsEnabled = ViewModel?.CanUndoEffect == true;
        RedoEffectButton.IsEnabled = ViewModel?.CanRedoEffect == true;
        if (!ImageEditRenderer.IsSupported(entry.FilePath))
        {
            EffectsGallery.IsEnabled = UndoEffectButton.IsEnabled = RedoEffectButton.IsEnabled = false;
            EffectsStatus.Text = EditorEffectStrings.Get("EditorEffectsRawUnavailable");
            return;
        }
        try
        {
            var recipe = ImageEditService.ReadEditParameters(entry.FilePath);
            _syncingEffect = true;
            try { EffectsGallery.SelectedItem = _effects.Find(item => item.Effect == recipe.Effect); }
            finally { _syncingEffect = false; }
            EffectsGallery.IsEnabled = ViewModel?.IsApplyingEffect != true;
            EffectsStatus.Text = EditorEffectStrings.Get("EditorEffectsLoading");
            var thumbnails = await _effectThumbnails.RequestAsync(entry.FilePath, recipe);
            if (thumbnails is null || version != _effectsVersion || !ReferenceEquals(entry, ViewModel?.CurrentEntry)) return;
            foreach (var thumbnail in thumbnails)
            {
                var bitmap = new WriteableBitmap(thumbnail.Width, thumbnail.Height);
                using var buffer = bitmap.PixelBuffer.AsStream();
                buffer.Write(thumbnail.Pixels);
                bitmap.Invalidate();
                _effects.Find(item => item.Effect == thumbnail.Effect)!.Thumbnail = bitmap;
            }
            EffectsStatus.Text = EditorEffectStrings.Get("EditorEffectsReady");
        }
        catch (Exception ex)
        {
            if (version == _effectsVersion) SetEffectError(ex);
        }
    }

    private void OnEffectsClosed(object sender, object args)
    {
        _effectsVersion++;
        _effectsEntry = null;
        _effectThumbnails.Invalidate();
        foreach (var item in _effects) item.Thumbnail = null;
    }

    private async void OnUndoEffect(object sender, RoutedEventArgs args) => await MoveEffectHistoryAsync(undo: true);
    private async void OnRedoEffect(object sender, RoutedEventArgs args) => await MoveEffectHistoryAsync(undo: false);

    private async Task MoveEffectHistoryAsync(bool undo)
    {
        var vm = ViewModel;
        if (vm is null || !ReferenceEquals(_effectsEntry, vm.CurrentEntry) ||
            !(undo ? vm.CanUndoEffect : vm.CanRedoEffect)) return;
        EffectsGallery.IsEnabled = UndoEffectButton.IsEnabled = RedoEffectButton.IsEnabled = false;
        try
        {
            if (undo) await vm.UndoEffectAsync();
            else await vm.RedoEffectAsync();
            EffectsFlyout.Hide();
        }
        catch (Exception ex) { SetEffectError(ex, applying: true); }
        finally
        {
            EffectsGallery.IsEnabled = true;
            UndoEffectButton.IsEnabled = vm.CanUndoEffect;
            RedoEffectButton.IsEnabled = vm.CanRedoEffect;
        }
    }

    private async void OnEffectSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_syncingEffect || EffectsGallery.SelectedItem is not EffectGalleryItem item ||
            ViewModel?.CurrentEntry is null || ViewModel.IsApplyingEffect ||
            !ReferenceEquals(_effectsEntry, ViewModel.CurrentEntry)) return;
        EffectsGallery.IsEnabled = false;
        try
        {
            await ViewModel.ApplyEffectAsync(item.Effect);
            EffectsFlyout.Hide();
        }
        catch (Exception ex) { SetEffectError(ex, applying: true); }
        finally
        {
            EffectsGallery.IsEnabled = true;
            UndoEffectButton.IsEnabled = ViewModel?.CanUndoEffect == true;
            RedoEffectButton.IsEnabled = ViewModel?.CanRedoEffect == true;
        }
    }

    private void SetEffectError(Exception exception, bool applying = false)
    {
        EffectsStatus.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
            EditorEffectStrings.Get(applying ? "EditorEffectsApplyFailed" : "EditorEffectsFailed"), exception.Message);
        if (App.ViewModel is { } main) main.StatusText = EffectsStatus.Text;
    }
}
