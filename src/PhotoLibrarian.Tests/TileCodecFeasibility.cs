// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Graphics.Canvas;
using Microsoft.Win32.SafeHandles;
using Windows.Graphics.DirectX;

namespace PhotoLibrarian.Tests;

// P1 experiment only. Stored-pixel coordinates, premultiplied BGRA8, no metadata or edit graph.
internal readonly record struct PixelTile(int X, int Y, int Width, int Height)
{
    internal int Stride => checked(Width * 4);
    internal int ByteCount => checked(Stride * Height);

    internal void Validate(int width, int height, int capacity)
    {
        if (X < 0 || Y < 0 || Width <= 0 || Height <= 0 ||
            (long)X + Width > width || (long)Y + Height > height || capacity < ByteCount)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Tile must fit the source and caller-owned buffer.");
    }
}

internal interface IBgraTileSource
{
    int Width { get; }
    int Height { get; }
    void Read(PixelTile tile, byte[] destination, CancellationToken token);
}

internal interface IBgraTileSink
{
    void Write(PixelTile tile, byte[] pixels, CancellationToken token);
}

internal sealed class BgraScratchSurface : IBgraTileSource, IBgraTileSink, IDisposable
{
    private readonly FileStream _file;
    internal long Length => checked((long)Width * Height * 4);
    public int Width { get; }
    public int Height { get; }

    internal BgraScratchSurface(string path, int width, int height, bool sparse = false)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        _file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            1, FileOptions.RandomAccess | FileOptions.DeleteOnClose);
        if (sparse && !SetSparse(_file.SafeFileHandle, 0x000900c4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            _file.Dispose();
            throw new System.ComponentModel.Win32Exception(error, "Could not create sparse scratch fixture.");
        }
    }

    public void Read(PixelTile tile, byte[] destination, CancellationToken token)
    {
        tile.Validate(Width, Height, destination.Length);
        for (var row = 0; row < tile.Height; row++)
        {
            token.ThrowIfCancellationRequested();
            var bytes = destination.AsSpan(row * tile.Stride, tile.Stride);
            var offset = Offset(tile.X, tile.Y + row);
            while (!bytes.IsEmpty)
            {
                var read = RandomAccess.Read(_file.SafeFileHandle, bytes, offset);
                if (read == 0) throw new EndOfStreamException("Scratch surface contains an unwritten region.");
                bytes = bytes[read..];
                offset += read;
            }
        }
    }

    public void Write(PixelTile tile, byte[] pixels, CancellationToken token)
    {
        tile.Validate(Width, Height, pixels.Length);
        for (var row = 0; row < tile.Height; row++)
        {
            token.ThrowIfCancellationRequested();
            RandomAccess.Write(_file.SafeFileHandle, pixels.AsSpan(row * tile.Stride, tile.Stride),
                Offset(tile.X, tile.Y + row));
        }
    }

    private long Offset(int x, int y) => checked(((long)y * Width + x) * 4);
    public void Dispose() => _file.Dispose();

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSparse(SafeFileHandle file, uint code, IntPtr input, uint inputSize,
        IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
}

internal sealed class WicTileSource : IBgraTileSource, IDisposable
{
    private readonly CodecSourceHandle _handle;
    public int Width { get; }
    public int Height { get; }

    internal WicTileSource(string path)
    {
        CodecNative.Check(CodecNative.OpenSource(path, out _handle, out var width, out var height));
        try
        {
            Width = checked((int)width);
            Height = checked((int)height);
        }
        catch
        {
            _handle.Dispose();
            throw;
        }
    }

    public void Read(PixelTile tile, byte[] destination, CancellationToken token)
    {
        tile.Validate(Width, Height, destination.Length);
        token.ThrowIfCancellationRequested();
        CodecNative.Check(CodecNative.Read(_handle, tile.X, tile.Y, tile.Width, tile.Height,
            (uint)tile.Stride, (uint)destination.Length, destination));
        token.ThrowIfCancellationRequested();
    }

    public void Dispose() => _handle.Dispose();
}

internal enum ProbeImageFormat { Png, Jpeg, Tiff, Bmp, InterlacedPng }
internal enum TileJobStatus { Completed, Cancelled }
internal sealed record TileJobResult(TileJobStatus Status, long Tiles, long PeakManagedPixelBytes,
    long PeakTexturePixelBytes, TimeSpan LongestRead, TimeSpan LongestDraw);

internal static class TileCodecFeasibility
{
    internal static TileJobStatus Materialize(WicTileSource source, BgraScratchSurface scratch,
        CancellationToken token, Action<int>? rowsRead = null)
    {
        var stride = checked(source.Width * 4);
        var rows = Math.Max(1, Math.Min(source.Height, 262144 / stride));
        var buffer = new byte[checked(stride * rows)];
        try
        {
            for (long y = 0; y < source.Height; y += rows)
            {
                var tile = new PixelTile(0, (int)y, source.Width, (int)Math.Min(rows, source.Height - y));
                source.Read(tile, buffer, token);
                scratch.Write(tile, buffer, token);
                rowsRead?.Invoke((int)(y + tile.Height));
            }
            token.ThrowIfCancellationRequested();
            return TileJobStatus.Completed;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return TileJobStatus.Cancelled;
        }
    }

