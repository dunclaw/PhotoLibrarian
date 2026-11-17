// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;

namespace PhotoLibrarian.Views;

public sealed partial class ImageViewerOverlay
{
    private readonly PhotoPresetStore? _presetStoreOverride;
    private readonly EffectThumbnailSession _effectThumbnails = new();
    private readonly EditRecipePreviewSession _presetPreview = new();
    private readonly List<EffectGalleryItem> _effects = [];
    private bool _syncingEffect;
    private int _effectsVersion;
    private int _presetPreviewVersion;
    private ImageEntry? _effectsEntry;
    private EditParameters? _presetBaseRecipe;
    private Task<string>? _presetEntryIdentityTask;
    private Task<string>? _presetTargetIdentityTask;

    public static string PresetFavoriteAutomationId(string id) => $"EditorPresetFavorite_{id}";
    public static string PresetDeleteAutomationId(string id) => $"EditorPresetDelete_{id}";

    private PhotoPresetStore? PresetStore => _presetStoreOverride ?? App.ViewModel?.PresetStore;

    public async Task ShowEffectsAsync(FrameworkElement anchor)
    {
        if (IsPhotoFusing) return;
        var entry = ViewModel?.CurrentEntry;
        if (entry?.MediaType != MediaType.Image) return;
        StopFocusGuard();
        _effectsEntry = entry;
        _presetEntryIdentityTask = null;
        _presetTargetIdentityTask = null;
        try { _presetBaseRecipe = ImageEditService.ReadEditParameters(entry.FilePath); }
        catch (Exception exception)
        {
            SetPresetError(exception);
            return;
        }
        ReloadPresetGallery();
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

        _presetEntryIdentityTask = FileEditTransaction.ReadIdentityAsync(entry.FilePath);
        _ = ObservePresetEntryIdentityAsync(_presetEntryIdentityTask, entry);
        if (!ImageEditRenderer.IsSupported(entry.FilePath))
        {
            EffectsGallery.IsEnabled = false;
            EffectsStatus.Text = EditorEffectStrings.Get("EditorEffectsRawUnavailable");
            return;
        }
        try
        {
            var recipe = _presetBaseRecipe.Clone();
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

    private void ReloadPresetGallery(string? selectedId = null)
    {
        var result = PresetStore?.Load();
        if (result is null)
        {
            PresetGallery.ItemsSource = Array.Empty<PhotoPreset>();
            PresetsStatus.Text = EditorEffectStrings.Get("EditPresetsUnavailable");
            return;
        }

        var presets = result.Presets
            .OrderByDescending(preset => preset.IsFavorite)
            .ThenBy(preset => preset.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        PresetGallery.ItemsSource = presets;
        if (selectedId is not null)
            PresetGallery.SelectedItem = presets.FirstOrDefault(preset => preset.Id == selectedId);
        EditorApplyPresetButton.IsEnabled = PresetGallery.SelectedItem is PhotoPreset;

        PresetsStatus.Text = result.Status switch
        {
            PhotoPresetCatalogStatus.Missing => EditorEffectStrings.Get("EditPresetsEmpty"),
            PhotoPresetCatalogStatus.Invalid or PhotoPresetCatalogStatus.UnsupportedVersion =>
                result.Message ?? EditorEffectStrings.Get("EditPresetsUnavailable"),
            _ => presets.Length == 0
                ? EditorEffectStrings.Get("EditPresetsEmpty")
                : string.Format(CultureInfo.CurrentCulture,
                    EditorEffectStrings.Get("EditPresetsCount"), presets.Length)
        };
    }

    private void OnEffectsClosed(object sender, object args)
    {
        _effectsVersion++;
        _presetPreviewVersion++;
        _effectsEntry = null;
        _presetBaseRecipe = null;
        _presetEntryIdentityTask = null;
        _presetTargetIdentityTask = null;
        _effectThumbnails.Invalidate();
        _presetPreview.Invalidate();
        PresetPreviewImage.Source = null;
        PresetPreviewImage.Visibility = Visibility.Collapsed;
        foreach (var item in _effects) item.Thumbnail = null;
    }

    public async Task MoveEffectHistoryAsync(bool undo)
    {
        var vm = ViewModel;
        if (vm is null || IsPhotoFusing || IsCropping || IsStraightening || IsRedEyeRemoving ||
            !(undo ? vm.CanUndoEffect : vm.CanRedoEffect)) return;
        EffectsGallery.IsEnabled = false;
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
        }
    }

    private async void OnEffectSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (IsPhotoFusing || _syncingEffect || EffectsGallery.SelectedItem is not EffectGalleryItem item ||
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
        }
    }

    private void OnPresetSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        EditorApplyPresetButton.IsEnabled = PresetGallery.SelectedItem is PhotoPreset;
        PresetAmountSlider.IsEnabled = PresetGallery.SelectedItem is PhotoPreset;
        if (PresetGallery.SelectedItem is PhotoPreset)
            PresetAmountSlider.Value = 100;
        _ = UpdatePresetPreviewAsync();
    }

