// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using PhotoLibrarian.Core.Services;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace PhotoLibrarian.Views;

internal sealed class RecoveryWindow : Window
{
    private static readonly ResourceLoader Resources = new(
        Path.Combine(AppContext.BaseDirectory, "PhotoLibrarian.pri"), "Resources");
    private readonly Grid _root = new();
    private readonly TextBlock _status = new()
    {
        Margin = new Thickness(24),
        TextWrapping = TextWrapping.Wrap
    };
    private bool _allowClose;

    internal RecoveryWindow()
    {
        Title = Resources.GetString("RecoveryWindowTitle");
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(800 * scale), (int)(600 * scale)));
        AppWindow.Closing += (_, args) => args.Cancel = !_allowClose;
        _status.Text = Resources.GetString("RecoveryWindowMessage");
        AutomationProperties.SetAutomationId(_status, "RecoveryStatus");
        _root.Children.Add(_status);
        Content = _root;
    }

    internal async Task<bool> ResolveAsync(
        string historyRoot, IReadOnlyList<FileEditRecoveryCandidate> candidates)
    {
        if (_root.XamlRoot is null)
        {
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _root.Loaded += OnLoaded;
            await loaded.Task;
            void OnLoaded(object sender, RoutedEventArgs args)
            {
                _root.Loaded -= OnLoaded;
                loaded.TrySetResult();
            }
        }

        while (candidates.Count > 0)
        {
            var candidate = candidates.FirstOrDefault(item => FileEditRecovery.CanRestore(item, candidates)) ??
                candidates.FirstOrDefault(item => item.SourcePath is null ||
                    item.Inspection.Status is FileEditRecoveryStatus.Invalid or
                        FileEditRecoveryStatus.Unsupported or FileEditRecoveryStatus.Unavailable or FileEditRecoveryStatus.Busy) ??
                candidates[0];
            var ready = FileEditRecovery.CanRestore(candidate, candidates);
            var details = candidate.Inspection.Status == FileEditRecoveryStatus.Ready && !ready
                ? Resources.GetString("RecoveryAmbiguousDetails")
                : candidate.Inspection.Message ?? Resources.GetString("RecoveryReadyDetails");
            var content = new TextBlock
            {
                Text = Format("RecoveryDetails", candidate.SourcePath ?? Resources.GetString("RecoveryUnknownSource"),
                    candidate.TransactionDirectory, details),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            };
            AutomationProperties.SetAutomationId(content, "RecoveryDetails");
            var dialog = new ContentDialog
            {
                XamlRoot = _root.XamlRoot,
                Title = Resources.GetString(ready ? "RecoveryReadyTitle" : "RecoveryBlockedTitle"),
                Content = new ScrollViewer { Content = content, MaxHeight = 400 },
                PrimaryButtonText = Resources.GetString(ready ? "RecoveryRestoreBefore" : "RecoveryRetry"),
                SecondaryButtonText = ready ? Resources.GetString("RecoveryRestoreAfter") : string.Empty,
                CloseButtonText = Resources.GetString("RecoveryQuit"),
                DefaultButton = ContentDialogButton.None
            };
            AutomationProperties.SetAutomationId(dialog, "RecoveryDecisionDialog");
            var decision = await dialog.ShowAsync();
            if (decision == ContentDialogResult.None)
                return false;
            if (ready)
            {
                var choice = decision == ContentDialogResult.Primary
                    ? FileEditRecoveryChoice.Before : FileEditRecoveryChoice.After;
                _status.Text = Resources.GetString("RecoveryApplying");
                var result = await Task.Run(() => FileEditRecovery.RestoreAsync(
                    historyRoot, candidate.TransactionDirectory, candidate.SourcePath!, choice));
                _status.Text = Resources.GetString("RecoveryWindowMessage");
                var message = new ContentDialog
                {
                    XamlRoot = _root.XamlRoot,
                    Title = Resources.GetString(result.Status == FileEditRecoveryStatus.Applied
                        ? "RecoveryAppliedTitle" : "RecoveryFailedTitle"),
                    Content = new TextBlock
                    {
                        Text = result.Message ?? Resources.GetString("RecoveryRetryDetails"),
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true
                    },
                    CloseButtonText = Resources.GetString("RecoveryContinue")
                };
                AutomationProperties.SetAutomationId(message, "RecoveryResultDialog");
                await message.ShowAsync();
            }
            candidates = await Task.Run(() => FileEditRecovery.FindAsync(historyRoot));
        }
        return true;
    }

    internal void CloseAfterRecovery()
    {
        _allowClose = true;
        Close();
    }

    private static string Format(string key, params object?[] values) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, Resources.GetString(key), values);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
}
