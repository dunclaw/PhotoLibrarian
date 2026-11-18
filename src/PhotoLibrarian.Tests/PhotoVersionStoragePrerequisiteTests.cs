// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Services;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class PhotoVersionStoragePrerequisiteTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"PhotoLibrarian-version-storage-{Guid.NewGuid():N}");

    public PhotoVersionStoragePrerequisiteTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task TiffCustomXmpRoundTripsWithoutChangingPixelsOrOtherMetadata()
    {
        var path = Path.Combine(_directory, "source.tif");
        await WicTestImage.CreateAsync(path, 32, 24);
        var before = await WindowsImagingCodec.DecodeAsync(path);
        await MetadataWriterService.WriteColorLabelAsync(path, "Blue");
        var payload = new string('x', 80_000);

        await EmbeddedMetadataWriter.WriteAsync(path, photoTagMetadata: payload);

        var xmp = FaceMetadataStore.ReadEmbeddedXmp(path);
        Assert.NotNull(xmp);
        Assert.Equal(payload, xmp.GetPropertyString(
            MetadataWriterService.PhotoLibrarianNamespace,
            EmbeddedMetadataWriter.PhotoTagMetadataPropertyName));
        Assert.Equal("Blue", new MetadataReaderService().ReadMetadata(path).ColorLabel);
        var after = await WindowsImagingCodec.DecodeAsync(path);
        Assert.Equal(before.Width, after.Width);
        Assert.Equal(before.Height, after.Height);
        Assert.Equal(before.Pixels, after.Pixels);
        Assert.False(File.Exists(path + ".xmp"));
        Assert.False(File.Exists(Path.ChangeExtension(path, ".xmp")));
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".tif")]
    public async Task RasterSourceIdentityIgnoresMetadataButDetectsPixelChanges(string extension)
    {
        var path = Path.Combine(_directory, "source" + extension);
        await WicTestImage.CreateAsync(path, 32, 24, jpeg: extension == ".jpg");
        var original = await RecognitionSourceIdentity.ComputeAsync(path);
        Assert.StartsWith("pixels-v1:", original);

        await MetadataWriterService.WriteColorLabelAsync(path, "Blue");
        Assert.Equal(original, await RecognitionSourceIdentity.ComputeAsync(path));

        await WicTestImage.CreateAsync(path, 32, 24, jpeg: extension == ".jpg",
            color: (_, _) => (10, 20, 30));
        Assert.NotEqual(original, await RecognitionSourceIdentity.ComputeAsync(path));
    }

    [Fact]
    public async Task RawSourceIdentityIgnoresSidecarChangesButDetectsRawByteChanges()
    {
        var path = Path.Combine(_directory, "source.cr3");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var original = await RecognitionSourceIdentity.ComputeAsync(path);
        Assert.StartsWith("file-v1:", original);

        await MetadataWriterService.WriteColorLabelAsync(path, "Blue");
        Assert.Equal(original, await RecognitionSourceIdentity.ComputeAsync(path));
        await File.WriteAllBytesAsync(path, [1, 2, 4]);
        Assert.NotEqual(original, await RecognitionSourceIdentity.ComputeAsync(path));
    }

    [Fact]
    public async Task WicBoundedRegionsPreserveSixteenBitSamples()
    {
        const uint width = 5, height = 3;
        var samples = new ushort[width * height * 4];
        for (var index = 0; index < samples.Length; index += 4)
        {
            samples[index] = (ushort)(index * 97 + 1);
            samples[index + 1] = (ushort)(index * 79 + 2);
            samples[index + 2] = (ushort)(index * 53 + 3);
            samples[index + 3] = ushort.MaxValue;
        }
        var expected = new byte[samples.Length * sizeof(ushort)];
        System.Buffer.BlockCopy(samples, 0, expected, 0, expected.Length);
        var path = Path.Combine(_directory, "high-depth.tif");
        using (var buffer = new InMemoryRandomAccessStream())
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.TiffEncoderId, buffer);
            encoder.SetPixelData(BitmapPixelFormat.Rgba16, BitmapAlphaMode.Straight,
                width, height, 96, 96, expected);
            await encoder.FlushAsync();
            buffer.Seek(0);
            await using var file = File.Create(path);
            await buffer.AsStreamForRead().CopyToAsync(file);
        }

        await using var input = File.OpenRead(path);
        var decoder = await BitmapDecoder.CreateAsync(input.AsRandomAccessStream());
        var actual = new byte[expected.Length];
        for (uint y = 0; y < height; y++)
        {
            for (uint x = 0; x < width; x += 2)
            {
                var tileWidth = Math.Min(2u, width - x);
                var transform = new BitmapTransform
                {
                    Bounds = new BitmapBounds { X = x, Y = y, Width = tileWidth, Height = 1 }
                };
                var data = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Rgba16, BitmapAlphaMode.Straight, transform,
                    ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
                var tile = data.DetachPixelData();
                System.Buffer.BlockCopy(tile, 0, actual, checked((int)((y * width + x) * 8)), tile.Length);
            }
        }
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task OrientationChangesTheInterpretedImageEvenWhenDimensionsMatch()
    {
        var path = Path.Combine(_directory, "orientation.jpg");
        await WicTestImage.CreateAsync(path, 8, 8, jpeg: true,
            color: (x, y) => ((byte)(x * 25), (byte)(y * 25), (byte)0));
        var before = await WindowsImagingCodec.DecodeAsync(path);

        await EmbeddedMetadataWriter.WriteAsync(path, orientation: 3);

        var after = await WindowsImagingCodec.DecodeAsync(path);
        Assert.Equal(before.Width, after.Width);
        Assert.Equal(before.Height, after.Height);
        Assert.NotEqual(before.Pixels, after.Pixels);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
