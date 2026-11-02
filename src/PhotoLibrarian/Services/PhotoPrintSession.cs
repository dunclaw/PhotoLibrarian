// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Printing;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Printing;
using WinRT.Interop;

namespace PhotoLibrarian.Services;

internal sealed class PhotoPrintSession : IDisposable
{
    private readonly Window _window;
    private readonly IReadOnlyList<ImageEntry> _photos;
    private readonly PhotoPrintLayout _layout;
    private readonly PhotoPrintPlacement _placement;
    private readonly PrintManager _printManager;
    private readonly PrintDocument _printDocument;
    private readonly SemaphoreSlim _renderLock = new(1, 1);
    private readonly CancellationTokenSource _cancellation = new();
    private PrintPageDescription _pageDescription;
    private PrintTask? _printTask;
    private int _pageCount;
    private bool _disposed;

    public event EventHandler<string>? Error;
    public event EventHandler<PrintTaskCompletion>? Completed;

    public PhotoPrintSession(
        Window window,
        IReadOnlyList<ImageEntry> photos,
        PhotoPrintLayout layout,
        PhotoPrintPlacement placement)
    {
        _window = window;
        _photos = photos;
        _layout = layout;
        _placement = placement;
        var windowHandle = WindowNative.GetWindowHandle(window);
        _printManager = PrintManagerInterop.GetForWindow(windowHandle);
        _printManager.PrintTaskRequested += OnPrintTaskRequested;

        _printDocument = new PrintDocument();
        _printDocument.Paginate += OnPaginate;
        _printDocument.GetPreviewPage += OnGetPreviewPage;
        _printDocument.AddPages += OnAddPages;
    }

    public async Task<bool> ShowPrintUiAsync()
    {
        var windowHandle = WindowNative.GetWindowHandle(_window);
        return await PrintManagerInterop.ShowPrintUIForWindowAsync(windowHandle);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cancellation.Cancel();
        _printManager.PrintTaskRequested -= OnPrintTaskRequested;
        _printDocument.Paginate -= OnPaginate;
        _printDocument.GetPreviewPage -= OnGetPreviewPage;
        _printDocument.AddPages -= OnAddPages;
        if (_printTask is not null)
            _printTask.Completed -= OnPrintTaskCompleted;
        _cancellation.Dispose();
    }

    private void OnPrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
    {
        _printTask = args.Request.CreatePrintTask(
            PhotoPrintStrings.Get("PrintTaskTitle"),
            request => request.SetSource(_printDocument.DocumentSource));
        _printTask.Completed += OnPrintTaskCompleted;
    }

    private void OnPrintTaskCompleted(PrintTask sender, PrintTaskCompletedEventArgs args)
    {
        if (args.Completion != PrintTaskCompletion.Submitted)
            _cancellation.Cancel();
        Completed?.Invoke(this, args.Completion);
    }

    private void OnPaginate(object sender, PaginateEventArgs args)
    {
        try
        {
            _pageDescription = args.PrintTaskOptions.GetPageDescription(1);
            var imageable = ToPhotoPrintRectangle(_pageDescription.ImageableRect);
            _pageCount = PhotoPrintLayoutService.GetPageCount(_photos.Count, _layout);
            _ = PhotoPrintLayoutService.CreatePageSlots(
                1,
                _photos.Count,
                _layout,
                _pageDescription.PageSize.Width,
                _pageDescription.PageSize.Height,
                imageable);
            _printDocument.SetPreviewPageCount(_pageCount, PreviewPageCountType.Final);
        }
        catch (Exception exception)
        {
            _cancellation.Cancel();
            Error?.Invoke(this, exception.Message);
            _printDocument.SetPreviewPageCount(0, PreviewPageCountType.Final);
        }
    }

    private async void OnGetPreviewPage(object sender, GetPreviewPageEventArgs args)
    {
        try
        {
            var page = await CreatePageAsync(args.PageNumber, _cancellation.Token);
            _printDocument.SetPreviewPage(args.PageNumber, page);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _cancellation.Cancel();
            Error?.Invoke(this, exception.Message);
        }
    }

