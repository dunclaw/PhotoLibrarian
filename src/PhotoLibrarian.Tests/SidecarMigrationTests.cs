// Copyright (c) Duncan Lawler. All Rights Reserved.
using MetadataExtractor;
using MetadataExtractor.Formats.Xmp;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using System.Text;
using System.Xml.Linq;
using XmpCore;
using XmpCore.Options;
using Xunit;
using Directory = System.IO.Directory;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace PhotoLibrarian.Tests;

public sealed class SidecarMigrationTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-migration-{Guid.NewGuid():N}");
    private readonly SidecarMigrationService _service = new();
    private CancellationToken Token => TestContext.Current.CancellationToken;

    public SidecarMigrationTests() => Directory.CreateDirectory(_folder);

    [Theory]
    [InlineData(".jpg", false)]
    [InlineData(".png", true)]
    public async Task RoundTrip_PreservesWholePacketPixelsAndOriginalBackups(string extension, bool qualifiedName)
    {
        var image = await CreateImageAsync("photo" + extension);
        var sidecar = qualifiedName ? image + ".xmp" : Path.ChangeExtension(image, ".xmp");
        var packet = CreatePortablePacket();
        File.WriteAllText(sidecar, packet);
        var mediaBefore = File.ReadAllBytes(image);
        var sidecarBefore = File.ReadAllBytes(sidecar);
        var creationBefore = File.GetCreationTimeUtc(image);
        var identityBefore = GetFileId(image);
        var nonXmpBefore = MigrationXmpPacket.Inspect(mediaBefore, extension).NonXmpBytes;

        var preview = await _service.PreviewAsync(_folder, cancellationToken: Token);
        Assert.Equal(SidecarMigrationState.Ready, Assert.Single(preview).State);
        Assert.Equal(mediaBefore, File.ReadAllBytes(image));
        Assert.Equal(sidecarBefore, File.ReadAllBytes(sidecar));
        var result = Assert.Single(await _service.MigrateAsync(preview, cancellationToken: Token));
        Assert.Equal(SidecarMigrationState.Migrated, result.State);
        Assert.False(File.Exists(sidecar));
        Assert.Equal(creationBefore, File.GetCreationTimeUtc(image));
        Assert.Equal(identityBefore, GetFileId(image));
        var after = MigrationXmpPacket.Inspect(File.ReadAllBytes(image), extension);
        Assert.Equal(nonXmpBefore, after.NonXmpBytes);
        Assert.True(MigrationXmpPacket.Equivalent(sidecarBefore, after.Packet!));
        Assert.Equal(mediaBefore, File.ReadAllBytes(Path.Combine(result.BackupDirectory!, Path.GetFileName(image) + ".original")));
        Assert.Equal(sidecarBefore, File.ReadAllBytes(Path.Combine(result.BackupDirectory!, Path.GetFileName(sidecar) + ".original")));
        // Verify through actual application readers, not only the container inspection helper.
        var xmp = ImageMetadataReader.ReadMetadata(image).OfType<XmpDirectory>().Single().XmpMeta!;
        Assert.Equal("True", xmp.GetPropertyString(MetadataWriterService.PhotoLibrarianNamespace, "Flagged"));
        Assert.Contains("pipeline-v1", xmp.GetPropertyString(MetadataWriterService.PhotoLibrarianNamespace, "TagMetadata"));
        var face = Assert.Single(new FaceMetadataStore().Read(image).Faces);
        Assert.Equal("Alex", face.PersonName);
        Assert.True(face.IsPersonRepresentative);
        Assert.True(face.SuggestionsHidden);
        Assert.Equal(5, new MetadataReaderService().ReadMetadata(image).Rating);
        var portable = new MetadataReaderService().ReadPortableTagMetadata(image);
        Assert.NotNull(portable);
        Assert.Equal("pipeline-v1", portable.AutoTagScanVersion);
        var tag = Assert.Single(portable.Tags);
        Assert.Equal("Auto/landscape", tag.Tag);
        Assert.Equal("AutoML", tag.Source);
        Assert.Equal(0.91f, tag.Confidence);
        Assert.Empty(await _service.PreviewAsync(_folder, cancellationToken: Token));
    }

    [Fact]
    public async Task Merge_DisjointUnknownPropertiesKeepsExistingEmbeddedValues()
    {
        var image = await CreateImageAsync("photo.jpg");
        const string embedded = "<u:Embedded xmlns:u='urn:migration:test'>keep</u:Embedded>";
        Assert.True(FaceMetadataStore.TryWriteEmbedded(image, Packet(embedded)));
        File.WriteAllText(Path.ChangeExtension(image, ".xmp"), Packet("<v:Sidecar xmlns:v='urn:unknown:sidecar'><rdf:Seq><rdf:li xml:lang='en'>retain</rdf:li></rdf:Seq></v:Sidecar>"));
        var before = MigrationXmpPacket.Inspect(File.ReadAllBytes(image), ".jpg");
        var result = Assert.Single(await _service.MigrateAsync(
            await _service.PreviewAsync(_folder, cancellationToken: Token), cancellationToken: Token));
        Assert.Equal(SidecarMigrationState.Migrated, result.State);
        var after = MigrationXmpPacket.Inspect(File.ReadAllBytes(image), ".jpg");
        Assert.Equal(before.NonXmpBytes, after.NonXmpBytes);
        var xml = XDocument.Parse(Encoding.UTF8.GetString(after.Packet!));
        Assert.Equal("keep", xml.Descendants(XName.Get("Embedded", "urn:migration:test")).Single().Value);
        Assert.Equal("retain", xml.Descendants(XName.Get("Sidecar", "urn:unknown:sidecar")).Single().Value);
    }

    [Fact]
    public async Task ExistingExifOrientationAndCaptionStayUnchanged()
    {
        var image = await CreateImageAsync("oriented.jpg");
        await EmbeddedMetadataWriter.WriteAsync(image, orientation: 6, title: "Existing caption");
        var before = File.ReadAllBytes(image);
        Assert.Equal(6, new MetadataReaderService().ReadMetadata(image).Orientation);
        File.WriteAllText(Path.ChangeExtension(image, ".xmp"), CreatePortablePacket());
        var preview = await _service.PreviewAsync(_folder, cancellationToken: Token);
        var candidate = Assert.Single(preview);
        Assert.True(candidate.State == SidecarMigrationState.Ready,
            candidate.Reason + ": " + candidate.Error + Environment.NewLine +
            Encoding.UTF8.GetString(MigrationXmpPacket.Inspect(before, ".jpg").Packet ?? []));
        var result = Assert.Single(await _service.MigrateAsync(preview, cancellationToken: Token));
        Assert.Equal(SidecarMigrationState.Migrated, result.State);
        var after = File.ReadAllBytes(image);
        Assert.Equal(MigrationXmpPacket.Inspect(before, ".jpg").NonXmpBytes,
            MigrationXmpPacket.Inspect(after, ".jpg").NonXmpBytes);
        XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        var oldSubject = XDocument.Parse(Encoding.UTF8.GetString(MigrationXmpPacket.Inspect(before, ".jpg").Packet!))
            .Descendants(rdf + "Description").First().Attribute(rdf + "about")!.Value;
        var newSubject = XDocument.Parse(Encoding.UTF8.GetString(MigrationXmpPacket.Inspect(after, ".jpg").Packet!))
            .Descendants(rdf + "Description").Single().Attribute(rdf + "about")!.Value;
        Assert.Equal(oldSubject, newSubject);
        var metadata = new MetadataReaderService().ReadMetadata(image);
        Assert.Equal(6, metadata.Orientation);
        Assert.Equal("Existing caption", metadata.Caption);
        Assert.Equal((8u, 6u), await WicTestImage.ReadSizeAsync(image));
    }

    [Fact]
    public async Task ConflictsAndDuplicatePacketsAreUntouched()
    {
        var image = await CreateImageAsync("photo.png");
        Assert.True(FaceMetadataStore.TryWriteEmbedded(image, Packet("<xmp:Rating xmlns:xmp='http://ns.adobe.com/xap/1.0/'>3</xmp:Rating>")));
        var before = File.ReadAllBytes(image);
        var sidecar = Path.ChangeExtension(image, ".xmp");
        File.WriteAllText(sidecar, Packet("<xmp:Rating xmlns:xmp='http://ns.adobe.com/xap/1.0/'>5</xmp:Rating>"));
        var preview = await _service.PreviewAsync(_folder, cancellationToken: Token);
        Assert.Equal("MigrationConflict", Assert.Single(preview).Reason);
        await _service.MigrateAsync(preview, cancellationToken: Token);
        Assert.Equal(before, File.ReadAllBytes(image));
        Assert.True(File.Exists(sidecar));

        File.WriteAllText(sidecar, Packet("<u:A xmlns:u='urn:test'>a</u:A><u:A xmlns:u='urn:test'>b</u:A>"));
        Assert.Equal(SidecarMigrationState.Skipped,
            Assert.Single(await _service.PreviewAsync(_folder, cancellationToken: Token)).State);
    }

    [Theory]
    [InlineData(".cr2")]
    [InlineData(".mp4")]
    [InlineData(".tiff")]
    [InlineData(".heic")]
    public async Task UnsupportedMediaRemainsByteExact(string extension)
    {
        var image = Path.Combine(_folder, "photo" + extension);
        File.WriteAllBytes(image, [1, 2, 3, 4]);
        var sidecar = Path.ChangeExtension(image, ".xmp");
        File.WriteAllText(sidecar, CreatePortablePacket());
        var packet = File.ReadAllBytes(sidecar);
        var preview = await _service.PreviewAsync(_folder, cancellationToken: Token);
        Assert.Equal("MigrationUnsupported", Assert.Single(preview).Reason);
        await _service.MigrateAsync(preview, cancellationToken: Token);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(image));
        Assert.Equal(packet, File.ReadAllBytes(sidecar));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AmbiguousAssociationsNeverRemoveSidecars(bool sharedBasename)
    {
        var image = await CreateImageAsync("photo.jpg");
        File.WriteAllText(Path.ChangeExtension(image, ".xmp"), CreatePortablePacket());
        if (sharedBasename) File.WriteAllBytes(Path.ChangeExtension(image, ".cr2"), [1, 2, 3]);
        else File.WriteAllText(image + ".xmp", CreatePortablePacket());
        var before = File.ReadAllBytes(image);
        var preview = await _service.PreviewAsync(_folder, cancellationToken: Token);
        Assert.All(preview, p => Assert.Equal("MigrationAmbiguous", p.Reason));
        await _service.MigrateAsync(preview, cancellationToken: Token);
        Assert.Equal(before, File.ReadAllBytes(image));
        Assert.True(File.Exists(Path.ChangeExtension(image, ".xmp")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangesSincePreviewAreNotOverwritten(bool changeSidecar)
    {
        var image = await CreateImageAsync("photo.jpg");
        var sidecar = Path.ChangeExtension(image, ".xmp");
        File.WriteAllText(sidecar, CreatePortablePacket());
        var preview = await _service.PreviewAsync(_folder, cancellationToken: Token);
        if (changeSidecar) File.AppendAllText(sidecar, " ");
        else File.AppendAllText(image, "newer bytes");
        var mediaBefore = File.ReadAllBytes(image);
        var packetBefore = File.ReadAllBytes(sidecar);
        var result = Assert.Single(await _service.MigrateAsync(preview, cancellationToken: Token));
        Assert.Equal("MigrationChanged", result.Reason);
        Assert.Equal(mediaBefore, File.ReadAllBytes(image));
        Assert.Equal(packetBefore, File.ReadAllBytes(sidecar));
    }

    [Fact]
    public async Task LockFailureDoesNotBlockOtherFiles_AndCancellationLeavesRemainingUntouched()
    {
        var first = await CreateImageAsync("one.jpg");
        var second = await CreateImageAsync("two.png");
        foreach (var image in new[] { first, second })
            File.WriteAllText(Path.ChangeExtension(image, ".xmp"), CreatePortablePacket());
        var preview = await _service.PreviewAsync(_folder, cancellationToken: Token);
        var firstBefore = File.ReadAllBytes(first);
        using (var locked = File.Open(first, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var results = await _service.MigrateAsync(preview, cancellationToken: Token);
            Assert.Single(results, item => item.State == SidecarMigrationState.Failed);
            Assert.Single(results, item => item.State == SidecarMigrationState.Migrated);
        }
        Assert.Equal(firstBefore, File.ReadAllBytes(first));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var canceledResults = await _service.MigrateAsync(
            await _service.PreviewAsync(_folder, cancellationToken: Token), cancellationToken: canceled.Token);
        Assert.Equal(SidecarMigrationState.Canceled, Assert.Single(canceledResults).State);
        Assert.Equal(firstBefore, File.ReadAllBytes(first));
        Assert.True(File.Exists(Path.ChangeExtension(first, ".xmp")));
    }

    [Fact]
    public async Task ReadOnlySidecarAndRecipeAndOrientationAreRetained()
    {
        var image = await CreateImageAsync("photo.jpg");
        var sidecar = Path.ChangeExtension(image, ".xmp");
        File.WriteAllText(sidecar, CreatePortablePacket());
        var before = File.ReadAllBytes(image);
        File.SetAttributes(sidecar, FileAttributes.ReadOnly);
        try
        {
            Assert.Equal("MigrationFileError", Assert.Single(
                await _service.PreviewAsync(_folder, cancellationToken: Token)).Reason);
        }
        finally { File.SetAttributes(sidecar, FileAttributes.Normal); }

        var xmp = XmpMetaFactory.Create();
        EditParametersSerializer.WriteToXmp(xmp, new EditParameters { Exposure = 1.25, NoiseReduction = 0.3 });
        var recipe = XmpMetaFactory.SerializeToString(xmp, new SerializeOptions()).TrimStart('\uFEFF');
        // The packet merger retains recipe properties exactly, but the migration refuses
        // to make the current sidecar-only recipe-clear path unsafe.
        var merged = MigrationXmpPacket.Merge(null, Encoding.UTF8.GetBytes(recipe));
        Assert.Equal(1.25, EditParametersSerializer.ReadFromXmp(XmpMetaFactory.ParseFromString(merged)).Exposure);
        File.WriteAllText(sidecar, recipe);
        var preview = await _service.PreviewAsync(_folder, cancellationToken: Token);
        Assert.Equal("MigrationRecipe", Assert.Single(preview).Reason);
        await _service.MigrateAsync(preview, cancellationToken: Token);
        Assert.Equal(recipe, File.ReadAllText(sidecar));
        File.WriteAllText(sidecar, Packet("<tiff:Orientation xmlns:tiff='http://ns.adobe.com/tiff/1.0/'>6</tiff:Orientation>"));
        Assert.Equal("MigrationOrientation", Assert.Single(
            await _service.PreviewAsync(_folder, cancellationToken: Token)).Reason);
        Assert.Equal(before, File.ReadAllBytes(image));
    }

    [Fact]
    public async Task InvalidXmlAndOversizedJpegPacketAreSkipped()
    {
        var image = await CreateImageAsync("photo.jpg");
        var sidecar = Path.ChangeExtension(image, ".xmp");
        File.WriteAllText(sidecar, "<broken>");
        Assert.Equal(SidecarMigrationState.Skipped, Assert.Single(
            await _service.PreviewAsync(_folder, cancellationToken: Token)).State);
        File.WriteAllText(sidecar, Packet("<u:Large xmlns:u='urn:test'>" + new string('x', 65535) + "</u:Large>"));
        Assert.Equal("MigrationPacketTooLarge", Assert.Single(
            await _service.PreviewAsync(_folder, cancellationToken: Token)).Reason);
        Assert.True(File.Exists(sidecar));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CommitFaultsRollbackOriginalAndRetainSidecar(bool throwDuringWrite)
    {
        var image = await CreateImageAsync("photo.png");
        var sidecar = Path.ChangeExtension(image, ".xmp");
        File.WriteAllText(sidecar, CreatePortablePacket());
        var before = File.ReadAllBytes(image);
        var packetBefore = File.ReadAllBytes(sidecar);
        var modifiedBefore = File.GetLastWriteTimeUtc(image);
        var service = new SidecarMigrationService((stream, candidate) =>
        {
            // Both path replacement and direct writes by competing editors are denied.
            Assert.Throws<IOException>(() => File.Move(sidecar, sidecar + ".renamed"));
            Assert.Throws<IOException>(() => File.WriteAllText(sidecar, "newer metadata"));
            Assert.Throws<IOException>(() => File.Move(image, image + ".renamed"));
            stream.Position = 0;
            stream.Write(candidate.AsSpan(0, candidate.Length / 2));
            if (throwDuringWrite) throw new IOException("Injected interrupted write.");
            // Returning an incomplete write exercises verification-triggered rollback too.
            stream.SetLength(candidate.Length / 2);
            stream.Flush(true);
        });
        var result = Assert.Single(await service.MigrateAsync(
            await service.PreviewAsync(_folder, cancellationToken: Token), cancellationToken: Token));
        Assert.Equal(SidecarMigrationState.Failed, result.State);
        Assert.NotNull(result.BackupDirectory);
        Assert.Equal(before, File.ReadAllBytes(image));
        Assert.Equal(packetBefore, File.ReadAllBytes(sidecar));
        Assert.Equal(modifiedBefore, File.GetLastWriteTimeUtc(image));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(result.BackupDirectory, "photo.png.original")));
    }

    [Fact]
    public async Task MidBatchCancellationFinishesOneFileAndLeavesRestUnchanged()
    {
        foreach (var name in new[] { "one.jpg", "two.png", "three.jpg" })
        {
            var image = await CreateImageAsync(name);
            File.WriteAllText(Path.ChangeExtension(image, ".xmp"), CreatePortablePacket());
        }
        var preview = await _service.PreviewAsync(_folder, cancellationToken: Token);
        var originals = preview.ToDictionary(item => item.ImagePath!, item => File.ReadAllBytes(item.ImagePath!));
        using var cancellation = new CancellationTokenSource();
        var progress = new ImmediateProgress(_ => cancellation.Cancel());
        var results = await _service.MigrateAsync(preview, progress, cancellation.Token);
        Assert.Single(results, item => item.State == SidecarMigrationState.Migrated);
        Assert.Equal(2, results.Count(item => item.State == SidecarMigrationState.Canceled));
        foreach (var untouched in results.Where(item => item.State == SidecarMigrationState.Canceled))
        {
            Assert.Equal(originals[untouched.ImagePath!], File.ReadAllBytes(untouched.ImagePath!));
            Assert.True(File.Exists(untouched.SidecarPath));
        }
    }

    [Fact]
    public async Task SidecarDeletionFailureRollsBackVerifiedMedia()
    {
        var image = await CreateImageAsync("photo.png");
        var sidecar = Path.ChangeExtension(image, ".xmp");
        File.WriteAllText(sidecar, CreatePortablePacket());
        var before = File.ReadAllBytes(image);
        var packetBefore = File.ReadAllBytes(sidecar);
        var service = new SidecarMigrationService((stream, candidate) =>
        {
            stream.Position = 0;
            stream.Write(candidate);
            stream.SetLength(candidate.Length);
            stream.Flush(true);
            File.SetAttributes(sidecar, FileAttributes.ReadOnly);
        });
        try
        {
            var result = Assert.Single(await service.MigrateAsync(
                await service.PreviewAsync(_folder, cancellationToken: Token), cancellationToken: Token));
            Assert.Equal(SidecarMigrationState.Failed, result.State);
            Assert.Equal(before, File.ReadAllBytes(image));
            Assert.Equal(packetBefore, File.ReadAllBytes(sidecar));
            Assert.NotNull(result.BackupDirectory);
            Assert.DoesNotContain(Directory.GetFiles(result.BackupDirectory), path =>
                FolderScannerService.IsSupportedFile(path));
        }
        finally { File.SetAttributes(sidecar, FileAttributes.Normal); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExtendedAndDuplicateJpegPacketsAreSkipped(bool extended)
    {
        var image = await CreateImageAsync("photo.jpg");
        Assert.True(FaceMetadataStore.TryWriteEmbedded(image, Packet("<u:A xmlns:u='urn:test'>a</u:A>")));
        var bytes = File.ReadAllBytes(image);
        var header = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
        var headerOffset = bytes.AsSpan().IndexOf(header);
        Assert.True(headerOffset > 4);
        var segmentOffset = headerOffset - 4;
        var segmentLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(segmentOffset + 2, 2)) + 2;
        var extra = extended
            ? Encoding.ASCII.GetBytes("http://ns.adobe.com/xmp/extension/\0")
            : bytes.AsSpan(segmentOffset, segmentLength).ToArray();
        using (var stream = File.Create(image))
        {
            stream.Write(bytes.AsSpan(0, 2));
            stream.Write(extra);
            stream.Write(bytes.AsSpan(2));
        }
        var before = File.ReadAllBytes(image);
        File.WriteAllText(Path.ChangeExtension(image, ".xmp"), CreatePortablePacket());
        var preview = await _service.PreviewAsync(_folder, cancellationToken: Token);
        Assert.Equal(SidecarMigrationState.Skipped, Assert.Single(preview).State);
        await _service.MigrateAsync(preview, cancellationToken: Token);
        Assert.Equal(before, File.ReadAllBytes(image));
        Assert.True(File.Exists(Path.ChangeExtension(image, ".xmp")));
    }

    [Fact]
    public void ConflictingUnknownPrefixBindingsCannotBeSilentlyCollapsed()
    {
        var first = Encoding.UTF8.GetBytes(Packet("<u:A xmlns:u='urn:test' xmlns:q='urn:first'>q:value</u:A>"));
        var second = Encoding.UTF8.GetBytes(Packet("<u:A xmlns:u='urn:test' xmlns:q='urn:second'>q:value</u:A>"));
        Assert.False(MigrationXmpPacket.Equivalent(first, second));
        Assert.Throws<InvalidDataException>(() => MigrationXmpPacket.Merge(first, second));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadOnlyMediaOrSidecarAfterPreviewCannotBeModified(bool readOnlyMedia)
    {
        var image = await CreateImageAsync("photo.png");
        var sidecar = Path.ChangeExtension(image, ".xmp");
        File.WriteAllText(sidecar, CreatePortablePacket());
        var before = File.ReadAllBytes(image);
        var preview = await _service.PreviewAsync(_folder, cancellationToken: Token);
        var path = readOnlyMedia ? image : sidecar;
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var result = Assert.Single(await _service.MigrateAsync(preview, cancellationToken: Token));
            Assert.Equal(SidecarMigrationState.Failed, result.State);
            Assert.Equal(before, File.ReadAllBytes(image));
            Assert.True(File.Exists(sidecar));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    [Fact]
    public void WhitespaceLiteralUnknownFieldsAreNotTrimmed()
    {
        var packet = Encoding.UTF8.GetBytes(Packet("<u:Blank xmlns:u='urn:test'>   </u:Blank>"));
        var merged = MigrationXmpPacket.Merge(null, packet);
        var xml = XDocument.Parse(merged, LoadOptions.PreserveWhitespace);
        Assert.Equal("   ", xml.Descendants(XName.Get("Blank", "urn:test")).Single().Value);
        Assert.True(MigrationXmpPacket.Equivalent(packet, Encoding.UTF8.GetBytes(merged)));
    }

    [Fact]
    public void ConflictingXmpAliasesAreNotMerged()
    {
        var embedded = Encoding.UTF8.GetBytes(Packet("""
            <dc:creator xmlns:dc="http://purl.org/dc/elements/1.1/"><rdf:Seq><rdf:li>Original</rdf:li></rdf:Seq></dc:creator>
            """));
        var sidecar = Encoding.UTF8.GetBytes(Packet("""
            <xmp:Author xmlns:xmp="http://ns.adobe.com/xap/1.0/">Different</xmp:Author>
            """));
        Assert.Throws<XmpException>(() => MigrationXmpPacket.Merge(embedded, sidecar));
    }

    [Fact]
    public void ConflictingExplicitSubjectsAreNotRetargeted()
    {
        var first = Encoding.UTF8.GetBytes(Packet("<u:A xmlns:u='urn:test'>a</u:A>")
            .Replace("rdf:about=''", "rdf:about='urn:image:first'", StringComparison.Ordinal));
        var second = Encoding.UTF8.GetBytes(Packet("<u:B xmlns:u='urn:test'>b</u:B>")
            .Replace("rdf:about=''", "rdf:about='urn:image:second'", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => MigrationXmpPacket.Merge(first, second));
    }

    [Fact]
    public void InheritedXmlContextIsSkippedRatherThanReinterpretedAsMetadata()
    {
        var packet = Packet("<u:A xmlns:u='urn:test'>value</u:A>")
            .Replace("rdf:about=''", "rdf:about='' xml:lang='fr'", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => MigrationXmpPacket.Merge(null, Encoding.UTF8.GetBytes(packet)));
    }

    [Fact]
    public void MigrationUiHasLocalizedLabelsKeyboardControlsAndLiveStatus()
    {
        var view = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "SidecarMigrationPanel.xaml"));
        var resources = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "Resources.resw"))
            .Root!.Elements("data").Select(e => (string)e.Attribute("name")!).ToHashSet();
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var interactive = view.Descendants().Where(e => e.Name == presentation + "Button" ||
            e.Name == presentation + "CheckBox" || e.Name == presentation + "ListView").ToArray();
        Assert.Equal(3, interactive.Length);
        Assert.All(interactive, control => Assert.NotNull(control.Attribute("AutomationProperties.AutomationId")));
        foreach (var element in view.Descendants().Where(e => e.Attribute(xaml + "Uid") is not null))
        {
            var uid = (string)element.Attribute(xaml + "Uid")!;
            Assert.Contains(resources, key => key.StartsWith(uid + ".", StringComparison.Ordinal));
        }
        Assert.Contains(view.Descendants(), e => (string?)e.Attribute("AutomationProperties.LiveSetting") == "Polite");
        Assert.DoesNotContain(view.Descendants(), e => e.Name == presentation + "ScrollViewer");
    }

    private async Task<string> CreateImageAsync(string name)
    {
        var path = Path.Combine(_folder, name);
        await WicTestImage.CreateAsync(path, 8, 6, Path.GetExtension(name) == ".jpg");
        return path;
    }

    private static string Packet(string properties) =>
        "<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'>" +
        "<rdf:Description rdf:about=''>" + properties + "</rdf:Description></rdf:RDF></x:xmpmeta>";

    private static string CreatePortablePacket() => Packet("""
        <dc:subject xmlns:dc="http://purl.org/dc/elements/1.1/"><rdf:Bag><rdf:li>Auto/landscape</rdf:li><rdf:li>family</rdf:li></rdf:Bag></dc:subject>
        <xmp:Rating xmlns:xmp="http://ns.adobe.com/xap/1.0/">5</xmp:Rating>
        <plib:Flagged xmlns:plib="http://ns.photolibrarian.app/1.0/">True</plib:Flagged>
        <plib:TagMetadata xmlns:plib="http://ns.photolibrarian.app/1.0/">{"schemaVersion":1,"autoTagScanVersion":"pipeline-v1","tags":[{"tag":"Auto/landscape","source":"AutoML","confidence":0.91}]}</plib:TagMetadata>
        <plib:FaceReview xmlns:plib="http://ns.photolibrarian.app/1.0/">{"Version":1,"Faces":[{"X":0.2,"Y":0.25,"Width":0.15,"Height":0.2,"PersonName":"Alex","SuggestionsHidden":true,"PersonSuggestionsHidden":true,"RejectedPersonNames":["Pat"],"IsPersonRepresentative":true}]}</plib:FaceReview>
        <plib:FaceMetadataAuthority xmlns:plib="http://ns.photolibrarian.app/1.0/">True</plib:FaceMetadataAuthority>
        <mwg-rs:Regions xmlns:mwg-rs="http://www.metadataworkinggroup.com/schemas/regions/" rdf:parseType="Resource"><mwg-rs:RegionList><rdf:Bag><rdf:li rdf:parseType="Resource"><mwg-rs:Name>Alex</mwg-rs:Name><mwg-rs:Type>Face</mwg-rs:Type><mwg-rs:Area xmlns:stArea="http://ns.adobe.com/xmp/sType/Area#" stArea:x="0.275" stArea:y="0.35" stArea:w="0.15" stArea:h="0.2" stArea:unit="normalized" /></rdf:li></rdf:Bag></mwg-rs:RegionList></mwg-rs:Regions>
        <u:Unknown xmlns:u="urn:third-party:test" rdf:parseType="Resource"><u:Text xml:lang="en">keep &amp; retain</u:Text><u:Sequence><rdf:Seq><rdf:li>A</rdf:li><rdf:li>B</rdf:li></rdf:Seq></u:Sequence></u:Unknown>
        """);

    private sealed class ImmediateProgress(Action<SidecarMigrationItem> report) : IProgress<SidecarMigrationItem>
    {
        public void Report(SidecarMigrationItem value) => report(value);
    }

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
    }

    private static FileId GetFileId(string path)
    {
        using var handle = File.OpenHandle(path);
        Assert.True(GetFileInformationByHandleEx(handle, 18, out var identity, Marshal.SizeOf<FileId>()));
        return identity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private record struct FileId(ulong Volume, ulong Low, ulong High);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle, int informationClass, out FileId information, int size);
}
