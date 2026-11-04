// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PhotoLibrarian.Core.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class MotionPhotoReaderTests
{
    private const string Namespaces = """
        xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
        xmlns:c="http://ns.google.com/photos/1.0/camera/"
        xmlns:container="http://ns.google.com/photos/1.0/container/"
        xmlns:item="http://ns.google.com/photos/1.0/container/item/"
        """;

    [Theory]
    [InlineData(false, 0, 0)]
    [InlineData(true, 0, 0)]
    [InlineData(true, 12, 0)]
    [InlineData(true, 12, 24)]
    public async Task LocatesLegacyModernAndGainMapLayouts(bool modern, int padding, int gainMapLength)
    {
        byte[] video = Mp4();
        using var fixture = Fixture(Metadata(video.Length, modern, padding, gainMapLength),
            video, padding + gainMapLength);
        var result = Assert.IsType<MotionPhotoInfo>(await MotionPhotoReader.ReadAsync(fixture));
        Assert.Equal(fixture.Length - video.Length, result.VideoOffset);
        Assert.Equal(video.Length, result.VideoLength);
        Assert.Equal(1234567L, result.PresentationTimestampUs);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("-1", null)]
    [InlineData("0", 0L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    public async Task RetainsTimestampWithoutConvertingOrSeeking(string? timestamp, long? expected)
    {
        string metadata = Metadata(Mp4().Length).Replace("c:MotionPhotoPresentationTimestampUs=\"1234567\"",
            timestamp is null ? "" : $"c:MotionPhotoPresentationTimestampUs=\"{timestamp}\"");
        using var fixture = Fixture(metadata, Mp4());
        Assert.Equal(expected, (await MotionPhotoReader.ReadAsync(fixture))!.PresentationTimestampUs);
    }

    [Fact]
    public async Task SupportsElementPropertiesAndIgnoresConflictingLegacyOffsetForModernFiles()
    {
        string metadata = Metadata(Mp4().Length)
            .Replace("c:MotionPhoto=\"1\"", "c:MicroVideoOffset=\"1\"")
            .Replace("<container:Directory>", "<c:MotionPhoto>1</c:MotionPhoto><container:Directory>");
        using var fixture = Fixture(metadata, Mp4());
        Assert.Equal(Mp4().Length, (await MotionPhotoReader.ReadAsync(fixture))!.VideoLength);
    }

    [Theory]
    [InlineData("c:MotionPhoto=\"1\"", "c:MotionPhoto=\"0\"")]
    [InlineData("c:MotionPhoto=\"1\"", "c:MotionPhoto=\"2\"")]
    [InlineData("c:MotionPhoto=\"1\"", "")]
    public async Task MotionFlagIsRequired(string from, string to)
    {
        using var fixture = Fixture(Metadata(Mp4().Length).Replace(from, to), Mp4());
        Assert.Null(await MotionPhotoReader.ReadAsync(fixture));
    }

    [Theory]
    [InlineData("c:MotionPhotoVersion=\"1\"", "c:MotionPhotoVersion=\"2\"")]
    [InlineData("c:MotionPhotoVersion=\"1\"", "")]
    [InlineData("item:Length=\"40\"", "item:Length=\"-1\"")]
    [InlineData("item:Length=\"40\"", "item:Length=\"0\"")]
    [InlineData("item:Length=\"40\"", "item:Length=\"9999999999999999999999\"")]
    [InlineData("item:Length=\"40\"", "item:Length=\"67108865\"")]
    [InlineData("item:Length=\"40\"", "item:Length=\"39\"")]
    [InlineData("item:Length=\"40\"", "item:Length=\"41\"")]
    [InlineData("item:Mime=\"video/mp4\"", "item:Mime=\"video/quicktime\"")]
    [InlineData("item:Semantic=\"MotionPhoto\"", "item:Semantic=\"Other\"")]
    [InlineData("c:MotionPhotoPresentationTimestampUs=\"1234567\"", "c:MotionPhotoPresentationTimestampUs=\"-2\"")]
    [InlineData("item:Padding=\"0\"", "item:Padding=\"999999999\"")]
    [InlineData("item:Length=\"0\"", "item:Length=\"5\"")]
    public async Task RejectsInvalidOrUnsupportedMetadata(string from, string to)
    {
        Assert.Equal(40, Mp4().Length);
        using var fixture = Fixture(Metadata(Mp4().Length).Replace(from, to), Mp4());
        await Assert.ThrowsAsync<InvalidDataException>(() => MotionPhotoReader.ReadAsync(fixture));
    }

    [Fact]
    public async Task RejectsDuplicateAndMalformedXmpWithoutResolvingEntities()
    {
        string metadata = Metadata(Mp4().Length)
            .Replace("<container:Directory>", "<c:MotionPhoto>1</c:MotionPhoto><container:Directory>");
        using var duplicate = Fixture(metadata, Mp4());
        await Assert.ThrowsAsync<InvalidDataException>(() => MotionPhotoReader.ReadAsync(duplicate));
        using var malformed = Fixture("<broken", Mp4());
        await Assert.ThrowsAsync<System.Xml.XmlException>(() => MotionPhotoReader.ReadAsync(malformed));
        using var entity = Fixture("<!DOCTYPE x [<!ENTITY file SYSTEM 'file:///not-read'>]><x>&file;</x>", Mp4());
        await Assert.ThrowsAsync<System.Xml.XmlException>(() => MotionPhotoReader.ReadAsync(entity));
    }

    [Theory]
    [InlineData("<container:Directory>", "<container:Directory><rdf:Seq />")]
    [InlineData("item:Length=\"40\" />", "item:Length=\"40\"><item:Length>1</item:Length></container:Item>")]
    [InlineData("<rdf:li>", "<rdf:other>")]
    public async Task AmbiguousDirectoriesAndItemsAreRejected(string from, string to)
    {
        string metadata = Metadata(Mp4().Length).Replace(from, to);
        if (from == "<rdf:li>") metadata = metadata.Replace("</rdf:li>", "</rdf:other>");
        using var fixture = Fixture(metadata, Mp4());
        await Assert.ThrowsAsync<InvalidDataException>(() => MotionPhotoReader.ReadAsync(fixture));
    }

    [Fact]
    public async Task OrdinaryImagesAndStandaloneVideoAreNotMotionPhotos()
    {
        using var jpeg = new MemoryStream([0xff, 0xd8, 0xff, 0xd9]);
        using var video = new MemoryStream(Mp4());
        using var heic = new MemoryStream(Encoding.ASCII.GetBytes("not a JPEG"));
        Assert.Null(await MotionPhotoReader.ReadAsync(jpeg));
        Assert.Null(await MotionPhotoReader.ReadAsync(video));
        Assert.Null(await MotionPhotoReader.ReadAsync(heic));
    }

    [Fact]
    public async Task RejectsTruncatedJpegAndMp4()
    {
        using var truncatedHeader = new MemoryStream([0xff, 0xd8, 0xff, 0xe1, 0, 20, 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => MotionPhotoReader.ReadAsync(truncatedHeader));
        byte[] video = Mp4();
        for (int length = 0; length < video.Length; length++)
        {
            using var fixture = Fixture(Metadata(video.Length), video[..length]);
            await Assert.ThrowsAsync<InvalidDataException>(() => MotionPhotoReader.ReadAsync(fixture));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RejectsBadBoxLengthsMissingMovieAndMissingMedia(int kind)
    {
        byte[] video = Mp4();
        if (kind == 0) BinaryPrimitives.WriteUInt32BigEndian(video, uint.MaxValue);
        if (kind == 1) Encoding.ASCII.GetBytes("free").CopyTo(video, 20);
        if (kind == 2) Encoding.ASCII.GetBytes("free").CopyTo(video, 32);
        using var fixture = Fixture(Metadata(video.Length), video);
        await Assert.ThrowsAsync<InvalidDataException>(() => MotionPhotoReader.ReadAsync(fixture));
    }

    [Fact]
    public async Task SupportsExtendedBoxSizeAndFinalBoxExtendingToEnd()
    {
        byte[] video = [.. Box("ftyp", new byte[8]), 0, 0, 0, 1, (byte)'m', (byte)'o', (byte)'o', (byte)'v',
            0, 0, 0, 0, 0, 0, 0, 20, 1, 2, 3, 4, .. Box("mdat", [1, 2, 3, 4])];
        BinaryPrimitives.WriteUInt32BigEndian(video.AsSpan(video.Length - 12), 0);
        using var fixture = Fixture(Metadata(video.Length), video);
        Assert.NotNull(await MotionPhotoReader.ReadAsync(fixture));
    }

    [Fact]
    public async Task ExtractionIsReadOnlyAndCreatesNoAdjacentArtifacts()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"MotionPhoto-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "generated.jpg");
        using var fixture = Fixture(Metadata(Mp4().Length), Mp4());
        byte[] original = fixture.ToArray();
        await File.WriteAllBytesAsync(path, original);
        var modified = File.GetLastWriteTimeUtc(path);
        try
        {
            var clip = await MotionPhotoReader.OpenVideoAsync(path);
            using (clip.Video)
            {
                Assert.False(clip.Video.CanWrite);
                Assert.Equal(Mp4(), clip.Video.ToArray());
            }
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                MotionPhotoReader.OpenVideoAsync(path, cancellation.Token));
            Assert.Equal(SHA256.HashData(original), SHA256.HashData(await File.ReadAllBytesAsync(path)));
            Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
            Assert.Single(Directory.GetFiles(folder));
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(folder);
        }
    }

    [Fact]
    public async Task CancellationDuringMetadataAndMp4ValidationStopsReads()
    {
        using var fixture = Fixture(Metadata(Mp4().Length), Mp4());
        using var cancellation = new CancellationTokenSource();
        using var stream = new CancelingStream(fixture.ToArray(), cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MotionPhotoReader.ReadAsync(stream, cancellation.Token));
        Assert.True(cancellation.IsCancellationRequested);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task EnforcesExactVideoByteLimit(int excess, bool accepted)
    {
        int length = MotionPhotoReader.MaximumVideoBytes + excess;
        using var header = Fixture(Metadata(length), Mp4());
        using var fixture = new MemoryStream();
        await header.CopyToAsync(fixture, TestContext.Current.CancellationToken);
        long offset = fixture.Length - Mp4().Length;
        fixture.SetLength(offset + length);
        fixture.Position = offset + 28;
        var size = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(size, (uint)(length - 28));
        fixture.Write(size);
        if (accepted)
            Assert.Equal(length, (await MotionPhotoReader.ReadAsync(fixture))!.VideoLength);
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => MotionPhotoReader.ReadAsync(fixture));
    }

    [Fact]
    public async Task ExcessiveJpegFillAndMp4BoxesAreBounded()
    {
        byte[] fill = new byte[4 * 1024 * 1024 + 16];
        Array.Fill(fill, (byte)0xff);
        fill[1] = 0xd8;
        using var jpeg = new MemoryStream(fill);
        await Assert.ThrowsAsync<InvalidDataException>(() => MotionPhotoReader.ReadAsync(jpeg));
        byte[] free = Box("free", []);
        using var boxes = new MemoryStream();
        boxes.Write(Mp4());
        for (int i = 0; i < 4096; i++) boxes.Write(free);
        using var excessive = Fixture(Metadata((int)boxes.Length), boxes.ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => MotionPhotoReader.ReadAsync(excessive));
    }

    private sealed class CancelingStream(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        private int _reads;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (++_reads == 5) cancellation.Cancel();
            return base.ReadAsync(buffer, token);
        }
    }

    private static string Metadata(int videoLength, bool modern = true, int padding = 0, int gainMapLength = 0)
    {
        if (!modern)
            return $"""<rdf:RDF {Namespaces}><rdf:Description c:MicroVideo="1" c:MicroVideoVersion="1" c:MicroVideoOffset="{videoLength}" c:MicroVideoPresentationTimestampUs="1234567" /></rdf:RDF>""";
        string gainMap = gainMapLength == 0 ? "" : $"""
            <rdf:li><container:Item item:Mime="image/jpeg" item:Semantic="GainMap" item:Length="{gainMapLength}" /></rdf:li>
            """;
        return $"""
            <rdf:RDF {Namespaces}><rdf:Description c:MotionPhoto="1" c:MotionPhotoVersion="1" c:MotionPhotoPresentationTimestampUs="1234567">
            <container:Directory><rdf:Seq>
            <rdf:li><container:Item item:Mime="image/jpeg" item:Semantic="Primary" item:Length="0" item:Padding="{padding}" /></rdf:li>
            {gainMap}
            <rdf:li><container:Item item:Mime="video/mp4" item:Semantic="MotionPhoto" item:Length="{videoLength}" /></rdf:li>
            </rdf:Seq></container:Directory></rdf:Description></rdf:RDF>
            """;
    }

    private static MemoryStream Fixture(string xml, byte[] video, int padding = 0)
    {
        byte[] xmp = Encoding.UTF8.GetBytes("http://ns.adobe.com/xap/1.0/\0" + xml);
        byte[] length = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)(xmp.Length + 2)));
        return new MemoryStream([0xff, 0xd8, 0xff, 0xe1, .. length, .. xmp,
            0xff, 0xda, 0, 2, 0xff, 0xd9, .. new byte[padding], .. video]);
    }

    private static byte[] Mp4() => [.. Box("ftyp", new byte[8]), .. Box("moov", [1, 2, 3, 4]),
        .. Box("mdat", [1, 2, 3, 4])];

    private static byte[] Box(string type, byte[] payload)
    {
        byte[] box = new byte[payload.Length + 8];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        payload.CopyTo(box, 8);
        return box;
    }
}
