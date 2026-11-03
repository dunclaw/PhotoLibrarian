// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Dispatching;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.ViewModels;
using System.ComponentModel;
using System.Numerics;
using Windows.UI.ViewManagement;

namespace PhotoLibrarian.Views;

public sealed partial class SlideshowWindow : Window
{
    private readonly DispatcherQueueTimer _timer;
    private readonly UISettings _uiSettings = new();
    private bool _active;
    private bool _closed;
    private bool _isTransitioning;
    private int _transitionVersion;
    private float _panDirection = 1f;

    public SlideshowViewModel ViewModel { get; }

    public SlideshowWindow(
        SlideshowSequence sequence,
        double intervalSeconds,
        SlideshowTheme theme = SlideshowTheme.Contemporary)
    {
        ViewModel = new SlideshowViewModel(sequence, intervalSeconds, theme);
        InitializeComponent();
        var isCinematic = theme == SlideshowTheme.Cinematic;
        CinematicTopBar.Visibility = isCinematic ? Visibility.Visible : Visibility.Collapsed;
        CinematicBottomBar.Visibility = isCinematic ? Visibility.Visible : Visibility.Collapsed;
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
        if (_active && !ViewModel.IsPaused)
            StartThemeAnimation();
        else
            StopThemeAnimation(ElementCompositionPreview.GetElementVisual(CurrentPhotoImage));
        ResetTimer();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SlideshowViewModel.CurrentImage))
            UpdateDisplayedPhoto();

        if (e.PropertyName == nameof(SlideshowViewModel.IsLoading))
        {
            if (ViewModel.IsLoading)
                StopPhotoAnimations();
            else if (!_isTransitioning)
                StartThemeAnimation();
        }
        else if (e.PropertyName == nameof(SlideshowViewModel.IsPaused))
        {
            if (ViewModel.IsPaused || !_active)
                StopThemeAnimation(ElementCompositionPreview.GetElementVisual(CurrentPhotoImage));
            else
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
        if (image is null)
        {
            PreviousPhotoImage.Source = null;
            CurrentPhotoImage.Source = null;
            return;
        }

        var previous = CurrentPhotoImage.Source;
        CurrentPhotoImage.Source = image;
        if (previous is null || ViewModel.Theme == SlideshowTheme.PanAndZoom ||
            !_uiSettings.AnimationsEnabled)
        {
            PreviousPhotoImage.Source = null;
            CurrentPhotoImage.Opacity = 1;
            StartThemeAnimation();
            return;
        }

        PreviousPhotoImage.Source = previous;
        StartCrossfade();
    }

    private void StartCrossfade()
    {
        var compositor = ElementCompositionPreview.GetElementVisual(CurrentPhotoImage).Compositor;
        var previousVisual = ElementCompositionPreview.GetElementVisual(PreviousPhotoImage);
        var currentVisual = ElementCompositionPreview.GetElementVisual(CurrentPhotoImage);
        previousVisual.StopAnimation("Opacity");
        currentVisual.StopAnimation("Opacity");
        previousVisual.Opacity = 1;
        currentVisual.Opacity = 0;
        _isTransitioning = true;
        var version = ++_transitionVersion;
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed || version != _transitionVersion) return;
            PreviousPhotoImage.Source = null;
            previousVisual.Opacity = 1;
            currentVisual.Opacity = 1;
            _isTransitioning = false;
            StartThemeAnimation();
        });

        var fadeOut = compositor.CreateScalarKeyFrameAnimation();
        fadeOut.Duration = TimeSpan.FromMilliseconds(350);
        fadeOut.InsertKeyFrame(1f, 0f);
        var fadeIn = compositor.CreateScalarKeyFrameAnimation();
        fadeIn.Duration = TimeSpan.FromMilliseconds(350);
        fadeIn.InsertKeyFrame(1f, 1f);
        previousVisual.StartAnimation("Opacity", fadeOut);
        currentVisual.StartAnimation("Opacity", fadeIn);
        batch.End();
    }

    private void StartThemeAnimation()
    {
        if (_closed || !_active || ViewModel.IsPaused || ViewModel.IsLoading ||
            _isTransitioning || CurrentPhotoImage.Source is null)
            return;

        var visual = ElementCompositionPreview.GetElementVisual(CurrentPhotoImage);
        StopThemeAnimation(visual);
        if (!_uiSettings.AnimationsEnabled) return;

        visual.CenterPoint = new Vector3(
            (float)(CurrentPhotoImage.ActualWidth / 2),
            (float)(CurrentPhotoImage.ActualHeight / 2),
            0);

        var targetScale = ViewModel.Theme switch
        {
            SlideshowTheme.PanAndZoom => 1.12f,
            SlideshowTheme.Contemporary => 1.04f,
            SlideshowTheme.Cinematic => 1.06f,
            _ => 1f
        };
        if (targetScale > 1f)
        {
            var scale = visual.Compositor.CreateVector3KeyFrameAnimation();
            scale.Duration = TimeSpan.FromSeconds(ViewModel.IntervalSeconds);
            scale.InsertKeyFrame(0f, Vector3.One);
            scale.InsertKeyFrame(1f, new Vector3(targetScale, targetScale, 1f));
            visual.StartAnimation("Scale", scale);
        }

        if (ViewModel.Theme is SlideshowTheme.PanAndZoom or SlideshowTheme.Cinematic)
        {
            var direction = _panDirection;
            _panDirection = -_panDirection;
            var pan = visual.Compositor.CreateVector3KeyFrameAnimation();
            pan.Duration = TimeSpan.FromSeconds(ViewModel.IntervalSeconds);
            pan.InsertKeyFrame(0f, Vector3.Zero);
            pan.InsertKeyFrame(1f, new Vector3(direction * 24f, direction * -12f, 0));
            visual.StartAnimation("Offset", pan);
        }
    }

    private void StopPhotoAnimations()
    {
        ++_transitionVersion;
        _isTransitioning = false;
        var previousVisual = ElementCompositionPreview.GetElementVisual(PreviousPhotoImage);
        var currentVisual = ElementCompositionPreview.GetElementVisual(CurrentPhotoImage);
        previousVisual.StopAnimation("Opacity");
        StopThemeAnimation(currentVisual);
        currentVisual.StopAnimation("Opacity");
        previousVisual.Opacity = 1;
        currentVisual.Opacity = 1;
        PreviousPhotoImage.Source = null;
    }

    private void StopThemeAnimation(Microsoft.UI.Composition.Visual visual)
    {
        visual.StopAnimation("Scale");
        visual.StopAnimation("Offset");
        visual.Scale = Vector3.One;
        visual.Offset = Vector3.Zero;
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
        StopPhotoAnimations();
        CurrentPhotoImage.Source = null;
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
