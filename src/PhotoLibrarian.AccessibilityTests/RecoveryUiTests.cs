// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Views;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhotoLibrarian.AccessibilityTests;

internal static class RecoveryUiTests
{
    internal static async Task RunAsync(List<string> passed)
    {
        var root = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-recovery-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await TestRestoreAsync(root, restoreAfter: false, passed);
            await TestRestoreAsync(root, restoreAfter: true, passed);
            await TestBlockedAsync(root, passed);
            await TestAmbiguousAsync(root, passed);
            await TestRetryAsync(root, passed);
            await TestNestedAsync(root, passed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task TestRestoreAsync(string root, bool restoreAfter, List<string> passed)
    {
        var folder = Path.Combine(root, restoreAfter ? "after" : "before");
        var history = Path.Combine(folder, "history");
        var transaction = Path.Combine(history, "session", "transaction");
        Directory.CreateDirectory(Path.Combine(transaction, "before"));
        Directory.CreateDirectory(Path.Combine(transaction, "checkpoints"));
        var source = Path.Combine(folder, "source.cr3");
        var sidecar = Path.ChangeExtension(source, ".xmp");
        byte[] raw = [1, 2, 3];
        var before = Encoding.UTF8.GetBytes(Packet("before"));
        var after = Encoding.UTF8.GetBytes(Packet("after"));
        await File.WriteAllBytesAsync(source, raw);
        await File.WriteAllBytesAsync(sidecar, restoreAfter ? before : after);
        var beforePath = Path.Combine(transaction, "before", "1");
        var afterPath = Path.Combine(transaction, "checkpoints", "1");
        await File.WriteAllBytesAsync(beforePath, before);
        await File.WriteAllBytesAsync(afterPath, after);
        var sourceVersion = new { Copy = (string?)null, Hash = Hash(raw) };
        var emptyVersion = new { Copy = (string?)null, Hash = (string?)null };
        var manifest = new
        {
            SchemaVersion = 1,
            SourcePath = source,
            Companions = Array.Empty<string>(),
            Undo = false,
            Changes = new object[]
            {
                new { Path = source, Before = sourceVersion, After = sourceVersion, RecoveryPath = (string?)null, ReplacementPath = (string?)null },
                new { Path = sidecar, Before = new { Copy = beforePath, Hash = Hash(before) },
                    After = new { Copy = afterPath, Hash = Hash(after) }, RecoveryPath = (string?)null, ReplacementPath = (string?)null },
                new { Path = source + ".xmp", Before = emptyVersion, After = emptyVersion, RecoveryPath = (string?)null, ReplacementPath = (string?)null }
            }
        };
        await File.WriteAllTextAsync(Path.Combine(transaction, "recovery.json"), JsonSerializer.Serialize(manifest));
        var candidates = await FileEditRecovery.FindAsync(history);
        Check(candidates.Count == 1 && candidates[0].Inspection.Status == FileEditRecoveryStatus.Ready,
            "The recovery fixture is ready");
        var window = new RecoveryWindow();
        window.Activate();
        try
        {
            var resolving = window.ResolveAsync(history, candidates);
            var dialog = await WaitForDialogAsync(window, "RecoveryDecisionDialog");
            Check(dialog.DefaultButton == ContentDialogButton.None, "Recovery has no destructive default");
            Check(FindDescendant<TextBlock>(dialog, item =>
                AutomationProperties.GetAutomationId(item) == "RecoveryDetails") is not null,
                "Recovery details are accessible");
            await InvokeButtonAsync(dialog, restoreAfter ? "Restore after edit" : "Restore before edit");
            var result = await WaitForDialogAsync(window, "RecoveryResultDialog");
            Check(result.Title?.ToString() == "Recovery applied", "Recovery reports its result");
            await InvokeButtonAsync(result, "Continue");
            Check(await resolving, "Successful recovery permits startup");
            Check((await File.ReadAllBytesAsync(sidecar)).SequenceEqual(restoreAfter ? after : before),
                "The chosen checkpoint is restored");
            Check((await File.ReadAllBytesAsync(source)).SequenceEqual(raw), "The RAW original is unchanged");
            Check(File.Exists(Path.Combine(transaction, "recovery.completed")), "Recovery completion is recorded");
            passed.Add(restoreAfter ? "Restore after checkpoint" : "Restore before checkpoint");
        }
        finally { window.CloseAfterRecovery(); }
    }

    private static async Task TestBlockedAsync(string root, List<string> passed)
    {
        var history = Path.Combine(root, "blocked");
        await File.WriteAllTextAsync(history, "not a directory");
        var candidates = await FileEditRecovery.FindAsync(history);
        var window = new RecoveryWindow();
        window.Activate();
        try
        {
            var resolving = window.ResolveAsync(history, candidates);
            var dialog = await WaitForDialogAsync(window, "RecoveryDecisionDialog");
            Check(dialog.Title?.ToString() == "Recovery needs attention", "Blocked recovery explains the problem");
            Check(string.IsNullOrEmpty(dialog.SecondaryButtonText), "Blocked recovery cannot restore a checkpoint");
            await InvokeButtonAsync(dialog, "Quit PhotoLibrarian");
            Check(!await resolving, "Quitting does not permit startup");
            Check(await File.ReadAllTextAsync(history) == "not a directory", "Blocked recovery leaves files unchanged");
            passed.Add("Blocked recovery prevents startup and preserves files");
        }
        finally { window.CloseAfterRecovery(); }
    }

    private static async Task TestAmbiguousAsync(string root, List<string> passed)
    {
        var source = Path.Combine(root, "same-source.cr3");
        var candidates = new[]
        {
            new FileEditRecoveryCandidate(Path.Combine(root, "first"), source,
                new FileEditRecoveryInspection(FileEditRecoveryStatus.Ready)),
            new FileEditRecoveryCandidate(Path.Combine(root, "second"), source,
                new FileEditRecoveryInspection(FileEditRecoveryStatus.Ready))
        };
        var window = new RecoveryWindow();
        window.Activate();
        try
        {
            var resolving = window.ResolveAsync(root, candidates);
            var dialog = await WaitForDialogAsync(window, "RecoveryDecisionDialog");
            Check(dialog.Title?.ToString() == "Recovery needs attention", "Ambiguous recovery is blocked");
            Check(string.IsNullOrEmpty(dialog.SecondaryButtonText), "Ambiguous recovery has no restore choice");
            await InvokeButtonAsync(dialog, "Quit PhotoLibrarian");
            Check(!await resolving, "Ambiguous recovery does not permit startup");
            passed.Add("Unrelated recoveries of one source require attention");
        }
        finally { window.CloseAfterRecovery(); }
    }

    private static async Task TestRetryAsync(string root, List<string> passed)
    {
        var history = Path.Combine(root, "retry");
        await File.WriteAllTextAsync(history, "not a directory");
        var window = new RecoveryWindow();
        window.Activate();
        try
        {
            var resolving = window.ResolveAsync(history, await FileEditRecovery.FindAsync(history));
            var dialog = await WaitForDialogAsync(window, "RecoveryDecisionDialog");
            File.Delete(history);
            await InvokeButtonAsync(dialog, "Check again");
            Check(await resolving, "Retry permits startup after the recovery obstruction is removed");
            passed.Add("Retry rechecks recovery before startup");
        }
        finally { window.CloseAfterRecovery(); }
    }

    private static async Task TestNestedAsync(string root, List<string> passed)
    {
        var source = Path.Combine(root, "nested-source.cr3");
        var parent = Path.Combine(root, "nested");
        var candidates = new[]
        {
            new FileEditRecoveryCandidate(parent, source,
                new FileEditRecoveryInspection(FileEditRecoveryStatus.Conflict, "A nested recovery must be addressed first.")),
            new FileEditRecoveryCandidate(Path.Combine(parent, "recovery-attempts", "attempt"), source,
                new FileEditRecoveryInspection(FileEditRecoveryStatus.Ready))
        };
        var window = new RecoveryWindow();
        window.Activate();
        try
        {
            var resolving = window.ResolveAsync(root, candidates);
            var dialog = await WaitForDialogAsync(window, "RecoveryDecisionDialog");
            Check(dialog.Title?.ToString() == "Recover an interrupted edit",
                "A nested recovery is offered before its parent");
            await InvokeButtonAsync(dialog, "Quit PhotoLibrarian");
            Check(!await resolving, "Quitting a nested recovery does not permit startup");
            passed.Add("Nested recovery is offered before its parent");
        }
        finally { window.CloseAfterRecovery(); }
    }

    private static async Task<ContentDialog> WaitForDialogAsync(Window window, string automationId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (window.Content.XamlRoot is { } root)
            {
                foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
                {
                    if (popup.Child is { } child &&
                        FindDescendant<ContentDialog>(child,
                            dialog => AutomationProperties.GetAutomationId(dialog) == automationId) is { } found)
                        return found;
                }
            }
            await Task.Delay(100);
        }
        throw new InvalidOperationException($"Dialog {automationId} was not shown.");
    }

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
    {
        if (root is T match && predicate(match)) return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var result = FindDescendant(VisualTreeHelper.GetChild(root, index), predicate);
            if (result is not null) return result;
        }
        return null;
    }

    private static async Task InvokeButtonAsync(ContentDialog dialog, string label)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var button = FindDescendant<Button>(dialog, item => item.Content?.ToString() == label);
            if (button is not null)
            {
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(button);
                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
                return;
            }
            await Task.Delay(100);
        }
        throw new InvalidOperationException($"Button '{label}' was not found.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Packet(string value) =>
        $"""<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"><rdf:Description rdf:about="" xmlns:fixture="https://photolibrarian.test/recovery/" fixture:value="{value}"/></rdf:RDF></x:xmpmeta>""";
}
