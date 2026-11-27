// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Services;
using PhotoLibrarian.ViewModels;

namespace PhotoLibrarian.Views;

public sealed partial class ImageGridView
{
    private async Task ShowAutoAdjustAsync(MainViewModel model, IReadOnlyCollection<ImageEntry> selection)
    {
        var title = PhotoContextMenuStrings.Get("BatchAutoAdjustAction");
        using var cancellation = new CancellationTokenSource();
        var label = new TextBlock
        {
            Text = PhotoContextMenuStrings.Format("BatchAutoAdjustProgress", 0, selection.Count),
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetAutomationId(label, "BatchAutoAdjustStatus");
        AutomationProperties.SetLiveSetting(label, AutomationLiveSetting.Polite);
        var bar = new ProgressBar { Maximum = selection.Count };
        AutomationProperties.SetAutomationId(bar, "BatchAutoAdjustProgress");
        AutomationProperties.SetName(bar, title);
        var dialog = new ContentDialog
        {
            Title = title,
            CloseButtonText = PhotoContextMenuStrings.Get("PhotoContextCancel"),
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Content = new StackPanel { Spacing = 16, Children = { label, bar } }
        };
        var finished = false;
        dialog.Closing += (_, args) =>
        {
            if (finished) return;
            args.Cancel = true;
            cancellation.Cancel();
            label.Text = PhotoContextMenuStrings.Get("BatchAutoAdjustCancelling");
        };
        var progress = new Progress<AutoAdjustProgress>(update =>
        {
            if (finished || cancellation.IsCancellationRequested) return;
            bar.Maximum = update.Total;
            bar.Value = update.Examined;
            bar.IsIndeterminate = update.IsSaving;
            label.Text = update.IsSaving
                ? PhotoContextMenuStrings.Get("BatchAutoAdjustSaving")
                : PhotoContextMenuStrings.Format("BatchAutoAdjustProgress", update.Examined, update.Total);
        });
        var showing = dialog.ShowAsync();
        AutoAdjustResult result;
        try
        {
            result = await model.AutoAdjustSelectionAsync(selection, progress, cancellation.Token);
        }
        finally
        {
            finished = true;
            dialog.Hide();
            await showing;
        }

        var summary = result.IsCancelled
            ? PhotoContextMenuStrings.Get("BatchAutoAdjustCancelled")
            : result.NotSaved > 0
                ? PhotoContextMenuStrings.Format("BatchAutoAdjustNotSaved", result.NotSaved)
                : PhotoContextMenuStrings.Format("BatchAutoAdjustSummary", result.Applied, result.Unchanged,
                    result.Issues.Count(issue => !issue.IsFailure), result.Issues.Count(issue => issue.IsFailure));
        model.StatusText = summary;
        if (result.SaveMessage is null && result.Issues.Count == 0) return;

        var details = new List<string> { summary };
        if (result.SaveMessage is not null) details.Add(result.SaveMessage);
        details.AddRange(result.Issues.Select(issue => PhotoContextMenuStrings.Format(
            issue.IsFailure ? "BatchAutoAdjustFailedItem" : "BatchAutoAdjustSkippedItem", issue.Path, issue.Reason)));
        await DialogService.Instance.ErrorAsync(this, title, new ScrollViewer
        {
            MaxHeight = 400,
            Content = new TextBlock
            {
                Text = string.Join(Environment.NewLine, details),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            }
        }, PhotoContextMenuStrings.Get("BatchAutoAdjustClose"));
    }
}
