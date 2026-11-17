// Copyright (c) Duncan Lawler. All Rights Reserved.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PhotoLibrarian.Controls;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.ViewModels;
using PhotoLibrarian.Views;
using Windows.Graphics;

namespace PhotoLibrarian.AccessibilityTests;

public partial class App
{
    private async Task RunViewerFocusTestsAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-viewer-focus-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var viewModel = new ImageViewerViewModel();
        var viewer = new ImageViewerOverlay(viewModel) { Visibility = Visibility.Collapsed };
        try
        {
            var photos = new List<ImageEntry>();
            for (var index = 0; index < 2; index++)
            {
                var path = Path.Combine(directory, $"Focus-{index}.png");
                await WriteSyntheticPhotoAsync(path);
                photos.Add(new ImageEntry
                {
                    Id = index + 1,
                    FilePath = path,
                    FileName = Path.GetFileName(path),
                    MediaType = MediaType.Image
                });
            }

            var grid = new VirtualizingPhotoGrid { ItemSize = 120 };
            var group = new PhotoGroup { Header = "Focus fixtures" };
            foreach (var photo in photos)
                group.Items.Add(new ImageThumbnailViewModel(photo) { IsLoading = false });
            grid.SetGroups([group]);
            grid.ItemDoubleClicked += (_, photo) => viewModel.OpenImage(photo.Entry, photos);
            // Match the window's visibility/focus handoff without initializing a user's library.
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(ImageViewerViewModel.IsOpen)) return;
                viewer.Visibility = viewModel.IsOpen ? Visibility.Visible : Visibility.Collapsed;
                if (viewModel.IsOpen) viewer.FocusViewer();
            };
            var outside = new TextBox { PlaceholderText = "Outside viewer" };
            AutomationProperties.SetAutomationId(outside, "ViewerFocusOutsideInput");
            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition());
            layout.Children.Add(outside);
            layout.Children.Add(grid);
            layout.Children.Add(viewer);
            Grid.SetRow(grid, 1);
            Grid.SetRow(viewer, 1);
            layout.AddHandler(UIElement.PointerPressedEvent,
                new PointerEventHandler((_, _) => viewer.StopFocusGuard()), true);
            _window = new Window { Title = "Viewer keyboard focus regression", Content = layout };
            _window.AppWindow.Resize(new SizeInt32(1000, 800));
            _window.Activate();
            await Task.Delay(500);

            await RunViewerUiCommandAsync("click", photos[0].FileName, "--double");
            await UntilAsync(() => viewModel.CurrentImage is not null && viewer.HasFocusWithin());
            Check(ReferenceEquals(FocusManager.GetFocusedElement(layout.XamlRoot), viewer),
                "Double-clicking a grid photo focuses the viewer surface");
            await PressViewerKeysAsync(0x27); // Right
            Check(ReferenceEquals(viewModel.CurrentEntry, photos[1]),
                "Right navigates immediately after opening without clicking a button");
            await PressViewerKeysAsync(0x25); // Left
            Check(ReferenceEquals(viewModel.CurrentEntry, photos[0]),
                "Left navigates immediately after opening");
            await PressViewerKeysAsync(0x1B); // Escape
            Check(!viewModel.IsOpen, "Escape closes the viewer immediately after opening");

            await RunViewerUiCommandAsync("click", photos[0].FileName, "--double");
            await UntilAsync(() => viewModel.CurrentImage is not null && viewer.HasFocusWithin());
            viewer.StopFocusGuard();
            outside.Focus(FocusState.Keyboard);
            await RunViewerUiCommandAsync("click", "ViewerPhotoSurface");
            Check(ReferenceEquals(FocusManager.GetFocusedElement(layout.XamlRoot), viewer),
                "Clicking the displayed image restores viewer focus from an input");
            await PressViewerKeysAsync(0x27);
            Check(ReferenceEquals(viewModel.CurrentEntry, photos[1]),
                "Right navigates after clicking the displayed image");
            await UntilAsync(() => viewModel.CurrentImage is not null);
            outside.Focus(FocusState.Keyboard);
            await RunViewerUiCommandAsync("click", photos[1].FileName);
            Check(ReferenceEquals(FocusManager.GetFocusedElement(layout.XamlRoot), viewer),
                "Clicking the image header restores viewer focus from an input");
            await PressViewerKeysAsync(0x25);
            Check(ReferenceEquals(viewModel.CurrentEntry, photos[0]),
                "Left navigates after clicking the image header");
            await RunViewerUiCommandAsync("screenshot", "--output",
                Path.Combine(AppContext.BaseDirectory, "viewer-focus.png"));
            await PressViewerKeysAsync(0x1B);
            Check(!viewModel.IsOpen, "Escape closes after clicking the image/header");
        }
        finally
        {
            viewer.StopFocusGuard();
            viewModel.CloseCommand.Execute(null);
            await Task.Delay(200);
            Directory.Delete(directory, recursive: true);
        }
    }
}
