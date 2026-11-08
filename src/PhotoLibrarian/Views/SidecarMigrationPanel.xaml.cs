// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using PhotoLibrarian.ViewModels;

namespace PhotoLibrarian.Views;

public sealed partial class SidecarMigrationPanel : UserControl
{
    public SidecarMigrationViewModel ViewModel { get; } = new();

    public SidecarMigrationPanel()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(ViewModel.Status)) return;
            var peer = FrameworkElementAutomationPeer.FromElement(StatusText) ??
                FrameworkElementAutomationPeer.CreatePeerForElement(StatusText);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        };
    }
}
