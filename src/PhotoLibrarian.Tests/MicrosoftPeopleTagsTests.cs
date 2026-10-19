using MetadataExtractor.Formats.Xmp;
using ImageMetadataReader = MetadataExtractor.ImageMetadataReader;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using System.Globalization;
using System.Xml.Linq;
using Windows.Storage;
using XmpCore;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class MicrosoftPeopleTagsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"PhotoLibrarian-{Guid.NewGuid():N}");
    private readonly FaceMetadataStore _store = new();

    public MicrosoftPeopleTagsTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task Sidecar_ContainsMicrosoftBagAndMwgWithInvariantCoordinates()
    {
        var path = Path.Combine(_directory, "portrait.cr3");
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            await _store.WriteAsync(path, Metadata(Face("Alex & Sam"), Face(null)),
                TestContext.Current.CancellationToken);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }

        var xml = XDocument.Load(Path.ChangeExtension(path, ".xmp"));
        XNamespace mp = MicrosoftPeopleTags.Namespace;
        XNamespace mpri = MicrosoftPeopleTags.RegionInfoNamespace;
        XNamespace mpreg = MicrosoftPeopleTags.RegionNamespace;
        XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        var regions = Assert.Single(xml.Descendants(mp + "RegionInfo").Descendants(mpri + "Regions"));
        var bag = Assert.Single(regions.Elements(rdf + "Bag"));
        var item = Assert.Single(bag.Elements(rdf + "li"));
        Assert.Equal("Alex & Sam", (string?)item.Attribute(mpreg + "PersonDisplayName"));
        Assert.Equal("0.100000, 0.200000, 0.300000, 0.400000", (string?)item.Attribute(mpreg + "Rectangle"));

        var xmp = LoadSidecar(path);
        Assert.Equal(2, xmp.CountArrayItems(CropMetadataRemapper.MwgRs, "Regions/mwg-rs:RegionList"));
        Assert.Equal("0.250000", xmp.GetStructField(
            CropMetadataRemapper.MwgRs, "Regions/mwg-rs:RegionList[1]/mwg-rs:Area",
            CropMetadataRemapper.StArea, "x")?.Value);
        Assert.Equal(2, _store.Read(path).Faces.Count);
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    public async Task EmbeddedImage_ContainsBothSchemasAndClearsStaleSidecarTags(string extension)
    {
        var path = Path.Combine(_directory, $"portrait{extension}");
        await WicTestImage.CreateAsync(path, 120, 80, extension == ".jpg");
        var sidecar = XmpMetaFactory.ParseFromString(MicrosoftOnlyXmp);
        sidecar.SetProperty("http://ns.adobe.com/xap/1.0/", "CreatorTool", "Keep me");
        File.WriteAllText(FaceMetadataStore.GetSidecarPathForImage(path),
            XmpMetaFactory.SerializeToString(sidecar, new XmpCore.Options.SerializeOptions()));

        await _store.WriteAsync(path, Metadata(Face("New name")),
            TestContext.Current.CancellationToken);

        var embedded = Assert.Single(ImageMetadataReader.ReadMetadata(path)
            .OfType<XmpDirectory>()).XmpMeta!;
        Assert.Equal("New name", PersonName(embedded));
        Assert.Equal(1, embedded.CountArrayItems(CropMetadataRemapper.MwgRs, "Regions/mwg-rs:RegionList"));
        var cleanedSidecar = LoadSidecar(path);
        Assert.Equal(0, cleanedSidecar.CountArrayItems(
            MicrosoftPeopleTags.Namespace, MicrosoftPeopleTags.RegionsPath));
        Assert.Equal("Keep me", cleanedSidecar.GetPropertyString("http://ns.adobe.com/xap/1.0/", "CreatorTool"));
        Assert.Equal("New name", Assert.Single(_store.Read(path).Faces).PersonName);
        Assert.Equal((120u, 80u), await WicTestImage.ReadSizeAsync(path));
    }

    [Fact]
    public async Task Jpeg_ExposesNamesThroughWindowsExplorerPeopleProperty()
    {
        var path = Path.Combine(_directory, "people.jpg");
        await WicTestImage.CreateAsync(path, 120, 80, true);
        await _store.WriteAsync(path, Metadata(
            Face("Alex"), Face("Sam") with { X = 0.6 }, Face(null)),
            TestContext.Current.CancellationToken);

        var file = await StorageFile.GetFileFromPathAsync(path);
        var properties = await file.Properties.RetrievePropertiesAsync(["System.Photo.PeopleNames"]);
        var names = Assert.IsAssignableFrom<IEnumerable<string>>(properties["System.Photo.PeopleNames"]);
        Assert.Equal(["Alex", "Sam"], names.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Sidecar_RenameAndRemoveReplaceMicrosoftTagsAndPreserveOtherFields()
    {
        var path = Path.Combine(_directory, "portrait.cr3");
        var existing = XmpMetaFactory.ParseFromString(MicrosoftOnlyXmp);
        existing.SetProperty("http://ns.adobe.com/xap/1.0/", "CreatorTool", "Keep me");
        File.WriteAllText(Path.ChangeExtension(path, ".xmp"),
            XmpMetaFactory.SerializeToString(existing, new XmpCore.Options.SerializeOptions()));

        await _store.WriteAsync(path, Metadata(Face("Renamed")), TestContext.Current.CancellationToken);
        var renamed = LoadSidecar(path);
        Assert.Equal("Renamed", PersonName(renamed));
        Assert.Equal(1, renamed.CountArrayItems(MicrosoftPeopleTags.Namespace, MicrosoftPeopleTags.RegionsPath));
        Assert.Equal("Keep me", renamed.GetPropertyString("http://ns.adobe.com/xap/1.0/", "CreatorTool"));
        Assert.Equal("2020-01-01T00:00:00Z", renamed.GetStructField(
            MicrosoftPeopleTags.Namespace, "RegionInfo",
            MicrosoftPeopleTags.RegionInfoNamespace, "DateRegionsValid")?.Value);

        await _store.WriteAsync(path, Metadata(Face(null)), TestContext.Current.CancellationToken);
        Assert.Equal(0, LoadSidecar(path).CountArrayItems(
            MicrosoftPeopleTags.Namespace, MicrosoftPeopleTags.RegionsPath));
        Assert.Null(Assert.Single(_store.Read(path).Faces).PersonName);

        await _store.WriteAsync(path, Metadata(), TestContext.Current.CancellationToken);
        Assert.Empty(_store.Read(path).Faces);
        Assert.Equal(0, LoadSidecar(path).CountArrayItems(
            MicrosoftPeopleTags.Namespace, MicrosoftPeopleTags.RegionsPath));
    }

    [Fact]
    public async Task Read_ImportsMicrosoftOnlyRectanglesAndKeepsMwgNameOnConflict()
    {
        var path = Path.Combine(_directory, "portrait.cr3");
        File.WriteAllText(Path.ChangeExtension(path, ".xmp"), MicrosoftOnlyXmp);
        var imported = Assert.Single(_store.Read(path).Faces);
        Assert.Equal("Photo Gallery person", imported.PersonName);
        Assert.Equal(0.1, imported.X, 6);
        Assert.Equal(0.2, imported.Y, 6);
        Assert.Equal(0.3, imported.Width, 6);
        Assert.Equal(0.4, imported.Height, 6);
        await _store.WriteAsync(path, Metadata(imported with { PersonName = "MWG name" }),
            TestContext.Current.CancellationToken);
        var xmp = LoadSidecar(path);
        xmp.SetStructField(MicrosoftPeopleTags.Namespace, $"{MicrosoftPeopleTags.RegionsPath}[1]",
            MicrosoftPeopleTags.RegionNamespace, "PersonDisplayName", "Stale Microsoft name");
        File.WriteAllText(Path.ChangeExtension(path, ".xmp"),
            XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions()));

        Assert.Equal("MWG name", Assert.Single(_store.Read(path).Faces).PersonName);
    }

    [Fact]
    public void Crop_MicrosoftOnlyRegionsClipAndDropFacesWhilePreservingOtherFields()
    {
        var xmp = XmpMetaFactory.ParseFromString(MicrosoftOnlyXmp);
        Assert.True(CropMetadataRemapper.RemapMwgRegions(
            xmp, 100, 100, new CropRectangle(25, 20, 50, 40)));
        Assert.Equal("0.000000, 0.000000, 0.300000, 1.000000", xmp.GetStructField(
            MicrosoftPeopleTags.Namespace, $"{MicrosoftPeopleTags.RegionsPath}[1]",
            MicrosoftPeopleTags.RegionNamespace, "Rectangle")?.Value);
        Assert.Equal("Photo Gallery person", PersonName(xmp));

        Assert.True(CropMetadataRemapper.RemapMwgRegions(
            xmp, 50, 40, new CropRectangle(40, 0, 10, 40)));
        Assert.Equal(0, xmp.CountArrayItems(MicrosoftPeopleTags.Namespace, MicrosoftPeopleTags.RegionsPath));
    }

    [Theory]
    [InlineData("NaN, 0.2, 0.3, 0.4")]
    [InlineData("0.1, 0.2, 0, 0.4")]
    [InlineData("0.1, 0.2, 0.3")]
    [InlineData("0.9, 0.2, 0.3, 0.4")]
    public void Crop_InvalidRectangleFailsExplicitly(string rectangle)
    {
        var xmp = XmpMetaFactory.ParseFromString(
            MicrosoftOnlyXmp.Replace("0.1, 0.2, 0.3, 0.4", rectangle));
        Assert.Throws<InvalidDataException>(() => CropMetadataRemapper.RemapMwgRegions(
            xmp, 100, 100, new CropRectangle(25, 20, 50, 40)));
    }

    [Fact]
    public void Crop_NameOnlyTagIsPreserved()
    {
        var xmp = XmpMetaFactory.ParseFromString(MicrosoftOnlyXmp);
        xmp.DeleteStructField(MicrosoftPeopleTags.Namespace, $"{MicrosoftPeopleTags.RegionsPath}[1]",
            MicrosoftPeopleTags.RegionNamespace, "Rectangle");
        Assert.True(CropMetadataRemapper.RemapMwgRegions(
            xmp, 100, 100, new CropRectangle(25, 20, 50, 40)));
        Assert.Equal("Photo Gallery person", PersonName(xmp));
    }

    private static PortableFaceMetadata Face(string? name) =>
        new(1, 0.1, 0.2, 0.3, 0.4, name, false, false, []);

    private static PhotoFaceMetadata Metadata(params PortableFaceMetadata[] faces) => new(120, 80, faces);

    private static IXmpMeta LoadSidecar(string path) =>
        XmpMetaFactory.ParseFromString(File.ReadAllText(FaceMetadataStore.GetSidecarPathForImage(path)));

    private static string? PersonName(IXmpMeta xmp) =>
        xmp.GetStructField(MicrosoftPeopleTags.Namespace, $"{MicrosoftPeopleTags.RegionsPath}[1]",
            MicrosoftPeopleTags.RegionNamespace, "PersonDisplayName")?.Value;

    private const string MicrosoftOnlyXmp = """
        <x:xmpmeta xmlns:x="adobe:ns:meta/">
          <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
            <rdf:Description rdf:about="" xmlns:MP="http://ns.microsoft.com/photo/1.2/"
                xmlns:MPRI="http://ns.microsoft.com/photo/1.2/t/RegionInfo#"
                xmlns:MPReg="http://ns.microsoft.com/photo/1.2/t/Region#">
              <MP:RegionInfo rdf:parseType="Resource">
                <MPRI:DateRegionsValid>2020-01-01T00:00:00Z</MPRI:DateRegionsValid>
                <MPRI:Regions>
                  <rdf:Bag>
                    <rdf:li rdf:parseType="Resource">
                      <MPReg:PersonDisplayName>Photo Gallery person</MPReg:PersonDisplayName>
                      <MPReg:Rectangle>0.1, 0.2, 0.3, 0.4</MPReg:Rectangle>
                    </rdf:li>
                  </rdf:Bag>
                </MPRI:Regions>
              </MP:RegionInfo>
            </rdf:Description>
          </rdf:RDF>
        </x:xmpmeta>
        """;

    public void Dispose() => Directory.Delete(_directory, true);
}