    private async void OnAddPages(object sender, AddPagesEventArgs args)
    {
        try
        {
            for (var pageNumber = 1; pageNumber <= _pageCount; pageNumber++)
            {
                _cancellation.Token.ThrowIfCancellationRequested();
                var page = await CreatePageAsync(pageNumber, _cancellation.Token);
                _printDocument.AddPage(page);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _cancellation.Cancel();
            Error?.Invoke(this, exception.Message);
        }
        finally
        {
            _printDocument.AddPagesComplete();
        }
    }

    private async Task<Canvas> CreatePageAsync(int pageNumber, CancellationToken cancellationToken)
    {
        await _renderLock.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageSize = _pageDescription.PageSize;
            var imageable = ToPhotoPrintRectangle(_pageDescription.ImageableRect);
            var slots = PhotoPrintLayoutService.CreatePageSlots(
                pageNumber,
                _photos.Count,
                _layout,
                pageSize.Width,
                pageSize.Height,
                imageable);
            var page = new Canvas
            {
                Width = pageSize.Width,
                Height = pageSize.Height,
                Background = new SolidColorBrush(Microsoft.UI.Colors.White)
            };

            foreach (var slot in slots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var photo = _photos[slot.PhotoIndex];
                var maxDimension = _layout switch
                {
                    PhotoPrintLayout.ContactSheet => 640u,
                    PhotoPrintLayout.Wallet => 1200u,
                    _ => PhotoPrintImageDecoder.MaximumPhotoDimension
                };
                PhotoPrintImageData decoded;
                try
                {
                    decoded = await PhotoPrintImageDecoder.DecodeAsync(
                        photo.FilePath,
                        maxDimension,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        PhotoPrintStrings.Format("PrintPhotoLoadFailed", photo.FileName, exception.Message),
                        exception);
                }
                var frame = slot.ImageBounds;
                if (_layout is PhotoPrintLayout.FourBySix or PhotoPrintLayout.FiveBySeven or PhotoPrintLayout.EightByTen)
                {
                    frame = PhotoPrintLayoutService.CreatePhotoFrame(
                        _layout,
                        imageable,
                        sourceIsPortrait: decoded.Height > decoded.Width);
                }

                var bitmap = new WriteableBitmap(
                    checked((int)decoded.Width),
                    checked((int)decoded.Height));
                using (var pixelStream = bitmap.PixelBuffer.AsStream())
                    await pixelStream.WriteAsync(decoded.Pixels.AsMemory(), cancellationToken);
                bitmap.Invalidate();

                var image = new Image
                {
                    Source = bitmap,
                    Width = frame.Width,
                    Height = frame.Height,
                    Stretch = _placement == PhotoPrintPlacement.Fill
                        ? Stretch.UniformToFill
                        : Stretch.Uniform
                };
                Canvas.SetLeft(image, frame.X);
                Canvas.SetTop(image, frame.Y);
                page.Children.Add(image);

                if (slot.CaptionBounds is { } caption)
                {
                    var label = new TextBlock
                    {
                        Text = photo.FileName,
                        Width = caption.Width,
                        Height = caption.Height,
                        FontSize = 12,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Canvas.SetLeft(label, caption.X);
                    Canvas.SetTop(label, caption.Y);
                    page.Children.Add(label);
                }
            }

            page.Measure(new Windows.Foundation.Size(pageSize.Width, pageSize.Height));
            page.Arrange(new Windows.Foundation.Rect(0, 0, pageSize.Width, pageSize.Height));
            return page;
        }
        finally
        {
            _renderLock.Release();
        }
    }

    private static PhotoPrintRectangle ToPhotoPrintRectangle(Windows.Foundation.Rect bounds) =>
        new(bounds.X, bounds.Y, bounds.Width, bounds.Height);
}
