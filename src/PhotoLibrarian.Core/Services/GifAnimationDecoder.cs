// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Runtime.InteropServices;

namespace PhotoLibrarian.Core.Services;

public readonly record struct AnimatedGifFrame(ReadOnlyMemory<byte> Pixels, TimeSpan Delay);

/// <summary>Reads and composes GIF frames into premultiplied BGRA pixels without retaining all frames.</summary>
public sealed class GifAnimationDecoder
{
    private const int MaximumFileBytes = 64 * 1024 * 1024;
    private const int MaximumCanvasBytes = 48 * 1024 * 1024;
    private const int MaximumFrames = 10_000;

    private readonly byte[] _encoded;
    private readonly List<Frame> _frames;
    private readonly byte[] _canvas;
    private readonly byte[]? _background;
    private int _nextFrame;
    private Frame? _previousFrame;
    private byte[]? _restorePixels;

    public int Width { get; }
    public int Height { get; }
    public int? LoopCount { get; }
    public int FrameCount => _frames.Count;

    private GifAnimationDecoder(
        byte[] encoded, int width, int height, List<Frame> frames,
        byte[]? background, int? loopCount)
    {
        _encoded = encoded;
        Width = width;
        Height = height;
        _frames = frames;
        _background = background;
        LoopCount = loopCount;
        _canvas = new byte[checked(width * height * 4)];
        Reset();
    }

    public static async Task<GifAnimationDecoder> OpenAsync(
        string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("The GIF file no longer exists.", path);
        if (info.Length <= 0 || info.Length > MaximumFileBytes)
            throw new InvalidDataException("This GIF exceeds the supported 64 MiB file limit.");

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > MaximumFileBytes)
            throw new InvalidDataException("This GIF exceeds the supported 64 MiB file limit.");
        var encoded = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(encoded, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Parse(encoded, cancellationToken);
    }

    public AnimatedGifFrame? ReadNextFrame(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_nextFrame >= _frames.Count)
            return null;

        var frame = _frames[_nextFrame++];
        ApplyPreviousDisposal();
        if (frame.Disposal == 3)
            _restorePixels = CopyRectangle(frame.Left, frame.Top, frame.Width, frame.Height);

