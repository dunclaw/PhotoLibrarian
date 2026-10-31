// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Buffers.Binary;
using System.Diagnostics;
using PhotoLibrarian.Core.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class RawDevelopmentTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-RawDevelopment-{Guid.NewGuid():N}");

    public RawDevelopmentTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData(false, 1, 56, 40)]
    [InlineData(false, 6, 40, 56)]
    [InlineData(true, 6, 520, 520)]
    public async Task DevelopsSyntheticCfaRawWithLinearHeadroomAndPreservesOriginal(
        bool xTrans,
        ushort orientation,
        uint expectedWidth,
        uint expectedHeight)
    {
        var path = Path.Combine(_directory, xTrans ? "synthetic-xtrans.dng" : "synthetic-bayer.dng");
        var original = SyntheticDngFixture.Create(xTrans, orientation);
        await File.WriteAllBytesAsync(path, original);

        var result = await RawDevelopmentService.DecodeAsync(path);
        var repeated = await RawDevelopmentService.DecodeAsync(path);

        Assert.Equal(expectedWidth, result.Metadata.OutputWidth);
        Assert.Equal(expectedHeight, result.Metadata.OutputHeight);
        Assert.Equal(orientation is 5 or 6 or 7 ? expectedHeight : expectedWidth, result.Metadata.VisibleWidth);
        Assert.Equal(orientation is 5 or 6 or 7 ? expectedWidth : expectedHeight, result.Metadata.VisibleHeight);
        Assert.Equal((uint)orientation, result.Metadata.SourceOrientation);
        Assert.Equal((int)(result.Metadata.OutputWidth * result.Metadata.OutputHeight * 3), result.RgbValues.Length);
        Assert.Equal("Linear ProPhoto RGB", RawDevelopmentService.WorkingColorSpace);
        Assert.Equal("D65 (LibRaw output_color=4)", RawDevelopmentService.WorkingWhitePoint);
        Assert.Equal(0.25f, result.Metadata.ReferenceExposureScale);
        Assert.Equal(result.RgbValues.ToArray(), repeated.RgbValues.ToArray());
        Assert.Contains(result.RgbValues.Span.ToArray(), value => value > 1.0f);
        Assert.All(result.RgbValues.ToArray(), value => Assert.True(float.IsFinite(value)));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ReportsCorruptAndUnsupportedInputInsteadOfUsingAnEmbeddedPreview()
    {
        var path = Path.Combine(_directory, "not-a-raw-image.bin");
        await File.WriteAllBytesAsync(path, [0x43, 0x4f, 0x50, 0x59, 0x00, 0xff]);

        var exception = await Assert.ThrowsAsync<RawDecodeException>(
            () => RawDevelopmentService.DecodeAsync(path));

        Assert.True(exception.Failure is RawDecodeFailure.Unsupported or RawDecodeFailure.Corrupt);
        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
    }

    [Fact]
    public async Task RejectsOversizedFilesBeforeAllocatingOrCallingNativeDecoder()
    {
        var path = Path.Combine(_directory, "oversized.dng");
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.SetLength(512L * 1024 * 1024 + 1);

        var exception = await Assert.ThrowsAsync<RawDecodeException>(
            () => RawDevelopmentService.DecodeAsync(path));

        Assert.Equal(RawDecodeFailure.TooLarge, exception.Failure);
    }

    [Fact]
    public async Task HonorsCancellationBeforeNativeDecode()
    {
        var path = Path.Combine(_directory, "synthetic-bayer.dng");
        await File.WriteAllBytesAsync(path, SyntheticDngFixture.Create(xTrans: false, orientation: 1));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RawDevelopmentService.DecodeAsync(path, cancellation.Token));
    }

    [Fact]
    public async Task ReportsSyntheticDecodeLatencyAndWorkingSetGrowth()
    {
        var path = Path.Combine(_directory, "synthetic-benchmark.dng");
        await File.WriteAllBytesAsync(
            path,
            SyntheticDngFixture.Create(xTrans: false, orientation: 1, width: 1024, height: 768));

        using var process = Process.GetCurrentProcess();
        var startingWorkingSet = process.WorkingSet64;
        var peakWorkingSet = startingWorkingSet;
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                process.Refresh();
                peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
                await Task.Delay(2);
            }
        });

        var stopwatch = Stopwatch.StartNew();
        var result = await RawDevelopmentService.DecodeAsync(path);
        stopwatch.Stop();
        sampling.Cancel();
        await sampler;
        process.Refresh();
        peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);

        Assert.Equal(1016u, result.Metadata.OutputWidth);
        Assert.Equal(760u, result.Metadata.OutputHeight);
        Console.WriteLine(
            $"Synthetic Bayer decode: {new FileInfo(path).Length} input bytes, " +
            $"{stopwatch.Elapsed.TotalMilliseconds:F1} ms, " +
            $"{Math.Max(0, peakWorkingSet - startingWorkingSet) / (1024d * 1024):F1} MiB peak working-set growth.");
    }

    internal static class SyntheticDngFixture
    {
        private sealed record Entry(ushort Tag, ushort Type, uint Count, byte[] Data);

        internal static byte[] Create(
            bool xTrans,
            ushort orientation,
            ushort width = 0,
            ushort height = 48)
        {
            if (width == 0)
                width = xTrans ? (ushort)528 : (ushort)64;

            // LibRaw's X-Trans demosaic requires at least one 512-pixel tile in each dimension.
            if (xTrans && height < 512)
                height = 528;

            var cfa = xTrans
                ? new byte[]
                {
                    1, 0, 1, 1, 2, 1,
                    2, 1, 2, 0, 1, 0,
                    1, 0, 1, 1, 2, 1,
                    1, 2, 1, 1, 0, 1,
                    0, 1, 0, 2, 1, 2,
                    1, 2, 1, 1, 0, 1
                }
                : [0, 1, 1, 2];
            var patternSize = xTrans ? new ushort[] { 6, 6 } : [2, 2];
            var rawPixels = CreateRawPixels(width, height, cfa, xTrans ? 6 : 2);
            var entries = new List<Entry>
            {
                Long(256, width),
                Long(257, height),
                Short(258, 16),
                Short(259, 1),
                Short(262, 32803),
                Long(273, 0),
                Short(274, orientation),
                Short(277, 1),
                Long(278, height),
                Long(279, checked((uint)rawPixels.Length)),
                Short(284, 1),
                Shorts(33421, patternSize),
                Bytes(33422, cfa),
                Bytes(50706, [1, 4, 0, 0]),
                Bytes(50707, [1, 3, 0, 0]),
                Ascii(50708, xTrans ? "Synthetic X-Trans fixture" : "Synthetic Bayer fixture"),
                Bytes(50710, [0, 1, 2]),
                Short(50711, 1),
                Shorts(50713, [1, 1]),
                Short(50714, 64),
                Long(50717, 16383),
                Longs(50829, [4, 4, checked((uint)(height - 4)), checked((uint)(width - 4))]),
                RationalMatrix(50721),
                Rationals(50728, [1, 1, 1]),
                Short(50778, 21)
            };

            entries.Sort((left, right) => left.Tag.CompareTo(right.Tag));
            var firstExternalOffset = 8 + 2 + entries.Count * 12 + 4;
            var externalOffsets = new Dictionary<ushort, uint>();
            var nextOffset = firstExternalOffset;
            foreach (var entry in entries)
            {
                if (entry.Data.Length <= 4)
                    continue;

                externalOffsets.Add(entry.Tag, checked((uint)nextOffset));
                nextOffset = Align(nextOffset + entry.Data.Length, 2);
            }

            var stripOffset = checked((uint)Align(nextOffset, 2));
            var file = new byte[checked((int)stripOffset + rawPixels.Length)];
            file[0] = (byte)'I';
            file[1] = (byte)'I';
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(2), 42);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 8);
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(8), checked((ushort)entries.Count));

            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                var entryOffset = 10 + index * 12;
                BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(entryOffset), entry.Tag);
                BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(entryOffset + 2), entry.Type);
                BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(entryOffset + 4), entry.Count);

                if (entry.Tag == 273)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(entryOffset + 8), stripOffset);
                }
                else if (entry.Data.Length <= 4)
                {
                    entry.Data.CopyTo(file, entryOffset + 8);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(entryOffset + 8), externalOffsets[entry.Tag]);
                    entry.Data.CopyTo(file, checked((int)externalOffsets[entry.Tag]));
                }
            }

            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(10 + entries.Count * 12), 0);
            rawPixels.CopyTo(file, checked((int)stripOffset));
            return file;
        }

        private static byte[] CreateRawPixels(ushort width, ushort height, byte[] cfa, int patternDimension)
        {
            var result = new byte[checked(width * height * 2)];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var color = cfa[(y % patternDimension) * patternDimension + x % patternDimension];
                    var value = color switch
                    {
                        0 => x > width * 2 / 3 ? 16383 : 2400 + x * 55 + y * 30,
                        1 => 900 + x * 90 + y * 65,
                        2 => 500 + x * 70 + y * 45,
                        _ => throw new InvalidDataException("The synthetic CFA pattern is invalid.")
                    };
                    var offset = checked((y * width + x) * 2);
                    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(offset), checked((ushort)Math.Min(value, 16383)));
                }
            }

            return result;
        }

        private static Entry Short(ushort tag, ushort value) =>
            new(tag, 3, 1, UInt16Bytes([value]));

        private static Entry Shorts(ushort tag, ushort[] values) =>
            new(tag, 3, checked((uint)values.Length), UInt16Bytes(values));

        private static Entry Long(ushort tag, uint value)
        {
            var data = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(data, value);
            return new Entry(tag, 4, 1, data);
        }

        private static Entry Longs(ushort tag, uint[] values)
        {
            var data = new byte[values.Length * 4];
            for (var index = 0; index < values.Length; index++)
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(index * 4), values[index]);
            return new Entry(tag, 4, checked((uint)values.Length), data);
        }

        private static Entry Bytes(ushort tag, byte[] values) =>
            new(tag, 1, checked((uint)values.Length), values);

        private static Entry Ascii(ushort tag, string value)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(value + '\0');
            return new Entry(tag, 2, checked((uint)bytes.Length), bytes);
        }

        private static Entry RationalMatrix(ushort tag)
        {
            var data = new byte[9 * 8];
            for (var index = 0; index < 9; index++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(
                    data.AsSpan(index * 8),
                    index % 4 == 0 ? 1 : 0);
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(index * 8 + 4), 1);
            }

            return new Entry(tag, 10, 9, data);
        }

        private static Entry Rationals(ushort tag, uint[] values)
        {
            var data = new byte[values.Length * 8];
            for (var index = 0; index < values.Length; index++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(index * 8), values[index]);
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(index * 8 + 4), 1);
            }

            return new Entry(tag, 5, checked((uint)values.Length), data);
        }

        private static byte[] UInt16Bytes(ushort[] values)
        {
            var data = new byte[values.Length * 2];
            for (var index = 0; index < values.Length; index++)
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(index * 2), values[index]);
            return data;
        }

        private static int Align(int value, int alignment) =>
            checked((value + alignment - 1) / alignment * alignment);
    }
}
