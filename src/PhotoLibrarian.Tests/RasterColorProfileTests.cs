// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Graphics.Canvas;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using System.Numerics;
using Windows.Graphics.Imaging;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class RasterColorProfileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-ICC-{Guid.NewGuid():N}");

    public RasterColorProfileTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".tif")]
    [InlineData(".bmp")]
    public async Task UntaggedImagesKeepTheirExistingOrientedPixels(string extension)
    {
        var path = Path.Combine(_directory, $"untagged{extension}");
        await WicTestImage.CreateAsync(path, 13, 7, jpeg: extension == ".jpg");
        var expected = await ReadAsync(path, ColorManagementMode.DoNotColorManage);
        var decoded = await RasterImageDecoder.DecodeAsync(path);
        Assert.Equal(RasterColorProfileStatus.Untagged, decoded.Profile.Status);
        Assert.Equal(expected, decoded.Pixels);
        Assert.Equal(13u, decoded.Width);
        Assert.Equal(7u, decoded.Height);
    }

    [Fact]
    public async Task UntaggedScaledDecodeKeepsTheLegacyRecipeThumbnailPixels()
    {
        var path = Path.Combine(_directory, "scaled.png");
        await RenderQualityFixtures.WriteImageAsync(path, 47, 31,
            RenderQualityFixtures.CreatePixels(RenderQualityFixtureKind.FineTexture, 47, 31, alpha: true));
        await using var stream = File.OpenRead(path);
        var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
        var legacy = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform { ScaledWidth = 17, ScaledHeight = 11 },
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        var decoded = await RasterImageDecoder.DecodeAsync(path, 17);
        Assert.Equal(legacy.DetachPixelData(), decoded.Pixels);
        Assert.Equal(17u, decoded.Width);
        Assert.Equal(11u, decoded.Height);
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 6)]
    public async Task EmbeddedLinearRgbIsConvertedBeforeEditorAndRecipeRendering(bool jpeg, bool alpha, ushort orientation)
    {
        var path = await CreateTaggedAsync(jpeg, alpha, orientation);
        var original = await File.ReadAllBytesAsync(path);
        var expected = await ReadAsync(path, ColorManagementMode.ColorManageToSRgb);
        var unmanaged = await ReadAsync(path, ColorManagementMode.DoNotColorManage);
        var decoded = await RasterImageDecoder.DecodeAsync(path);
        Assert.Equal(RasterColorProfileStatus.Embedded, decoded.Profile.Status);
        Assert.NotEqual(unmanaged, decoded.Pixels);
        AssertClose(expected, decoded.Pixels!, 2);
        Assert.True(decoded.Pixels![4] > unmanaged[4] + 30);
        for (var i = 3; i < expected.Length; i += 4)
            Assert.Equal(unmanaged[i], decoded.Pixels[i]);

        using var device = new CanvasDevice();
        using var preview = await ImageEditRenderer.LoadOrientedAsync(device, path);
        Assert.Equal(decoded.Pixels, preview.GetPixelBytes());
        var parameters = new EditParameters { Exposure = 0.2, Contrast = 0.1 };
        var rendered = await ImageEditRenderer.RenderToPixelsAsync(device, path, parameters);
        var thumbnail = await EffectThumbnailSession.RenderRecipeThumbnailAsync(
            path, parameters, 100, TestContext.Current.CancellationToken);
        Assert.Equal(rendered.Pixels, thumbnail.Pixels);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task OrientedScaledTaggedDecodeUsesOrientedDimensionsAndCancellation()
    {
        var path = await CreateTaggedAsync(jpeg: true, alpha: false, orientation: 6);
        var decoded = await RasterImageDecoder.DecodeAsync(path, 6);
        Assert.Equal(4u, decoded.Width);
        Assert.Equal(6u, decoded.Height);
        Assert.Equal(7u, decoded.SourceWidth);
        Assert.Equal(11u, decoded.SourceHeight);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RasterImageDecoder.DecodeAsync(path, cancellationToken: cancelled.Token));
    }

    [Fact]
    public async Task IsolatedCreativePrimitivesConsumeTheConvertedSrgbWorkingPixelsWithoutChangingTheTaggedSource()
    {
        var path = await CreateTaggedAsync(jpeg: false, alpha: true);
        var original = await File.ReadAllBytesAsync(path);
        var expectedWorkingPixels = await ReadAsync(path, ColorManagementMode.ColorManageToSRgb);
        var unmanaged = await ReadAsync(path, ColorManagementMode.DoNotColorManage);
        Assert.NotEqual(unmanaged, expectedWorkingPixels);

        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var loaded = await ImageEditRenderer.LoadOrientedAsync(device, path);
        using var reference = CanvasBitmap.CreateFromBytes(device, expectedWorkingPixels, 11, 7,
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
        var actualPixels = RenderCreativePrimitives(device, loaded);
        var expectedPixels = RenderCreativePrimitives(device, reference);
        AssertClose(expectedPixels, actualPixels, 3);
        for (var i = 3; i < actualPixels.Length; i += 4)
            Assert.Equal(unmanaged[i], actualPixels[i]);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    private static byte[] RenderCreativePrimitives(CanvasDevice device, CanvasBitmap source)
    {
        var size = new Vector2((float)source.Size.Width, (float)source.Size.Height);
        Assert.True(SourceDetailEffect.TryCreate(source, size, 0.6, 0.4,
            out var detailImage, out var detailError), detailError);
        using var detail = Assert.IsType<Microsoft.Graphics.Canvas.Effects.CropEffect>(detailImage);
        Assert.True(PostCropEffect.TryCreate(detail, size,
            new() { VignetteAmount = -0.5, GrainAmount = 0.4, Seed = 0xff00ff00 },
            size, Vector2.Zero, Vector2.One, out var outputImage, out var outputError), outputError);
        using var output = Assert.IsType<Microsoft.Graphics.Canvas.Effects.CropEffect>(outputImage);
        using var target = new CanvasRenderTarget(device, size.X, size.Y, 96);
        using (var session = target.CreateDrawingSession())
            session.DrawImage(output);
        return target.GetPixelBytes();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SrgbAndWideGamutProfilesFollowTheWindowsColorReference(bool wideGamut)
    {
        var profile = SdrDerivativeMetadata.CreateSrgbProfile();
        if (wideGamut)
        {
            // Display P3 primaries, Bradford-adapted to the ICC D50 PCS.
            SetXyz(profile, "rXYZ", 0.51514644, 0.24120032, -0.00105013);
            SetXyz(profile, "gXYZ", 0.29198410, 0.69222254, 0.04187801);
            SetXyz(profile, "bXYZ", 0.15710491, 0.06657714, 0.78407128);
        }
        var path = await CreateTaggedAsync(jpeg: false, alpha: false, profile: profile);
        var decoded = await RasterImageDecoder.DecodeAsync(path);
        Assert.Equal(RasterColorProfileStatus.Embedded, decoded.Profile.Status);
        AssertClose(await ReadAsync(path, ColorManagementMode.ColorManageToSRgb), decoded.Pixels!, 3);
        if (!wideGamut)
            Assert.Equal(await ReadAsync(path, ColorManagementMode.DoNotColorManage), decoded.Pixels);
        using var device = new CanvasDevice();
        var rendered = await ImageEditRenderer.RenderToPixelsAsync(device, path, new EditParameters());
        await ImageEditRenderer.RenderToFileAsync(path, new EditParameters(), device);
        var reopened = await RasterImageDecoder.DecodeAsync(path);
        AssertClose(rendered.Pixels, reopened.Pixels!, 3);
        Assert.Equal(profile, reopened.Profile.Data);
    }

    [Fact]
    public async Task TaggedTiffBakePreservesItsProfileAndMetadata()
    {
        var source = await CreateTaggedAsync(jpeg: false, alpha: false);
        var profile = RasterColorProfile.Read(source);
        var path = Path.Combine(_directory, "tagged.tif");
        await WicTestImage.CreateAsync(path, 11, 7);
        await EmbeddedMetadataWriter.WriteAsync(path, title: "TIFF ICC fixture", orientation: 6);
        await MetadataWriterService.PreserveRasterColorProfileAsync(path, profile);
        var decoded = await RasterImageDecoder.DecodeAsync(path);
        Assert.Equal(7u, decoded.Width);
        Assert.Equal(11u, decoded.Height);
        AssertClose(await ReadAsync(path, ColorManagementMode.ColorManageToSRgb), decoded.Pixels!, 2);
        using var device = new CanvasDevice();
        var parameters = new EditParameters { Exposure = 0.1 };
        var rendered = await ImageEditRenderer.RenderToPixelsAsync(device, path, parameters);
        await ImageEditRenderer.RenderToFileAsync(path, parameters, device);
        var reopened = await RasterImageDecoder.DecodeAsync(path);
        Assert.Equal(profile.Data, reopened.Profile.Data);
        AssertClose(rendered.Pixels, reopened.Pixels!, 3);
        await using var stream = File.OpenRead(path);
        var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
        var metadata = await decoder.BitmapProperties.GetPropertiesAsync(["System.Title", "System.Photo.Orientation"]);
        Assert.Equal("TIFF ICC fixture", metadata["System.Title"].Value);
        Assert.Equal((ushort)1, metadata["System.Photo.Orientation"].Value);
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 1)]
    [InlineData(true, false, 6)]
    public async Task BakeRetainsTheOriginalProfileAndReopensLikeTheSrgbRender(bool jpeg, bool alpha, ushort orientation)
    {
        var path = await CreateTaggedAsync(jpeg, alpha, orientation);
        var originalProfile = RasterColorProfile.Read(path);
        var parameters = new EditParameters { Exposure = 0.15 };
        using var device = new CanvasDevice();
        var rendered = await ImageEditRenderer.RenderToPixelsAsync(device, path, parameters);
        await ImageEditRenderer.RenderToFileAsync(path, parameters, device);
        var decoded = await RasterImageDecoder.DecodeAsync(path);
        Assert.Equal(RasterColorProfileStatus.Embedded, decoded.Profile.Status);
        Assert.Equal(originalProfile.Data, decoded.Profile.Data);
        Assert.Equal(rendered.Width, decoded.Width);
        Assert.Equal(rendered.Height, decoded.Height);
        AssertClose(rendered.Pixels, decoded.Pixels!, jpeg ? 8 : 3);
        var raw = await ReadAsync(path, ColorManagementMode.DoNotColorManage);
        Assert.True(decoded.Pixels![4] > raw[4] + 30);
        await using var stream = File.OpenRead(path);
        var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
        var properties = await decoder.BitmapProperties.GetPropertiesAsync(["System.Title", "System.Photo.Orientation"]);
        Assert.Equal("ICC fixture", properties["System.Title"].Value);
        if (jpeg)
            Assert.Equal((ushort)1, properties["System.Photo.Orientation"].Value);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task InvalidAndUnsupportedProfilesAreExplicitAndCannotOverwriteTheSource(bool jpeg, bool unsupported)
    {
        var profile = CreateLinearProfile();
        profile[unsupported ? 16 : 36] = (byte)'?';
        var path = await CreateTaggedAsync(jpeg, alpha: false, profile: profile);
        var before = await File.ReadAllBytesAsync(path);
        var decoded = await RasterImageDecoder.DecodeAsync(path);
        Assert.Equal(unsupported ? RasterColorProfileStatus.Unsupported : RasterColorProfileStatus.Invalid,
            decoded.Profile.Status);
        Assert.False(decoded.Profile.IsSupported);
        Assert.Null(decoded.Pixels);
        Assert.False(string.IsNullOrWhiteSpace(decoded.Profile.Message));
        using var device = new CanvasDevice();
        var loaded = await ImageEditRenderer.TryLoadOrientedAsync(device, path);
        Assert.Null(loaded.Bitmap);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ImageEditRenderer.RenderToFileAsync(path, new EditParameters(), device));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task FailedPublicationRetainsTheOriginalAndRemovesThePrivateBake()
    {
        var path = await CreateTaggedAsync(jpeg: false, alpha: true);
        var before = await File.ReadAllBytesAsync(path);
        using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var device = new CanvasDevice();
        await Assert.ThrowsAsync<IOException>(() =>
            ImageEditRenderer.RenderToFileAsync(path, new EditParameters { Exposure = 0.1 }, device));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task UnusableProfileTagTableIsReportedRatherThanDecodedUnmanaged()
    {
        var profile = CreateLinearProfile();
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(136), uint.MaxValue);
        var path = await CreateTaggedAsync(jpeg: false, alpha: false, profile: profile);
        var decoded = await RasterImageDecoder.DecodeAsync(path);
        Assert.False(decoded.Profile.IsSupported);
        Assert.Null(decoded.Pixels);
        Assert.False(string.IsNullOrWhiteSpace(decoded.Profile.Message));
    }

    [Theory]
    [InlineData(0x4d424544u)]
    [InlineData(0x4c494e4bu)]
    public async Task BmpProfilesAreExplicitlyUnsupportedWithoutFollowingLinkedPaths(uint colorSpace)
    {
        var profile = colorSpace == 0x4c494e4b
            ? System.Text.Encoding.ASCII.GetBytes("never-open-this-profile.icc\0")
            : CreateLinearProfile();
        var bitmap = new byte[142 + profile.Length];
        bitmap[0] = (byte)'B';
        bitmap[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(2), bitmap.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(10), 138);
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(14), 124);
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(18), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bitmap.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bitmap.AsSpan(28), 32);
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(34), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bitmap.AsSpan(70), colorSpace);
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(126), 128);
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(130), profile.Length);
        profile.CopyTo(bitmap, 142);
        var path = Path.Combine(_directory, "profile.bmp");
        await File.WriteAllBytesAsync(path, bitmap);
        var decoded = await RasterImageDecoder.DecodeAsync(path);
        Assert.Equal(RasterColorProfileStatus.Unsupported, decoded.Profile.Status);
        Assert.Null(decoded.Pixels);
        Assert.Contains("BMP", decoded.Profile.Message);
    }

    [Fact]
    public async Task FailedTransactionPreparationDoesNotPublishAColorManagedBake()
    {
        var path = await CreateTaggedAsync(jpeg: true, alpha: false);
        var before = await File.ReadAllBytesAsync(path);
        var history = Path.Combine(_directory, "history");
        using var device = new CanvasDevice();
        await Assert.ThrowsAsync<IOException>(() => FileEditTransaction.PrepareAsync(history, [path], async copies =>
        {
            await ImageEditRenderer.RenderToFileAsync(copies[path], new EditParameters { Exposure = 0.1 }, device);
            throw new IOException("Simulated failure after preparing the private bake.");
        }));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFileSystemEntries(history));
    }

    [Fact]
    public async Task FailedBackupDoesNotStartRenderingOrChangeThePhoto()
    {
        var path = await CreateTaggedAsync(jpeg: false, alpha: false);
        var before = await File.ReadAllBytesAsync(path);
        var backupDirectory = Path.Combine(_directory, "backups");
        var backup = new OriginalBackupService(backupDirectory);
        var hash = await OriginalBackupService.ComputeFileHashAsync(path);
        await File.WriteAllTextAsync(Path.Combine(backupDirectory, hash[..2]), "Blocks creation of the backup subdirectory.");
        var service = new ImageEditService(backup);
        var called = false;
        await Assert.ThrowsAsync<IOException>(() => service.ApplyEditsAsync(path, new EditParameters(), (_, _) =>
        {
            called = true;
            return Task.FromResult((11u, 7u));
        }));
        Assert.False(called);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    private async Task<string> CreateTaggedAsync(bool jpeg, bool alpha, ushort orientation = 1, byte[]? profile = null)
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.{(jpeg ? "jpg" : "png")}");
        var pixels = new byte[11 * 7 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var a = alpha ? (byte)(i == 0 ? 0 : 192) : (byte)255;
            pixels[i] = (byte)(40 * a / 255);
            pixels[i + 1] = (byte)(90 * a / 255);
            pixels[i + 2] = (byte)(150 * a / 255);
            pixels[i + 3] = a;
        }
        await RenderQualityFixtures.WriteImageAsync(path, 11, 7, pixels, dpi: 300, orientation, jpeg);
        await EmbeddedMetadataWriter.WriteAsync(path, title: "ICC fixture");
        profile ??= CreateLinearProfile();
        var encoded = await File.ReadAllBytesAsync(path);
        using var output = new MemoryStream();
        if (jpeg)
        {
            output.Write(encoded, 0, 2);
            output.Write([0xff, 0xe2, (byte)((profile.Length + 16) >> 8), (byte)(profile.Length + 16)]);
            output.Write("ICC_PROFILE\0"u8);
            output.Write([1, 1]);
            output.Write(profile);
            output.Write(encoded, 2, encoded.Length - 2);
        }
        else
        {
            output.Write(encoded, 0, 33);
            using var compressed = new MemoryStream();
            compressed.Write("Linear RGB\0"u8);
            compressed.WriteByte(0);
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(profile);
            WritePngChunk(output, compressed.ToArray());
            // WIC may emit an sRGB chunk. It cannot coexist with an ICC chunk.
            for (var offset = 33; offset < encoded.Length;)
            {
                var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(encoded.AsSpan(offset)));
                if (!encoded.AsSpan(offset + 4, 4).SequenceEqual("sRGB"u8))
                    output.Write(encoded, offset, length + 12);
                offset += length + 12;
            }
        }
        await File.WriteAllBytesAsync(path, output.ToArray());
        return path;
    }

    private static byte[] CreateLinearProfile()
    {
        var profile = SdrDerivativeMetadata.CreateSrgbProfile();
        var count = BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(128));
        for (var i = 0; i < count; i++)
        {
            var entry = 132 + i * 12;
            if (!profile.AsSpan(entry + 1, 3).SequenceEqual("TRC"u8)) continue;
            var offset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(entry + 4)));
            for (var sample = 0; sample < 4096; sample++)
                BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(offset + 12 + sample * 2),
                    (ushort)Math.Round(sample / 4095d * 65535));
        }
        return profile;
    }

    private static void SetXyz(byte[] profile, string tag, double x, double y, double z)
    {
        var count = BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(128));
        for (var i = 0; i < count; i++)
        {
            var entry = 132 + i * 12;
            if (System.Text.Encoding.ASCII.GetString(profile, entry, 4) != tag) continue;
            var offset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(entry + 4)));
            BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(offset + 8), (int)Math.Round(x * 65536));
            BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(offset + 12), (int)Math.Round(y * 65536));
            BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(offset + 16), (int)Math.Round(z * 65536));
            return;
        }
        Assert.Fail($"Missing generated ICC tag {tag}.");
    }

    private static void WritePngChunk(Stream output, byte[] data)
    {
        var bytes = new byte[data.Length + 12];
        BinaryPrimitives.WriteInt32BigEndian(bytes, data.Length);
        "iCCP"u8.CopyTo(bytes.AsSpan(4));
        data.CopyTo(bytes, 8);
        uint crc = 0xffffffff;
        foreach (var value in bytes.AsSpan(4, data.Length + 4))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320);
        }
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(bytes.Length - 4), ~crc);
        output.Write(bytes);
    }

    private static async Task<byte[]> ReadAsync(string path, ColorManagementMode mode)
    {
        await using var stream = File.OpenRead(path);
        var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform(), ExifOrientationMode.RespectExifOrientation, mode);
        return data.DetachPixelData();
    }

    private static void AssertClose(byte[] expected, byte[] actual, int tolerance)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) <= (i % 4 == 3 ? 0 : tolerance),
                $"Byte {i}: expected {expected[i]}, actual {actual[i]}.");
    }
}
