using System.Runtime.InteropServices.WindowsRuntime;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class ImageExportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-Export-{Guid.NewGuid():N}");

    public ImageExportTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData(4000u, 3000u, 1600, ExportResizeMode.MaximumDimension, 1600u, 1200u)]
    [InlineData(3000u, 4000u, 1600, ExportResizeMode.MaximumDimension, 1200u, 1600u)]
    [InlineData(400u, 300u, 1600, ExportResizeMode.MaximumDimension, 400u, 300u)]
    [InlineData(400u, 300u, 25, ExportResizeMode.Percentage, 100u, 75u)]
    [InlineData(400u, 300u, 100, ExportResizeMode.Percentage, 400u, 300u)]
    [InlineData(10000u, 1u, 1, ExportResizeMode.MaximumDimension, 1u, 1u)]
    public void DimensionsPreserveAspectRatioAndDoNotEnlarge(
        uint width, uint height, double size, ExportResizeMode mode, uint expectedWidth, uint expectedHeight)
    {
        var options = new ImageExportOptions { Size = size, ResizeMode = mode };
        Assert.Equal((expectedWidth, expectedHeight), options.GetDimensions(width, height));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.5)]
    [InlineData(65536)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidSizesAreRejected(double size) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageExportOptions { Size = size }.Validate());

    [Fact]
    public void InvalidPercentageQualityAndModeAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ImageExportOptions { ResizeMode = ExportResizeMode.Percentage, Size = 101 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageExportOptions { Quality = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageExportOptions { Quality = 101 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageExportOptions { ResizeMode = (ExportResizeMode)99 }.Validate());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportsOrientedJpegWithRequestedSizeAndMetadataPolicy(bool stripMetadata)
    {
        var source = await CreateImageAsync("portrait.jpg", orientation: 6, metadata: true);
        var original = await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(source + ".xmp", "private sidecar", TestContext.Current.CancellationToken);

        var result = await ImageExportService.ExportAsync(
            [source], _directory, new() { Size = 32, StripMetadata = stripMetadata },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.Failures);
        Assert.False(result.IsCancelled);
        var output = Assert.Single(result.ExportedPaths);
        await using var file = File.OpenRead(output);
        var decoder = await BitmapDecoder.CreateAsync(file.AsRandomAccessStream());
        Assert.Equal(BitmapDecoder.JpegDecoderId, decoder.DecoderInformation.CodecId);
        Assert.Equal(16u, decoder.PixelWidth);
        Assert.Equal(32u, decoder.PixelHeight);
        Assert.Equal(decoder.PixelWidth, decoder.OrientedPixelWidth);
        Assert.Equal(decoder.PixelHeight, decoder.OrientedPixelHeight);
        var properties = await decoder.BitmapProperties.GetPropertiesAsync(
            ["System.Title", "System.Keywords", "System.Rating", "System.Photo.DateTaken"]);
        if (stripMetadata)
        {
            Assert.All(properties, property => Assert.Null(property.Value.Value));
            var directories = MetadataExtractor.ImageMetadataReader.ReadMetadata(output);
            Assert.DoesNotContain(directories, directory =>
                directory is MetadataExtractor.Formats.Exif.ExifDirectoryBase or MetadataExtractor.Formats.Xmp.XmpDirectory);
        }
        else
        {
            Assert.Equal("Export test caption", properties["System.Title"].Value);
            Assert.Equal(new[] { "travel", "family" }, (string[])properties["System.Keywords"].Value);
            Assert.Equal((ushort)75, properties["System.Rating"].Value);
            Assert.NotNull(properties["System.Photo.DateTaken"].Value);
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(output + ".xmp"));
        Assert.False(File.Exists(Path.ChangeExtension(output, ".xmp")));
        AssertNoStagingFiles();
    }

    [Fact]
    public async Task HonorsPercentageAndQuality()
    {
        var source = await CreateImageAsync("quality.png");
        var low = await ImageExportService.ExportAsync([source], _directory,
            new() { ResizeMode = ExportResizeMode.Percentage, Size = 50, Quality = 10 },
            cancellationToken: TestContext.Current.CancellationToken);
        var high = await ImageExportService.ExportAsync([source], _directory,
            new() { ResizeMode = ExportResizeMode.Percentage, Size = 50, Quality = 100 },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(low.Failures);
        Assert.Empty(high.Failures);
        var lowPath = Assert.Single(low.ExportedPaths);
        var highPath = Assert.Single(high.ExportedPaths);
        Assert.True(new FileInfo(lowPath).Length < new FileInfo(highPath).Length);
        await using var stream = File.OpenRead(highPath);
        var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
        Assert.Equal(64u, decoder.PixelWidth);
        Assert.Equal(32u, decoder.PixelHeight);
    }

    [Fact]
    public async Task TransparentPixelsAreFlattenedToWhite()
    {
        var source = await CreateImageAsync("transparent.png", transparent: true);
        var result = await ImageExportService.ExportAsync(
            [source], _directory, new(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(result.Failures);
        await using var stream = File.OpenRead(Assert.Single(result.ExportedPaths));
        var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
        var data = await decoder.GetPixelDataAsync();
        Assert.All(data.DetachPixelData(), value => Assert.InRange(value, (byte)250, (byte)255));
    }

    [Fact]
    public async Task UsesUniqueNamesForCollisionsAndContinuesAfterCorruptInput()
    {
        var source = await CreateImageAsync("same.jpg");
        var subfolder = Path.Combine(_directory, "other");
        Directory.CreateDirectory(subfolder);
        var second = Path.Combine(subfolder, "same.jpg");
        File.Copy(source, second);
        var existing = Path.Combine(_directory, "same - Resized.jpg");
        await File.WriteAllTextAsync(existing, "do not overwrite", TestContext.Current.CancellationToken);
        var corrupt = Path.Combine(_directory, "broken.jpg");
        await File.WriteAllTextAsync(corrupt, "not an image", TestContext.Current.CancellationToken);

        var result = await ImageExportService.ExportAsync(
            [source, corrupt, second], _directory, new(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new[]
        {
            Path.Combine(_directory, "same - Resized (2).jpg"),
            Path.Combine(_directory, "same - Resized (3).jpg")
        }, result.ExportedPaths);
        Assert.Equal(corrupt, Assert.Single(result.Failures).SourcePath);
        Assert.Equal("do not overwrite", await File.ReadAllTextAsync(existing, TestContext.Current.CancellationToken));
        AssertNoStagingFiles();
    }

    [Fact]
    public async Task ConcurrentExportsAndDirectoryCollisionsDoNotOverwrite()
    {
        var source = await CreateImageAsync("parallel.png");
        Directory.CreateDirectory(Path.Combine(_directory, "parallel - Resized.jpg"));

        var results = await Task.WhenAll(
            ImageExportService.ExportAsync([source], _directory, new(), cancellationToken: TestContext.Current.CancellationToken),
            ImageExportService.ExportAsync([source], _directory, new(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.All(results, result => Assert.Empty(result.Failures));
        var paths = results.SelectMany(result => result.ExportedPaths).ToArray();
        Assert.Equal(2, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(paths, path => Assert.True(File.Exists(path)));
        AssertNoStagingFiles();
    }

    [Fact]
    public async Task CancellationKeepsCompletedCopiesAndReportsAccurateProgress()
    {
        var source = await CreateImageAsync("cancel.png");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var updates = new List<ImageExportProgress>();
        var progress = new ImmediateProgress(update =>
        {
            updates.Add(update);
            cancellation.Cancel();
        });

        var result = await ImageExportService.ExportAsync(
            [source, source], _directory, new(), progress, cancellation.Token);

        Assert.True(result.IsCancelled);
        Assert.Single(result.ExportedPaths);
        Assert.Empty(result.Failures);
        Assert.Equal(new ImageExportProgress(1, 2, "cancel.png"), Assert.Single(updates));
        AssertNoStagingFiles();
    }

    [Fact]
    public async Task PreCancelledBatchDoesNotCreateFiles()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var result = await ImageExportService.ExportAsync(
            ["unused.jpg"], _directory, new(), cancellationToken: cancellation.Token);
        Assert.True(result.IsCancelled);
        Assert.Empty(result.ExportedPaths);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task InvalidOutputFolderIsRejectedBeforeProcessing()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            ImageExportService.ExportAsync(["unused.jpg"], Path.Combine(_directory, "missing"), new(),
                cancellationToken: TestContext.Current.CancellationToken));
    }

    private void AssertNoStagingFiles() =>
        Assert.Empty(Directory.GetFiles(_directory, ".photolibrarian-export-*.tmp"));

    private async Task<string> CreateImageAsync(
        string name, ushort orientation = 1, bool metadata = false, bool transparent = false)
    {
        var path = Path.Combine(_directory, name);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(
            name.EndsWith(".png") ? BitmapEncoder.PngEncoderId : BitmapEncoder.JpegEncoderId,
            stream.AsRandomAccessStream());
        var pixels = new byte[128 * 64 * 4];
        if (!transparent)
        {
            var random = new Random(17);
            random.NextBytes(pixels);
            for (var i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;
        }
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, 128, 64, 96, 96, pixels);
        if (metadata)
        {
            await encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
            {
                ["System.Photo.Orientation"] = new BitmapTypedValue(orientation, PropertyType.UInt16),
                ["System.Title"] = new BitmapTypedValue("Export test caption", PropertyType.String),
                ["System.Keywords"] = new BitmapTypedValue(new[] { "travel", "family" }, PropertyType.StringArray),
                ["System.Rating"] = new BitmapTypedValue((ushort)75, PropertyType.UInt16),
                ["System.Photo.DateTaken"] = new BitmapTypedValue(
                    new DateTimeOffset(2020, 6, 15, 12, 0, 0, TimeSpan.Zero), PropertyType.DateTime)
            });
        }
        await encoder.FlushAsync();
        return path;
    }

    private sealed class ImmediateProgress(Action<ImageExportProgress> report) : IProgress<ImageExportProgress>
    {
        public void Report(ImageExportProgress value) => report(value);
    }
}
