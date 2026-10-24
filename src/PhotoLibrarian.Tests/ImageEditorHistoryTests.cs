// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.ViewModels;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class ImageEditorHistoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-history-{Guid.NewGuid():N}");
    private readonly OriginalBackupService _backups;
    private readonly ImageEntry _entry;

    public ImageEditorHistoryTests()
    {
        Directory.CreateDirectory(_directory);
        _backups = new OriginalBackupService(Path.Combine(_directory, "originals"));
        var path = Path.Combine(_directory, "image.jpg");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        _entry = new ImageEntry { FilePath = path, FileName = "image.jpg" };
    }

    private async Task<ImageEditorViewModel> OpenAsync()
    {
        var vm = new ImageEditorViewModel(_backups);
        await vm.OpenForEditAsync(_entry);
        return vm;
    }

    [Fact]
    public async Task IndividualEdits_UndoAndRedoInOrder_AndTrackDirtyState()
    {
        var vm = await OpenAsync();
        Assert.False(vm.CanUndo);
        Assert.False(vm.CanRedo);
        vm.Exposure = 0.4;
        vm.RotationAngle = 90;
        Assert.Equal(new[] { "Rotation", "Exposure" }, vm.UndoHistory);

        vm.UndoCommand.Execute(null);
        Assert.Equal(0, vm.RotationAngle);
        Assert.Equal(0.4, vm.Exposure);
        Assert.True(vm.HasChanges);
        vm.UndoCommand.Execute(null);
        Assert.False(vm.HasChanges);
        Assert.False(vm.CanUndo);
        Assert.True(vm.CanRedo);

        vm.RedoSteps(2);
        Assert.Equal(0.4, vm.Exposure);
        Assert.Equal(90, vm.RotationAngle);
        Assert.False(vm.CanRedo);
    }

    [Fact]
    public async Task Gesture_CoalescesAllParameters_AndRestoresPreviewOnce()
    {
        var vm = await OpenAsync();
        vm.BeginEdit("Adjustments");
        for (var i = 1; i <= 20; i++) vm.Exposure = i / 100.0;
        vm.Brightness = vm.Contrast = vm.Highlights = vm.Shadows = 0.1;
        vm.Saturation = vm.Temperature = vm.Tint = vm.Clarity = vm.Sharpness = vm.NoiseReduction = 0.2;
        vm.BlackPoint = 0.1;
        vm.WhitePoint = 0.9;
        vm.Midtones = 0.6;
        vm.RotationAngle = 45;
        vm.EndEdit();
        Assert.Single(vm.UndoHistory);
        var expected = vm.GetCurrentParameters();
        var previews = 0;
        vm.ParametersChanged += (_, _) => previews++;

        vm.UndoCommand.Execute(null);
        Assert.False(vm.GetCurrentParameters().HasAdjustments);
        Assert.Equal(1, previews);
        vm.RedoCommand.Execute(null);
        var actual = vm.GetCurrentParameters();
        foreach (var property in typeof(EditParameters).GetProperties().Where(p => p.PropertyType == typeof(double)))
            Assert.Equal(property.GetValue(expected), property.GetValue(actual));
        Assert.Equal(2, previews);
    }

    [Fact]
    public async Task NoOpGesture_PreservesRedo_WhileNewEditInvalidatesIt()
    {
        var vm = await OpenAsync();
        vm.Contrast = 0.5;
        vm.UndoCommand.Execute(null);
        vm.BeginEdit("Exposure");
        vm.Exposure = 0.2;
        vm.Exposure = 0;
        vm.EndEdit();
        Assert.Empty(vm.UndoHistory);
        Assert.Single(vm.RedoHistory);
        vm.Tint = 0.1;
        Assert.Empty(vm.RedoHistory);
    }

    [Fact]
    public async Task AutoEnhanceAndReset_AreAtomicAndUndoable()
    {
        var vm = await OpenAsync();
        vm.RotationAngle = 30;
        vm.ApplyAutoEnhance(new EditParameters
        {
            Exposure = 0.2, Contrast = 0.3, Highlights = -0.4,
            Shadows = 0.5, Saturation = 0.6, Clarity = 0.7
        });
        Assert.Equal(new[] { "Auto enhance", "Rotation" }, vm.UndoHistory);
        vm.ResetAllCommand.Execute(null);
        Assert.False(vm.GetCurrentParameters().HasAdjustments);
        Assert.False(vm.HasChanges);
        vm.UndoCommand.Execute(null);
        Assert.Equal(0.2, vm.Exposure);
        Assert.Equal(0.7, vm.Clarity);
        Assert.Equal(30, vm.RotationAngle);
        vm.UndoCommand.Execute(null);
        Assert.Equal(0, vm.Exposure);
        Assert.Equal(30, vm.RotationAngle);
    }

    [Fact]
    public async Task HistoryDropdownSteps_MoveContiguousEdits_InBothDirections()
    {
        var vm = await OpenAsync();
        vm.Exposure = 0.1;
        vm.Contrast = 0.2;
        vm.Saturation = 0.3;
        vm.UndoSteps(2);
        Assert.Equal(new[] { "Contrast", "Saturation" }, vm.RedoHistory);
        Assert.Equal(0.1, vm.Exposure);
        Assert.Equal(0, vm.Contrast);
        Assert.Equal(0, vm.Saturation);
        vm.RedoSteps(2);
        Assert.Equal(new[] { "Saturation", "Contrast", "Exposure" }, vm.UndoHistory);
        Assert.Equal(0.3, vm.Saturation);
        Assert.Equal(0.2, vm.Contrast);
    }

    [Fact]
    public async Task OpenAndClose_ClearHistory_AndDoNotLeakParametersToAnotherImage()
    {
        var vm = await OpenAsync();
        vm.Sharpness = 0.6;
        vm.CloseCommand.Execute(null);
        Assert.False(vm.CanUndo);
        Assert.Empty(vm.UndoHistory);
        await vm.OpenForEditAsync(_entry);
        Assert.False(vm.GetCurrentParameters().HasAdjustments);
        Assert.False(vm.HasChanges);
        vm.Saturation = 0.5;
        var other = Path.Combine(_directory, "other.jpg");
        File.WriteAllBytes(other, [5, 6, 7]);
        await vm.OpenForEditAsync(new ImageEntry { FilePath = other, FileName = "other.jpg" });
        Assert.Empty(vm.UndoHistory);
        Assert.False(vm.GetCurrentParameters().HasAdjustments);
    }

    [Fact]
    public async Task PendingXmpEdits_AreTheInitialUndoBaseline()
    {
        await new ImageEditService(_backups).SaveEditParametersAsync(
            _entry.FilePath, new EditParameters { Exposure = 0.3 });
        var vm = await OpenAsync();
        Assert.Empty(vm.UndoHistory);
        Assert.False(vm.HasChanges);
        vm.Exposure = 0.7;
        vm.UndoCommand.Execute(null);
        Assert.Equal(0.3, vm.Exposure);
        Assert.False(vm.HasChanges);
    }

    [Fact]
    public async Task NoiseReduction_TracksHistoryResetPendingEditsAndSave()
    {
        await new ImageEditService(_backups).SaveEditParametersAsync(
            _entry.FilePath, new EditParameters { NoiseReduction = 0.25 });
        var vm = await OpenAsync();
        Assert.Equal(0.25, vm.NoiseReduction);
        Assert.False(vm.HasChanges);
        vm.BeginEdit("Noise reduction");
        vm.NoiseReduction = 0.4;
        vm.NoiseReduction = 0.8;
        vm.EndEdit();
        Assert.Equal(new[] { "Noise reduction" }, vm.UndoHistory);
        Assert.True(vm.HasChanges);
        vm.UndoCommand.Execute(null);
        Assert.Equal(0.25, vm.NoiseReduction);
        Assert.False(vm.HasChanges);
        vm.RedoCommand.Execute(null);
        Assert.Equal(0.8, vm.GetCurrentParameters().NoiseReduction);
        vm.ResetAllCommand.Execute(null);
        Assert.Equal(0, vm.NoiseReduction);
        vm.UndoCommand.Execute(null);
        Assert.Equal(0.8, vm.NoiseReduction);
        Assert.True(await vm.SaveAsync((_, parameters) =>
        {
            Assert.Equal(0.8, parameters.NoiseReduction);
            return Task.FromResult((10u, 20u));
        }));
        Assert.Equal(0, vm.NoiseReduction);
        Assert.Empty(vm.UndoHistory);
        Assert.False(ImageEditService.ReadEditParameters(_entry.FilePath).HasAdjustments);
    }

    [Fact]
    public async Task UnsharpMaskSettings_RoundTripHistoryResetAndSave()
    {
        var initial = new EditParameters
        {
            SharpeningMode = SharpeningMode.UnsharpMask,
            UnsharpMaskAmount = 1.25,
            UnsharpMaskRadius = 2.4,
            UnsharpMaskThreshold = 0.04
        };
        await new ImageEditService(_backups).SaveEditParametersAsync(_entry.FilePath, initial);
        var vm = await OpenAsync();
        Assert.Equal(SharpeningMode.UnsharpMask, vm.SharpeningMode);
        Assert.Equal(1.25, vm.UnsharpMaskAmount);
        Assert.Equal(2.4, vm.UnsharpMaskRadius);
        Assert.Equal(0.04, vm.UnsharpMaskThreshold);
        Assert.False(vm.HasChanges);

        vm.BeginEdit("Unsharp mask");
        vm.UnsharpMaskAmount = 1.5;
        vm.UnsharpMaskRadius = 3.2;
        vm.UnsharpMaskThreshold = 0.08;
        vm.EndEdit();
        Assert.Single(vm.UndoHistory);
        vm.UndoCommand.Execute(null);
        Assert.Equal(initial.UnsharpMaskAmount, vm.UnsharpMaskAmount);
        Assert.Equal(initial.UnsharpMaskRadius, vm.UnsharpMaskRadius);
        Assert.Equal(initial.UnsharpMaskThreshold, vm.UnsharpMaskThreshold);
        vm.RedoCommand.Execute(null);
        Assert.Equal(1.5, vm.UnsharpMaskAmount);
        Assert.Equal(3.2, vm.UnsharpMaskRadius);
        Assert.Equal(0.08, vm.UnsharpMaskThreshold);

        vm.ResetAllCommand.Execute(null);
        Assert.Equal(SharpeningMode.Legacy, vm.SharpeningMode);
        Assert.Equal(0, vm.UnsharpMaskAmount);
        Assert.Equal(1, vm.UnsharpMaskRadius);
        Assert.Equal(0.02, vm.UnsharpMaskThreshold);
        vm.UndoCommand.Execute(null);
        Assert.Equal(SharpeningMode.UnsharpMask, vm.SharpeningMode);
        Assert.Equal(1.5, vm.UnsharpMaskAmount);
        Assert.True(await vm.SaveAsync((_, parameters) =>
        {
            Assert.Equal(SharpeningMode.UnsharpMask, parameters.SharpeningMode);
            Assert.Equal(1.5, parameters.UnsharpMaskAmount);
            Assert.Equal(3.2, parameters.UnsharpMaskRadius);
            Assert.Equal(0.08, parameters.UnsharpMaskThreshold);
            return Task.FromResult((10u, 20u));
        }));
        Assert.False(ImageEditService.ReadEditParameters(_entry.FilePath).HasAdjustments);
    }

    [Fact]
    public async Task SuccessfulSave_ClearsHistory_AndBusyStateDisablesCommands()
    {
        var vm = await OpenAsync();
        vm.Exposure = 0.4;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var save = vm.SaveAsync(async (_, parameters) =>
        {
            Assert.Equal(0.4, parameters.Exposure);
            started.SetResult();
            await finish.Task;
            return (10u, 20u);
        });
        await started.Task;
        Assert.True(vm.IsSaving);
        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.False(vm.RedoCommand.CanExecute(null));
        vm.UndoSteps(1);
        Assert.Equal(0.4, vm.Exposure);
        finish.SetResult();
        Assert.True(await save);
        Assert.False(vm.IsSaving);
        Assert.Empty(vm.UndoHistory);
        Assert.Empty(vm.RedoHistory);
        Assert.False(vm.HasChanges);
        vm.Contrast = 0.1;
        vm.UndoCommand.Execute(null);
        Assert.False(vm.GetCurrentParameters().HasAdjustments);
    }

    [Fact]
    public async Task FailedSave_PreservesUndoHistory()
    {
        var vm = await OpenAsync();
        vm.Brightness = 0.3;
        await Assert.ThrowsAsync<IOException>(() =>
            vm.SaveAsync((_, _) => throw new IOException("Renderer failed")));
        Assert.False(vm.IsSaving);
        Assert.True(vm.CanUndo);
        vm.UndoCommand.Execute(null);
        Assert.Equal(0, vm.Brightness);
    }

    [Fact]
    public async Task RevertToOriginal_ClearsHistory()
    {
        await _backups.BackupOriginalAsync(_entry.FilePath);
        var vm = await OpenAsync();
        vm.Exposure = 0.4;
        vm.UndoCommand.Execute(null);
        Assert.True(vm.CanRedo);
        await vm.RevertToOriginalCommand.ExecuteAsync(null);
        Assert.Empty(vm.UndoHistory);
        Assert.Empty(vm.RedoHistory);
        Assert.False(vm.HasChanges);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
