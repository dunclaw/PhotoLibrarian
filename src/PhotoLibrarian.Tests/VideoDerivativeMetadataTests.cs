// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using XmpCore;
using XmpCore.Options;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class VideoDerivativeMetadataTests
{
    private const string Dc = "http://purl.org/dc/elements/1.1/";
    private const string Xap = "http://ns.adobe.com/xap/1.0/";
    private const string Exif = "http://ns.adobe.com/exif/1.0/";
    private const string Tiff = "http://ns.adobe.com/tiff/1.0/";

    [Fact]
    public void SidecarOverridesEmbeddedButMissingFieldsComeFromEmbeddedOrWindows()
    {
        var embedded = XmpMetaFactory.Create();
        embedded.SetLocalizedText(Dc, "title", "", "x-default", "Embedded title");
        embedded.SetLocalizedText(Dc, "rights", "", "x-default", "Copyright retained");
        embedded.AppendArrayItem(Dc, "creator", new PropertyOptions { IsArrayOrdered = true }, "Author", null);
        var sidecar = XmpMetaFactory.Create();
        sidecar.SetLocalizedText(Dc, "title", "en", "en-US", "Edited title");
        sidecar.SetLocalizedText(Dc, "title", "fr", "fr-FR", "Titre");
        sidecar.SetPropertyInteger(Xap, "Rating", 4);
        var shell = new Dictionary<string, object>
        {
            ["System.Title"] = "Shell title",
            ["System.Comment"] = "Shell caption",
            ["System.Keywords"] = new[] { "family", "holiday" },
            ["System.Photo.CameraManufacturer"] = "Camera make",
            ["System.Media.DateEncoded"] = new DateTimeOffset(2024, 4, 5, 6, 7, 8, TimeSpan.Zero)
        };

        var serialized = VideoDerivativeMetadata.CreateXmp(embedded, sidecar, shell, 51.5, -0.125);
        var output = XmpMetaFactory.ParseFromString(serialized);

        Assert.Equal("Edited title", output.GetLocalizedText(Dc, "title", "", "x-default").Value);
        Assert.Equal("Titre", output.GetLocalizedText(Dc, "title", "fr", "fr-FR").Value);
        Assert.Equal("Copyright retained", output.GetLocalizedText(Dc, "rights", "", "x-default").Value);
        Assert.Equal("Author", output.GetArrayItem(Dc, "creator", 1).Value);
        Assert.Equal("holiday", output.GetArrayItem(Dc, "subject", 2).Value);
        Assert.Equal("Shell caption", output.GetLocalizedText(Dc, "description", "", "x-default").Value);
        Assert.Equal("Camera make", output.GetPropertyString(Tiff, "Make"));
        Assert.Equal(4, output.GetPropertyInteger(Xap, "Rating"));
        Assert.Equal("51,30N", output.GetPropertyString(Exif, "GPSLatitude"));
        Assert.Equal("0,7.5W", output.GetPropertyString(Exif, "GPSLongitude"));
        Assert.StartsWith("2024-04-05T06:07:08", output.GetPropertyString(Exif, "DateTimeOriginal"));
    }

    [Fact]
    public void DoesNotCopyStaleTrackDetailsGeometryOrEditRecipes()
    {
        const string video = "http://ns.adobe.com/xmp/1.0/DynamicMedia/";
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(video, "xmpDM");
        var source = XmpMetaFactory.Create();
        source.SetProperty(Tiff, "ImageWidth", 999);
        source.SetProperty(Tiff, "ImageLength", 888);
        source.SetProperty(Tiff, "Orientation", 6);
        source.SetProperty(video, "duration", "999");
        source.SetProperty(video, "videoCompressor", "Old codec");
        EditParametersSerializer.WriteToXmp(source, new EditParameters { Brightness = 0.5 });
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(MetadataWriterService.PhotoLibrarianNamespace, "plib");
        source.SetProperty(MetadataWriterService.PhotoLibrarianNamespace, "Flagged", "True");
        var output = XmpMetaFactory.ParseFromString(
            VideoDerivativeMetadata.CreateXmp(source, null, new Dictionary<string, object>()));

        Assert.False(output.DoesPropertyExist(Tiff, "ImageWidth"));
        Assert.False(output.DoesPropertyExist(Tiff, "ImageLength"));
        Assert.False(output.DoesPropertyExist(Tiff, "Orientation"));
        Assert.False(output.DoesPropertyExist(video, "duration"));
        Assert.False(output.DoesPropertyExist(video, "videoCompressor"));
        Assert.False(output.DoesPropertyExist(EditParameters.XmpNamespace, "Brightness"));
        Assert.Equal("True", output.GetPropertyString(MetadataWriterService.PhotoLibrarianNamespace, "Flagged"));
    }

    [Fact]
    public void PublishingCollisionRollsBackOnlyOurSidecar()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-Publish-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var output = Path.Combine(directory, "output.mp4");
            var temporary = Path.Combine(directory, "encoded.tmp");
            File.WriteAllText(output, "Other export");
            File.WriteAllText(temporary, "Our export");
            Assert.Throws<IOException>(() => VideoStabilizationService.PublishOutput("<xmp/>", temporary, output));
            Assert.Equal("Other export", File.ReadAllText(output));
            Assert.Equal("Our export", File.ReadAllText(temporary));
            Assert.False(File.Exists(Path.ChangeExtension(output, ".xmp")));
            Assert.Empty(Directory.GetFiles(directory, ".photolibrarian-*"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

    }

    [Fact]
    public void SidecarCollisionPreservesBothExistingFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-Publish-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var output = Path.Combine(directory, "output.mp4");
            var sidecar = Path.ChangeExtension(output, ".xmp");
            var temporary = Path.Combine(directory, "encoded.tmp");
            File.WriteAllText(sidecar, "Other metadata");
            File.WriteAllText(temporary, "Our export");
            Assert.Throws<IOException>(() => VideoStabilizationService.PublishOutput("<xmp/>", temporary, output));
            Assert.Equal("Other metadata", File.ReadAllText(sidecar));
            Assert.Equal("Our export", File.ReadAllText(temporary));
            Assert.False(File.Exists(output));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