    // Work-unit sizes, not input dimensions or output resolution limits.
    internal static TileJobResult CopyThroughGpu(IBgraTileSource source, IBgraTileSink sink,
        CanvasDevice device, int tileEdge, CancellationToken token, Action<long>? tileCompleted = null)
    {
        if (tileEdge <= 0 || tileEdge > device.MaximumBitmapSizeInPixels)
            throw new ArgumentOutOfRangeException(nameof(tileEdge));
        var buffer = new byte[checked(tileEdge * tileEdge * 4)];
        var readback = new byte[buffer.Length];
        long tiles = 0, peakTextures = 0;
        var longestRead = TimeSpan.Zero;
        var longestDraw = TimeSpan.Zero;
        try
        {
            for (long y = 0; y < source.Height; y += tileEdge)
            for (long x = 0; x < source.Width; x += tileEdge)
            {
                token.ThrowIfCancellationRequested();
                var tile = new PixelTile((int)x, (int)y,
                    (int)Math.Min(tileEdge, source.Width - x), (int)Math.Min(tileEdge, source.Height - y));
                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                source.Read(tile, buffer, token);
                longestRead = Max(longestRead, System.Diagnostics.Stopwatch.GetElapsedTime(start));
                token.ThrowIfCancellationRequested();
                start = System.Diagnostics.Stopwatch.GetTimestamp();
                using var bitmap = CanvasBitmap.CreateFromBytes(device, buffer.AsBuffer(0, tile.ByteCount),
                    tile.Width, tile.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96,
                    CanvasAlphaMode.Premultiplied);
                using var target = new CanvasRenderTarget(device, tile.Width, tile.Height, 96);
                using (var draw = target.CreateDrawingSession())
                {
                    draw.Clear(Microsoft.UI.Colors.Transparent);
                    draw.DrawImage(bitmap);
                }
                target.GetPixelBytes(readback.AsBuffer(0, tile.ByteCount), 0, 0, tile.Width, tile.Height);
                longestDraw = Max(longestDraw, System.Diagnostics.Stopwatch.GetElapsedTime(start));
                peakTextures = Math.Max(peakTextures, (long)tile.ByteCount * 2);
                token.ThrowIfCancellationRequested();
                sink.Write(tile, readback, token);
                tiles++;
                tileCompleted?.Invoke(tiles);
            }
            token.ThrowIfCancellationRequested();
            return Result(TileJobStatus.Completed);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return Result(TileJobStatus.Cancelled);
        }

        TileJobResult Result(TileJobStatus status) =>
            new(status, tiles, (long)buffer.Length + readback.Length, peakTextures, longestRead, longestDraw);
    }

    internal static TileJobStatus Encode(IBgraTileSource source, string destination,
        ProbeImageFormat format, CancellationToken token, Action<int>? rowsWritten = null,
        Action? beforePublish = null)
    {
        var staging = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            token.ThrowIfCancellationRequested();
            var stride = checked(source.Width * 4);
            var rows = Math.Max(1, Math.Min(source.Height, 262144 / stride));
            var buffer = new byte[checked(stride * rows)];
            CodecNative.Check(CodecNative.OpenSink(staging, (uint)format,
                (uint)source.Width, (uint)source.Height, out var handle));
            using (handle)
            {
                for (long y = 0; y < source.Height; y += rows)
                {
                    var count = (int)Math.Min(rows, source.Height - y);
                    source.Read(new(0, (int)y, source.Width, count), buffer, token);
                    token.ThrowIfCancellationRequested();
                    CodecNative.Check(CodecNative.WriteRows(handle, (uint)count,
                        (uint)stride, (uint)buffer.Length, buffer));
                    rowsWritten?.Invoke((int)(y + count));
                }
                token.ThrowIfCancellationRequested();
                CodecNative.Check(CodecNative.Commit(handle));
            }
            beforePublish?.Invoke();
            token.ThrowIfCancellationRequested();
            File.Move(staging, destination, overwrite: false);
            return TileJobStatus.Completed;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return TileJobStatus.Cancelled;
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;
}

internal sealed class CodecSourceHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private CodecSourceHandle() : base(true) { }
    protected override bool ReleaseHandle() { CodecNative.CloseSource(handle); return true; }
}

internal sealed class CodecSinkHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private CodecSinkHandle() : base(true) { }
    protected override bool ReleaseHandle() { CodecNative.CloseSink(handle); return true; }
}

internal static class CodecNative
{
    private const string Library = "PhotoLibrarian.TileCodecProbe.dll";
    internal static void Check(int result) => Marshal.ThrowExceptionForHR(result);

    [DllImport(Library, EntryPoint = "tile_open_source", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int OpenSource(string path, out CodecSourceHandle handle, out uint width, out uint height);
    [DllImport(Library, EntryPoint = "tile_read", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Read(CodecSourceHandle handle, int x, int y, int width, int height,
        uint stride, uint capacity, [Out] byte[] pixels);
    [DllImport(Library, EntryPoint = "tile_close_source", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void CloseSource(IntPtr handle);
    [DllImport(Library, EntryPoint = "tile_open_sink", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int OpenSink(string path, uint format, uint width, uint height, out CodecSinkHandle handle);
    [DllImport(Library, EntryPoint = "tile_write_rows", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int WriteRows(CodecSinkHandle handle, uint rows, uint stride, uint capacity, [In] byte[] pixels);
    [DllImport(Library, EntryPoint = "tile_commit", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Commit(CodecSinkHandle handle);
    [DllImport(Library, EntryPoint = "tile_close_sink", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void CloseSink(IntPtr handle);
}
