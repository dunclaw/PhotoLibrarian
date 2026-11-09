// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Runtime.InteropServices.WindowsRuntime;
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