    private void OnPresetAmountChanged(
        object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs args)
    {
        if (PresetAmountValue is null) return;
        PresetAmountValue.Text = $"{args.NewValue.ToString("0", CultureInfo.CurrentCulture)}%";
        if (PresetGallery?.SelectedItem is PhotoPreset)
            _ = UpdatePresetPreviewAsync();
    }

    private async Task UpdatePresetPreviewAsync()
    {
        var version = ++_presetPreviewVersion;
        var entry = _effectsEntry;
        var preset = PresetGallery.SelectedItem as PhotoPreset;
        var baseline = _presetBaseRecipe;
        if (entry is null || baseline is null || preset is null ||
            !ReferenceEquals(entry, ViewModel?.CurrentEntry))
        {
            _presetPreview.Invalidate();
            PresetPreviewImage.Source = null;
            PresetPreviewImage.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            var amount = (int)Math.Round(PresetAmountSlider.Value);
            var composition = await ComposeSelectedPresetAsync(preset, entry, baseline, amount);
            if (version != _presetPreviewVersion) return;
            PresetsStatus.Text = composition.SkippedGroups.Count == 0
                ? EditorEffectStrings.Get("EditPresetPreviewReady")
                : string.Format(CultureInfo.CurrentCulture,
                    EditorEffectStrings.Get("EditPresetPreviewSkipped"),
                    string.Join(", ", composition.SkippedGroups.Select(DescribeGroup)));
            if (amount == 0 || EditRecipeComposer.ContentEquals(baseline, composition.Parameters))
            {
                _presetPreview.Invalidate();
                PresetPreviewImage.Source = null;
                PresetPreviewImage.Visibility = Visibility.Collapsed;
                return;
            }

            var preview = await _presetPreview.RequestAsync(entry.FilePath, composition.Parameters);
            if (preview is null || version != _presetPreviewVersion ||
                !ReferenceEquals(entry, ViewModel?.CurrentEntry) ||
                !ReferenceEquals(preset, PresetGallery.SelectedItem))
                return;
            var bitmap = new WriteableBitmap(preview.Width, preview.Height);
            using (var buffer = bitmap.PixelBuffer.AsStream())
                buffer.Write(preview.Pixels);
            bitmap.Invalidate();
            PresetPreviewImage.Source = bitmap;
            PresetPreviewImage.Visibility = Visibility.Visible;
        }
        catch (Exception exception)
        {
            if (version == _presetPreviewVersion)
                PresetsStatus.Text = string.Format(CultureInfo.CurrentCulture,
                    EditorEffectStrings.Get("EditPresetPreviewFailed"), exception.Message);
        }
    }

    private async Task<EditRecipeComposition> ComposeSelectedPresetAsync(
        PhotoPreset preset,
        ImageEntry entry,
        EditParameters baseline,
        int amount)
    {
        var sameSource = false;
        if (amount > 0 && preset.Groups.HasFlag(EditSettingGroup.Retouch) &&
            preset.SourceIdentity is { Length: > 0 } sourceIdentity)
        {
            _presetTargetIdentityTask ??= RecognitionSourceIdentity.ComputeAsync(entry.FilePath);
            var targetIdentity = await _presetTargetIdentityTask;
            sameSource = string.Equals(sourceIdentity, targetIdentity, StringComparison.Ordinal);
        }
        return EditRecipeComposer.Compose(
            baseline,
            preset.Parameters,
            preset.Groups,
            amount,
            RawToneMappingService.IsRaw(entry.FilePath),
            sameSource);
    }

    private async Task ObservePresetEntryIdentityAsync(Task<string> identityTask, ImageEntry entry)
    {
        try { await identityTask; }
        catch (Exception exception)
        {
            if (ReferenceEquals(_effectsEntry, entry) &&
                ReferenceEquals(_presetEntryIdentityTask, identityTask))
                SetPresetError(exception);
        }
    }

    private async void OnSavePresetClick(object sender, RoutedEventArgs args)
    {
        var entry = ViewModel?.CurrentEntry;
        if (entry is null || !ReferenceEquals(entry, _effectsEntry) || _presetBaseRecipe is null) return;
        try
        {
            var result = await EditSettingGroupDialog.ShowAsync(
                XamlRoot,
                ActualTheme,
                EditorEffectStrings.Get("EditPresetSaveTitle"),
                EditSettingGroupDialog.AvailableForPhoto(entry.FilePath),
                includeName: true);
            if (result is null || App.ViewModel is not { } mainViewModel ||
                _presetEntryIdentityTask is null)
                return;
            string? sourceIdentity = null;
            if (result.Groups.HasFlag(EditSettingGroup.Retouch))
            {
                _presetTargetIdentityTask ??= RecognitionSourceIdentity.ComputeAsync(entry.FilePath);
                sourceIdentity = await _presetTargetIdentityTask;
            }
            var expectedIdentity = await _presetEntryIdentityTask;
            var preset = await mainViewModel.CreatePresetAsync(
                result.Name!,
                entry.FilePath,
                _presetBaseRecipe,
                result.Groups,
                expectedIdentity,
                sourceIdentity);
            if (preset is null)
            {
                PresetsStatus.Text = mainViewModel.StatusText;
                return;
            }
            mainViewModel.PresetStore.Save(preset);
            ReloadPresetGallery(preset.Id);
            PresetsStatus.Text = string.Format(CultureInfo.CurrentCulture,
                EditorEffectStrings.Get("EditPresetSaved"), preset.Name);
        }
        catch (Exception exception) { SetPresetError(exception); }
    }

