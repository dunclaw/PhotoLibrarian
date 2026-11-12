// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class TileCodecFeasibilityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "PhotoLibrarian-Tiles-" + Guid.NewGuid().ToString("N"));
    public TileCodecFeasibilityTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void RendersBeyondDeviceTextureLimitAndEncodesWithoutManagedFullFrame()
    {
        using var device = new CanvasDevice();
        var width = checked(device.MaximumBitmapSizeInPixels + 17);
        const int height = 131;
        var source = new PatternSource(width, height);
        var scratchPath = Path.Combine(_directory, "render.bgra");
        using (var scratch = new BgraScratchSurface(scratchPath, width, height))
        {
            var result = TileCodecFeasibility.CopyThroughGpu(source, scratch, device, 128, CancellationToken.None);
            Assert.Equal(TileJobStatus.Completed, result.Status);
            Assert.True(result.Tiles > 2);
            Assert.Equal(128L * 128 * 8, result.PeakManagedPixelBytes);
            Assert.True(result.PeakTexturePixelBytes <= 128L * 128 * 8);
            Assert.Equal((long)width * height * 4, scratch.Length);
            AssertSamples(scratch, source);
            var output = Path.Combine(_directory, "wide.png");
            Assert.Equal(TileJobStatus.Completed,
                TileCodecFeasibility.Encode(scratch, output, ProbeImageFormat.Png, CancellationToken.None));
            using var reopened = new WicTileSource(output);
            Assert.Equal(width, reopened.Width);
            Assert.Equal(height, reopened.Height);
            AssertSamples(reopened, source);
            Console.WriteLine($"Texture max {device.MaximumBitmapSizeInPixels}; rendered {width}x{height}; {result}");
        }
        Assert.False(File.Exists(scratchPath));
    }

    [Theory]
    [InlineData(0, 127, 79)]
    [InlineData(1, 127, 79)]
    [InlineData(2, 127, 79)]
    [InlineData(3, 127, 79)]
    [InlineData(4, 127, 79)]
    public void WicFormatsReadArbitraryRegionsAfterIncrementalEncoding(int format, int width, int height)
    {
        var path = Path.Combine(_directory, "encoded.image");
        var source = new PatternSource(width, height);
        Assert.Equal(TileJobStatus.Completed, TileCodecFeasibility.Encode(source, path, (ProbeImageFormat)format, CancellationToken.None));
        if (format is (int)ProbeImageFormat.Png or (int)ProbeImageFormat.InterlacedPng)
        {
            using var file = File.OpenRead(path);
            file.Position = 28; // PNG IHDR interlace method.
            Assert.Equal(format == (int)ProbeImageFormat.InterlacedPng ? 1 : 0, file.ReadByte());
        }
        using var decoder = new WicTileSource(path);
        Assert.Equal(width, decoder.Width);
        Assert.Equal(height, decoder.Height);
        // JPEG is lossy; separate lossless parity from its successful incremental decode/encode.
        if (format != (int)ProbeImageFormat.Jpeg) AssertSamples(decoder, source);
        var pixels = new byte[17 * 11 * 4];
        decoder.Read(new(31, 23, 17, 11), pixels, CancellationToken.None);
        Assert.Contains(pixels, value => value != 0);
    }

    [Fact]
    public void PreservesPremultipliedAlphaThroughGpuScratchAndPng()
    {
        using var device = new CanvasDevice();
        var source = new PatternSource(263, 137, alpha: true);
        using var scratch = new BgraScratchSurface(Path.Combine(_directory, "alpha.bgra"), source.Width, source.Height);
        Assert.Equal(TileJobStatus.Completed,
            TileCodecFeasibility.CopyThroughGpu(source, scratch, device, 64, CancellationToken.None).Status);
        AssertSamples(scratch, source);
        var output = Path.Combine(_directory, "alpha.png");
        TileCodecFeasibility.Encode(scratch, output, ProbeImageFormat.Png, CancellationToken.None);
        using var decoder = new WicTileSource(output);
        AssertSamples(decoder, source, tolerance: 1);
    }

    [Fact]
    public void CancelsActiveGpuAndEncodeWorkWithoutPublishingOrOverwriting()
    {
        using var device = new CanvasDevice();
        var source = new PatternSource(1025, 517);
        var scratchPath = Path.Combine(_directory, "cancel.bgra");
        using (var scratch = new BgraScratchSurface(scratchPath, source.Width, source.Height))
        using (var cancellation = new CancellationTokenSource())
        {
            var result = TileCodecFeasibility.CopyThroughGpu(source, scratch, device, 64,
                cancellation.Token, _ => cancellation.Cancel());
            Assert.Equal(TileJobStatus.Cancelled, result.Status);
            Assert.Equal(1, result.Tiles);
        }
        Assert.False(File.Exists(scratchPath));
        var output = Path.Combine(_directory, "cancel.png");
        using (var cancellation = new CancellationTokenSource())
        {
            Assert.Equal(TileJobStatus.Cancelled, TileCodecFeasibility.Encode(source, output,
                ProbeImageFormat.Png, cancellation.Token, _ => cancellation.Cancel()));
        }
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        using (var cancellation = new CancellationTokenSource())
        {
            Assert.Equal(TileJobStatus.Cancelled, TileCodecFeasibility.Encode(source, output,
                ProbeImageFormat.Png, cancellation.Token, beforePublish: cancellation.Cancel));
        }
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        File.WriteAllText(output, "existing");
        Assert.Throws<IOException>(() => TileCodecFeasibility.Encode(source, output,
            ProbeImageFormat.Png, CancellationToken.None));
        Assert.Equal("existing", File.ReadAllText(output));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void ScratchOffsetsCanExceedSingleArrayAddressability()
    {
        const int width = 65537, height = 16385;
        var path = Path.Combine(_directory, "sparse.bgra");
        var tile = new PixelTile(width - 13, height - 7, 13, 7);
        var pixels = new byte[tile.ByteCount];
        new PatternSource(width, height).Read(tile, pixels, CancellationToken.None);
        using (var scratch = new BgraScratchSurface(path, width, height, sparse: true))
        {
            Assert.True(scratch.Length > uint.MaxValue);
            scratch.Write(tile, pixels, CancellationToken.None);
            var reopened = new byte[pixels.Length];
            scratch.Read(tile, reopened, CancellationToken.None);
            Assert.Equal(pixels, reopened);
            Assert.Equal(scratch.Length, new FileInfo(path).Length);
        }
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void CancellationAfterNonCooperativeSourceReadCannotPublishATile()
    {
        using var device = new CanvasDevice();
        using var cancellation = new CancellationTokenSource();
        var source = new CancellingSource(new PatternSource(263, 137), cancellation);
        var sink = new CountingSink();
        var result = TileCodecFeasibility.CopyThroughGpu(source, sink, device, 64, cancellation.Token);
        Assert.Equal(TileJobStatus.Cancelled, result.Status);
        Assert.Equal(0, sink.Count);
    }

    [Fact]
    public void FixedWorkUnitsDoNotGrowWithImageArea()
    {
        using var device = new CanvasDevice();
        TileJobResult Render(int width, int height)
        {
            var source = new PatternSource(width, height);
            return TileCodecFeasibility.CopyThroughGpu(source, new VerifyingSink(source), device, 256, CancellationToken.None);
        }
        var small = Render(1024, 1024);
        var large = Render(2048, 2048);
        Assert.Equal(small.PeakManagedPixelBytes, large.PeakManagedPixelBytes);
        Assert.Equal(small.PeakTexturePixelBytes, large.PeakTexturePixelBytes);
        Assert.Equal(small.Tiles * 4, large.Tiles);
    }

    [Theory]
    [InlineData(8256, 5504)]
    [InlineData(12288, 8192)]
    public void FullResolutionSyntheticPhotosUseTheSameTileEnvelope(int width, int height)
    {
        using var device = new CanvasDevice();
        var source = new PatternSource(width, height);
        using var scratch = new BgraScratchSurface(Path.Combine(_directory, "full-resolution.bgra"), width, height);
        var result = TileCodecFeasibility.CopyThroughGpu(source, new VerifyingSink(source, scratch), device,
            512, TestContext.Current.CancellationToken);
        Assert.Equal(TileJobStatus.Completed, result.Status);
        Assert.Equal(((width + 511L) / 512) * ((height + 511L) / 512), result.Tiles);
        Assert.True(result.PeakManagedPixelBytes <= 2L * 512 * 512 * 4);
        Assert.True(result.PeakTexturePixelBytes <= 2L * 512 * 512 * 4);
        var path = Path.Combine(_directory, "full-resolution.png");
        Assert.Equal(TileJobStatus.Completed, TileCodecFeasibility.Encode(scratch, path,
            ProbeImageFormat.Png, TestContext.Current.CancellationToken));
        using var reopened = new WicTileSource(path);
        Assert.Equal(width, reopened.Width);
        Assert.Equal(height, reopened.Height);
        AssertSamples(reopened, source);
        Console.WriteLine($"Synthetic full-resolution {width}x{height}: {result}");
    }

    [Fact]
    public void DecodesSequentiallyToDiskBeforeRandomTileAccessAndCancelsAfterCodecReads()
    {
        var pattern = new PatternSource(769, 389);
        var path = Path.Combine(_directory, "source.png");
        TileCodecFeasibility.Encode(pattern, path, ProbeImageFormat.Png, CancellationToken.None);
        using var source = new WicTileSource(path);
        using (var scratch = new BgraScratchSurface(Path.Combine(_directory, "decoded.bgra"), source.Width, source.Height))
        {
            Assert.Equal(TileJobStatus.Completed, TileCodecFeasibility.Materialize(source, scratch, CancellationToken.None));
            AssertSamples(scratch, pattern);
            using var device = new CanvasDevice();
            Assert.Equal(TileJobStatus.Completed,
                TileCodecFeasibility.CopyThroughGpu(scratch, new VerifyingSink(pattern), device, 128, CancellationToken.None).Status);
        }
        using var cancelled = new CancellationTokenSource();
        using (var scratch = new BgraScratchSurface(Path.Combine(_directory, "cancelled-decode.bgra"), source.Width, source.Height))
        {
            Assert.Equal(TileJobStatus.Cancelled, TileCodecFeasibility.Materialize(source, scratch,
                cancelled.Token, _ => cancelled.Cancel()));
        }
        Assert.False(File.Exists(Path.Combine(_directory, "cancelled-decode.bgra")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task MeasuresRealCodecPrivateAllocationsInFreshProcesses(int format)
    {
        foreach (var size in new[] { (Width: 2048, Height: 1536), (Width: 8192, Height: 6144) })
        {
            var path = Path.Combine(_directory, $"memory-{format}-{size.Width}.image");
            foreach (var operation in new[] { "encode", "decode", "decode-tiles", "decode-reverse" })
            {
                var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "PhotoLibrarian.CodecMemoryProbe.exe"))
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
                };
                foreach (var argument in new[] { operation, path, size.Width.ToString(), size.Height.ToString(), format.ToString() })
                    start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
                var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
                try { await process.WaitForExitAsync(TestContext.Current.CancellationToken); }
                finally
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(CancellationToken.None);
                    }
                }
                var output = await stdout;
                Assert.True(process.ExitCode == 0, $"{operation}: {output} {await stderr}");
                using var data = JsonDocument.Parse(output);
                Assert.Equal(0, data.RootElement.GetProperty("hresult").GetInt32());
                Assert.True(data.RootElement.GetProperty("buffer").GetInt64() <= 262144);
                Console.WriteLine($"WIC {(ProbeImageFormat)format} {operation} {size.Width}x{size.Height}: {output.Trim()}");
            }
        }
    }

    private static void AssertSamples(IBgraTileSource actual, PatternSource expected, int tolerance = 0)
    {
        foreach (var tile in new[]
        {
            new PixelTile(0, 0, Math.Min(17, actual.Width), Math.Min(11, actual.Height)),
            new PixelTile(actual.Width - 13, actual.Height - 7, 13, 7),
            new PixelTile(actual.Width / 2, actual.Height / 2, 9, 5)
        })
        {
            var pixels = new byte[tile.ByteCount];
            var reference = new byte[tile.ByteCount];
            actual.Read(tile, pixels, CancellationToken.None);
            expected.Read(tile, reference, CancellationToken.None);
            for (var i = 0; i < pixels.Length; i++)
                Assert.InRange(Math.Abs(pixels[i] - reference[i]), 0, i % 4 == 3 ? 0 : tolerance);
        }
    }

    private sealed class PatternSource(int width, int height, bool alpha = false) : IBgraTileSource
    {
        public int Width => width;
        public int Height => height;
        public void Read(PixelTile tile, byte[] destination, CancellationToken token)
        {
            tile.Validate(width, height, destination.Length);
            for (var y = 0; y < tile.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (var x = 0; x < tile.Width; x++)
                {
                    var offset = y * tile.Stride + x * 4;
                    var opacity = alpha ? (tile.X + x) % 256 : 255;
                    destination[offset] = (byte)(((tile.X + x) % 256) * opacity / 255);
                    destination[offset + 1] = (byte)(((tile.Y + y) % 256) * opacity / 255);
                    destination[offset + 2] = (byte)(((tile.X + x + tile.Y + y) % 256) * opacity / 255);
                    destination[offset + 3] = (byte)opacity;
                }
            }
        }
    }

    private sealed class VerifyingSink(PatternSource source, IBgraTileSink? next = null) : IBgraTileSink
    {
        private byte[] _expected = [];
        public void Write(PixelTile tile, byte[] pixels, CancellationToken token)
        {
            if (_expected.Length < tile.ByteCount) _expected = new byte[tile.ByteCount];
            source.Read(tile, _expected, token);
            Assert.True(pixels.AsSpan(0, tile.ByteCount).SequenceEqual(_expected.AsSpan(0, tile.ByteCount)));
            next?.Write(tile, pixels, token);
        }
    }

    private sealed class CancellingSource(PatternSource source, CancellationTokenSource cancellation) : IBgraTileSource
    {
        public int Width => source.Width;
        public int Height => source.Height;
        public void Read(PixelTile tile, byte[] destination, CancellationToken token)
        {
            source.Read(tile, destination, token);
            cancellation.Cancel();
        }
    }

    private sealed class CountingSink : IBgraTileSink
    {
        internal int Count { get; private set; }
        public void Write(PixelTile tile, byte[] pixels, CancellationToken token) => Count++;
    }
}
