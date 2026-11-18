// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Buffers.Binary;
using PhotoLibrarian.Core.Models;
using XmpCore;

namespace PhotoLibrarian.Core.Services;

/// <summary>Reads TIFF XMP directories without buffering image strips or tiles.</summary>
internal static class TiffFamilyXmpReader
{
    internal static (PhotoVersionRecipeStatus Status, IXmpMeta? Xmp) Read(
        string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[8];
        if (!ReadAt(stream, 0, header))
            return (PhotoVersionRecipeStatus.InvalidMetadata, null);
        var littleEndian = header[..2].SequenceEqual("II"u8);
        if (!littleEndian && !header[..2].SequenceEqual("MM"u8))
            return (PhotoVersionRecipeStatus.InvalidMetadata, null);
        var magic = ReadUInt16(header[2..4], littleEndian);
        if (magic == 43)
            return (PhotoVersionRecipeStatus.Unsupported, null);
        if (magic != 42)
            return (PhotoVersionRecipeStatus.InvalidMetadata, null);

        var pending = new Queue<uint>();
        var discovered = new HashSet<uint>();
        var first = ReadUInt32(header[4..8], littleEndian);
        if (first == 0)
            return (PhotoVersionRecipeStatus.InvalidMetadata, null);
        pending.Enqueue(first);
        discovered.Add(first);
        IXmpMeta? xmp = null;
        var foundXmp = false;
        Span<byte> countBytes = stackalloc byte[2];
        Span<byte> entry = stackalloc byte[12];
        Span<byte> pointerBytes = stackalloc byte[4];
        while (pending.TryDequeue(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReadAt(stream, directory, countBytes))
                return (PhotoVersionRecipeStatus.InvalidMetadata, null);
            var count = ReadUInt16(countBytes, littleEndian);
            var end = (ulong)directory + 2UL + 12UL * count;
            if (end + 4UL > (ulong)stream.Length)
                return (PhotoVersionRecipeStatus.InvalidMetadata, null);
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReadAt(stream, (ulong)directory + 2UL + 12UL * (uint)index, entry))
                    return (PhotoVersionRecipeStatus.InvalidMetadata, null);
                var tag = ReadUInt16(entry[..2], littleEndian);
                var type = ReadUInt16(entry[2..4], littleEndian);
                var length = ReadUInt32(entry[4..8], littleEndian);
                var value = ReadUInt32(entry[8..12], littleEndian);
                if (tag == 700)
                {
                    if (foundXmp)
                        return (PhotoVersionRecipeStatus.Conflict, null);
                    foundXmp = true;
                    if (type is not (1 or 2 or 7) || length == 0)
                        return (PhotoVersionRecipeStatus.InvalidMetadata, null);
                    if (length > 4 && (ulong)value + length > (ulong)stream.Length)
                        return (PhotoVersionRecipeStatus.InvalidMetadata, null);
                    if (length > Array.MaxLength)
                        return (PhotoVersionRecipeStatus.Unsupported, null);
                    var packet = new byte[(int)length];
                    if (length <= 4)
                        entry.Slice(8, (int)length).CopyTo(packet);
                    else if (!ReadAt(stream, value, packet, cancellationToken))
                        return (PhotoVersionRecipeStatus.InvalidMetadata, null);
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using var data = new MemoryStream(packet, writable: false);
                        xmp = XmpMetaFactory.Parse(data);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    catch (XmpException)
                    {
                        return (PhotoVersionRecipeStatus.InvalidMetadata, null);
                    }
                }
                else if (type == 13 || tag is 330 or 34665 or 34853 or 40965)
                {
                    if (type is not (4 or 13) || length == 0)
                        return (PhotoVersionRecipeStatus.InvalidMetadata, null);
                    var bytes = (ulong)length * 4UL;
                    if (length == 1)
                    {
                        if (value != 0 && !TryEnqueue(value, pending, discovered))
                            return (PhotoVersionRecipeStatus.InvalidMetadata, null);
                    }
                    else
                    {
                        if ((ulong)value + bytes > (ulong)stream.Length)
                            return (PhotoVersionRecipeStatus.InvalidMetadata, null);
                        for (uint item = 0; item < length; item++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!ReadAt(stream, (ulong)value + 4UL * item, pointerBytes))
                                return (PhotoVersionRecipeStatus.InvalidMetadata, null);
                            var pointer = ReadUInt32(pointerBytes, littleEndian);
                            if (pointer != 0 && !TryEnqueue(pointer, pending, discovered))
                                return (PhotoVersionRecipeStatus.InvalidMetadata, null);
                        }
                    }
                }
            }
            if (!ReadAt(stream, end, pointerBytes))
                return (PhotoVersionRecipeStatus.InvalidMetadata, null);
            var next = ReadUInt32(pointerBytes, littleEndian);
            if (next != 0 && !TryEnqueue(next, pending, discovered))
                return (PhotoVersionRecipeStatus.InvalidMetadata, null);
        }
        return (PhotoVersionRecipeStatus.Success, xmp);
    }

    private static bool TryEnqueue(uint directory, Queue<uint> pending, HashSet<uint> discovered)
    {
        if (!discovered.Add(directory)) return false;
        pending.Enqueue(directory);
        return true;
    }

    private static bool ReadAt(
        FileStream stream, ulong offset, Span<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (offset > (ulong)stream.Length || (ulong)buffer.Length > (ulong)stream.Length - offset)
            return false;
        stream.Position = (long)offset;
        while (!buffer.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer[..Math.Min(buffer.Length, 81920)]);
            if (read == 0) return false;
            buffer = buffer[read..];
        }
        return true;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, bool littleEndian) =>
        littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, bool littleEndian) =>
        littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);
}
