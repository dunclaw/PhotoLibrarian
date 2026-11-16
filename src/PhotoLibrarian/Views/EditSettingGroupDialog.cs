// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;

namespace PhotoLibrarian.Views;

internal sealed record EditSettingGroupDialogResult(string? Name, EditSettingGroup Groups);

internal static class EditSettingGroupDialog
{
    private static readonly EditSettingGroup[] Groups =
    [
        EditSettingGroup.Tone,
        EditSettingGroup.Color,
        EditSettingGroup.Effect,
        EditSettingGroup.Detail,
        EditSettingGroup.ToneCurves,
        EditSettingGroup.ColorAdjustments,
        EditSettingGroup.Geometry,
        EditSettingGroup.Retouch,
        EditSettingGroup.RawDevelopment
    ];

    internal static EditSettingGroup AvailableForPhoto(string path) =>
        RawToneMappingService.IsRaw(path)
            ? EditSettingGroup.All
            : EditSettingGroup.All & ~EditSettingGroup.RawDevelopment;

    internal static async Task<EditSettingGroupDialogResult?> ShowAsync(
        XamlRoot root,
        ElementTheme theme,
        string title,
        EditSettingGroup availableGroups,
        bool includeName)
    {
        var content = new StackPanel { Spacing = 8 };
        TextBox? nameBox = null;
        if (includeName)
        {
            nameBox = new TextBox
            {
                Header = EditorEffectStrings.Get("EditPresetName"),
                PlaceholderText = EditorEffectStrings.Get("EditPresetNamePlaceholder")
            };
            AutomationProperties.SetAutomationId(nameBox, "EditPresetName");
            content.Children.Add(nameBox);
        }

        var checkBoxes = new List<(EditSettingGroup Group, CheckBox CheckBox)>();
        foreach (var group in Groups.Where(group => availableGroups.HasFlag(group)))
        {
            var checkBox = new CheckBox
            {
                Content = EditorEffectStrings.Get($"EditGroup{group}"),
                IsChecked = true
            };
            AutomationProperties.SetAutomationId(checkBox, $"EditPresetGroup{group}");
            checkBoxes.Add((group, checkBox));
            content.Children.Add(checkBox);
        }

        var validationMessage = new TextBlock { TextWrapping = TextWrapping.Wrap };
        content.Children.Add(validationMessage);
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = EditorEffectStrings.Get(includeName ? "EditPresetSave" : "EditPresetContinue"),
            CloseButtonText = EditorEffectStrings.Get("EditPresetCancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
            RequestedTheme = theme
        };
        dialog.PrimaryButtonClick += (_, eventArgs) =>
        {
            if (checkBoxes.Any(item => item.CheckBox.IsChecked == true) &&
                (!includeName || !string.IsNullOrWhiteSpace(nameBox?.Text)))
                return;
            validationMessage.Text = checkBoxes.All(item => item.CheckBox.IsChecked != true)
                ? EditorEffectStrings.Get("EditPresetGroupsRequired")
                : EditorEffectStrings.Get("EditPresetNameRequired");
            eventArgs.Cancel = true;
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;

        var selectedGroups = checkBoxes
            .Where(item => item.CheckBox.IsChecked == true)
            .Aggregate(EditSettingGroup.None, (selected, item) => selected | item.Group);
        var name = nameBox?.Text.Trim();
        return new(name, selectedGroups);
    }
}
