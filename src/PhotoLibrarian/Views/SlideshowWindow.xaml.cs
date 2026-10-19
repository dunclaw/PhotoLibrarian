using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.ViewModels;
using System.ComponentModel;

namespace PhotoLibrarian.Views;

public sealed partial class SlideshowWindow : Window
{
    private readonly DispatcherQueueTimer _timer;
    private bool _active;
    private bool _closed;

    public SlideshowViewModel ViewModel { get; }

    public SlideshowWindow(SlideshowSequence sequence, double intervalSeconds)
    {
        ViewModel = new SlideshowViewModel(sequence, intervalSeconds);
        InitializeComponent();
        _timer = DispatcherQueue.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += OnTimerTick;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Activated += OnActivated;
        Closed += OnClosed;
        if (App.MainWindow is { } owner) AppWindow.Move(owner.AppWindow.Position);
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        SlideshowRoot.Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        PreviousButton.Focus(FocusState.Programmatic);
        await ViewModel.LoadAsync();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        _active = args.WindowActivationState != WindowActivationState.Deactivated;
        if (_active) PreviousButton.Focus(FocusState.Programmatic);
        ResetTimer();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SlideshowViewModel.IsLoading) or
            nameof(SlideshowViewModel.IsPaused) or nameof(SlideshowViewModel.IntervalSeconds))
            ResetTimer();
    }

    private void ResetTimer()
    {
        _timer.Stop();
        if (_closed || !_active || ViewModel.IsLoading || ViewModel.IsPaused) return;
        _timer.Interval = TimeSpan.FromSeconds(ViewModel.IntervalSeconds);
        _timer.Start();
    }

    private async void OnTimerTick(DispatcherQueueTimer sender, object args) =>
        await ViewModel.NextCommand.ExecuteAsync(null);

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        SlideshowRoot.Loaded -= OnLoaded;
        Activated -= OnActivated;
        ViewModel.Close();
    }

    private void OnExitInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Close();
    }

    private async void OnNextInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await ViewModel.NextCommand.ExecuteAsync(null);
    }

    private async void OnPreviousInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await ViewModel.PreviousCommand.ExecuteAsync(null);
    }

    private void OnExitClick(object sender, RoutedEventArgs args) => Close();
}
