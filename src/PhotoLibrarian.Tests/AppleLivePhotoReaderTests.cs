// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PhotoLibrarian.Core.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class AppleLivePhotoReaderTests
{
    private const string FirstIdentifier = "123e4567-e89b-12d3-a456-426614174000";
    private const string OtherIdentifier = "123e4567-e89b-12d3-a456-426614174001";

    [Fact]
    public async Task PairsRenamedJpegAndMovByIdentifierAndValidatesWithoutWriting()
    {
        using var folder = new TemporaryFolder();
        string stillPath = folder.Write("renamed-still.jpeg", CreateStill(FirstIdentifier));
        string moviePath = folder.Write("unrelated-name.mov", CreateMovie(FirstIdentifier));
        byte[] stillHash = SHA256.HashData(File.ReadAllBytes(stillPath));
        byte[] movieHash = SHA256.HashData(File.ReadAllBytes(moviePath));
        DateTime stillWritten = File.GetLastWriteTimeUtc(stillPath);
        DateTime movieWritten = File.GetLastWriteTimeUtc(moviePath);

        AppleLivePhotoPair pair = await AppleLivePhotoReader.FindPairAsync(stillPath, TestContext.Current.CancellationToken);
        Assert.Equal(AppleLivePhotoPairState.Paired, pair.State);
        Assert.Equal(FirstIdentifier, pair.ContentIdentifier);
        Assert.Equal(moviePath, pair.MoviePath);
        Assert.Equal(stillPath, pair.StillPath);

        await AppleLivePhotoReader.ValidatePairAsync(pair, TestContext.Current.CancellationToken);

        Assert.Equal(stillHash, SHA256.HashData(File.ReadAllBytes(stillPath)));
        Assert.Equal(movieHash, SHA256.HashData(File.ReadAllBytes(moviePath)));
        Assert.Equal(stillWritten, File.GetLastWriteTimeUtc(stillPath));
        Assert.Equal(movieWritten, File.GetLastWriteTimeUtc(moviePath));
        Assert.Equal(2, Directory.GetFiles(folder.Path).Length);
    }

    [Fact]
    public async Task DoesNotPairByMatchingNameWhenIdentifiersDiffer()
    {
        using var folder = new TemporaryFolder();
        string stillPath = folder.Write("same-name.jpg", CreateStill(FirstIdentifier));
        folder.Write("same-name.mov", CreateMovie(OtherIdentifier));

        AppleLivePhotoPair pair = await AppleLivePhotoReader.FindPairAsync(stillPath, TestContext.Current.CancellationToken);

        Assert.Equal(AppleLivePhotoPairState.MissingMovie, pair.State);
        Assert.Null(pair.MoviePath);
    }

    [Fact]
    public async Task ReportsMissingTimingAndAmbiguousIdentifierMatches()
    {
        using (var missingTimingFolder = new TemporaryFolder())
        {
            string stillPath = missingTimingFolder.Write("still.jpg", CreateStill(FirstIdentifier));
            missingTimingFolder.Write("clip.mov", CreateMovie(FirstIdentifier, includeStillImageTime: false));
            AppleLivePhotoPair pair = await AppleLivePhotoReader.FindPairAsync(stillPath, TestContext.Current.CancellationToken);
            Assert.Equal(AppleLivePhotoPairState.MissingStillImageTime, pair.State);
        }

        using (var missingTimingValueFolder = new TemporaryFolder())
        {
            string stillPath = missingTimingValueFolder.Write("still.jpg", CreateStill(FirstIdentifier));
            missingTimingValueFolder.Write("clip.mov",
                CreateMovie(FirstIdentifier, includeStillImageTime: true, includeStillImageTimeItem: false));
            AppleLivePhotoPair pair = await AppleLivePhotoReader.FindPairAsync(
                stillPath, TestContext.Current.CancellationToken);
            Assert.Equal(AppleLivePhotoPairState.MissingStillImageTime, pair.State);
        }

        using (var ambiguousFolder = new TemporaryFolder())
        {
            string stillPath = ambiguousFolder.Write("still.jpg", CreateStill(FirstIdentifier));
            ambiguousFolder.Write("one.mov", CreateMovie(FirstIdentifier));
            ambiguousFolder.Write("two.mov", CreateMovie(FirstIdentifier, includeStillImageTime: false));
            AppleLivePhotoPair pair = await AppleLivePhotoReader.FindPairAsync(stillPath, TestContext.Current.CancellationToken);
            Assert.Equal(AppleLivePhotoPairState.AmbiguousMovies, pair.State);
        }
    }

    [Fact]
    public async Task RevalidationRejectsChangedMovie()
    {
        using var folder = new TemporaryFolder();
        string stillPath = folder.Write("still.jpg", CreateStill(FirstIdentifier));
        string moviePath = folder.Write("clip.mov", CreateMovie(FirstIdentifier));
        AppleLivePhotoPair pair = await AppleLivePhotoReader.FindPairAsync(stillPath, TestContext.Current.CancellationToken);
        Assert.Equal(AppleLivePhotoPairState.Paired, pair.State);

        File.WriteAllBytes(moviePath, CreateMovie(OtherIdentifier));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AppleLivePhotoReader.ValidatePairAsync(pair, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellationIsObservedBeforeScanning()
    {
        using var folder = new TemporaryFolder();
        string stillPath = folder.Write("still.jpg", CreateStill(FirstIdentifier));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => AppleLivePhotoReader.FindPairAsync(stillPath, cancellation.Token));
    }

    [Fact]
    public async Task ReportsUnverifiedHeicPairingInsteadOfSilentlyIgnoringIt()
    {
        using var folder = new TemporaryFolder();
        string stillPath = folder.Write("still.heic", [1, 2, 3]);

        AppleLivePhotoPair pair = await AppleLivePhotoReader.FindPairAsync(
            stillPath, TestContext.Current.CancellationToken);

        Assert.Equal(AppleLivePhotoPairState.UnsupportedStillFormat, pair.State);
    }

    [Fact]
    public async Task RejectsMalformedMakerNoteInsteadOfTreatingItAsAnIdentifier()
    {
        using var folder = new TemporaryFolder();
        string stillPath = folder.Write("malformed.jpg", CreateStill(FirstIdentifier, malformedMakerNote: true));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AppleLivePhotoReader.FindPairAsync(stillPath, TestContext.Current.CancellationToken));
    }

    private static byte[] CreateStill(string identifier, bool malformedMakerNote = false)
    {
        byte[] makerNote = CreateMakerNote(identifier, malformedMakerNote);
        const int makeOffset = 38;
        const int exifIfdOffset = 44;
        const int makerNoteOffset = 62;
        var tiff = new byte[makerNoteOffset + makerNote.Length];
        tiff[0] = (byte)'I';
        tiff[1] = (byte)'I';
        WriteUInt16LittleEndian(tiff, 2, 42);
        WriteUInt32LittleEndian(tiff, 4, 8);

        WriteUInt16LittleEndian(tiff, 8, 2);
        WriteIfdEntry(tiff, 10, 0x010f, 2, 6, makeOffset);
        WriteIfdEntry(tiff, 22, 0x8769, 4, 1, exifIfdOffset);
        WriteUInt32LittleEndian(tiff, 34, 0);
        Encoding.ASCII.GetBytes("Apple\0").CopyTo(tiff, makeOffset);

        WriteUInt16LittleEndian(tiff, exifIfdOffset, 1);
        WriteIfdEntry(tiff, exifIfdOffset + 2, 0x927c, 7, makerNote.Length, makerNoteOffset);
        WriteUInt32LittleEndian(tiff, exifIfdOffset + 14, 0);
        makerNote.CopyTo(tiff, makerNoteOffset);

        byte[] prefix = Encoding.ASCII.GetBytes("Exif\0\0");
        int app1Length = checked(2 + prefix.Length + tiff.Length);
        if (app1Length > ushort.MaxValue)
            throw new InvalidOperationException("The generated EXIF segment is too large.");

        using var jpeg = new MemoryStream();
        jpeg.Write([0xff, 0xd8, 0xff, 0xe1]);
        WriteUInt16BigEndian(jpeg, app1Length);
        jpeg.Write(prefix);
        jpeg.Write(tiff);
        jpeg.Write([0xff, 0xd9]);
        return jpeg.ToArray();
    }

    private static byte[] CreateMakerNote(string identifier, bool malformed)
    {
        byte[] text = Encoding.ASCII.GetBytes(identifier + "\0");
        var note = new byte[18 + text.Length];
        WriteUInt16LittleEndian(note, 0, 1);
        WriteIfdEntry(note, 2, 0x0011, 2, malformed ? 300 : text.Length, 18);
        WriteUInt32LittleEndian(note, 14, 0);
        text.CopyTo(note, 18);
        return note;
    }

    private static byte[] CreateMovie(
        string identifier, bool includeStillImageTime = true, bool includeStillImageTimeItem = true)
    {
        byte[] identifierKey = Encoding.UTF8.GetBytes("com.apple.quicktime.content.identifier");
        byte[] stillTimeKey = Encoding.UTF8.GetBytes("com.apple.quicktime.still-image-time");
        var keys = new List<byte>();
        keys.AddRange(new byte[4]);
        WriteUInt32BigEndian(keys, (uint)(includeStillImageTime ? 2 : 1));
        AddQuickTimeKey(keys, identifierKey);
        if (includeStillImageTime)
            AddQuickTimeKey(keys, stillTimeKey);

        byte[] contentItem = Atom("data",
            [.. UInt32BigEndian(1), .. new byte[4], .. Encoding.UTF8.GetBytes(identifier)]);
        byte[] contentListItem = Atom(1, contentItem);
        byte[] itemList = contentListItem;
        if (includeStillImageTime && includeStillImageTimeItem)
        {
            byte[] timing = Atom("data", [.. UInt32BigEndian(21), .. new byte[4], 0xff]);
            itemList = [.. contentListItem, .. Atom(2, timing)];
        }

        byte[] handler = Atom("hdlr", [.. new byte[8], .. Encoding.ASCII.GetBytes("mdta"), .. new byte[4]]);
        byte[] keyTable = Atom("keys", keys.ToArray());
        byte[] list = Atom("ilst", itemList);
        byte[] metadata = Atom("meta", [.. new byte[4], .. handler, .. keyTable, .. list]);
        byte[] movie = Atom("moov", metadata);
        byte[] fileType = Atom("ftyp",
            [.. Encoding.ASCII.GetBytes("qt  "), .. new byte[4], .. Encoding.ASCII.GetBytes("qt  ")]);
        byte[] mediaData = Atom("mdat", [1, 2, 3, 4]);
        return [.. fileType, .. movie, .. mediaData];
    }

    private static void AddQuickTimeKey(List<byte> keys, byte[] key)
    {
        WriteUInt32BigEndian(keys, checked((uint)(key.Length + 8)));
        keys.AddRange(Encoding.ASCII.GetBytes("mdta"));
        keys.AddRange(key);
    }

    private static byte[] Atom(string type, byte[] payload) =>
        Atom(BinaryPrimitives.ReadUInt32BigEndian(Encoding.ASCII.GetBytes(type)), payload);

    private static byte[] Atom(uint type, byte[] payload)
    {
        using var atom = new MemoryStream();
        WriteUInt32BigEndian(atom, checked((uint)(payload.Length + 8)));
        WriteUInt32BigEndian(atom, type);
        atom.Write(payload);
        return atom.ToArray();
    }

    private static byte[] UInt32BigEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static void WriteIfdEntry(byte[] bytes, int offset, ushort tag, ushort type, int count, int value)
    {
        WriteUInt16LittleEndian(bytes, offset, tag);
        WriteUInt16LittleEndian(bytes, offset + 2, type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 4), checked((uint)count));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 8), checked((uint)value));
    }

    private static void WriteUInt16LittleEndian(byte[] bytes, int offset, int value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), checked((ushort)value));

    private static void WriteUInt32LittleEndian(byte[] bytes, int offset, int value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), checked((uint)value));

    private static void WriteUInt16BigEndian(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, checked((ushort)value));
        stream.Write(bytes);
    }

    private static void WriteUInt32BigEndian(List<byte> bytes, uint value) =>
        bytes.AddRange(UInt32BigEndian(value));

    private static void WriteUInt32BigEndian(Stream stream, uint value) =>
        stream.Write(UInt32BigEndian(value));

    private sealed class TemporaryFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"AppleLivePhoto-{Guid.NewGuid():N}");

        public TemporaryFolder() => Directory.CreateDirectory(Path);

        public string Write(string name, byte[] bytes)
        {
            string path = System.IO.Path.Combine(Path, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public void Dispose()
        {
            foreach (string file in Directory.EnumerateFiles(Path))
                File.Delete(file);
            Directory.Delete(Path);
        }
    }
}
