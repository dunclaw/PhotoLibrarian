// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Runtime.InteropServices.WindowsRuntime;
using System.Buffers.Binary;
using System.Text;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using Windows.Graphics.Imaging;
using XmpCore;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class EmbeddedMetadataCompatibilityTests : IDisposable
{
    private const string XmpPacket =
        "<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF " +
        "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
        "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" " +
        "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
        "xmlns:crs=\"http://ns.adobe.com/camera-raw-settings/1.0/\" " +
        "xmlns:mwg-rs=\"http://www.metadataworkinggroup.com/schemas/regions/\" " +
        "xmlns:stDim=\"http://ns.adobe.com/xap/1.0/sType/Dimensions#\" " +
        "xmlns:stArea=\"http://ns.adobe.com/xmp/sType/Area#\" " +
        "xmp:Rating=\"3\" crs:Exposure2012=\"+0.50\">" +
        "<dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">Generated caf\u00E9 caption</rdf:li>" +
        "<rdf:li xml:lang=\"fr-FR\">L\u00E9gende g\u00E9n\u00E9r\u00E9e</rdf:li></rdf:Alt></dc:description>" +
        "<mwg-rs:Regions rdf:parseType=\"Resource\">" +
        "<mwg-rs:AppliedToDimensions stDim:w=\"32\" stDim:h=\"24\" stDim:unit=\"pixel\"/>" +
        "<mwg-rs:RegionList><rdf:Bag>" +
        "<rdf:li rdf:parseType=\"Resource\"><mwg-rs:Name>Generated person</mwg-rs:Name>" +
        "<mwg-rs:Type>Face</mwg-rs:Type><mwg-rs:Area stArea:x=\"0.5\" stArea:y=\"0.5\" " +
        "stArea:w=\"0.25\" stArea:h=\"0.25\" stArea:unit=\"normalized\"/></rdf:li>" +
        "</rdf:Bag></mwg-rs:RegionList></mwg-rs:Regions>" +
        "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"PhotoLibrarian-embedded-compatibility-{Guid.NewGuid():N}");

    public EmbeddedMetadataCompatibilityTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".tif")]
    public async Task LabelSetAndClearAreEmbeddedWithoutChangingPixels(string extension)
    {
        var path = Path.Combine(_directory, "label" + extension);
        await WicTestImage.CreateAsync(path, 32, 24, jpeg: extension == ".jpg");
        var pixels = await ReadPixelsAsync(path);
        var reader = new MetadataReaderService();
        await MetadataWriterService.WriteColorLabelAsync(path, "Blue");
        Assert.Equal("Blue", reader.ReadMetadata(path).ColorLabel);
        await MetadataWriterService.WriteColorLabelAsync(path, null);
        Assert.True(string.IsNullOrEmpty(reader.ReadMetadata(path).ColorLabel));
        Assert.Equal(pixels, await ReadPixelsAsync(path));
        Assert.False(File.Exists(path + ".xmp"));
        Assert.False(File.Exists(Path.ChangeExtension(path, ".xmp")));
    }

    [Fact]
    public async Task LeadingUtf8Preamble_FailsAtInPlaceEncoderCreationBeforeSettingProperties()
    {
        var path = await CreateFixtureAsync(leadingPreamble: true);
        var before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var packet = ReadXmp(path);
        Assert.Equal("3", packet.GetPropertyString("http://ns.adobe.com/xap/1.0/", "Rating"));

        using (var file = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        using (var stream = file.AsRandomAccessStream())
        {
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var error = await Assert.ThrowsAsync<ArgumentException>(async () =>
                await BitmapEncoder.CreateForInPlacePropertyEncodingAsync(decoder));
            Assert.Equal(unchecked((int)0x80070057), error.HResult);
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false, "tags")]
    [InlineData(true, "tags")]
    [InlineData(true, "rating")]
    [InlineData(true, "caption")]
    [InlineData(true, "flag")]
    [InlineData(true, "date")]
    [InlineData(true, "gps")]
    [InlineData(false, "label")]
    [InlineData(true, "label")]
    public async Task ValidPacket_WritesEmbeddedWithoutFallbackAndPreservesOtherMetadata(
        bool leadingPreamble, string property)
    {
        var path = await CreateFixtureAsync(leadingPreamble);
        var beforePixels = await ReadPixelsAsync(path);
        var beforeScan = ReadScan(path);
        var notices = new List<MetadataWriteFallbackEventArgs>();
        void OnFallback(object? sender, MetadataWriteFallbackEventArgs e)
        {
            if (e.ImagePath == path) notices.Add(e);
        }

        MetadataWriterService.SidecarFallback += OnFallback;
        try
        {
            for (var edit = 0; edit < 2; edit++)
            {
                await WritePropertyAsync(path, property);
                var metadata = ReadXmp(path);
                Assert.Equal("+0.50", metadata.GetPropertyString(
                    "http://ns.adobe.com/camera-raw-settings/1.0/", "Exposure2012"));
                Assert.Equal("Generated person", metadata.GetStructField(
                    "http://www.metadataworkinggroup.com/schemas/regions/",
                    "Regions/mwg-rs:RegionList[1]",
                    "http://www.metadataworkinggroup.com/schemas/regions/", "Name")?.Value);
                Assert.Equal("Face", metadata.GetStructField(
                    "http://www.metadataworkinggroup.com/schemas/regions/",
                    "Regions/mwg-rs:RegionList[1]",
                    "http://www.metadataworkinggroup.com/schemas/regions/", "Type")?.Value);
                Assert.Equal("0.25", metadata.GetStructField(
                    "http://www.metadataworkinggroup.com/schemas/regions/",
                    "Regions/mwg-rs:RegionList[1]/mwg-rs:Area",
                    "http://ns.adobe.com/xmp/sType/Area#", "w")?.Value);
                if (property != "caption")
                {
                    Assert.Equal("Generated caf\u00E9 caption", metadata.GetLocalizedText(
                        "http://purl.org/dc/elements/1.1/", "description", null, "x-default")?.Value);
                    Assert.Equal("L\u00E9gende g\u00E9n\u00E9r\u00E9e", metadata.GetLocalizedText(
                        "http://purl.org/dc/elements/1.1/", "description", null, "fr-FR")?.Value);
                }
                AssertProperty(path, property, metadata);
                Assert.Equal(beforeScan, ReadScan(path));
                Assert.Equal(beforePixels, await ReadPixelsAsync(path));
            }
            Assert.Empty(notices);
            Assert.False(File.Exists(path + ".xmp"));
            Assert.False(File.Exists(Path.ChangeExtension(path, ".xmp")));
        }
        finally
        {
            MetadataWriterService.SidecarFallback -= OnFallback;
        }
    }

    [Fact]
    public async Task LargeJpegXmp_UsesExtendedPacketsAndPreservesUnrelatedProperties()
    {
        const string testNamespace = "https://photolibrarian.test/xmp/1.0/";
        const string testPrefix = "pltest";
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(testNamespace, testPrefix);

        var path = Path.Combine(_directory, "extended-xmp.jpg");
        await WicTestImage.CreateAsync(path, 32, 24, jpeg: true);
        var metadata = XmpMetaFactory.Create();
        metadata.SetProperty(testNamespace, $"{testPrefix}:Payload", new string('x', 80_000));
        var packet = XmpMetaFactory.SerializeToString(
            metadata, new XmpCore.Options.SerializeOptions()).TrimStart('\uFEFF');

        Assert.True(FaceMetadataStore.TryWriteEmbedded(path, packet));
        await ImageEditService.WritePendingRecipeAsync(path, new EditParameters { Exposure = 0.4 });

        var updated = FaceMetadataStore.ReadEmbeddedXmp(path);
        Assert.NotNull(updated);
        Assert.Equal(new string('x', 80_000),
            updated.GetPropertyString(testNamespace, $"{testPrefix}:Payload"));
        Assert.Equal(0.4, ImageEditService.ReadEditParameters(path).Exposure);
        Assert.False(File.Exists(path + ".xmp"));

        await ImageEditService.ClearPendingEditParametersAsync(path);

        var cleared = FaceMetadataStore.ReadEmbeddedXmp(path);
        Assert.NotNull(cleared);
        Assert.Equal(new string('x', 80_000),
            cleared.GetPropertyString(testNamespace, $"{testPrefix}:Payload"));
        Assert.False(ImageEditService.ReadEditParameters(path).HasAdjustments);
    }

    [Fact]
    public async Task ExtendedJpegReadPreservesStandardPacketPropertiesAndQualifiers()
    {
        const string ns = "https://photolibrarian.test/extended-standard/";
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(ns, "plstandard");
        var path = Path.Combine(_directory, "split-packet.jpg");
        await WicTestImage.CreateAsync(path, 32, 24, jpeg: true);
        var extended = XmpMetaFactory.Create();
        extended.SetProperty(ns, "plstandard:Payload", new string('x', 80_000));
        extended.SetProperty(ns, "plstandard:Shared", "same");
        Assert.True(FaceMetadataStore.TryWriteEmbedded(path,
            XmpMetaFactory.SerializeToString(extended, new XmpCore.Options.SerializeOptions()).TrimStart('\uFEFF')));
        Assert.NotNull(FaceMetadataStore.ReadEmbeddedXmp(path));
        RewriteStandardPacket(path, standard =>
        {
            standard.SetProperty(ns, "plstandard:Shared", "same");
            standard.SetProperty(ns, "plstandard:StandardOnly", "preserve");
            standard.SetQualifier(ns, "plstandard:StandardOnly", ns, "plstandard:Meaning", "qualified");
            standard.AppendArrayItem(ns, "plstandard:Ordered",
                new XmpCore.Options.PropertyOptions { IsArrayOrdered = true }, "first", null);
            standard.AppendArrayItem(ns, "plstandard:Ordered", "second");
        });

        Assert.Equal(PhotoVersionRecipeStatus.Success,
            FaceMetadataStore.TryReadCompleteJpegXmp(path, out var merged));
        Assert.NotNull(merged);
        Assert.Equal("preserve", merged.GetPropertyString(ns, "plstandard:StandardOnly"));
        Assert.Equal("qualified", merged.GetQualifier(ns, "plstandard:StandardOnly", ns, "plstandard:Meaning")!.Value);
        Assert.Equal("first", merged.GetArrayItem(ns, "plstandard:Ordered", 1)!.Value);
        Assert.Equal("second", merged.GetArrayItem(ns, "plstandard:Ordered", 2)!.Value);
        Assert.Equal(new string('x', 80_000), merged.GetPropertyString(ns, "plstandard:Payload"));
        Assert.True(FaceMetadataStore.TryWriteEmbedded(path,
            XmpMetaFactory.SerializeToString(merged, new XmpCore.Options.SerializeOptions()).TrimStart('\uFEFF')));
        var reloaded = FaceMetadataStore.ReadEmbeddedXmp(path)!;
        Assert.Equal("qualified", reloaded.GetQualifier(ns, "plstandard:StandardOnly", ns, "plstandard:Meaning")!.Value);
        Assert.Equal("second", reloaded.GetArrayItem(ns, "plstandard:Ordered", 2)!.Value);
    }

    [Fact]
    public async Task ExtendedJpegReadRejectsConflictingPropertiesWithoutWriting()
    {
        const string ns = "https://photolibrarian.test/extended-conflict/";
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(ns, "plconflict");
        var path = Path.Combine(_directory, "conflicting-packet.jpg");
        await WicTestImage.CreateAsync(path, 32, 24, jpeg: true);
        var extended = XmpMetaFactory.Create();
        extended.SetProperty(ns, "plconflict:Payload", new string('x', 80_000));
        extended.SetProperty(ns, "plconflict:Value", "extended");
        Assert.True(FaceMetadataStore.TryWriteEmbedded(path,
            XmpMetaFactory.SerializeToString(extended, new XmpCore.Options.SerializeOptions()).TrimStart('\uFEFF')));
        Assert.NotNull(FaceMetadataStore.ReadEmbeddedXmp(path));
        RewriteStandardPacket(path, standard => standard.SetProperty(ns, "plconflict:Value", "standard"));
        var before = await File.ReadAllBytesAsync(path);

        Assert.Equal(PhotoVersionRecipeStatus.Conflict,
            FaceMetadataStore.TryReadCompleteJpegXmp(path, out var merged));
        Assert.Null(merged);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ExtendedJpegChunksCanBeReadOutOfFileOrder()
    {
        const string ns = "https://photolibrarian.test/extended-reordered/";
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(ns, "plreordered");
        var path = Path.Combine(_directory, "reordered.jpg");
        await WicTestImage.CreateAsync(path, 32, 24, jpeg: true);
        var xmp = XmpMetaFactory.Create();
        xmp.SetProperty(ns, "plreordered:Payload", new string('x', 80_000));
        Assert.True(FaceMetadataStore.TryWriteEmbedded(path,
            XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions()).TrimStart('\uFEFF')));
        var bytes = await File.ReadAllBytesAsync(path);
        var header = Encoding.ASCII.GetBytes("http://ns.adobe.com/xmp/extension/\0");
        var chunks = new List<(int Start, int Length)>();
        for (var offset = 2; offset + 4 <= bytes.Length && bytes[offset] == 0xff;)
        {
            var marker = bytes[offset + 1];
            if (marker is 0xda or 0xd9) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 2, 2)) + 2;
            if (marker == 0xe1 && bytes.AsSpan(offset + 4, length - 4).StartsWith(header))
                chunks.Add((offset, length));
            offset += length;
        }
        Assert.Equal(2, chunks.Count);
        Assert.Equal(chunks[0].Start + chunks[0].Length, chunks[1].Start);
        using (var output = new MemoryStream())
        {
            output.Write(bytes, 0, chunks[0].Start);
            output.Write(bytes, chunks[1].Start, chunks[1].Length);
            output.Write(bytes, chunks[0].Start, chunks[0].Length);
            var end = chunks[1].Start + chunks[1].Length;
            output.Write(bytes, end, bytes.Length - end);
            await File.WriteAllBytesAsync(path, output.ToArray());
        }

        Assert.Equal(PhotoVersionRecipeStatus.Success,
            FaceMetadataStore.TryReadCompleteJpegXmp(path, out var merged));
        Assert.Equal(new string('x', 80_000), merged!.GetPropertyString(ns, "plreordered:Payload"));
    }

    [Fact]
    public async Task IncompleteExtendedJpegDoesNotAllocateClaimedPacketLength()
    {
        const string ns = "https://photolibrarian.test/extended-length/";
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(ns, "pllength");
        var path = Path.Combine(_directory, "incomplete.jpg");
        await WicTestImage.CreateAsync(path, 32, 24, jpeg: true);
        var xmp = XmpMetaFactory.Create();
        xmp.SetProperty(ns, "pllength:Payload", new string('x', 80_000));
        Assert.True(FaceMetadataStore.TryWriteEmbedded(path,
            XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions()).TrimStart('\uFEFF')));

        var bytes = await File.ReadAllBytesAsync(path);
        var header = Encoding.ASCII.GetBytes("http://ns.adobe.com/xmp/extension/\0");
        var chunks = 0;
        for (var offset = 2; offset + 4 <= bytes.Length && bytes[offset] == 0xff;)
        {
            var marker = bytes[offset + 1];
            if (marker is 0xda or 0xd9) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 2, 2)) + 2;
            if (marker == 0xe1 && bytes.AsSpan(offset + 4, length - 4).StartsWith(header))
            {
                BinaryPrimitives.WriteUInt32BigEndian(
                    bytes.AsSpan(offset + 4 + header.Length + 32, 4), 128 * 1024 * 1024);
                chunks++;
            }
            offset += length;
        }
        Assert.Equal(2, chunks);
        await File.WriteAllBytesAsync(path, bytes);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var status = FaceMetadataStore.TryReadCompleteJpegXmp(path, out var metadata);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(PhotoVersionRecipeStatus.InvalidMetadata, status);
        Assert.Null(metadata);
        Assert.True(allocated < 8 * 1024 * 1024, $"Incomplete JPEG metadata allocated {allocated} bytes.");
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task JpegMetadataReadAndWritePreserveMarkerFillBytes()
    {
        var path = Path.Combine(_directory, "marker-fill.jpg");
        await WicTestImage.CreateAsync(path, 32, 24, jpeg: true);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(0xff, bytes[2]);
        using (var output = new MemoryStream())
        {
            output.Write(bytes, 0, 2);
            output.WriteByte(0xff);
            output.Write(bytes, 2, bytes.Length - 2);
            await File.WriteAllBytesAsync(path, output.ToArray());
        }

        Assert.Equal(PhotoVersionRecipeStatus.Success,
            FaceMetadataStore.TryReadCompleteJpegXmp(path, out _));
        var xmp = XmpMetaFactory.Create();
        xmp.SetProperty("http://ns.adobe.com/xap/1.0/", "xmp:Label", "Blue");
        Assert.True(FaceMetadataStore.TryWriteEmbedded(path,
            XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions()).TrimStart('\uFEFF')));
        var written = await File.ReadAllBytesAsync(path);
        Assert.Equal(0xff, written[2]);
        Assert.Equal(0xff, written[3]);
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            FaceMetadataStore.TryReadCompleteJpegXmp(path, out var metadata));
        Assert.Equal("Blue", metadata!.GetPropertyString("http://ns.adobe.com/xap/1.0/", "xmp:Label"));
    }

    [Fact]
    public async Task TruncatedJpegMarkerIsRejectedWithoutReplacingTheFile()
    {
        var path = Path.Combine(_directory, "truncated.jpg");
        byte[] bytes = [0xff, 0xd8, 0xff, 0xe1];
        await File.WriteAllBytesAsync(path, bytes);
        Assert.Equal(PhotoVersionRecipeStatus.InvalidMetadata,
            FaceMetadataStore.TryReadCompleteJpegXmp(path, out _));
        Assert.Throws<InvalidDataException>(() =>
            FaceMetadataStore.TryWriteEmbedded(path, "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task InvalidUtf8InJpegXmpIsNotSilentlyReplaced()
    {
        var path = Path.Combine(_directory, "invalid-utf8.jpg");
        await WicTestImage.CreateAsync(path, 32, 24, jpeg: true);
        var xmp = XmpMetaFactory.Create();
        xmp.SetProperty("http://ns.adobe.com/xap/1.0/", "xmp:Label", "unique-label");
        Assert.True(FaceMetadataStore.TryWriteEmbedded(path,
            XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions()).TrimStart('\uFEFF')));
        var bytes = await File.ReadAllBytesAsync(path);
        var index = bytes.AsSpan().IndexOf("unique-label"u8);
        Assert.True(index >= 0);
        bytes[index] = 0xff;
        await File.WriteAllBytesAsync(path, bytes);

        Assert.Equal(PhotoVersionRecipeStatus.InvalidMetadata,
            FaceMetadataStore.TryReadCompleteJpegXmp(path, out var metadata));
        Assert.Null(metadata);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelledEmbeddedXmpWriteLeavesTheSourceUnchanged(bool jpeg)
    {
        var path = Path.Combine(_directory, jpeg ? "cancelled.jpg" : "cancelled.png");
        await WicTestImage.CreateAsync(path, 32, 24, jpeg: jpeg);
        var before = await File.ReadAllBytesAsync(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            FaceMetadataStore.TryWriteEmbedded(path, "unused", cancellationToken: cancellation.Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(_directory, $".{Path.GetFileName(path)}.*.tmp"));
    }

    [Fact]
    public void PacketMergeRejectsDifferingQualifiersWithoutMutatingEitherInput()
    {
        const string ns = "https://photolibrarian.test/extended-qualified-conflict/";
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(ns, "plqualified");
        var standard = XmpMetaFactory.Create();
        var extended = XmpMetaFactory.Create();
        standard.SetProperty(ns, "plqualified:Value", "same");
        extended.SetProperty(ns, "plqualified:Value", "same");
        standard.SetQualifier(ns, "plqualified:Value", ns, "plqualified:Qualifier", "standard");
        extended.SetQualifier(ns, "plqualified:Value", ns, "plqualified:Qualifier", "extended");
        standard.SetProperty(ns, "plqualified:StandardOnly", "preserve");
        Assert.Equal(PhotoVersionRecipeStatus.Conflict,
            FaceMetadataStore.TryMergeJpegXmpPackets(standard, extended, out var merged));
        Assert.Null(merged);
        Assert.Equal("standard", standard.GetQualifier(ns, "plqualified:Value", ns, "plqualified:Qualifier")!.Value);
        Assert.Equal("extended", extended.GetQualifier(ns, "plqualified:Value", ns, "plqualified:Qualifier")!.Value);
        Assert.False(extended.DoesPropertyExist(ns, "plqualified:StandardOnly"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PacketMergeRejectsAmbiguousExtendedXmpReferences(bool qualifiedStandardReference)
    {
        const string noteNamespace = "http://ns.adobe.com/xmp/note/";
        const string qualifierNamespace = "https://photolibrarian.test/reference-qualifier/";
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(noteNamespace, "xmpNote");
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(qualifierNamespace, "plref");
        var standard = XmpMetaFactory.Create();
        var extended = XmpMetaFactory.Create();
        standard.SetProperty(noteNamespace, "xmpNote:HasExtendedXMP", "reference");
        if (qualifiedStandardReference)
            standard.SetQualifier(noteNamespace, "xmpNote:HasExtendedXMP",
                qualifierNamespace, "plref:Meaning", "keep");
        else
            extended.SetProperty(noteNamespace, "xmpNote:HasExtendedXMP", "another reference");

        Assert.Equal(PhotoVersionRecipeStatus.Unsupported,
            FaceMetadataStore.TryMergeJpegXmpPackets(standard, extended, out var merged));
        Assert.Null(merged);
        Assert.True(standard.DoesPropertyExist(noteNamespace, "xmpNote:HasExtendedXMP"));
    }

    [Fact]
    public async Task CompleteJpegXmpReadDoesNotBufferImagePayload()
    {
        const string ns = "https://photolibrarian.test/streaming-xmp/";
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(ns, "plstream");
        var path = Path.Combine(_directory, "large-payload.jpg");
        await WicTestImage.CreateAsync(path, 32, 24, jpeg: true);
        var xmp = XmpMetaFactory.Create();
        xmp.SetProperty(ns, "plstream:Payload", new string('x', 80_000));
        Assert.True(FaceMetadataStore.TryWriteEmbedded(path,
            XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions()).TrimStart('\uFEFF')));
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            stream.SetLength(stream.Length + 16 * 1024 * 1024);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var status = FaceMetadataStore.TryReadCompleteJpegXmp(path, out var read);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(PhotoVersionRecipeStatus.Success, status);
        Assert.Equal(new string('x', 80_000), read!.GetPropertyString(ns, "plstream:Payload"));
        Assert.True(allocated < 8 * 1024 * 1024, $"JPEG metadata read allocated {allocated} bytes.");
    }

    [Fact]
    public async Task JpegXmpWriteStreamsAndPreservesImagePayload()
    {
        const string ns = "https://photolibrarian.test/streaming-write/";
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(ns, "plstreamwrite");
        var path = Path.Combine(_directory, "large-write.jpg");
        await WicTestImage.CreateAsync(path, 32, 24, jpeg: true);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            stream.SetLength(stream.Length + 16 * 1024 * 1024);
        var scan = ReadScan(path);
        var xmp = XmpMetaFactory.Create();
        xmp.SetProperty(ns, "plstreamwrite:Value", "preserved");
        var packet = XmpMetaFactory.SerializeToString(
            xmp, new XmpCore.Options.SerializeOptions()).TrimStart('\uFEFF');

        var before = GC.GetAllocatedBytesForCurrentThread();
        var written = FaceMetadataStore.TryWriteEmbedded(path, packet);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(written);
        Assert.True(allocated < 8 * 1024 * 1024, $"JPEG metadata write allocated {allocated} bytes.");
        Assert.Equal(scan, ReadScan(path));
        Assert.Equal("preserved", FaceMetadataStore.ReadEmbeddedXmp(path)!.GetPropertyString(ns, "plstreamwrite:Value"));
    }

    [Fact]
    public async Task PngXmpWriteStreamsAndPreservesTrailingData()
    {
        const string ns = "https://photolibrarian.test/streaming-png/";
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(ns, "plstreampng");
        var path = Path.Combine(_directory, "large-write.png");
        await WicTestImage.CreateAsync(path, 32, 24);
        var pixels = await ReadPixelsAsync(path);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(stream.Length + 16 * 1024 * 1024);
            stream.Position = stream.Length - 4;
            stream.Write([1, 2, 3, 4]);
        }
        var xmp = XmpMetaFactory.Create();
        xmp.SetProperty(ns, "plstreampng:Value", "preserved");
        var packet = XmpMetaFactory.SerializeToString(
            xmp, new XmpCore.Options.SerializeOptions()).TrimStart('\uFEFF');

        var before = GC.GetAllocatedBytesForCurrentThread();
        var written = FaceMetadataStore.TryWriteEmbedded(path, packet);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(written);
        Assert.True(allocated < 8 * 1024 * 1024, $"PNG metadata write allocated {allocated} bytes.");
        using (var stream = File.OpenRead(path))
        {
            stream.Position = stream.Length - 4;
            var tail = new byte[4];
            stream.ReadExactly(tail);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, tail);
        }
        Assert.Equal(pixels, await ReadPixelsAsync(path));
        Assert.Equal("preserved", FaceMetadataStore.ReadEmbeddedXmp(path)!.GetPropertyString(ns, "plstreampng:Value"));
    }

    internal static void RewriteStandardPacket(string path, Action<IXmpMeta> update)
    {
        var bytes = File.ReadAllBytes(path);
        var header = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
        var offset = 2;
        while (offset + 4 <= bytes.Length && bytes[offset] == 0xff)
        {
            var marker = bytes[offset + 1];
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 2, 2));
            if (marker == 0xe1 && bytes.AsSpan(offset + 4, length - 2).StartsWith(header))
            {
                var standard = XmpMetaFactory.ParseFromString(Encoding.UTF8.GetString(
                    bytes, offset + 4 + header.Length, length - 2 - header.Length).TrimEnd('\0'));
                update(standard);
                var packet = Encoding.UTF8.GetBytes(XmpMetaFactory.SerializeToString(
                    standard, new XmpCore.Options.SerializeOptions()).TrimStart('\uFEFF'));
                using var output = new MemoryStream();
                output.Write(bytes, 0, offset);
                output.WriteByte(0xff);
                output.WriteByte(0xe1);
                Span<byte> size = stackalloc byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(size, checked((ushort)(header.Length + packet.Length + 2)));
                output.Write(size);
                output.Write(header);
                output.Write(packet);
                output.Write(bytes, offset + length + 2, bytes.Length - offset - length - 2);
                File.WriteAllBytes(path, output.ToArray());
                return;
            }
            offset += length + 2;
        }
        throw new InvalidOperationException("The generated JPEG has no standard XMP packet.");
    }

    private async Task<string> CreateFixtureAsync(bool leadingPreamble)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".jpg");
        await WicTestImage.CreateAsync(path, 32, 24, jpeg: true);
        Assert.True(FaceMetadataStore.TryWriteEmbedded(path,
            (leadingPreamble ? "\uFEFF" : "") + XmpPacket));
        return path;
    }

    private static Task WritePropertyAsync(string path, string property) => property switch
    {
        "tags" => TagWriterService.WriteTagsToSidecarAsync(path,
            ["generated tag"], [new ImageTag { Tag = "generated tag", Source = TagSource.Manual }],
            "generated-scan-version"),
        "rating" => MetadataWriterService.WriteRatingAsync(path, 4),
        "caption" => MetadataWriterService.WriteCaptionAsync(path, "New generated caption"),
        "flag" => MetadataWriterService.WriteFlagAsync(path, true),
        "date" => MetadataWriterService.WriteDateTakenAsync(path, new DateTime(2024, 2, 3, 4, 5, 6)),
        "gps" => EmbeddedMetadataWriter.WriteGeotagAsync(path, new GeoCoordinate(10, -20)),
        "label" => MetadataWriterService.WriteColorLabelAsync(path, "Red"),
        _ => throw new InvalidOperationException("Unknown test property")
    };

    private static void AssertProperty(string path, string property, IXmpMeta metadata)
    {
        var reader = new MetadataReaderService();
        var image = reader.ReadMetadata(path);
        switch (property)
        {
            case "tags":
                var tags = reader.ReadPortableTagMetadata(path);
                Assert.NotNull(tags);
                Assert.Equal("generated-scan-version", tags.AutoTagScanVersion);
                Assert.Equal("generated tag", Assert.Single(tags.Tags).Tag);
                break;
            case "rating":
                Assert.Equal(4, image.Rating);
                break;
            case "caption":
                Assert.Equal("New generated caption", image.Caption);
                break;
            case "flag":
                Assert.True(image.IsFlagged);
                break;
            case "date":
                Assert.Equal(new DateTime(2024, 2, 3, 4, 5, 6), image.DateTaken);
                break;
            case "gps":
                Assert.Equal(10, image.GpsLatitude!.Value, 6);
                Assert.Equal(-20, image.GpsLongitude!.Value, 6);
                break;
            case "label":
                Assert.Equal("Red", image.ColorLabel);
                Assert.Equal("Red", metadata.GetPropertyString("http://ns.adobe.com/xap/1.0/", "Label"));
                break;
        }
        if (property != "rating")
            Assert.Equal("3", metadata.GetPropertyString("http://ns.adobe.com/xap/1.0/", "Rating"));
    }

    private static IXmpMeta ReadXmp(string path) =>
        MetadataExtractor.ImageMetadataReader.ReadMetadata(path)
            .OfType<MetadataExtractor.Formats.Xmp.XmpDirectory>().Single().XmpMeta!;

    private static async Task<byte[]> ReadPixelsAsync(string path)
    {
        using var file = File.OpenRead(path);
        using var stream = file.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        return (await decoder.GetPixelDataAsync()).DetachPixelData();
    }

    private static byte[] ReadScan(string path)
    {
        var bytes = File.ReadAllBytes(path);
        for (var offset = 2; offset + 4 <= bytes.Length && bytes[offset] == 0xff;)
        {
            if (bytes[offset + 1] == 0xda) return bytes[offset..];
            offset += (bytes[offset + 2] << 8 | bytes[offset + 3]) + 2;
        }
        throw new InvalidDataException("The generated JPEG has no scan.");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
