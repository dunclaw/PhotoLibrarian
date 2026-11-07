// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.ViewModels;
using System.ComponentModel;
using Windows.UI.ViewManagement;

namespace PhotoLibrarian.Views;

public sealed partial class SlideshowWindow : Window
{
    private readonly DispatcherQueueTimer _timer;
    private readonly UISettings _uiSettings = new();
    private bool _active;
    private bool _closed;
    private bool _isTransitioning;
    private double _panDirection = 1;
    private Storyboard? _transition;
    private Storyboard? _motion;
    private bool _motionPaused;
    private bool _selectingTheme;

    public SlideshowViewModel ViewModel { get; }

    public SlideshowWindow(
        SlideshowSequence sequence,
        double intervalSeconds,
        SlideshowTheme theme = SlideshowTheme.Contemporary)
    {
        ViewModel = new SlideshowViewModel(sequence, intervalSeconds, theme);
        InitializeComponent();
        UpdateThemeControls();
        _timer = DispatcherQueue.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += OnTimerTick;
        _uiSettings.AnimationsEnabledChanged += OnAnimationsEnabledChanged;
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
        StartThemeAnimation();
        ResetTimer();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SlideshowViewModel.Theme))
            UpdateThemeControls();
        else if (e.PropertyName == nameof(SlideshowViewModel.CurrentImage))
            UpdateDisplayedPhoto();

        if (e.PropertyName == nameof(SlideshowViewModel.IsLoading))
        {
            StartThemeAnimation();
        }
        else if (e.PropertyName == nameof(SlideshowViewModel.IsPaused))
        {
            StartThemeAnimation();
        }
        else if (e.PropertyName == nameof(SlideshowViewModel.IntervalSeconds) && !_isTransitioning)
        {
            StartThemeAnimation();
        }

        if (e.PropertyName is nameof(SlideshowViewModel.IsLoading) or
            nameof(SlideshowViewModel.IsPaused) or nameof(SlideshowViewModel.IntervalSeconds))
            ResetTimer();
    }

    private void UpdateDisplayedPhoto()
    {
        var image = ViewModel.CurrentImage;
        var previousScaleX = CurrentPhotoScale.ScaleX;
        var previousScaleY = CurrentPhotoScale.ScaleY;
        var previousTranslationX = CurrentPhotoTranslation.X;
        var previousTranslationY = CurrentPhotoTranslation.Y;
        StopPhotoAnimations();
        if (image is null)
        {
            PreviousPhotoImage.Source = null;
            CurrentPhotoImage.Source = null;
            return;
        }

        var previous = CurrentPhotoImage.Source;
        CurrentPhotoImage.Source = image;
        PhotoViewport.UpdateLayout();
        PreviousPhotoScale.ScaleX = previousScaleX;
        PreviousPhotoScale.ScaleY = previousScaleY;
        PreviousPhotoTranslation.X = previousTranslationX;
        PreviousPhotoTranslation.Y = previousTranslationY;
        if (previous is null || ViewModel.Theme == SlideshowTheme.PanAndZoom ||
            !_uiSettings.AnimationsEnabled)
        {
            PreviousPhotoImage.Source = null;
            StartThemeAnimation();
            return;
        }

        PreviousPhotoImage.Source = previous;
        StartCrossfade();
    }

    private void StartCrossfade()
    {
        CurrentPhotoLayer.Opacity = 0;
        _isTransitioning = true;
        _transition = new Storyboard();
        AddAnimation(_transition, PreviousPhotoLayer, "Opacity", 1, 0, 0.35);
        AddAnimation(_transition, CurrentPhotoLayer, "Opacity", 0, 1, 0.35);
        _transition.Completed += OnTransitionCompleted;
        _transition.Begin();
    }

    private void OnTransitionCompleted(object? sender, object args)
    {
        if (_closed || !ReferenceEquals(sender, _transition)) return;
        StopTransition();
        StartThemeAnimation();
    }

    private void StopTransition()
    {
        if (_transition is not null)
        {
            _transition.Completed -= OnTransitionCompleted;
            _transition.Stop();
            _transition = null;
        }
        _isTransitioning = false;
        PreviousPhotoLayer.Opacity = 1;
        CurrentPhotoLayer.Opacity = 1;
        PreviousPhotoImage.Source = null;
    }

    private void StartThemeAnimation()
    {
        if (_closed) return;
        if (!_active || ViewModel.IsPaused || ViewModel.IsLoading)
        {
            if (_motion is not null && !_motionPaused)
            {
                _motion.Pause();
                _motionPaused = true;
            }
            return;
        }
        if (_isTransitioning || CurrentPhotoImage.Source is null) return;
        if (_motion is not null)
        {
            if (_motionPaused)
            {
                _motion.Resume();
                _motionPaused = false;
            }
            return;
        }

        if (!_uiSettings.AnimationsEnabled ||
            ViewModel.Theme is not (SlideshowTheme.PanAndZoom or SlideshowTheme.Contemporary) ||
            PhotoViewport.ActualWidth <= 0 || PhotoViewport.ActualHeight <= 0) return;

        // XAML owns animation lifetime; layout offsets remain untouched.
        _motion = new Storyboard();
        var targetScale = ViewModel.Theme == SlideshowTheme.PanAndZoom ? 1.12 : 1.04;
        AddAnimation(_motion, CurrentPhotoScale, "ScaleX", CurrentPhotoScale.ScaleX,
            targetScale, ViewModel.IntervalSeconds);
        AddAnimation(_motion, CurrentPhotoScale, "ScaleY", CurrentPhotoScale.ScaleY,
            targetScale, ViewModel.IntervalSeconds);
        if (ViewModel.Theme == SlideshowTheme.PanAndZoom)
        {
            var direction = _panDirection;
            _panDirection = -_panDirection;
            AddAnimation(_motion, CurrentPhotoTranslation, "X", CurrentPhotoTranslation.X,
                direction * 24, ViewModel.IntervalSeconds);
            AddAnimation(_motion, CurrentPhotoTranslation, "Y", CurrentPhotoTranslation.Y,
                direction * -12, ViewModel.IntervalSeconds);
        }
        _motionPaused = false;
        _motion.Begin();
    }

    private static void AddAnimation(Storyboard storyboard, DependencyObject target,
        string property, double from, double to, double seconds)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromSeconds(seconds))
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    private void StopPhotoAnimations()
    {
        StopTransition();
        _motion?.Stop();
        _motion = null;
        _motionPaused = false;
        CurrentPhotoScale.ScaleX = 1;
        CurrentPhotoScale.ScaleY = 1;
        CurrentPhotoTranslation.X = 0;
        CurrentPhotoTranslation.Y = 0;
    }

    private void OnPhotoViewportSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (_closed) return;
        PhotoViewport.Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, args.NewSize.Width, args.NewSize.Height)
        };
        StopPhotoAnimations();
        StartThemeAnimation();
    }

    private void OnAnimationsEnabledChanged(UISettings sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            StopPhotoAnimations();
            StartThemeAnimation();
        });
    }

    private void UpdateThemeControls()
    {
        _selectingTheme = true;
        try
        {
            ThemePicker.SelectedItem = ThemePicker.Items.OfType<ComboBoxItem>().Single(
                item => (string)item.Tag == ViewModel.Theme.ToString());
        }
        finally
        {
            _selectingTheme = false;
        }
        var bars = ViewModel.Theme == SlideshowTheme.Cinematic ? Visibility.Visible : Visibility.Collapsed;
        CinematicTopBar.Visibility = bars;
        CinematicBottomBar.Visibility = bars;
    }

    private async void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_selectingTheme || ThemePicker.SelectedItem is not ComboBoxItem item) return;
        await ViewModel.ChangeThemeAsync(Enum.Parse<SlideshowTheme>((string)item.Tag));
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
        _uiSettings.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        SlideshowRoot.Loaded -= OnLoaded;
        Activated -= OnActivated;
        ViewModel.Close();
        StopPhotoAnimations();
        CurrentPhotoImage.Source = null;
    }

    private void OnExitInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (sender.Key == Windows.System.VirtualKey.Escape && ThemePicker.IsDropDownOpen) return;
        args.Handled = true;
        Close();
    }

    private async void OnNextInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsPhotoSettingFocused()) return;
        args.Handled = true;
        await ViewModel.NextCommand.ExecuteAsync(null);
    }

    private async void OnPreviousInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsPhotoSettingFocused()) return;
        args.Handled = true;
        await ViewModel.PreviousCommand.ExecuteAsync(null);
    }

    private void OnExitClick(object sender, RoutedEventArgs args) => Close();

    private bool IsPhotoSettingFocused()
    {
        if (ThemePicker.IsDropDownOpen) return true;
        var element = FocusManager.GetFocusedElement(SlideshowRoot.XamlRoot) as DependencyObject;
        while (element is not null)
        {
            if (element is ComboBox or NumberBox) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }
}
