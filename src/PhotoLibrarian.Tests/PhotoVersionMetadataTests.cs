// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using System.Buffers.Binary;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using XmpCore;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class PhotoVersionMetadataTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"PhotoLibrarian-version-metadata-{Guid.NewGuid():N}");

    public PhotoVersionMetadataTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(".cr3")]
    [InlineData(".jpg")]
    public async Task CancelledFamilyReadAndStagedWriteDoNotChangeMetadata(string extension)
    {
        var path = Path.Combine(_directory, "source" + extension);
        if (extension == ".cr3")
            await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);
        else
            await WicTestImage.CreateAsync(path, 16, 16, jpeg: true);
        var before = await File.ReadAllBytesAsync(path);
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryCreate(
            Guid.NewGuid(), "file-v1:fixture", Guid.NewGuid(), "Original", "<main/>",
            Guid.NewGuid(), "Copy", out var value));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(
            () => MetadataWriterService.ReadVersionFamily(path, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(path, value!, null, cancellation.Token));
        Assert.False(File.Exists(Path.ChangeExtension(path, ".xmp")));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".tif")]
    [InlineData(".bmp")]
    [InlineData(".cr3")]
    public async Task FamilyRoundTripsThroughTransactionWithoutChangingSourcePixels(string extension)
    {
        var path = Path.Combine(_directory, "source" + extension);
        var raw = extension == ".cr3";
        if (raw)
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4], TestContext.Current.CancellationToken);
        else
            await WicTestImage.CreateAsync(path, 32, 24, jpeg: extension == ".jpg");
        var bytes = raw ? await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken) : null;
        var pixels = raw ? null : await WindowsImagingCodec.DecodeAsync(path);
        await MetadataWriterService.WriteColorLabelAsync(path, "Blue");
        var sourceId = Guid.NewGuid();
        var main = Guid.NewGuid();
        var copy = Guid.NewGuid();
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryCreate(
            sourceId, "pixels-v1:fixture", main, "Original",
            "<recipe>" + new string('x', 80_000) + "</recipe>", copy, "Copy 1", out var value));

        using var transaction = await FileEditTransaction.PrepareAsync(
            Path.Combine(_directory, "history"), [path], async copies =>
            {
                var result = await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(
                    copies[path], value!, expectedValue: null);
                Assert.Equal(PhotoVersionRecipeStatus.Success, result);
            });
        Assert.Equal(EditHistoryStatus.Applied, (await transaction.MoveAsync(false, () => Task.CompletedTask)).Status);

        var (status, stored) = MetadataWriterService.ReadVersionFamily(path);
        Assert.Equal(PhotoVersionRecipeStatus.Success, status);
        Assert.Equal(value, stored);
        Assert.Equal("Blue", new MetadataReaderService().ReadMetadata(path).ColorLabel);
        if (raw)
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        else
            Assert.Equal(pixels!.Pixels, (await WindowsImagingCodec.DecodeAsync(path)).Pixels);
        var embedded = extension is ".jpg" or ".png" or ".tif";
        Assert.Equal(!embedded, File.Exists(Path.ChangeExtension(path, ".xmp")));
        Assert.False(File.Exists(path + ".xmp"));

        Assert.Equal(EditHistoryStatus.Applied, (await transaction.MoveAsync(true, () => Task.CompletedTask)).Status);
        Assert.Equal(PhotoVersionRecipeStatus.VersionMissing, MetadataWriterService.ReadVersionFamily(path).Status);
        Assert.Equal(EditHistoryStatus.Applied, (await transaction.MoveAsync(false, () => Task.CompletedTask)).Status);
        Assert.Equal(value, MetadataWriterService.ReadVersionFamily(path).Value);
        await MetadataWriterService.WriteColorLabelAsync(path, "Green");
        Assert.Equal(value, MetadataWriterService.ReadVersionFamily(path).Value);
        if (extension == ".jpg")
        {
            await MetadataWriterService.WriteFlagAsync(path, true);
            Assert.True(new MetadataReaderService().ReadMetadata(path).IsFlagged);
            Assert.Equal(value, MetadataWriterService.ReadVersionFamily(path).Value);
            await MetadataWriterService.WriteFlagAsync(path, false);
            Assert.False(new MetadataReaderService().ReadMetadata(path).IsFlagged);
            Assert.Equal(value, MetadataWriterService.ReadVersionFamily(path).Value);
        }
    }

    [Fact]
    public async Task ConflictingOwnedFamilyDataCannotBeOverwritten()
    {
        var path = Path.Combine(_directory, "source.png");
        await WicTestImage.CreateAsync(path, 16, 16);
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryCreate(
            Guid.NewGuid(), "pixels-v1:fixture", Guid.NewGuid(), "Original", "<main/>",
            Guid.NewGuid(), "Copy", out var first));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(path, first!, null));
        var xmp = XmpMetaFactory.Create();
        xmp.SetProperty(MetadataWriterService.PhotoLibrarianNamespace, "plib:VirtualCopies", "different");
        var sidecar = path + ".xmp";
        await File.WriteAllTextAsync(sidecar, XmpMetaFactory.SerializeToString(
            xmp, new XmpCore.Options.SerializeOptions()), TestContext.Current.CancellationToken);
        var before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var beforeSidecar = await File.ReadAllBytesAsync(sidecar, TestContext.Current.CancellationToken);

        Assert.Equal(PhotoVersionRecipeStatus.Conflict, MetadataWriterService.ReadVersionFamily(path).Status);
        Assert.Equal(PhotoVersionRecipeStatus.Conflict,
            await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(path, first!, first));
        Assert.Equal(before, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(beforeSidecar, await File.ReadAllBytesAsync(sidecar, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MalformedPngXmpIsNotMistakenForMissingFamily()
    {
        var path = Path.Combine(_directory, "malformed.png");
        await WicTestImage.CreateAsync(path, 16, 16);
        Assert.True(FaceMetadataStore.TryWriteEmbedded(path, "<unclosed"));
        var before = await File.ReadAllBytesAsync(path);

        Assert.Equal(PhotoVersionRecipeStatus.InvalidMetadata,
            MetadataWriterService.ReadVersionFamily(path).Status);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task LegacyRecipeApisRequireAnExplicitTargetForVersionedPhoto()
    {
        var path = Path.Combine(_directory, "versioned.png");
        await WicTestImage.CreateAsync(path, 16, 16);
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryCreate(
            Guid.NewGuid(), "pixels-v1:fixture", Guid.NewGuid(), "Original", "<main/>",
            Guid.NewGuid(), "Copy", out var family));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(path, family!, null));
        var before = await File.ReadAllBytesAsync(path);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => ImageEditService.WritePendingRecipeAsync(path, new EditParameters { Brightness = 0.2 }));
        Assert.Throws<NotSupportedException>(() => ImageEditService.ReadEditParameters(path));
        await Assert.ThrowsAsync<NotSupportedException>(
            () => ImageEditService.ClearPendingEditParametersAsync(path));
        var backups = new OriginalBackupService(Path.Combine(_directory, "originals"));
        var rendererInvoked = false;
        var editService = new ImageEditService(backups);
        await Assert.ThrowsAsync<NotSupportedException>(
            () => editService.ApplyEditsAsync(path, new EditParameters(),
                (_, _) =>
                {
                    rendererInvoked = true;
                    return Task.FromResult((16u, 16u));
                }));
        Assert.False(rendererInvoked);
        var originalHash = await OriginalBackupService.ComputeFileHashAsync(path);
        await backups.BackupOriginalAsync(path);
        await Assert.ThrowsAsync<NotSupportedException>(
            () => backups.RestoreOriginalAsync(path, originalHash));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task LegacyRecipeWriteFailsClosedWhenFamilyMetadataIsMalformed()
    {
        var path = Path.Combine(_directory, "malformed-family.png");
        await WicTestImage.CreateAsync(path, 16, 16);
        var xmp = XmpMetaFactory.Create();
        xmp.SetProperty(MetadataWriterService.PhotoLibrarianNamespace, "plib:VirtualCopies", "{broken");
        Assert.True(FaceMetadataStore.TryWriteEmbedded(path,
            XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions())));
        var before = await File.ReadAllBytesAsync(path);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ImageEditService.WritePendingRecipeAsync(path, new EditParameters { Brightness = 0.2 }));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task MultiplePngXmpPacketsAreNotTreatedAsOneFamily()
    {
        var path = Path.Combine(_directory, "multiple.png");
        await WicTestImage.CreateAsync(path, 16, 16);
        var xmp = XmpMetaFactory.Create();
        xmp.SetProperty(MetadataWriterService.PhotoLibrarianNamespace, "plib:VirtualCopies", "first");
        Assert.True(FaceMetadataStore.TryWriteEmbedded(path,
            XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions())));
        var bytes = await File.ReadAllBytesAsync(path);
        byte[]? packetChunk = null;
        var endOffset = 0;
        for (var offset = 8; offset < bytes.Length;)
        {
            var size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4)) + 12);
            var type = bytes.AsSpan(offset + 4, 4);
            if (type.SequenceEqual("iTXt"u8))
                packetChunk = bytes.AsSpan(offset, size).ToArray();
            if (type.SequenceEqual("IEND"u8))
            {
                endOffset = offset;
                break;
            }
            offset += size;
        }
        Assert.NotNull(packetChunk);
        Assert.True(endOffset > 0);
        using var output = new MemoryStream();
        output.Write(bytes, 0, endOffset);
        output.Write(packetChunk);
        output.Write(bytes, endOffset, bytes.Length - endOffset);
        await File.WriteAllBytesAsync(path, output.ToArray());
        var before = await File.ReadAllBytesAsync(path);

        Assert.Equal(PhotoVersionRecipeStatus.Conflict, MetadataWriterService.ReadVersionFamily(path).Status);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".tif")]
    public async Task ReadingRasterFamilyDoesNotBufferImagePayload(string extension)
    {
        var path = Path.Combine(_directory, "large" + extension);
        var random = new Random(42);
        await WicTestImage.CreateAsync(path, 2048, 2048, jpeg: false,
            (_, _) => ((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
        Assert.True(new FileInfo(path).Length > 8 * 1024 * 1024);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        var result = MetadataWriterService.ReadVersionFamily(path);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(PhotoVersionRecipeStatus.VersionMissing, result.Status);
        Assert.True(allocated < 8 * 1024 * 1024, $"{extension} family read allocated {allocated} bytes.");
    }

    [Fact]
    public async Task RawSharedMetadataPreservesFamilyAndUnrelatedSidecarProperties()
    {
        var path = Path.Combine(_directory, "source.cr3");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var sidecar = Path.ChangeExtension(path, ".xmp");
        const string ns = "https://photolibrarian.test/raw-shared/";
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(ns, "plrawshared");
        var xmp = XmpMetaFactory.Create();
        xmp.SetProperty(ns, "plrawshared:Unknown", "keep");
        await File.WriteAllTextAsync(sidecar, XmpMetaFactory.SerializeToString(
            xmp, new XmpCore.Options.SerializeOptions()));
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryCreate(
            Guid.NewGuid(), "file-v1:fixture", Guid.NewGuid(), "Original", "<main/>",
            Guid.NewGuid(), "Copy", out var family));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(path, family!, null));

        await MetadataWriterService.WriteFlagAsync(path, true);
        await MetadataWriterService.WriteRatingAsync(path, 4);
        await MetadataWriterService.WriteColorLabelAsync(path, "Blue");
        await MetadataWriterService.WriteCaptionAsync(path, "caption");
        await MetadataWriterService.WriteDateTakenAsync(path, new DateTime(2020, 1, 2, 3, 4, 5));
        await TagWriterService.WriteTagsToSidecarAsync(path, ["tag"]);
        await new FaceMetadataStore().WriteAsync(path, new PhotoFaceMetadata(16, 16,
            [new PortableFaceMetadata(1, 0.1, 0.1, 0.3, 0.3, "Person", false, false, [])]));
        var geotagWriter = new GeotagMetadataWriterService(
            new OriginalBackupService(Path.Combine(_directory, "backups")));
        await geotagWriter.WriteAsync(path, new GeoCoordinate(10, 20));

        Assert.Equal(family, MetadataWriterService.ReadVersionFamily(path).Value);
        var actual = XmpMetaFactory.ParseFromString(await File.ReadAllTextAsync(sidecar));
        Assert.Equal("keep", actual.GetPropertyString(ns, "plrawshared:Unknown"));
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task TiffFamilyReadHandlesEmbeddedGpsDirectory()
    {
        var path = Path.Combine(_directory, "gps.tif");
        await WicTestImage.CreateAsync(path, 16, 16);
        var writer = new GeotagMetadataWriterService(
            new OriginalBackupService(Path.Combine(_directory, "backups")));
        await writer.WriteAsync(path, new GeoCoordinate(10, 20));
        Assert.Equal(PhotoVersionRecipeStatus.VersionMissing, MetadataWriterService.ReadVersionFamily(path).Status);
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryCreate(
            Guid.NewGuid(), "pixels-v1:fixture", Guid.NewGuid(), "Original", "<main/>",
            Guid.NewGuid(), "Copy", out var family));

        Assert.Equal(PhotoVersionRecipeStatus.Success,
            await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(path, family!, null));
        Assert.Equal(family, MetadataWriterService.ReadVersionFamily(path).Value);
    }

    [Fact]
    public async Task TiffFamilyWritePreservesExistingXmpProperties()
    {
        var path = Path.Combine(_directory, "existing.tif");
        await WicTestImage.CreateAsync(path, 16, 16);
        const string existing = "{\"future\":\"unrelated\"}";
        await EmbeddedMetadataWriter.WriteAsync(path, photoTagMetadata: existing);
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryCreate(
            Guid.NewGuid(), "pixels-v1:fixture", Guid.NewGuid(), "Original", "<main/>",
            Guid.NewGuid(), "Copy", out var family));

        Assert.Equal(PhotoVersionRecipeStatus.Success,
            await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(path, family!, null));
        var (status, xmp) = TiffFamilyXmpReader.Read(path, CancellationToken.None);
        Assert.Equal(PhotoVersionRecipeStatus.Success, status);
        Assert.Equal(existing, xmp!.GetPropertyString(
            MetadataWriterService.PhotoLibrarianNamespace, EmbeddedMetadataWriter.PhotoTagMetadataPropertyName));
        Assert.Equal(family, MetadataWriterService.ReadVersionFamily(path).Value);
    }

    [Fact]
    public async Task StagedTiffTranscodePreservesPixelsAndHonorsCancellation()
    {
        var path = Path.Combine(_directory, "transcode.tif");
        await WicTestImage.CreateAsync(path, 32, 24);
        var before = (await WindowsImagingCodec.DecodeAsync(path)).Pixels;
        var properties = new BitmapPropertySet
        {
            ["/ifd/xmp/{wstr=http://ns.photolibrarian.app/1.0/}:VirtualCopies"] =
                new BitmapTypedValue("family", Windows.Foundation.PropertyType.String)
        };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var originalBytes = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => EmbeddedMetadataWriter.TranscodeToStagedFileAsync(path, properties, cancellation.Token));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(_directory));

        await EmbeddedMetadataWriter.TranscodeToStagedFileAsync(path, properties, CancellationToken.None);
        var (status, xmp) = TiffFamilyXmpReader.Read(path, CancellationToken.None);
        Assert.Equal(PhotoVersionRecipeStatus.Success, status);
        Assert.Equal("family", xmp!.GetPropertyString(
            MetadataWriterService.PhotoLibrarianNamespace, "plib:VirtualCopies"));
        Assert.Equal(before, (await WindowsImagingCodec.DecodeAsync(path)).Pixels);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AtomicSidecarWriteRejectsStaleSnapshot(bool existed)
    {
        var path = Path.Combine(_directory, "source.xmp");
        if (existed)
            await File.WriteAllTextAsync(path, "before");
        var snapshot = TagWriterService.CaptureSidecarSnapshotAt(path);
        await File.WriteAllTextAsync(path, "external change");

        Assert.Throws<IOException>(() => TagWriterService.WriteOwnedSidecarAtomically(
            path, "our change", snapshot));
        Assert.Equal("external change", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task JpegFamilyUpdatesPreserveStandardAndExtendedPacketProperties()
    {
        const string ns = "https://photolibrarian.test/family-standard/";
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(ns, "plfamilytest");
        var path = Path.Combine(_directory, "family.jpg");
        await WicTestImage.CreateAsync(path, 16, 16, jpeg: true);
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryCreate(
            Guid.NewGuid(), "pixels-v1:fixture", Guid.NewGuid(), "Original",
            "<recipe>" + new string('x', 80_000) + "</recipe>", Guid.NewGuid(), "Copy", out var value));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(path, value!, null));
        EmbeddedMetadataCompatibilityTests.RewriteStandardPacket(path, standard =>
        {
            standard.SetProperty(ns, "plfamilytest:Unknown", "keep");
            standard.SetQualifier(ns, "plfamilytest:Unknown", ns, "plfamilytest:Qualifier", "keep too");
        });
        Assert.Equal(value, MetadataWriterService.ReadVersionFamily(path).Value);
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryRead(value!, out var document));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            document!.TryRenameCopy(document.GetRecords()[1].Id, "Renamed", document.Revision, out var updated));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(path, updated!, value));

        Assert.Equal(PhotoVersionRecipeStatus.Success, FaceMetadataStore.TryReadCompleteJpegXmp(path, out var xmp));
        Assert.NotNull(xmp);
        Assert.Equal("keep", xmp.GetPropertyString(ns, "plfamilytest:Unknown"));
        Assert.Equal("keep too", xmp.GetQualifier(ns, "plfamilytest:Unknown", ns, "plfamilytest:Qualifier")!.Value);
        Assert.Equal(updated, MetadataWriterService.ReadVersionFamily(path).Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task JpegFamilySharedMetadataPreservesBothPackets(bool flag)
    {
        const string ns = "https://photolibrarian.test/family-shared/";
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(ns, "plfamilyshared");
        var path = Path.Combine(_directory, "family-shared.jpg");
        await WicTestImage.CreateAsync(path, 16, 16, jpeg: true);
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryCreate(
            Guid.NewGuid(), "pixels-v1:fixture", Guid.NewGuid(), "Original",
            "<recipe>" + new string('x', 80_000) + "</recipe>", Guid.NewGuid(), "Copy", out var value));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(path, value!, null));
        EmbeddedMetadataCompatibilityTests.RewriteStandardPacket(path,
            standard => standard.SetProperty(ns, "plfamilyshared:Unknown", "keep"));

        if (flag)
            await MetadataWriterService.WriteFlagAsync(path, true);
        else
            await MetadataWriterService.WriteColorLabelAsync(path, "Blue");

        Assert.Equal(PhotoVersionRecipeStatus.Success, FaceMetadataStore.TryReadCompleteJpegXmp(path, out var xmp));
        Assert.Equal("keep", xmp!.GetPropertyString(ns, "plfamilyshared:Unknown"));
        Assert.Equal(value, MetadataWriterService.ReadVersionFamily(path).Value);
    }

    [Fact]
    public async Task JpegFaceWritePreservesBothFamilyPackets()
    {
        const string ns = "https://photolibrarian.test/family-faces/";
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(ns, "plfamilyfaces");
        var path = Path.Combine(_directory, "family-faces.jpg");
        await WicTestImage.CreateAsync(path, 16, 16, jpeg: true);
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryCreate(
            Guid.NewGuid(), "pixels-v1:fixture", Guid.NewGuid(), "Original",
            "<recipe>" + new string('x', 80_000) + "</recipe>", Guid.NewGuid(), "Copy", out var value));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(path, value!, null));
        EmbeddedMetadataCompatibilityTests.RewriteStandardPacket(path,
            standard => standard.SetProperty(ns, "plfamilyfaces:Unknown", "keep"));

        await new FaceMetadataStore().WriteAsync(path, new PhotoFaceMetadata(16, 16,
            [new PortableFaceMetadata(1, 0.1, 0.1, 0.3, 0.3, "Person", false, false, [])]));

        Assert.Equal(PhotoVersionRecipeStatus.Success, FaceMetadataStore.TryReadCompleteJpegXmp(path, out var xmp));
        Assert.Equal("keep", xmp!.GetPropertyString(ns, "plfamilyfaces:Unknown"));
        Assert.Equal(value, MetadataWriterService.ReadVersionFamily(path).Value);
    }

    [Fact]
    public async Task JpegFamilyConflictCannotBeWritten()
    {
        const string ns = "https://photolibrarian.test/family-conflict/";
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(ns, "plfamilyconflict");
        var path = Path.Combine(_directory, "family-conflict.jpg");
        await WicTestImage.CreateAsync(path, 16, 16, jpeg: true);
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryCreate(
            Guid.NewGuid(), "pixels-v1:fixture", Guid.NewGuid(), "Original",
            "<recipe>" + new string('x', 80_000) + "</recipe>", Guid.NewGuid(), "Copy", out var value));
        var extended = XmpMetaFactory.Create();
        extended.SetProperty(MetadataWriterService.PhotoLibrarianNamespace, "plib:VirtualCopies", value);
        extended.SetProperty(ns, "plfamilyconflict:Value", "extended");
        Assert.True(FaceMetadataStore.TryWriteEmbedded(path,
            XmpMetaFactory.SerializeToString(extended, new XmpCore.Options.SerializeOptions()).TrimStart('\uFEFF')));
        EmbeddedMetadataCompatibilityTests.RewriteStandardPacket(path,
            standard => standard.SetProperty(ns, "plfamilyconflict:Value", "standard"));
        var before = await File.ReadAllBytesAsync(path);

        Assert.Equal(PhotoVersionRecipeStatus.Conflict, MetadataWriterService.ReadVersionFamily(path).Status);
        Assert.Equal(PhotoVersionRecipeStatus.Conflict,
            await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(path, value!, value));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        await Assert.ThrowsAsync<IOException>(() => MetadataWriterService.WriteFlagAsync(path, true));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        await Assert.ThrowsAsync<IOException>(() =>
            new FaceMetadataStore().WriteAsync(path, new PhotoFaceMetadata(16, 16, [])));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task UnreferencedExtendedJpegChunksAreReportedWithoutChangingTheFile()
    {
        var path = Path.Combine(_directory, "missing-chunk.jpg");
        await WicTestImage.CreateAsync(path, 16, 16, jpeg: true);
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryCreate(
            Guid.NewGuid(), "pixels-v1:fixture", Guid.NewGuid(), "Original",
            "<recipe>" + new string('x', 80_000) + "</recipe>", Guid.NewGuid(), "Copy", out var value));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(path, value!, null));
        EmbeddedMetadataCompatibilityTests.RewriteStandardPacket(path,
            standard => standard.DeleteProperty("http://ns.adobe.com/xmp/note/", "xmpNote:HasExtendedXMP"));
        var before = await File.ReadAllBytesAsync(path);

        Assert.Equal(PhotoVersionRecipeStatus.InvalidMetadata, MetadataWriterService.ReadVersionFamily(path).Status);
        Assert.Equal(PhotoVersionRecipeStatus.InvalidMetadata,
            await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(path, value!, value));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task MalformedStandardJpegPacketIsNotReplacedByFamilyCreation()
    {
        const string ns = "https://photolibrarian.test/malformed-standard/";
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(ns, "plmalformed");
        var path = Path.Combine(_directory, "malformed-standard.jpg");
        await WicTestImage.CreateAsync(path, 16, 16, jpeg: true);
        var existing = XmpMetaFactory.Create();
        existing.SetProperty(ns, "plmalformed:Value", "unique-broken-value");
        Assert.True(FaceMetadataStore.TryWriteEmbedded(path,
            XmpMetaFactory.SerializeToString(existing, new XmpCore.Options.SerializeOptions()).TrimStart('\uFEFF')));
        var bytes = await File.ReadAllBytesAsync(path);
        var offset = bytes.AsSpan().IndexOf("unique-broken-value"u8);
        Assert.True(offset >= 0);
        bytes[offset] = 0;
        await File.WriteAllBytesAsync(path, bytes);
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryCreate(
            Guid.NewGuid(), "pixels-v1:fixture", Guid.NewGuid(), "Original", "<main/>",
            Guid.NewGuid(), "Copy", out var value));

        Assert.Equal(PhotoVersionRecipeStatus.InvalidMetadata, MetadataWriterService.ReadVersionFamily(path).Status);
        Assert.Equal(PhotoVersionRecipeStatus.InvalidMetadata,
            await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(path, value!, null));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".cr3")]
    public async Task QualifiedFamilyPropertyIsNotOverwritten(string extension)
    {
        var path = Path.Combine(_directory, "qualified" + extension);
        if (extension == ".cr3")
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
        else
            await WicTestImage.CreateAsync(path, 16, 16, jpeg: true);
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryCreate(
            Guid.NewGuid(), "file-v1:fixture", Guid.NewGuid(), "Original", "<main/>",
            Guid.NewGuid(), "Copy", out var value));
        var xmp = XmpMetaFactory.Create();
        const string futureNamespace = "https://photolibrarian.test/family-qualifier/";
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(futureNamespace, "futurefamily");
        xmp.SetProperty(MetadataWriterService.PhotoLibrarianNamespace, "plib:VirtualCopies", value);
        xmp.SetQualifier(MetadataWriterService.PhotoLibrarianNamespace, "plib:VirtualCopies",
            futureNamespace, "futurefamily:meaning", "preserve");
        var packet = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
        var metadataPath = extension == ".cr3" ? Path.ChangeExtension(path, ".xmp") : path;
        if (extension == ".cr3")
            await File.WriteAllTextAsync(metadataPath, packet);
        else
            Assert.True(FaceMetadataStore.TryWriteEmbedded(path, packet));
        var before = await File.ReadAllBytesAsync(metadataPath);

        Assert.Equal(PhotoVersionRecipeStatus.Unsupported, MetadataWriterService.ReadVersionFamily(path).Status);
        Assert.Equal(PhotoVersionRecipeStatus.Unsupported,
            await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(path, value!, value));
        Assert.Equal(before, await File.ReadAllBytesAsync(metadataPath));
    }

    [Theory]
    [InlineData("pixelBase")]
    [InlineData("mainId")]
    public async Task ImmutableFamilyIdentityCannotBeChanged(string field)
    {
        var path = Path.Combine(_directory, "source.png");
        await WicTestImage.CreateAsync(path, 16, 16);
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryCreate(
            Guid.NewGuid(), "pixels-v1:fixture", Guid.NewGuid(), "Original", "<main/>",
            Guid.NewGuid(), "Copy", out var value));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(path, value!, null));
        var before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var updated = System.Text.Json.Nodes.JsonNode.Parse(value!)!.AsObject();
        updated["revision"] = Guid.NewGuid().ToString("D");
        if (field == "pixelBase")
            updated["pixelBase"] = "pixels-v1:different";
        else
            updated["main"]!["id"] = Guid.NewGuid().ToString("D");

        Assert.Equal(PhotoVersionRecipeStatus.Conflict,
            await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(path, updated.ToJsonString(), value));
        Assert.Equal(before, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RawAndUnsupportedRasterCompanionsDoNotShareVersionFamilies()
    {
        var raw = Path.Combine(_directory, "source.cr3");
        var raster = Path.Combine(_directory, "source.bmp");
        await File.WriteAllBytesAsync(raw, [1, 2, 3], TestContext.Current.CancellationToken);
        await WicTestImage.CreateAsync(raster, 16, 16);
        var rawIdentity = await RecognitionSourceIdentity.ComputeAsync(raw);
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryCreate(
            Guid.NewGuid(), rawIdentity, Guid.NewGuid(), "Original", "<raw/>",
            Guid.NewGuid(), "Copy", out var rawValue));
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryCreate(
            Guid.NewGuid(), "pixels-v1:raster", Guid.NewGuid(), "Original", "<raster/>",
            Guid.NewGuid(), "Copy", out var rasterValue));

        using var rawTransaction = await FileEditTransaction.PrepareAsync(
            Path.Combine(_directory, "history"), [raw], async copies =>
                Assert.Equal(PhotoVersionRecipeStatus.Success,
                    await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(
                        copies[raw], rawValue!, null)));
        Assert.Equal(EditHistoryStatus.Applied,
            (await rawTransaction.MoveAsync(false, () => Task.CompletedTask)).Status);
        var rawSidecar = raw + ".xmp";
        var originalRawSidecar = await File.ReadAllBytesAsync(rawSidecar, TestContext.Current.CancellationToken);
        Assert.Equal(PhotoVersionRecipeStatus.VersionMissing, MetadataWriterService.ReadVersionFamily(raster).Status);
        using var rasterTransaction = await FileEditTransaction.PrepareAsync(
            Path.Combine(_directory, "history"), [raster], async copies =>
                Assert.Equal(PhotoVersionRecipeStatus.Success,
                    await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(
                        copies[raster], rasterValue!, null)));
        Assert.Equal(EditHistoryStatus.Applied,
            (await rasterTransaction.MoveAsync(false, () => Task.CompletedTask)).Status);

        Assert.Equal(rawValue, MetadataWriterService.ReadVersionFamily(raw).Value);
        Assert.Equal(rasterValue, MetadataWriterService.ReadVersionFamily(raster).Value);
        Assert.Equal(originalRawSidecar, await File.ReadAllBytesAsync(rawSidecar, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(raster + ".xmp"));
    }

    [Fact]
    public async Task TiffFamilyWritePreservesAllFrames()
    {
        var path = Path.Combine(_directory, "multipage.tif");
        using (var buffer = new InMemoryRandomAccessStream())
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.TiffEncoderId, buffer);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, 2, 2, 96, 96,
                [10, 20, 30, 255, 10, 20, 30, 255, 10, 20, 30, 255, 10, 20, 30, 255]);
            await encoder.GoToNextFrameAsync();
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, 2, 2, 96, 96,
                [30, 20, 10, 255, 30, 20, 10, 255, 30, 20, 10, 255, 30, 20, 10, 255]);
            await encoder.FlushAsync();
            buffer.Seek(0);
            await using var file = File.Create(path);
            await buffer.AsStreamForRead().CopyToAsync(file);
        }
        var before = await ReadFramesAsync(path);
        Assert.Equal(2, before.Count);
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryCreate(
            Guid.NewGuid(), "pixels-v1:fixture", Guid.NewGuid(), "Original", "<main/>",
            Guid.NewGuid(), "Copy", out var value));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(path, value!, null));
        var properties = new BitmapPropertySet
        {
            ["/ifd/xmp/{wstr=http://ns.photolibrarian.app/1.0/}:VirtualCopies"] =
                new BitmapTypedValue(value!, Windows.Foundation.PropertyType.String)
        };
        await EmbeddedMetadataWriter.TranscodeToStagedFileAsync(path, properties, CancellationToken.None);
        var after = await ReadFramesAsync(path);
        Assert.Equal(before.Count, after.Count);
        for (var index = 0; index < before.Count; index++)
            Assert.Equal(before[index], after[index]);
    }

    [Fact]
    public async Task TiffFamilyWritePreservesSixteenBitPixels()
    {
        var path = Path.Combine(_directory, "high-depth.tif");
        ushort[] samples = [1, 257, 65534, 65535, 4444, 32123, 55555, 65535];
        var pixels = new byte[samples.Length * sizeof(ushort)];
        System.Buffer.BlockCopy(samples, 0, pixels, 0, pixels.Length);
        using (var buffer = new InMemoryRandomAccessStream())
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.TiffEncoderId, buffer);
            encoder.SetPixelData(BitmapPixelFormat.Rgba16, BitmapAlphaMode.Straight, 2, 1, 96, 96, pixels);
            await encoder.FlushAsync();
            buffer.Seek(0);
            await using var file = File.Create(path);
            await buffer.AsStreamForRead().CopyToAsync(file);
        }
        async Task<byte[]> ReadPixels()
        {
            await using var file = File.OpenRead(path);
            var decoder = await BitmapDecoder.CreateAsync(file.AsRandomAccessStream());
            Assert.Equal(BitmapPixelFormat.Rgba16, decoder.BitmapPixelFormat);
            var data = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Rgba16, BitmapAlphaMode.Straight, new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            return data.DetachPixelData();
        }

        var before = await ReadPixels();
        Assert.Equal(pixels, before);
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryCreate(
            Guid.NewGuid(), "pixels-v1:fixture", Guid.NewGuid(), "Original", "<main/>",
            Guid.NewGuid(), "Copy", out var value));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(path, value!, null));
        var properties = new BitmapPropertySet
        {
            ["/ifd/xmp/{wstr=http://ns.photolibrarian.app/1.0/}:VirtualCopies"] =
                new BitmapTypedValue(value!, Windows.Foundation.PropertyType.String)
        };
        await EmbeddedMetadataWriter.TranscodeToStagedFileAsync(path, properties, CancellationToken.None);
        Assert.Equal(before, await ReadPixels());
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".tif")]
    [InlineData(".bmp")]
    [InlineData(".cr3")]
    public async Task FamilySurvivesRelocationWithoutCache(string extension)
    {
        var source = Path.Combine(_directory, "source" + extension);
        if (extension == ".cr3")
            await File.WriteAllBytesAsync(source, [1, 2, 3], TestContext.Current.CancellationToken);
        else
            await WicTestImage.CreateAsync(source, 16, 16, jpeg: extension == ".jpg");
        var sourceId = Guid.NewGuid();
        var mainId = Guid.NewGuid();
        var copyId = Guid.NewGuid();
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryCreate(
            sourceId, "pixels-v1:fixture", mainId, "Original", "<main/>",
            copyId, "Copy", out var value));
        using (var transaction = await FileEditTransaction.PrepareAsync(
            Path.Combine(_directory, "history"), [source], async copies =>
                Assert.Equal(PhotoVersionRecipeStatus.Success,
                    await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(copies[source], value!, null))))
            Assert.Equal(EditHistoryStatus.Applied,
                (await transaction.MoveAsync(false, () => Task.CompletedTask)).Status);

        var destinationDirectory = Path.Combine(_directory, "relocated");
        Directory.CreateDirectory(destinationDirectory);
        var destination = Path.Combine(destinationDirectory, "renamed" + extension);
        File.Move(source, destination);
        var sidecar = Path.ChangeExtension(source, ".xmp");
        if (File.Exists(sidecar))
            File.Move(sidecar, Path.ChangeExtension(destination, ".xmp"));

        var (status, stored) = MetadataWriterService.ReadVersionFamily(destination);
        Assert.Equal(PhotoVersionRecipeStatus.Success, status);
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryRead(stored!, out var document));
        Assert.Equal(sourceId, document!.SourceId);
        Assert.Equal(new[] { mainId, copyId }, document.GetRecords().Select(record => record.Id));
        Assert.Equal(value, stored);
    }

    [Fact]
    public async Task AddingCompanionToExistingRasterSidecarDoesNotHideItsFamily()
    {
        var raster = Path.Combine(_directory, "source.bmp");
        var raw = Path.Combine(_directory, "source.cr3");
        await WicTestImage.CreateAsync(raster, 16, 16);
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryCreate(
            Guid.NewGuid(), "pixels-v1:raster", Guid.NewGuid(), "Original", "<main/>",
            Guid.NewGuid(), "Copy", out var value));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(raster, value!, null));
        var sidecar = Path.ChangeExtension(raster, ".xmp");
        var original = await File.ReadAllBytesAsync(sidecar, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(raw, [1, 2, 3], TestContext.Current.CancellationToken);

        Assert.Equal(PhotoVersionRecipeStatus.Conflict, MetadataWriterService.ReadVersionFamily(raster).Status);
        Assert.Equal(PhotoVersionRecipeStatus.Conflict, MetadataWriterService.ReadVersionFamily(raw).Status);
        Assert.Equal(PhotoVersionRecipeStatus.Conflict,
            await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(raster, value!, null));
        Assert.Equal(original, await File.ReadAllBytesAsync(sidecar, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IndistinguishableRawCompanionsDoNotClaimTheSameFamily()
    {
        var first = Path.Combine(_directory, "source.cr3");
        var second = Path.Combine(_directory, "source.nef");
        await File.WriteAllBytesAsync(first, [1, 2, 3], TestContext.Current.CancellationToken);
        var identity = await RecognitionSourceIdentity.ComputeAsync(first);
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryCreate(
            Guid.NewGuid(), identity, Guid.NewGuid(), "Original", "<main/>",
            Guid.NewGuid(), "Copy", out var value));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(first, value!, null));
        await File.WriteAllBytesAsync(second, [1, 2, 3], TestContext.Current.CancellationToken);

        Assert.Equal(PhotoVersionRecipeStatus.Conflict, MetadataWriterService.ReadVersionFamily(first).Status);
        Assert.Equal(PhotoVersionRecipeStatus.Conflict, MetadataWriterService.ReadVersionFamily(second).Status);
    }

    [Fact]
    public async Task StagedWriteDoesNotTreatCompanionPlaceholderAsProofOfSidecarOwnership()
    {
        var first = Path.Combine(_directory, "source.cr3");
        var second = Path.Combine(_directory, "source.nef");
        await File.WriteAllBytesAsync(first, [1, 2, 3], TestContext.Current.CancellationToken);
        var identity = await RecognitionSourceIdentity.ComputeAsync(first);
        var copyId = Guid.NewGuid();
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryCreate(
            Guid.NewGuid(), identity, Guid.NewGuid(), "Original", "<main/>",
            copyId, "Copy", out var value));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(first, value!, null));
        var sidecar = Path.ChangeExtension(first, ".xmp");
        var before = await File.ReadAllBytesAsync(sidecar, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(second, [1, 2, 3], TestContext.Current.CancellationToken);
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryRead(value!, out var document));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            document!.TryUpdateRecipe(copyId, document.Revision, "<new/>", out var updated));

        using var transaction = await FileEditTransaction.PrepareAsync(
            Path.Combine(_directory, "history"), [first], async copies =>
                Assert.Equal(PhotoVersionRecipeStatus.Conflict,
                    await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(
                        copies[first], updated!, value)));
        Assert.False(transaction.HasChanges);
        Assert.Equal(before, await File.ReadAllBytesAsync(sidecar, TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 1, 2, 3 },
            await File.ReadAllBytesAsync(second, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EmbeddedMigrationRetainsUnrelatedLegacySidecarProperties()
    {
        var path = Path.Combine(_directory, "source.jpg");
        await WicTestImage.CreateAsync(path, 16, 16, jpeg: true);
        var copyId = Guid.NewGuid();
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryCreate(
            Guid.NewGuid(), "pixels-v1:fixture", Guid.NewGuid(), "Original", "<main/>",
            copyId, "Copy", out var value));
        var xmp = XmpMetaFactory.Create();
        XmpMetaFactory.SchemaRegistry.RegisterNamespace("http://ns.example.test/", "example");
        xmp.SetProperty("http://ns.example.test/", "example:Keep", "preserved");
        xmp.SetProperty(MetadataWriterService.PhotoLibrarianNamespace, "plib:VirtualCopies", value);
        var sidecar = Path.ChangeExtension(path, ".xmp");
        await File.WriteAllTextAsync(sidecar, XmpMetaFactory.SerializeToString(
            xmp, new XmpCore.Options.SerializeOptions()), TestContext.Current.CancellationToken);
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryRead(value!, out var document));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            document!.TryUpdateRecipe(copyId, document.Revision, "<new/>", out var updated));

        using var transaction = await FileEditTransaction.PrepareAsync(
            Path.Combine(_directory, "history"), [path], async copies =>
                Assert.Equal(PhotoVersionRecipeStatus.Success,
                    await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(copies[path], updated!, value)));
        Assert.Equal(EditHistoryStatus.Applied, (await transaction.MoveAsync(false, () => Task.CompletedTask)).Status);
        Assert.Equal(updated, MetadataWriterService.ReadVersionFamily(path).Value);
        var remaining = XmpMetaFactory.ParseFromString(
            await File.ReadAllTextAsync(sidecar, TestContext.Current.CancellationToken));
        Assert.Equal("preserved", remaining.GetPropertyString("http://ns.example.test/", "example:Keep"));
        Assert.False(MetadataSidecarLocator.OwnsVersionFamily(remaining));
        Assert.Equal(EditHistoryStatus.Applied, (await transaction.MoveAsync(true, () => Task.CompletedTask)).Status);
        Assert.Equal(value, MetadataWriterService.ReadVersionFamily(path).Value);
    }

    [Fact]
    public async Task InvalidSupportedFormatDoesNotCreateSidecar()
    {
        var path = Path.Combine(_directory, "broken.jpg");
        await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryCreate(
            Guid.NewGuid(), "pixels-v1:fixture", Guid.NewGuid(), "Original", "<main/>",
            Guid.NewGuid(), "Copy", out var value));
        Assert.Equal(PhotoVersionRecipeStatus.InvalidMetadata,
            await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(path, value!, null));
        Assert.Equal(new byte[] { 1, 2, 3 },
            await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(path + ".xmp"));
        Assert.False(File.Exists(Path.ChangeExtension(path, ".xmp")));
    }

    private static async Task<List<byte[]>> ReadFramesAsync(string path)
    {
        await using var file = File.OpenRead(path);
        var decoder = await BitmapDecoder.CreateAsync(file.AsRandomAccessStream());
        var frames = new List<byte[]>();
        for (uint index = 0; index < decoder.FrameCount; index++)
        {
            var frame = await decoder.GetFrameAsync(index);
            frames.Add((await frame.GetPixelDataAsync()).DetachPixelData());
        }
        return frames;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
