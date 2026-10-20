using PhotoLibrarian.Core.Services;
using XmpCore;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class MetadataWriteFallbackTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"PhotoLibrarian-{Guid.NewGuid():N}");

    public MetadataWriteFallbackTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".tif")]
    public async Task EmbeddedWriteAndClear_RoundTripWithoutSidecar(string extension)
    {
        var path = Path.Combine(_directory, $"image{extension}");
        await WicTestImage.CreateAsync(path, 16, 16, jpeg: extension == ".jpg");
        await EmbeddedMetadataWriter.WriteAsync(path, title: new string('A', 5000), keywords: ["Travel"]);
        Assert.Equal(["Travel"], await EmbeddedMetadataWriter.ReadKeywordsAsync(path));

        await EmbeddedMetadataWriter.ClearPropertiesAsync(path, "System.Keywords");

        Assert.Empty(await EmbeddedMetadataWriter.ReadKeywordsAsync(path));
        Assert.False(File.Exists(Path.ChangeExtension(path, ".xmp")));
        Assert.Equal((16u, 16u), await WicTestImage.ReadSizeAsync(path));
    }

    [Fact]
    public async Task TemporaryLock_WritesEmbeddedMetadataWithoutSidecarOrWarning()
    {
        var path = Path.Combine(_directory, "temporary.jpg");
        await WicTestImage.CreateAsync(path, 16, 16, jpeg: true);
        var notices = new List<MetadataWriteFallbackEventArgs>();
        void OnFallback(object? sender, MetadataWriteFallbackEventArgs e)
        {
            if (e.ImagePath == path) notices.Add(e);
        }

        MetadataWriterService.SidecarFallback += OnFallback;
        try
        {
            using var lockedFile = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var write = MetadataWriterService.WriteCaptionAsync(path, "Retried caption");
            await Task.Delay(150, TestContext.Current.CancellationToken);
            lockedFile.Dispose();
            await write;

            Assert.False(File.Exists(Path.ChangeExtension(path, ".xmp")));
            Assert.Equal("Retried caption", MetadataWriterService.ReadCaptionFromSidecar(path));
            Assert.Empty(notices);
        }
        finally
        {
            MetadataWriterService.SidecarFallback -= OnFallback;
        }
    }

    [Theory]
    [InlineData("rating")]
    [InlineData("caption")]
    [InlineData("date taken")]
    [InlineData("flag")]
    [InlineData("tags")]
    public async Task PersistentLock_SavesSidecarAndReportsFallback(string propertyName)
    {
        var path = Path.Combine(_directory, "locked.jpg");
        await WicTestImage.CreateAsync(path, 16, 16, jpeg: true);
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        MetadataWriteFallbackEventArgs? notice = null;
        var sidecarExistedWhenReported = false;
        void OnFallback(object? sender, MetadataWriteFallbackEventArgs e)
        {
            if (e.ImagePath != path) return;
            notice = e;
            sidecarExistedWhenReported = File.Exists(e.SidecarPath);
        }

        MetadataWriterService.SidecarFallback += OnFallback;
        try
        {
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                await WriteAsync(path, propertyName);

            Assert.NotNull(notice);
            Assert.Equal(propertyName, notice.PropertyName);
            Assert.True(sidecarExistedWhenReported);
            Assert.Equal(unchecked((int)0x80070020), notice.Error.HResult);
            Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            var sidecar = XmpMetaFactory.ParseFromString(await File.ReadAllTextAsync(
                notice.SidecarPath, TestContext.Current.CancellationToken));
            AssertSidecarValue(sidecar, propertyName);
        }
        finally
        {
            MetadataWriterService.SidecarFallback -= OnFallback;
        }
    }

    [Theory]
    [InlineData("rating")]
    [InlineData("caption")]
    [InlineData("date taken")]
    [InlineData("flag")]
    [InlineData("tags")]
    public async Task RawSidecar_DoesNotReportUnexpectedFallback(string propertyName)
    {
        var path = Path.Combine(_directory, "raw.cr3");
        var notices = new List<MetadataWriteFallbackEventArgs>();
        void OnFallback(object? sender, MetadataWriteFallbackEventArgs e)
        {
            if (e.ImagePath == path) notices.Add(e);
        }

        MetadataWriterService.SidecarFallback += OnFallback;
        try
        {
            await WriteAsync(path, propertyName);
            Assert.Empty(notices);
            var sidecar = XmpMetaFactory.ParseFromString(await File.ReadAllTextAsync(
                Path.ChangeExtension(path, ".xmp"), TestContext.Current.CancellationToken));
            AssertSidecarValue(sidecar, propertyName);
        }
        finally
        {
            MetadataWriterService.SidecarFallback -= OnFallback;
        }
    }

    [Fact]
    public async Task FailedSidecarWrite_ThrowsWithoutReportingSavedFallback()
    {
        var path = Path.Combine(_directory, "missing.jpg");
        Directory.CreateDirectory(Path.ChangeExtension(path, ".xmp"));
        var notices = new List<MetadataWriteFallbackEventArgs>();
        void OnFallback(object? sender, MetadataWriteFallbackEventArgs e)
        {
            if (e.ImagePath == path) notices.Add(e);
        }

        MetadataWriterService.SidecarFallback += OnFallback;
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => MetadataWriterService.WriteCaptionAsync(path, "Caption"));
            Assert.Empty(notices);
        }
        finally
        {
            MetadataWriterService.SidecarFallback -= OnFallback;
        }
    }

    private static Task WriteAsync(string path, string propertyName) => propertyName switch
    {
        "rating" => MetadataWriterService.WriteRatingAsync(path, 4),
        "caption" => MetadataWriterService.WriteCaptionAsync(path, "Caption"),
        "date taken" => MetadataWriterService.WriteDateTakenAsync(path, new DateTime(2026, 10, 18, 12, 30, 0)),
        "flag" => MetadataWriterService.WriteFlagAsync(path, true),
        "tags" => TagWriterService.WriteTagsToSidecarAsync(path, ["Travel"]),
        _ => throw new ArgumentOutOfRangeException(nameof(propertyName))
    };

    private static void AssertSidecarValue(IXmpMeta xmp, string propertyName)
    {
        const string xap = "http://ns.adobe.com/xap/1.0/";
        const string dc = "http://purl.org/dc/elements/1.1/";
        switch (propertyName)
        {
            case "rating":
                Assert.Equal(4, xmp.GetPropertyInteger(xap, "xmp:Rating"));
                break;
            case "caption":
                Assert.Equal("Caption", xmp.GetLocalizedText(dc, "dc:description", "", "x-default").Value);
                break;
            case "date taken":
                Assert.Equal("2026-10-18T12:30:00", xmp.GetPropertyString(xap, "xmp:CreateDate"));
                break;
            case "flag":
                Assert.Equal("True", xmp.GetPropertyString(MetadataWriterService.PhotoLibrarianNamespace, "plib:Flagged"));
                break;
            case "tags":
                Assert.Equal("Travel", xmp.GetArrayItem(dc, "dc:subject", 1).Value);
                break;
        }
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