        var indices = DecodeImage(frame, cancellationToken);
        Composite(frame, indices, cancellationToken);
        _previousFrame = frame;
        return new AnimatedGifFrame(_canvas, frame.Delay);
    }

    public bool ShouldRepeat(int completedRepeats) =>
        completedRepeats >= 0 && (LoopCount == 0 || LoopCount is > 0 && completedRepeats < LoopCount);

    public void Reset()
    {
        Array.Clear(_canvas);
        if (_frames.Count > 0 && !_frames[0].HasTransparency && _background is not null)
            FillRectangle(0, 0, Width, Height, _background);
        _nextFrame = 0;
        _previousFrame = null;
        _restorePixels = null;
    }

    private static GifAnimationDecoder Parse(byte[] data, CancellationToken cancellationToken)
    {
        var reader = new GifReader(data, cancellationToken);
        var signature = reader.ReadAscii(6);
        if (signature is not ("GIF87a" or "GIF89a"))
            throw new InvalidDataException("The file is not a supported GIF image.");

        int width = reader.ReadUInt16();
        int height = reader.ReadUInt16();
        byte screenFlags = reader.ReadByte();
        int backgroundIndex = reader.ReadByte();
        reader.ReadByte();
        if (width == 0 || height == 0 || (long)width * height * 4 > MaximumCanvasBytes)
            throw new InvalidDataException("This GIF exceeds the supported 48 MiB canvas limit.");
        byte[]? globalPalette = (screenFlags & 0x80) != 0
            ? reader.ReadPalette(1 << ((screenFlags & 0x07) + 1))
            : null;
        byte[]? background = globalPalette is not null && backgroundIndex < globalPalette.Length / 3
            ? ToBgra(globalPalette, backgroundIndex)
            : null;

        var frames = new List<Frame>();
        int? loopCount = null;
        var control = GraphicControl.Default;
        while (!reader.EndOfData)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte marker = reader.ReadByte();
            if (marker == 0x3B)
                break;
            if (marker == 0x21)
            {
                byte label = reader.ReadByte();
                switch (label)
                {
                    case 0xF9:
                        control = ReadGraphicControl(reader);
                        break;
                    case 0xFF:
                        loopCount = ReadApplicationExtension(reader) ?? loopCount;
                        break;
                    case 0x01:
                        reader.SkipExtensionSubBlocks(reader.ReadByte());
                        control = GraphicControl.Default;
                        break;
                    default:
                        reader.SkipExtensionSubBlocks();
                        break;
                }
                continue;
            }

            if (marker != 0x2C)
                throw new InvalidDataException("The GIF contains an unsupported block.");

            int left = reader.ReadUInt16();
            int top = reader.ReadUInt16();
            int frameWidth = reader.ReadUInt16();
            int frameHeight = reader.ReadUInt16();
            byte imageFlags = reader.ReadByte();
            if (frameWidth == 0 || frameHeight == 0 ||
                left + frameWidth > width || top + frameHeight > height ||
                (long)frameWidth * frameHeight * 4 > MaximumCanvasBytes)
                throw new InvalidDataException("The GIF contains an invalid or oversized frame.");

            byte[] palette = (imageFlags & 0x80) != 0
                ? reader.ReadPalette(1 << ((imageFlags & 0x07) + 1))
                : globalPalette ?? throw new InvalidDataException("A GIF frame has no color table.");
            int minimumCodeSize = reader.ReadByte();
            if (minimumCodeSize is < 2 or > 8)
                throw new InvalidDataException("The GIF uses an unsupported LZW code size.");
            int dataStart = reader.Position;
            int dataEnd = reader.SkipImageSubBlocks();
            frames.Add(new Frame(left, top, frameWidth, frameHeight, palette,
                (imageFlags & 0x40) != 0, minimumCodeSize, dataStart, dataEnd,
                control.Disposal, control.Delay, control.TransparentIndex));
            if (frames.Count > MaximumFrames)
                throw new InvalidDataException("This GIF has more than 10,000 frames.");
            control = GraphicControl.Default;
        }

        if (frames.Count == 0)
            throw new InvalidDataException("The GIF contains no image frames.");
        return new GifAnimationDecoder(data, width, height, frames, background, loopCount);
    }

    private static GraphicControl ReadGraphicControl(GifReader reader)
    {
        int blockSize = reader.ReadByte();
        if (blockSize != 4)
            throw new InvalidDataException("The GIF contains invalid frame timing metadata.");
        byte flags = reader.ReadByte();
        int delayHundredths = reader.ReadUInt16();
        int transparentIndex = reader.ReadByte();
        if (reader.ReadByte() != 0)
            throw new InvalidDataException("The GIF contains an invalid graphics control block.");
        TimeSpan delay = delayHundredths == 0
            ? TimeSpan.FromMilliseconds(100)
            : TimeSpan.FromMilliseconds(delayHundredths * 10);
        return new GraphicControl((flags >> 2) & 0x07, delay,
            (flags & 1) != 0 ? transparentIndex : null);
    }

    private static int? ReadApplicationExtension(GifReader reader)
    {
        int identifierLength = reader.ReadByte();
        string identifier = reader.ReadAscii(identifierLength);
        var appData = new List<byte>(16);
        while (true)
        {
            int blockLength = reader.ReadByte();
            if (blockLength == 0)
                break;
            for (int i = 0; i < blockLength; i++)
            {
                byte value = reader.ReadByte();
                if (appData.Count < 16)
                    appData.Add(value);
            }
        }

        if ((identifier is "NETSCAPE2.0" or "ANIMEXTS1.0") &&
            appData.Count >= 3 && appData[0] == 1)
            return appData[1] | appData[2] << 8;
        return null;
    }

    private byte[] DecodeImage(Frame frame, CancellationToken cancellationToken)
    {
        int expected = checked(frame.Width * frame.Height);
        var output = new byte[expected];
        var prefix = new short[4096];
        var suffix = new byte[4096];
        var stack = new byte[4097];
        int clearCode = 1 << frame.MinimumCodeSize;
        int endCode = clearCode + 1;
        int codeSize = frame.MinimumCodeSize + 1;
        int nextCode = endCode + 1;
        int oldCode = -1;
        int first = 0;
        int outputPosition = 0;
        var bits = new GifBitReader(_encoded, frame.DataStart, frame.DataEnd);

        int iterations = 0;
        while (true)
        {
            if ((iterations++ & 0x3FFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            int code = bits.ReadCode(codeSize);
            if (code < 0)
                throw new InvalidDataException("The GIF image data ended before its LZW end code.");
            if (code == clearCode)
            {
                codeSize = frame.MinimumCodeSize + 1;
                nextCode = endCode + 1;
                oldCode = -1;
                continue;
            }
            if (code == endCode)
                break;
            if (code > nextCode || code >= 4096)
                throw new InvalidDataException("The GIF contains invalid LZW data.");

            int incomingCode = code;
            int stackCount = 0;
            if (code == nextCode)
            {
                if (oldCode < 0)
                    throw new InvalidDataException("The GIF contains invalid LZW data.");
                stack[stackCount++] = (byte)first;
                code = oldCode;
            }

            while (code >= clearCode + 2)
            {
                if (code >= nextCode || stackCount >= stack.Length)
                    throw new InvalidDataException("The GIF contains a cyclic LZW dictionary.");
                stack[stackCount++] = suffix[code];
                code = prefix[code];
            }
            if (code >= clearCode)
                throw new InvalidDataException("The GIF contains an invalid LZW literal.");
            first = code;
            stack[stackCount++] = (byte)first;

            while (stackCount > 0)
            {
                if (outputPosition >= expected)
                    throw new InvalidDataException("The GIF frame has too many decoded pixels.");
                output[outputPosition++] = stack[--stackCount];
            }

            if (oldCode >= 0 && nextCode < 4096)
            {
                prefix[nextCode] = (short)oldCode;
                suffix[nextCode] = (byte)first;
                nextCode++;
                if (nextCode == (1 << codeSize) && codeSize < 12)
                    codeSize++;
            }
            oldCode = incomingCode;
        }

        if (outputPosition != expected)
            throw new InvalidDataException("The GIF frame has an incomplete pixel stream.");
        return output;
    }

    private void Composite(Frame frame, byte[] indices, CancellationToken cancellationToken)
    {
        int index = 0;
        foreach (int row in GetRows(frame.Height, frame.Interlaced))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int target = ((frame.Top + row) * Width + frame.Left) * 4;
            for (int x = 0; x < frame.Width; x++, index++)
            {
                int colorIndex = indices[index];
                if (frame.TransparentIndex == colorIndex)
                    continue;
                if (colorIndex >= frame.Palette.Length / 3)
                    throw new InvalidDataException("A GIF frame refers to a missing palette color.");
                int paletteOffset = colorIndex * 3;
                int pixel = target + x * 4;
                _canvas[pixel] = frame.Palette[paletteOffset + 2];
                _canvas[pixel + 1] = frame.Palette[paletteOffset + 1];
                _canvas[pixel + 2] = frame.Palette[paletteOffset];
                _canvas[pixel + 3] = 255;
            }
        }
    }

    private static IEnumerable<int> GetRows(int height, bool interlaced)
    {
        if (!interlaced)
        {
            for (int row = 0; row < height; row++)
                yield return row;
            yield break;
        }

        int[] starts = [0, 4, 2, 1];
        int[] steps = [8, 8, 4, 2];
        for (int pass = 0; pass < starts.Length; pass++)
            for (int row = starts[pass]; row < height; row += steps[pass])
                yield return row;
    }

    private void ApplyPreviousDisposal()
    {
        if (_previousFrame is not { } previous)
            return;
        if (previous.Disposal == 2)
        {
            byte[] color = previous.HasTransparency ? [0, 0, 0, 0] : _background ?? [0, 0, 0, 0];
            FillRectangle(previous.Left, previous.Top, previous.Width, previous.Height, color);
        }
        else if (previous.Disposal == 3 && _restorePixels is not null)
        {
            RestoreRectangle(previous.Left, previous.Top, previous.Width, previous.Height, _restorePixels);
        }
        _restorePixels = null;
    }

    private byte[] CopyRectangle(int left, int top, int width, int height)
    {
        var result = new byte[checked(width * height * 4)];
        for (int row = 0; row < height; row++)
            Buffer.BlockCopy(_canvas, ((top + row) * Width + left) * 4,
                result, row * width * 4, width * 4);
        return result;
    }

    private void RestoreRectangle(int left, int top, int width, int height, byte[] pixels)
    {
        for (int row = 0; row < height; row++)
            Buffer.BlockCopy(pixels, row * width * 4,
                _canvas, ((top + row) * Width + left) * 4, width * 4);
    }

    private void FillRectangle(int left, int top, int width, int height, byte[] color)
    {
        uint packedColor = color[0] | (uint)color[1] << 8 | (uint)color[2] << 16 | (uint)color[3] << 24;
        for (int row = 0; row < height; row++)
        {
            int start = ((top + row) * Width + left) * 4;
            MemoryMarshal.Cast<byte, uint>(_canvas.AsSpan(start, width * 4)).Fill(packedColor);
        }
    }

    private static byte[] ToBgra(byte[] palette, int index)
    {
        int offset = index * 3;
        return [palette[offset + 2], palette[offset + 1], palette[offset], 255];
    }

    private sealed record Frame(
        int Left, int Top, int Width, int Height, byte[] Palette, bool Interlaced,
        int MinimumCodeSize, int DataStart, int DataEnd, int Disposal, TimeSpan Delay,
        int? TransparentIndex)
    {
        public bool HasTransparency => TransparentIndex.HasValue;
    }

    private readonly record struct GraphicControl(int Disposal, TimeSpan Delay, int? TransparentIndex)
    {
        public static GraphicControl Default => new(0, TimeSpan.FromMilliseconds(100), null);
    }

    private sealed class GifReader(byte[] data, CancellationToken cancellationToken)
    {
        private readonly byte[] _data = data;
        private readonly CancellationToken _cancellationToken = cancellationToken;
        public int Position { get; private set; }
        public bool EndOfData => Position >= _data.Length;

        public byte ReadByte()
        {
            EnsureAvailable(1);
            return _data[Position++];
        }

        public int ReadUInt16()
        {
            EnsureAvailable(2);
            int value = _data[Position] | _data[Position + 1] << 8;
            Position += 2;
            return value;
        }

        public string ReadAscii(int length)
        {
            EnsureAvailable(length);
            string value = System.Text.Encoding.ASCII.GetString(_data, Position, length);
            Position += length;
            return value;
        }

        public byte[] ReadPalette(int count)
        {
            int length = checked(count * 3);
            EnsureAvailable(length);
            var palette = new byte[length];
            Buffer.BlockCopy(_data, Position, palette, 0, length);
            Position += length;
            return palette;
        }

        public void SkipExtensionSubBlocks(int firstBlockLength = -1)
        {
            int size = firstBlockLength < 0 ? ReadByte() : firstBlockLength;
            while (size != 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                EnsureAvailable(size);
                Position += size;
                size = ReadByte();
            }
        }

        public int SkipImageSubBlocks()
        {
            int start = Position;
            SkipExtensionSubBlocks(-1);
            return Position;
        }

        private void EnsureAvailable(int count)
        {
            if (count < 0 || Position < 0 || count > _data.Length - Position)
                throw new InvalidDataException("The GIF file is truncated.");
        }
    }

    private sealed class GifBitReader(byte[] data, int position, int end)
    {
        private readonly byte[] _data = data;
        private int _position = position;
        private readonly int _end = end;
        private int _blockRemaining;
        private int _currentByte;
        private int _bitsRemaining;
        private bool _terminated;

        public int ReadCode(int bitCount)
        {
            int value = 0;
            for (int bit = 0; bit < bitCount; bit++)
            {
                int next = ReadBit();
                if (next < 0)
                    return -1;
                value |= next << bit;
            }
            return value;
        }

        private int ReadBit()
        {
            if (_bitsRemaining == 0)
            {
                if (_blockRemaining == 0)
                {
                    if (_terminated || _position >= _end)
                        return -1;
                    _blockRemaining = _data[_position++];
                    if (_blockRemaining == 0)
                    {
                        _terminated = true;
                        return -1;
                    }
                    if (_blockRemaining > _end - _position)
                        throw new InvalidDataException("The GIF image data is truncated.");
                }
                _currentByte = _data[_position++];
                _blockRemaining--;
                _bitsRemaining = 8;
            }
            int bit = _currentByte & 1;
            _currentByte >>= 1;
            _bitsRemaining--;
            return bit;
        }
    }
}
