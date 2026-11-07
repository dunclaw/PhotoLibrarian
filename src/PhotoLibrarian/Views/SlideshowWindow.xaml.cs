// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Dispatching;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    private CompositionScopedBatch? _transitionBatch;
    private AnimationController? _scaleController;
    private AnimationController? _panController;
    private bool _motionStarted;
    private bool _selectingTheme;

    public SlideshowViewModel ViewModel { get; }

    public SlideshowWindow(
        SlideshowSequence sequence,
        double intervalSeconds,
        SlideshowTheme theme = SlideshowTheme.Contemporary)
    {
        ViewModel = new SlideshowViewModel(sequence, intervalSeconds, theme);
        InitializeComponent();
        ElementCompositionPreview.SetIsTranslationEnabled(CurrentPhotoLayer, true);
        ElementCompositionPreview.SetIsTranslationEnabled(PreviousPhotoLayer, true);
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
        var compositor = ElementCompositionPreview.GetElementVisual(CurrentPhotoLayer).Compositor;
        var previousVisual = ElementCompositionPreview.GetElementVisual(PreviousPhotoLayer);
        var currentVisual = ElementCompositionPreview.GetElementVisual(CurrentPhotoLayer);
        previousVisual.StopAnimation("Opacity");
        currentVisual.StopAnimation("Opacity");
        previousVisual.Opacity = 1;
        currentVisual.Opacity = 0;
        _isTransitioning = true;
        var version = ++_transitionVersion;
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _transitionBatch = batch;
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed || version != _transitionVersion) return;
            PreviousPhotoImage.Source = null;
            previousVisual.Opacity = 1;
            currentVisual.Opacity = 1;
            _isTransitioning = false;
            _transitionBatch = null;
            batch.Dispose();
            StartThemeAnimation();
        });

        using var fadeOut = compositor.CreateScalarKeyFrameAnimation();
        fadeOut.Duration = TimeSpan.FromMilliseconds(350);
        fadeOut.InsertKeyFrame(1f, 0f);
        using var fadeIn = compositor.CreateScalarKeyFrameAnimation();
        fadeIn.Duration = TimeSpan.FromMilliseconds(350);
        fadeIn.InsertKeyFrame(1f, 1f);
        previousVisual.StartAnimation("Opacity", fadeOut);
        currentVisual.StartAnimation("Opacity", fadeIn);
        batch.End();
    }

    private void StartThemeAnimation()
    {
        if (_closed) return;
        if (!_active || ViewModel.IsPaused || ViewModel.IsLoading)
        {
            _scaleController?.Pause();
            _panController?.Pause();
            return;
        }
        if (_isTransitioning || CurrentPhotoImage.Source is null) return;
        if (_motionStarted)
        {
            _scaleController?.Resume();
            _panController?.Resume();
            return;
        }

        // Layout owns Offset. Animate Translation on a full-viewport layer instead.
        var visual = ElementCompositionPreview.GetElementVisual(CurrentPhotoLayer);
        if (!_uiSettings.AnimationsEnabled || ViewModel.Theme != SlideshowTheme.PanAndZoom ||
            PhotoViewport.ActualWidth <= 0 || PhotoViewport.ActualHeight <= 0) return;
        _motionStarted = true;

        visual.CenterPoint = new Vector3(
            (float)(PhotoViewport.ActualWidth / 2),
            (float)(PhotoViewport.ActualHeight / 2),
            0);

        using var scale = visual.Compositor.CreateVector3KeyFrameAnimation();
        scale.Duration = TimeSpan.FromSeconds(ViewModel.IntervalSeconds);
        scale.InsertKeyFrame(0f, Vector3.One);
        scale.InsertKeyFrame(1f, new Vector3(1.12f, 1.12f, 1f));
        visual.StartAnimation("Scale", scale);
        _scaleController = visual.TryGetAnimationController("Scale");

        var direction = _panDirection;
        _panDirection = -_panDirection;
        using var pan = visual.Compositor.CreateVector3KeyFrameAnimation();
        pan.Duration = TimeSpan.FromSeconds(ViewModel.IntervalSeconds);
        pan.InsertKeyFrame(0f, Vector3.Zero);
        pan.InsertKeyFrame(1f, new Vector3(direction * 24f, direction * -12f, 0));
        visual.StartAnimation("Translation", pan);
        _panController = visual.TryGetAnimationController("Translation");
    }

    private void StopPhotoAnimations()
    {
        ++_transitionVersion;
        _isTransitioning = false;
        _transitionBatch?.Dispose();
        _transitionBatch = null;
        var previousVisual = ElementCompositionPreview.GetElementVisual(PreviousPhotoLayer);
        var currentVisual = ElementCompositionPreview.GetElementVisual(CurrentPhotoLayer);
        previousVisual.StopAnimation("Opacity");
        StopThemeAnimation(currentVisual);
        currentVisual.StopAnimation("Opacity");
        previousVisual.Opacity = 1;
        currentVisual.Opacity = 1;
        PreviousPhotoImage.Source = null;
    }

    private void StopThemeAnimation(Visual visual)
    {
        _scaleController?.Dispose();
        _panController?.Dispose();
        _scaleController = null;
        _panController = null;
        _motionStarted = false;
        visual.StopAnimation("Scale");
        visual.StopAnimation("Translation");
        visual.Scale = Vector3.One;
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
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
