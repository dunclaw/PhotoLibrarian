// Copyright (c) Duncan Lawler. All Rights Reserved.

using Microsoft.UI.Xaml.Controls;

namespace PhotoLibrarian.Services;

public enum FeedbackSeverity
{
    Informational,
    Warning,
    Error
}

/// <summary>
/// Presents non-blocking messages on a caller-owned InfoBar. Call on the UI thread;
/// separate bars keep unrelated messages and actions from replacing one another.
/// </summary>
public sealed class FeedbackService
{
    public static FeedbackService Instance { get; } = new();

    public void Show(InfoBar host, FeedbackSeverity severity, string message)
    {
        host.Severity = severity switch
        {
            FeedbackSeverity.Informational => InfoBarSeverity.Informational,
            FeedbackSeverity.Warning => InfoBarSeverity.Warning,
            FeedbackSeverity.Error => InfoBarSeverity.Error,
            _ => throw new ArgumentOutOfRangeException(nameof(severity))
        };
        host.Message = message;
        host.IsOpen = true;
    }

    public void Dismiss(InfoBar host) => host.IsOpen = false;
}
