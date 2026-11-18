// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Buffers.Binary;
using System.IO.Compression;

namespace PhotoLibrarian.Core.Services;

public static partial class MetadataWriterService
{
    /// <summary>
    /// Restores the existing source ICC on a private encoded bake, whose working pixels have
    /// already been converted back to that profile. The caller must not publish until this
    /// completes. This never selects a new profile, writes a sidecar, or modifies the source.
    /// </summary>
    public static async Task PreserveRasterColorProfileAsync(
        string encodedCopyPath, RasterColorProfile sourceProfile, CancellationToken cancellationToken = default)
    {
        if (!sourceProfile.IsSupported) throw new InvalidDataException(sourceProfile.Message);
        if (sourceProfile.Data is not { } profile) return;
        var extension = Path.GetExtension(encodedCopyPath).ToLowerInvariant();
        if (extension is ".tif" or ".tiff")
        {
            await PreserveTiffProfileAsync(encodedCopyPath, profile, cancellationToken);
        }
        else
        {
            using var output = new MemoryStream();
            await using (var input = File.OpenRead(encodedCopyPath))
            {
                if (extension is ".jpg" or ".jpeg")
                {
                    var header = new byte[2];
                    await input.ReadExactlyAsync(header, cancellationToken);
                    if (header[0] != 0xff || header[1] != 0xd8)
                        throw new InvalidDataException("The private JPEG bake has an invalid signature.");
                    output.Write(header);
                    const int chunkSize = 65533 - 14; // JPEG APP2 payload minus ICC identifier and sequence.
                    var count = ((long)profile.Length + chunkSize - 1) / chunkSize;
                    if (count > 255)
                        throw new InvalidDataException("The existing ICC profile exceeds JPEG's APP2 sequence capacity.");
                    for (var index = 0; index < count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var offset = index * chunkSize;
                        var data = new byte[14 + Math.Min(chunkSize, profile.Length - offset)];
                        "ICC_PROFILE\0"u8.CopyTo(data);
                        data[12] = (byte)(index + 1);
                        data[13] = (byte)count;
                        profile.AsSpan(offset, data.Length - 14).CopyTo(data.AsSpan(14));
                        SdrDerivativeMetadata.WriteJpegSegment(output, 0xe2, data);
                    }
                    await SdrDerivativeMetadata.CopyJpegSegmentsAsync(input, output, cancellationToken, preserveXmp: true);
                }
                else if (extension == ".png")
                {
                    var header = new byte[33];
                    await input.ReadExactlyAsync(header, cancellationToken);
                    if (!header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                        !header.AsSpan(12, 4).SequenceEqual("IHDR"u8))
                        throw new InvalidDataException("The private PNG bake has an invalid signature or IHDR.");
                    output.Write(header);
                    using var compressed = new MemoryStream();
                    compressed.Write("ICC\0\0"u8);
                    using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                        zlib.Write(profile);
                    SdrDerivativeMetadata.WritePngChunk(output, "iCCP", compressed.ToArray());
                    await SdrDerivativeMetadata.CopyPngChunksAsync(input, output, cancellationToken);
                }
                else
                    throw new InvalidDataException($"Preserving an embedded ICC profile is not supported for {extension}.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            await File.WriteAllBytesAsync(encodedCopyPath, output.ToArray(), cancellationToken);
        }

        var written = await Task.Run(() => RasterColorProfile.Read(encodedCopyPath), cancellationToken);
        if (!written.IsSupported || written.Data is null || !profile.AsSpan().SequenceEqual(written.Data))
            throw new IOException("The private bake's original ICC profile could not be verified. The source was not replaced.");
    }

    private static async Task PreserveTiffProfileAsync(string path, byte[] profile, CancellationToken token)
    {
        // Append a replacement first IFD and ICC payload. Existing pixel, EXIF/XMP and later-frame
        // offsets remain valid. TIFF ICC tag 34675 must be UNDEFINED, not a WinRT UInt8Array/BYTE.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var header = new byte[8];
        await stream.ReadExactlyAsync(header, token);
        var littleEndian = header[0] == 'I' && header[1] == 'I';
        var bigEndian = header[0] == 'M' && header[1] == 'M';
        ushort Read16(ReadOnlySpan<byte> data) => littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(data) : BinaryPrimitives.ReadUInt16BigEndian(data);
        uint Read32(ReadOnlySpan<byte> data) => littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(data) : BinaryPrimitives.ReadUInt32BigEndian(data);
        void Write16(Span<byte> data, ushort value)
        {
            if (littleEndian) BinaryPrimitives.WriteUInt16LittleEndian(data, value);
            else BinaryPrimitives.WriteUInt16BigEndian(data, value);
        }
        void Write32(Span<byte> data, uint value)
        {
            if (littleEndian) BinaryPrimitives.WriteUInt32LittleEndian(data, value);
            else BinaryPrimitives.WriteUInt32BigEndian(data, value);
        }
        if ((!littleEndian && !bigEndian) || Read16(header.AsSpan(2)) != 42)
            throw new InvalidDataException("ICC preservation requires a classic TIFF private bake.");
        var ifdOffset = Read32(header.AsSpan(4));
        if (ifdOffset < 8 || ifdOffset > stream.Length - 2)
            throw new InvalidDataException("The private TIFF bake has an invalid first IFD.");
        stream.Position = ifdOffset;
        var countBytes = new byte[2];
        await stream.ReadExactlyAsync(countBytes, token);
        var count = Read16(countBytes);
        var table = new byte[count * 12 + 4];
        if (table.Length > stream.Length - stream.Position)
            throw new InvalidDataException("The private TIFF bake has a truncated IFD.");
        await stream.ReadExactlyAsync(table, token);
        var entries = new List<byte[]>();
        for (var index = 0; index < count; index++)
        {
            var entry = table.AsSpan(index * 12, 12);
            if (Read16(entry) != 34675) entries.Add(entry.ToArray());
        }
        if (entries.Count == ushort.MaxValue)
            throw new InvalidDataException("The private TIFF bake has no capacity for an ICC tag.");
        var profileOffset = (stream.Length + 3) & ~3L;
        var newIfdOffset = (profileOffset + profile.Length + 1) & ~1L;
        var end = newIfdOffset + 2 + (entries.Count + 1) * 12L + 4;
        if (end > uint.MaxValue)
            throw new InvalidDataException("The private bake exceeds classic TIFF's offset capacity.");
        var icc = new byte[12];
        Write16(icc, 34675);
        Write16(icc.AsSpan(2), 7); // TIFF UNDEFINED.
        Write32(icc.AsSpan(4), checked((uint)profile.Length));
        Write32(icc.AsSpan(8), checked((uint)profileOffset));
        entries.Add(icc);
        entries.Sort((left, right) => Read16(left).CompareTo(Read16(right)));
        var replacement = new byte[2 + entries.Count * 12 + 4];
        Write16(replacement, checked((ushort)entries.Count));
        for (var index = 0; index < entries.Count; index++)
            entries[index].CopyTo(replacement, 2 + index * 12);
        table.AsSpan(count * 12, 4).CopyTo(replacement.AsSpan(replacement.Length - 4));
        stream.Position = profileOffset;
        await stream.WriteAsync(profile, token);
        stream.Position = newIfdOffset;
        await stream.WriteAsync(replacement, token);
        Write32(header.AsSpan(4), checked((uint)newIfdOffset));
        stream.Position = 0;
        await stream.WriteAsync(header, token);
        await stream.FlushAsync(token);
    }
}
