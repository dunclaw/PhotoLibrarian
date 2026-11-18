// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Buffers.Binary;
using System.Text;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class TiffFamilyXmpReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"PhotoLibrarian-tiff-family-{Guid.NewGuid():N}");

    public TiffFamilyXmpReaderTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadsXmpFromEitherByteOrder(bool littleEndian)
    {
        var packet = Encoding.UTF8.GetBytes(
            "<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'>" +
            "<rdf:Description rdf:about='' xmlns:plib='http://ns.photolibrarian.app/1.0/' " +
            "plib:VirtualCopies='family'/></rdf:RDF></x:xmpmeta>");
        var data = CreateTiff(littleEndian, 1, packet.Length + 26);
        WriteEntry(data.AsSpan(10, 12), littleEndian, 700, 1, (uint)packet.Length, 26);
        packet.CopyTo(data, 26);
        var path = await WriteAsync(data);

        var (status, xmp) = TiffFamilyXmpReader.Read(path, CancellationToken.None);
        Assert.Equal(PhotoVersionRecipeStatus.Success, status);
        Assert.Equal("family", xmp!.GetPropertyString(MetadataWriterService.PhotoLibrarianNamespace, "plib:VirtualCopies"));
    }

    [Theory]
    [InlineData(330, 4)]
    [InlineData(65000, 13)]
    public async Task FindsXmpInSubdirectory(ushort tag, ushort type)
    {
        var packet = Encoding.UTF8.GetBytes(
            "<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'>" +
            "<rdf:Description rdf:about='' xmlns:plib='http://ns.photolibrarian.app/1.0/' " +
            "plib:VirtualCopies='family'/></rdf:RDF></x:xmpmeta>");
        var data = CreateTiff(true, 1, packet.Length + 44);
        WriteEntry(data.AsSpan(10, 12), true, tag, type, 1, 26);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(26, 2), 1);
        WriteEntry(data.AsSpan(28, 12), true, 700, 1, (uint)packet.Length, 44);
        packet.CopyTo(data, 44);
        var path = await WriteAsync(data);

        var (status, xmp) = TiffFamilyXmpReader.Read(path, CancellationToken.None);
        Assert.Equal(PhotoVersionRecipeStatus.Success, status);
        Assert.Equal("family", xmp!.GetPropertyString(MetadataWriterService.PhotoLibrarianNamespace, "plib:VirtualCopies"));
    }

    [Theory]
    [InlineData("bad-offset", PhotoVersionRecipeStatus.InvalidMetadata)]
    [InlineData("cycle", PhotoVersionRecipeStatus.InvalidMetadata)]
    [InlineData("duplicate", PhotoVersionRecipeStatus.Conflict)]
    [InlineData("big-tiff", PhotoVersionRecipeStatus.Unsupported)]
    public async Task RejectsUnsafeDirectoryLayouts(string layout, PhotoVersionRecipeStatus expected)
    {
        var data = CreateTiff(true, (ushort)(layout == "duplicate" ? 2 : 1), 256);
        switch (layout)
        {
            case "bad-offset":
                WriteEntry(data.AsSpan(10, 12), true, 700, 1, 100, 255);
                break;
            case "cycle":
                WriteEntry(data.AsSpan(10, 12), true, 330, 4, 1, 8);
                break;
            case "duplicate":
                var packet = Encoding.UTF8.GetBytes(
                    "<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'/></x:xmpmeta>");
                WriteEntry(data.AsSpan(10, 12), true, 700, 1, (uint)packet.Length, 38);
                WriteEntry(data.AsSpan(22, 12), true, 700, 1, (uint)packet.Length, 38);
                packet.CopyTo(data, 38);
                break;
            case "big-tiff":
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2, 2), 43);
                break;
        }
        var path = await WriteAsync(data);

        Assert.Equal(expected, TiffFamilyXmpReader.Read(path, CancellationToken.None).Status);
    }

    [Fact]
    public async Task CancelledReadDoesNotModifyFile()
    {
        var data = CreateTiff(true, 0, 14);
        var path = await WriteAsync(data);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => TiffFamilyXmpReader.Read(path, cancellation.Token));
        Assert.Equal(data, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task RejectsTruncatedDeclaredPacketBeforeAllocatingIt()
    {
        var data = CreateTiff(true, 1, 26);
        WriteEntry(data.AsSpan(10, 12), true, 700, 1, 1_000_000_000, 26);
        var path = await WriteAsync(data);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        var result = TiffFamilyXmpReader.Read(path, CancellationToken.None);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(PhotoVersionRecipeStatus.InvalidMetadata, result.Status);
        Assert.True(allocated < 1024 * 1024, $"Malformed TIFF read allocated {allocated} bytes.");
    }

    private static byte[] CreateTiff(bool littleEndian, ushort entries, int size)
    {
        var data = new byte[size];
        data[0] = data[1] = littleEndian ? (byte)'I' : (byte)'M';
        WriteUInt16(data.AsSpan(2, 2), littleEndian, 42);
        WriteUInt32(data.AsSpan(4, 4), littleEndian, 8);
        WriteUInt16(data.AsSpan(8, 2), littleEndian, entries);
        return data;
    }

    private static void WriteEntry(Span<byte> entry, bool littleEndian, ushort tag, ushort type, uint count, uint value)
    {
        WriteUInt16(entry[..2], littleEndian, tag);
        WriteUInt16(entry[2..4], littleEndian, type);
        WriteUInt32(entry[4..8], littleEndian, count);
        WriteUInt32(entry[8..12], littleEndian, value);
    }

    private static void WriteUInt16(Span<byte> bytes, bool littleEndian, ushort value)
    {
        if (littleEndian) BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        else BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
    }

    private static void WriteUInt32(Span<byte> bytes, bool littleEndian, uint value)
    {
        if (littleEndian) BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        else BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
    }

    private async Task<string> WriteAsync(byte[] data)
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.tif");
        await File.WriteAllBytesAsync(path, data);
        return path;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
