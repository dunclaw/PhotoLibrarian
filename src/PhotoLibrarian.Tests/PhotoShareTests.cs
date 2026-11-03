// Copyright (c) Duncan Lawler. All Rights Reserved.

using System.Runtime.InteropServices.WindowsRuntime;
using System.Xml.Linq;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class PhotoShareTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-ShareTest-{Guid.NewGuid():N}");
    private string Root => Path.Combine(_directory, "copies");
    private PhotoSharePreparationService Service => new(Root);

    public PhotoShareTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public async Task RejectsEmptyOrOversizedSelectionBeforeCreatingCopies(int count)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Service.PrepareAsync(Enumerable.Repeat("unused.jpg", count).ToArray(),
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Root));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(20L * 1024 * 1024)]
    public void PayloadSizeAllowsExactlyTwentyMiB(long bytes) =>
        PhotoSharePreparationService.ValidatePayloadSize(bytes);

    [Fact]
    public void PayloadSizeRejectsOneByteOverLimit() =>
        Assert.Throws<IOException>(() =>
            PhotoSharePreparationService.ValidatePayloadSize(20L * 1024 * 1024 + 1));

    [Fact]
    public async Task SelectionAllowsExactlyTwentyPhotos()
    {
        var source = await CreateImageAsync("twenty.png");
        using var prepared = await Service.PrepareAsync(
            Enumerable.Repeat(source, 20).ToArray(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(prepared.Result.ExportedPaths);
        Assert.Empty(prepared.Result.Failures);
    }

    [Fact]
    public async Task OversizedPayloadIsRemovedBeforeItCanBeHandedOff()
    {
        var source = await CreateImageAsync("oversized.png");
        var progress = new ResizeOutputProgress(Root);
        await Assert.ThrowsAsync<IOException>(() => Service.PrepareAsync(
            [source], progress, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetDirectories(Root));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task PreCancelledSelectionDoesNotCreateCopies()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service.PrepareAsync(["unused.jpg"], cancellationToken: cancellation.Token));
        Assert.False(Directory.Exists(Root));
    }

    [Fact]
    public async Task PreparesDistinctOrderedResizedJpegsWithoutChangingOriginalsOrSidecars()
    {
        var first = await CreateImageAsync("first.png", 2000, 1000);
        var second = await CreateImageAsync("second.png", 40, 20);
        var selection = new[] { first, second, first.ToUpperInvariant() };
        var originalSelection = selection.ToArray();
        var original = await File.ReadAllBytesAsync(first, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(first + ".xmp", "untouched sidecar", TestContext.Current.CancellationToken);
        string folder;
        using (var prepared = await Service.PrepareAsync(selection, cancellationToken: TestContext.Current.CancellationToken))
        {
            folder = prepared.Folder;
            Assert.Empty(prepared.Result.Failures);
            Assert.False(prepared.Result.IsCancelled);
            Assert.Equal(2, prepared.Result.ExportedPaths.Count);
            Assert.StartsWith("first", Path.GetFileName(prepared.Result.ExportedPaths[0]));
            Assert.StartsWith("second", Path.GetFileName(prepared.Result.ExportedPaths[1]));
            await AssertJpegAsync(prepared.Result.ExportedPaths[0], 1600, 800);
            await AssertJpegAsync(prepared.Result.ExportedPaths[1], 40, 20);
            Assert.Equal(originalSelection, selection);
            Assert.Equal(original, await File.ReadAllBytesAsync(first, TestContext.Current.CancellationToken));
            Assert.Equal("untouched sidecar", await File.ReadAllTextAsync(first + ".xmp", TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(folder, "*.xmp"));
        }
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task MultiSelectIncludesOnlySuccessfulCopiesAndReportsEachError()
    {
        var source = await CreateImageAsync("good.png");
        var corrupt = Path.Combine(_directory, "bad.png");
        await File.WriteAllTextAsync(corrupt, "not an image", TestContext.Current.CancellationToken);
        using var prepared = await Service.PrepareAsync(
            [source, corrupt, Path.Combine(_directory, "missing.jpg")],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(prepared.Result.ExportedPaths);
        Assert.Equal(2, prepared.Result.Failures.Count);
        Assert.All(prepared.Result.Failures, failure => Assert.NotEmpty(failure.Message));
    }

    [Fact]
    public async Task UnreadableSourceIsReportedWithoutModifyingIt()
    {
        var source = await CreateImageAsync("locked.png");
        using var locked = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None);
        using var prepared = await Service.PrepareAsync([source], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(prepared.Result.ExportedPaths);
        Assert.Equal(source, Assert.Single(prepared.Result.Failures).SourcePath);
    }

    [Fact]
    public async Task CancelledBatchIsRemovedRatherThanHandingOffPartialCopies()
    {
        var first = await CreateImageAsync("one.png");
        var second = await CreateImageAsync("two.png");
        using var cancellation = new CancellationTokenSource();
        var progress = new CancelProgress(cancellation);
        string folder;
        using (var prepared = await Service.PrepareAsync([first, second], progress, cancellation.Token))
        {
            folder = prepared.Folder;
            Assert.True(prepared.Result.IsCancelled);
            Assert.Single(prepared.Result.ExportedPaths);
        }
        Assert.False(Directory.Exists(folder));
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public async Task HandedOffCopiesSurviveDisposalAndOnlyExpiredUnlockedBatchesAreCollected()
    {
        var source = await CreateImageAsync("lifetime.png");
        var prepared = await Service.PrepareAsync([source], cancellationToken: TestContext.Current.CancellationToken);
        var folder = prepared.Folder;
        prepared.RetainForHandoff();
        Service.CleanupExpired();
        Assert.True(File.Exists(Assert.Single(prepared.Result.ExportedPaths)));
        prepared.Dispose();
        Service.CleanupExpired();
        Assert.True(Directory.Exists(folder));
        File.SetLastWriteTimeUtc(Path.Combine(folder, ".lease"), DateTime.UtcNow.AddDays(-8));
        Service.CleanupExpired();
        Assert.False(Directory.Exists(folder));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void CleanupDoesNotDeleteUnknownOrNestedContent()
    {
        Directory.CreateDirectory(Root);
        var unrelated = Path.Combine(Root, "share-unrelated");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "important.jpg"), "keep");
        Directory.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddDays(-8));
        var nested = Path.Combine(Root, $"share-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(nested, "nested"));
        File.WriteAllText(Path.Combine(nested, ".lease"), "");
        File.SetLastWriteTimeUtc(Path.Combine(nested, ".lease"), DateTime.UtcNow.AddDays(-8));
        Service.CleanupExpired();
        Assert.True(File.Exists(Path.Combine(unrelated, "important.jpg")));
        Assert.True(Directory.Exists(Path.Combine(nested, "nested")));
    }

    [Fact]
    public async Task LockedHandoffCopyKeepsExpiryMarkerAndIsCollectedOnNextAttempt()
    {
        var source = await CreateImageAsync("locked-copy.png");
        string folder;
        string copy;
        using (var prepared = await Service.PrepareAsync([source], cancellationToken: TestContext.Current.CancellationToken))
        {
            prepared.RetainForHandoff();
            folder = prepared.Folder;
            copy = Assert.Single(prepared.Result.ExportedPaths);
        }
        File.SetLastWriteTimeUtc(Path.Combine(folder, ".lease"), DateTime.UtcNow.AddDays(-8));
        using (new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Service.CleanupExpired();
            Assert.True(File.Exists(copy));
            Assert.True(File.Exists(Path.Combine(folder, ".lease")));
        }
        Service.CleanupExpired();
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void CleanupSkipsExpiredBatchWithAnActiveLease()
    {
        var folder = Path.Combine(Root, $"share-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var marker = Path.Combine(folder, ".lease");
        File.WriteAllText(marker, "");
        File.SetLastWriteTimeUtc(marker, DateTime.UtcNow.AddDays(-8));
        using (new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Service.CleanupExpired();
            Assert.True(Directory.Exists(folder));
        }
        Service.CleanupExpired();
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task InvalidTemporaryLocationSurfacesErrorWithoutTouchingSources()
    {
        var source = await CreateImageAsync("source.png");
        File.WriteAllText(Root, "not a directory");
        await Assert.ThrowsAsync<IOException>(() =>
            Service.PrepareAsync([source], cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(File.Exists(source));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task EmailFallbackReportsActualLaunchResultsAndNeverClaimsAttachments(bool folder, bool draft)
    {
        var uri = PhotoShareService.CreateMailUri("Photos & family", "Attach manually.\nNo photos attached.");
        var opened = new List<string>();
        var result = await PhotoShareService.OpenEmailAsync("copies", uri,
            path => { opened.Add(path); return Task.FromResult(folder); },
            value => { opened.Add(value.AbsoluteUri); return Task.FromResult(draft); });
        Assert.Equal((folder, draft), result);
        Assert.Equal("copies", opened[0]);
        Assert.Equal(uri.AbsoluteUri, opened[1]);
        Assert.Equal("mailto", uri.Scheme);
        Assert.Contains("Photos%20%26%20family", uri.AbsoluteUri);
        Assert.DoesNotContain("attachment=", uri.AbsoluteUri);
        Assert.True(uri.AbsoluteUri.Length < 1000);
    }

    [Fact]
    public async Task EmailFallbackStillOpensDraftWhenFolderLaunchThrows()
    {
        var result = await PhotoShareService.OpenEmailAsync("copies",
            PhotoShareService.CreateMailUri("Photos", "Attach manually."),
            _ => throw new UnauthorizedAccessException("denied"),
            _ => Task.FromResult(true));
        Assert.Equal((false, true), result);
    }

    [Fact]
    public async Task WindowsSharePackageContainsOnlyPreparedCopiesInSelectionOrder()
    {
        var first = await CreateImageAsync("package-one.png");
        var second = await CreateImageAsync("package-two.png");
        using var prepared = await Service.PrepareAsync([first, second],
            cancellationToken: TestContext.Current.CancellationToken);
        var package = await PhotoShareService.CreatePackageAsync(prepared.Result.ExportedPaths, "Share photos");
        var view = package.GetView();
        Assert.Equal(DataPackageOperation.Copy, view.RequestedOperation);
        Assert.Equal("Share photos", view.Properties.Title);
        Assert.True(view.Contains(StandardDataFormats.StorageItems));
        var items = await view.GetStorageItemsAsync();
        Assert.Equal(prepared.Result.ExportedPaths, items.Select(item => item.Path));
        Assert.DoesNotContain(items, item => item.Path == first || item.Path == second);
    }

    [Fact]
    public async Task WindowsSharePackageRejectsEmptyAndUnavailableCopies()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => PhotoShareService.CreatePackageAsync([], "Photos"));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            PhotoShareService.CreatePackageAsync([Path.Combine(_directory, "missing.jpg")], "Photos"));
    }

    [Fact]
    public void ShareActionsAndStringsAreAccessibleAndLocalized()
    {
        var assets = Path.Combine(AppContext.BaseDirectory, "TestAssets");
        var xaml = XDocument.Load(Path.Combine(assets, "ImageGridView.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var share = Assert.Single(xaml.Descendants(), element =>
            (string?)element.Attribute(x + "Name") == "ShareButton");
        Assert.Equal("GridShareButton", (string?)share.Attribute(x + "Uid"));
        Assert.Equal("GridShareButton", (string?)share.Attribute("AutomationProperties.AutomationId"));
        var resources = XDocument.Load(Path.Combine(assets, "Resources.resw"));
        Assert.Contains(resources.Root!.Elements("data"), element =>
            (string?)element.Attribute("name") == "ShareManualAttachment" &&
            element.Element("value")!.Value.Contains("NOT attached"));
        Assert.Equal("Share photos", ShareStrings.Get("ShareTitle"));
    }

    private async Task<string> CreateImageAsync(string name, uint width = 32, uint height = 16)
    {
        var path = Path.Combine(_directory, name);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream.AsRandomAccessStream());
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)255);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, 96, 96, pixels);
        await encoder.FlushAsync();
        return path;
    }

    private static async Task AssertJpegAsync(string path, uint width, uint height)
    {
        await using var stream = File.OpenRead(path);
        var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
        Assert.Equal(BitmapDecoder.JpegDecoderId, decoder.DecoderInformation.CodecId);
        Assert.Equal(width, decoder.PixelWidth);
        Assert.Equal(height, decoder.PixelHeight);
    }

    private sealed class CancelProgress(CancellationTokenSource cancellation) : IProgress<ImageExportProgress>
    {
        public void Report(ImageExportProgress value) => cancellation.Cancel();
    }

    private sealed class ResizeOutputProgress(string root) : IProgress<ImageExportProgress>
    {
        public void Report(ImageExportProgress value)
        {
            var path = Assert.Single(Directory.GetFiles(root, "*.jpg", SearchOption.AllDirectories));
            using var stream = File.OpenWrite(path);
            stream.SetLength(PhotoSharePreparationService.MaximumBytes + 1);
        }
    }
}
