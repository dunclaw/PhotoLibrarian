// Copyright (c) Duncan Lawler. All Rights Reserved.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoLibrarian.Services;

namespace PhotoLibrarian.AccessibilityTests;

public partial class App
{
    private async Task RunFeedbackServiceTestsAsync()
    {
        var action = new Button { Content = "Import" };
        var device = new InfoBar { Title = "Device", IsClosable = false, ActionButton = action };
        var error = new InfoBar();
        var status = new InfoBar();
        var layout = new StackPanel();
        layout.Children.Add(device);
        layout.Children.Add(error);
        layout.Children.Add(status);
        _window = new Window { Title = "Synthetic feedback regression", Content = layout };
        _window.Activate();
        await UntilAsync(() => device.IsLoaded);

        var service = FeedbackService.Instance;
        service.Show(device, FeedbackSeverity.Informational, "Device detected");
        service.Show(error, FeedbackSeverity.Error, "Scan failed");
        service.Show(status, FeedbackSeverity.Warning, "Source preserved");
        Check(device.IsOpen && device.Severity == InfoBarSeverity.Informational &&
              device.Message == "Device detected" && ReferenceEquals(device.ActionButton, action) &&
              !device.IsClosable,
            "Informational feedback preserves the actionable device prompt");
        Check(error.IsOpen && error.Severity == InfoBarSeverity.Error && error.Message == "Scan failed" &&
              status.IsOpen && status.Severity == InfoBarSeverity.Warning && status.Message == "Source preserved",
            "Separate feedback slots preserve error and warning messages");
        service.Dismiss(device);
        Check(!device.IsOpen && error.IsOpen && status.IsOpen,
            "Dismissing an actionable prompt does not dismiss unrelated feedback");
        service.Show(device, FeedbackSeverity.Informational, "Another device");
        Check(device.IsOpen && device.Message == "Another device" && ReferenceEquals(device.ActionButton, action),
            "A dismissed slot can be reused without losing its action");
    }
}