    private async void OnApplyPresetClick(object sender, RoutedEventArgs args)
    {
        var mainViewModel = App.ViewModel;
        if (mainViewModel is null || PresetGallery.SelectedItem is not PhotoPreset preset ||
            _effectsEntry is null || !ReferenceEquals(_effectsEntry, ViewModel?.CurrentEntry))
            return;
        EditorApplyPresetButton.IsEnabled = false;
        try
        {
            if (_presetBaseRecipe is null || _presetEntryIdentityTask is null) return;
            var composition = await ComposeSelectedPresetAsync(
                preset,
                _effectsEntry,
                _presetBaseRecipe,
                (int)Math.Round(PresetAmountSlider.Value));
            if (EditRecipeComposer.ContentEquals(_presetBaseRecipe, composition.Parameters))
            {
                PresetsStatus.Text = EditorEffectStrings.Get("EditStatusNoChanges");
                return;
            }
            var expectedIdentity = await _presetEntryIdentityTask;
            var applied = await mainViewModel.ApplyComposedRecipeAsync(
                _effectsEntry,
                composition.Parameters,
                expectedIdentity,
                $"apply preset {preset.Name}");
            if (applied) EffectsFlyout.Hide();
            else PresetsStatus.Text = mainViewModel.StatusText;
            if (composition.SkippedGroups.Count > 0)
            {
                var skipped = string.Format(CultureInfo.CurrentCulture,
                    PhotoContextMenuStrings.Get("EditStatusSkipped"),
                    _effectsEntry.FileName,
                    string.Join(", ", composition.SkippedGroups.Select(DescribeGroup)));
                mainViewModel.StatusText = applied
                    ? $"{mainViewModel.StatusText}{Environment.NewLine}{skipped}"
                    : skipped;
                PresetsStatus.Text = mainViewModel.StatusText;
            }
        }
        catch (Exception exception) { SetPresetError(exception); }
        finally
        {
            EditorApplyPresetButton.IsEnabled = PresetGallery.SelectedItem is PhotoPreset;
        }
    }

    private void OnFavoritePresetClick(object sender, RoutedEventArgs args)
    {
        if (PresetStore is not { } presetStore ||
            sender is not FrameworkElement { DataContext: PhotoPreset preset }) return;
        try
        {
            presetStore.SetFavorite(preset.Id, !preset.IsFavorite);
            ReloadPresetGallery(preset.Id);
        }
        catch (Exception exception) { SetPresetError(exception); }
    }

    private async void OnDeletePresetClick(object sender, RoutedEventArgs args)
    {
        if (PresetStore is not { } presetStore ||
            sender is not FrameworkElement { DataContext: PhotoPreset preset }) return;
        try
        {
            var result = await DialogService.Instance.ConfirmDestructiveAsync(
                this,
                EditorEffectStrings.Get("EditPresetDeleteTitle"),
                string.Format(CultureInfo.CurrentCulture,
                    EditorEffectStrings.Get("EditPresetDeleteMessage"), preset.Name),
                EditorEffectStrings.Get("EditPresetDelete"),
                EditorEffectStrings.Get("EditPresetCancel"));
            if (result != ContentDialogResult.Primary) return;

            presetStore.Delete(preset.Id);
            ReloadPresetGallery();
        }
        catch (Exception exception) { SetPresetError(exception); }
    }

    private static string DescribeGroup(EditSettingGroup group) =>
        EditorEffectStrings.Get($"EditGroup{group}");

    private void SetPresetError(Exception exception)
    {
        PresetsStatus.Text = string.Format(CultureInfo.CurrentCulture,
            EditorEffectStrings.Get("EditPresetOperationFailed"), exception.Message);
        if (App.ViewModel is { } main) main.StatusText = PresetsStatus.Text;
    }

    private void SetEffectError(Exception exception, bool applying = false)
    {
        EffectsStatus.Text = string.Format(CultureInfo.CurrentCulture,
            EditorEffectStrings.Get(applying ? "EditorEffectsApplyFailed" : "EditorEffectsFailed"), exception.Message);
        if (App.ViewModel is { } main) main.StatusText = EffectsStatus.Text;
    }
}
