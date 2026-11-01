// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Xml.Linq;
using System.Security.Cryptography;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Icc;
using MetadataExtractor.Formats.Xmp;
using Microsoft.Data.Sqlite;
using Microsoft.Graphics.Canvas;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using PhotoLibrarian.ViewModels;
using Windows.Graphics.Imaging;
using XmpCore;
using Xunit;
using Directory = System.IO.Directory;

namespace PhotoLibrarian.Tests;

public sealed class RawDerivativeExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-RawExport-{Guid.NewGuid():N}");
    public RawDerivativeExportTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private async Task<string> SourceAsync(string name = "source.dng", ushort orientation = 6)
    {
        var path = Path.Combine(_root, name);
        await File.WriteAllBytesAsync(path, RawDevelopmentTests.SyntheticDngFixture.Create(false, orientation));
        return path;
    }

    private static async Task<byte[]> ReopenAsync(string path, bool colorManage = false)
    {
        await using var file = File.OpenRead(path);
        var decoder = await BitmapDecoder.CreateAsync(file.AsRandomAccessStream());
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            new BitmapTransform(), ExifOrientationMode.RespectExifOrientation,
            colorManage ? ColorManagementMode.ColorManageToSRgb : ColorManagementMode.DoNotColorManage);
        return data.DetachPixelData();
    }

    private void NoStaging() => Assert.Empty(Directory.GetFiles(_root, ".photolibrarian-raw-*.tmp", SearchOption.AllDirectories));

    [Fact]
    public async Task ProfessionalResolutionRawOpensPreviewsAndExportsAboveFormer24MegapixelLimit()
    {
        var path = Path.Combine(_root, "professional-resolution.dng");
        await File.WriteAllBytesAsync(path,
            RawDevelopmentTests.SyntheticDngFixture.Create(false, 6, 8256, 5504),
            TestContext.Current.CancellationToken);
        byte[] originalHash;
        await using (var file = File.OpenRead(path))
            originalHash = await SHA256.HashDataAsync(file, TestContext.Current.CancellationToken);
        using var session = new RawPreviewSession();
        var parameters = new EditParameters { RawExposureEv = -1 };
        var preview = await session.RenderAsync(path, parameters, TestContext.Current.CancellationToken);
        Assert.Equal(5496, preview.Width);
        Assert.Equal(8248, preview.Height);
        Assert.True((long)preview.Width * preview.Height > 45_000_000);
        byte[] previewHash;
        using (var device = new CanvasDevice())
        {
            var pixels = preview.ToPreviewBgra8(TestContext.Current.CancellationToken);
            previewHash = SHA256.HashData(pixels);
            using var bitmap = CanvasBitmap.CreateFromBytes(device, pixels, preview.Width, preview.Height,
                Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
            Assert.Equal((uint)preview.Width, bitmap.SizeInPixels.Width);
            Assert.Equal((uint)preview.Height, bitmap.SizeInPixels.Height);
        }
        var result = await RawDerivativeExportService.ExportAsync(
            path, _root, RawDerivativeFormat.Png, parameters, session.RenderAsync, TestContext.Current.CancellationToken);
        Assert.Equal(preview.Width, result.Width);
        Assert.Equal(preview.Height, result.Height);
        Assert.Equal(previewHash, SHA256.HashData(await ReopenAsync(result.FilePath)));
        await using (var file = File.OpenRead(path))
            Assert.Equal(originalHash, await SHA256.HashDataAsync(file, TestContext.Current.CancellationToken));
        NoStaging();
    }

    [Fact]
    public void ExplicitAccessibleExportAndCancellationAreSeparateFromOverwriteSave()
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var editor = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "ImageEditorView.xaml"));
        var rawPanel = editor.Descendants().Single(element => (string?)element.Attribute(xaml + "Name") == "RawPreviewPanel");
        Assert.DoesNotContain(rawPanel.Descendants(), element => element.Name.LocalName == "InfoBar");
        var export = editor.Descendants().Single(element => (string?)element.Attribute(xaml + "Name") == "ExportRawBtn");
        Assert.Equal("OnExportRaw", (string?)export.Attribute("Click"));
        Assert.Equal("EditorExportRawDerivative", (string?)export.Attribute("AutomationProperties.AutomationId"));
        Assert.Contains("separate", (string?)export.Attribute("AutomationProperties.Name"));
        Assert.Contains(editor.Descendants(), element => (string?)element.Attribute(xaml + "Name") == "CancelRawExportBtn");
        Assert.Contains(editor.Descendants(), element => (string?)element.Attribute("Click") == "OnSave");
        var dialog = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "RawDerivativeExportDialog.xaml"));
        Assert.Equal("Export SDR derivative", (string?)dialog.Root!.Attribute("Title"));
        foreach (var name in new[] { "FormatBox", "FolderBox" })
        {
            var control = dialog.Descendants().Single(element => (string?)element.Attribute(xaml + "Name") == name);
            Assert.NotNull(control.Attribute("AutomationProperties.Name"));
            Assert.NotNull(control.Attribute("AutomationProperties.AutomationId"));
        }
    }

    [Fact]
    public async Task SourceDevelopmentVersionIsPinnedAndUnknownExplicitVersionFails()
    {
        var source = await SourceAsync();
        await ImageEditService.SaveRawPreviewRecipeAsync(source, new EditParameters());
        var path = Path.ChangeExtension(source, ".xmp");
        var xmp = XmpMetaFactory.ParseFromString(await File.ReadAllTextAsync(path));
        Assert.Equal(RawDevelopmentService.RecipeVersion,
            xmp.GetPropertyString(EditParameters.XmpNamespace, "RawDevelopment"));
        xmp.SetProperty(EditParameters.XmpNamespace, "RawDevelopment", "UnknownDecoderV2");
        await File.WriteAllTextAsync(path, XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions()));
        Assert.Throws<NotSupportedException>(() => ImageEditService.ReadEditParameters(source));
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(-1.5, 8)]
    [InlineData(2, 1)]
    public async Task PngReopensExactlyAsFullResolutionPreviewWithOneTransfer(double exposure, double white)
    {
        var source = await SourceAsync();
        var original = await File.ReadAllBytesAsync(source);
        var parameters = new EditParameters { RawExposureEv = exposure, RawWhitePoint = white, Brightness = 0.3, RotationAngle = 90 };
        using var session = new RawPreviewSession();
        var preview = await session.RenderAsync(source, parameters, CancellationToken.None);
        var result = await RawDerivativeExportService.ExportAsync(
            source, _root, RawDerivativeFormat.Png, parameters, session.RenderAsync);
        Assert.Equal(40, result.Width);
        Assert.Equal(56, result.Height);
        var expected = preview.ToPreviewBgra8();
        Assert.Equal(expected, await ReopenAsync(result.FilePath));
        using var device = new CanvasDevice();
        using var reopenedBitmap = await ImageEditRenderer.LoadOrientedAsync(device, result.FilePath);
        Assert.Equal(expected, reopenedBitmap.GetPixelBytes());
        var managed = await ReopenAsync(result.FilePath, colorManage: true);
        Assert.Equal(expected.Length, managed.Length);
        // WIC's ICC-to-system-sRGB conversion is separately quantized; the app's byte decoder
        // deliberately uses DoNotColorManage, whose lossless equality is asserted above.
        Assert.InRange(expected.Zip(managed, (a, b) => Math.Abs(a - b)).Max(), 0, 3);
        Assert.All(managed.Where((_, index) => index % 4 == 3), alpha => Assert.Equal(255, alpha));
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
        Assert.False(ImageEditService.ReadEditParameters(result.FilePath).HasAdjustments);
        var kept = ImageEditService.ReadEditParameters(source);
        Assert.Equal(exposure, kept.RawExposureEv);
        Assert.Equal(white, kept.RawWhitePoint);
        Assert.Equal(0.3, kept.Brightness);
        Assert.Equal(90, kept.RotationAngle);
        Assert.False(File.Exists(Path.ChangeExtension(result.FilePath, ".xmp")));
        NoStaging();
    }

    [Theory]
    [InlineData(RawDerivativeFormat.Png)]
    [InlineData(RawDerivativeFormat.Jpeg)]
    public async Task MetadataHasNewSrgbProfileNormalizedOrientationAndBakedProvenance(RawDerivativeFormat format)
    {
        const string dc = "http://purl.org/dc/elements/1.1/";
        const string xap = "http://ns.adobe.com/xap/1.0/";
        var source = await SourceAsync();
        var xmp = XmpMetaFactory.Create();
        xmp.SetLocalizedText(dc, "title", "", "x-default", "A portable title");
        xmp.SetLocalizedText(dc, "description", "", "x-default", "A portable caption");
        xmp.SetLocalizedText(dc, "rights", "", "x-default", "Rights retained");
        xmp.AppendArrayItem(dc, "subject", new XmpCore.Options.PropertyOptions { IsArray = true }, "travel", null);
        xmp.SetPropertyInteger(xap, "Rating", 4);
        xmp.SetProperty(xap, "CreateDate", "2024-04-05T06:07:08");
        xmp.SetProperty("http://ns.adobe.com/tiff/1.0/", "Make", "Fixture camera");
        xmp.SetProperty("http://ns.adobe.com/tiff/1.0/", "Model", "Fixture model");
        xmp.SetProperty("http://ns.adobe.com/exif/1.0/", "GPSLatitude", "51,0N");
        xmp.SetProperty("http://ns.adobe.com/tiff/1.0/", "Orientation", "6");
        EditParametersSerializer.WriteToXmp(xmp, new EditParameters { Brightness = 0.2, RawExposureEv = -1 });
        var sidecar = Path.ChangeExtension(source, ".xmp");
        var previous = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
        await File.WriteAllTextAsync(sidecar, previous);
        var previousBytes = await File.ReadAllBytesAsync(sidecar);
        var result = await RawDerivativeExportService.ExportAsync(source, _root, format,
            ImageEditService.ReadEditParameters(source));
        var directories = ImageMetadataReader.ReadMetadata(result.FilePath);
        var profile = Assert.Single(directories.OfType<IccDirectory>());
        Assert.Equal("RGB ", profile.GetString(IccDirectory.TagColorSpace));
        var output = Assert.Single(directories.OfType<XmpDirectory>()).XmpMeta!;
        Assert.Equal("A portable title", output.GetLocalizedText(dc, "title", "", "x-default").Value);
        Assert.Equal("Rights retained", output.GetLocalizedText(dc, "rights", "", "x-default").Value);
        Assert.Equal("travel", output.GetArrayItem(dc, "subject", 1).Value);
        Assert.Equal(1, output.GetPropertyInteger("http://ns.adobe.com/tiff/1.0/", "Orientation"));
        Assert.Equal(1, output.GetPropertyInteger("http://ns.adobe.com/exif/1.0/", "ColorSpace"));
        Assert.False(output.DoesPropertyExist("http://ns.adobe.com/exif/1.0/", "GPSLatitude"));
        Assert.False(output.DoesPropertyExist(EditParameters.XmpNamespace, "RawExposureEv"));
        Assert.False(output.DoesPropertyExist(EditParameters.XmpNamespace, "Brightness"));
        Assert.Equal("Baked", output.GetPropertyString(SdrDerivativeMetadata.ProvenanceNamespace, "State"));
        Assert.Equal(RawToneMappingService.RecipeVersion,
            output.GetPropertyString(SdrDerivativeMetadata.ProvenanceNamespace, "ToneMapping"));
        var metadata = new MetadataReaderService().ReadMetadata(result.FilePath);
        Assert.Equal("A portable title", metadata.Title);
        Assert.Equal("A portable caption", metadata.Caption);
        Assert.Equal(4, metadata.Rating);
        Assert.Equal(new DateTime(2024, 4, 5, 6, 7, 8), metadata.DateTaken);
        Assert.Equal("Fixture camera", metadata.CameraMake);
        Assert.Equal("Fixture model", metadata.CameraModel);
        if (format == RawDerivativeFormat.Png)
        {
            var png = await File.ReadAllBytesAsync(result.FilePath);
            var chunks = new List<string>();
            for (var offset = 8; offset < png.Length;)
            {
                var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
                chunks.Add(System.Text.Encoding.ASCII.GetString(png, offset + 4, 4));
                offset = checked(offset + length + 12);
            }
            Assert.Contains("iCCP", chunks);
            Assert.DoesNotContain("sRGB", chunks);
        }
        Assert.Equal(new[] { "travel" }, new MetadataReaderService().ReadTags(result.FilePath));
        Assert.Equal(previousBytes, File.ReadAllBytes(Assert.Single(Directory.GetFiles(_root, "source.xmp.*.bak"))));
        NoStaging();
    }

    [Fact]
    public async Task JpegQuality95HasSeparatelyBoundedLossOnSmoothColorAndSyntheticRaw()
    {
        var source = await SourceAsync(orientation: 1);
        var rgb = new float[256 * 128 * 3];
        for (var y = 0; y < 128; y++)
        for (var x = 0; x < 256; x++)
        {
            var i = (y * 256 + x) * 3;
            rgb[i] = x / 255f;
            rgb[i + 1] = y / 127f;
            rgb[i + 2] = (x + y) / 382f;
        }
        var smooth = new RawSdrImage(256, 128, rgb);
        var result = await RawDerivativeExportService.ExportAsync(source, _root, RawDerivativeFormat.Jpeg,
            new EditParameters(), (_, _, _) => Task.FromResult(smooth));
        AssertJpegError(smooth.ToPreviewBgra8(), await ReopenAsync(result.FilePath), 8, 1.25);
        using var session = new RawPreviewSession();
        var parameters = new EditParameters { RawExposureEv = -1 };
        var preview = await session.RenderAsync(source, parameters, CancellationToken.None);
        var rawResult = await RawDerivativeExportService.ExportAsync(source, _root, RawDerivativeFormat.Jpeg, parameters, session.RenderAsync);
        AssertJpegError(preview.ToPreviewBgra8(), await ReopenAsync(rawResult.FilePath), 12, 2);
        NoStaging();
    }

    private static void AssertJpegError(byte[] expected, byte[] actual, int max, double rms)
    {
        Assert.Equal(expected.Length, actual.Length);
        var errors = expected.Zip(actual, (a, b) => (double)Math.Abs(a - b))
            .Where((_, index) => index % 4 != 3).ToArray();
        var measuredRms = Math.Sqrt(errors.Average(value => value * value));
        Console.WriteLine($"JPEG quality 95 4:4:4: max={errors.Max()}, RMS={measuredRms:F4} code values");
        Assert.InRange(errors.Max(), 0, max);
        Assert.InRange(measuredRms, 0, rms);
    }

    [Fact]
    public async Task CollisionsIncludingForeignSidecarsAndConcurrentExportsNeverOverwrite()
    {
        var source = await SourceAsync();
        var existing = Path.Combine(_root, "source - Developed.png");
        var foreign = Path.Combine(_root, "source - Developed (2).xmp");
        await File.WriteAllTextAsync(existing, "original destination");
        await File.WriteAllTextAsync(foreign, "foreign sidecar");
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            RawDerivativeExportService.ExportAsync(source, _root, RawDerivativeFormat.Png, new EditParameters())));
        Assert.Equal(3, results.Select(result => result.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("original destination", await File.ReadAllTextAsync(existing));
        Assert.Equal("foreign sidecar", await File.ReadAllTextAsync(foreign));
        foreach (var result in results)
            Assert.False(ImageEditService.ReadEditParameters(result.FilePath).HasAdjustments);
        NoStaging();
    }

    [Fact]
    public async Task PrecancelledAndStaleNonCooperativeRendererCannotPublishOrKeepRecipe()
    {
        var source = await SourceAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RawDerivativeExportService.ExportAsync(source, _root, RawDerivativeFormat.Png, new EditParameters(), cancellation.Token));

        var vm = new ImageEditorViewModel(new OriginalBackupService(Path.Combine(_root, "backups")));
        await vm.OpenForEditAsync(new ImageEntry { FilePath = source, FileName = "source.dng" });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<RawSdrImage>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<RawSdrImage> Render(string _, EditParameters __, CancellationToken ___)
        {
            started.SetResult();
            return release.Task;
        }
        var export = vm.ExportRawDerivativeAsync(_root, RawDerivativeFormat.Png, Render);
        await started.Task;
        Assert.True(vm.IsSaving);
        Assert.False(vm.CanExportRawDerivative);
        var replacement = await SourceAsync("replacement.dng");
        await vm.OpenForEditAsync(new ImageEntry { FilePath = replacement, FileName = "replacement.dng" });
        release.SetResult(new RawSdrImage(1, 1, [0.5f, 0.5f, 0.5f]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export);
        Assert.Equal(replacement, vm.ImagePath);
        Assert.False(vm.IsSaving);
        Assert.False(File.Exists(Path.ChangeExtension(source, ".xmp")));
        Assert.Empty(Directory.GetFiles(_root, "*.png"));
        NoStaging();
    }

    [Fact]
    public async Task MetadataFailureAfterEncodingLeavesNoDerivativeAndOriginalRecipeUnchanged()
    {
        var source = await SourceAsync();
        var original = await File.ReadAllBytesAsync(source);
        var sidecar = Path.ChangeExtension(source, ".xmp");
        await File.WriteAllTextAsync(sidecar, "<invalid xmp");
        await Assert.ThrowsAnyAsync<Exception>(() =>
            RawDerivativeExportService.ExportAsync(source, _root, RawDerivativeFormat.Png, new EditParameters()));
        Assert.Equal("<invalid xmp", await File.ReadAllTextAsync(sidecar));
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
        Assert.Empty(Directory.GetFiles(_root, "*.png"));
        NoStaging();
    }

    [Fact]
    public async Task OversizedJpegMetadataAndWicEncodeFailureAreExplicitAndClean()
    {
        var source = await SourceAsync();
        var xmp = XmpMetaFactory.Create();
        xmp.SetLocalizedText("http://purl.org/dc/elements/1.1/", "description", "", "x-default", new string('a', 70000));
        var sidecar = Path.ChangeExtension(source, ".xmp");
        var originalXmp = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
        await File.WriteAllTextAsync(sidecar, originalXmp);
        var originalBytes = await File.ReadAllBytesAsync(sidecar);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            RawDerivativeExportService.ExportAsync(source, _root, RawDerivativeFormat.Jpeg, new EditParameters()));
        Assert.Contains("Choose PNG", error.Message);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(sidecar));
        Assert.Empty(Directory.GetFiles(_root, "*.jpg"));
        NoStaging();
        var failed = Path.Combine(_root, "invalid-encode.tmp");
        try
        {
            await using var file = new FileStream(failed, FileMode.CreateNew, FileAccess.ReadWrite);
            await Assert.ThrowsAnyAsync<Exception>(() => RawDerivativeExportService.EncodeAsync(
                file, new RawSdrImage(100, 100, new float[30000]), [0], RawDerivativeFormat.Png, CancellationToken.None));
        }
        finally { if (File.Exists(failed)) File.Delete(failed); }
    }

    [Fact]
    public async Task DeniedDestinationReportsPermissionFailureWithoutPublishing()
    {
        var source = await SourceAsync();
        var folder = Directory.CreateDirectory(Path.Combine(_root, "denied"));
        var originalAcl = folder.GetAccessControl();
        var deniedAcl = folder.GetAccessControl();
        var sid = WindowsIdentity.GetCurrent().User!;
        deniedAcl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.CreateFiles, AccessControlType.Deny));
        try
        {
            folder.SetAccessControl(deniedAcl);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                RawDerivativeExportService.ExportAsync(source, folder.FullName, RawDerivativeFormat.Png, new EditParameters()));
        }
        finally { folder.SetAccessControl(originalAcl); }
        Assert.Empty(folder.EnumerateFiles());
        Assert.False(File.Exists(Path.ChangeExtension(source, ".xmp")));
        NoStaging();
    }

    [Fact]
    public async Task CancellationDuringWicEncodingRemovesOwnedStagingFiles()
    {
        var source = await SourceAsync();
        var samples = new float[2048 * 1024 * 3];
        var random = new Random(114);
        for (var i = 0; i < samples.Length; i++) samples[i] = random.NextSingle();
        var frame = new RawSdrImage(2048, 1024, samples);
        using var cancellation = new CancellationTokenSource();
        var export = RawDerivativeExportService.ExportAsync(source, _root, RawDerivativeFormat.Png,
            new EditParameters(), (_, _, _) => Task.FromResult(frame), cancellation.Token);
        try
        {
            var timeout = Stopwatch.StartNew();
            string? encoded = null;
            while (encoded is null)
            {
                if (export.IsCompleted || timeout.Elapsed > TimeSpan.FromSeconds(30))
                    throw new InvalidOperationException("The encoder did not reach staging in time.");
                encoded = Directory.EnumerateFiles(_root, ".photolibrarian-raw-*.tmp").FirstOrDefault();
                await Task.Delay(1, TestContext.Current.CancellationToken);
            }
            // The noisy PNG is several MiB. Observe a partial encoded payload, not merely
            // an empty reservation or an already completed/tagged container.
            while (new FileInfo(encoded).Length is <= 33 or >= 2_000_000)
            {
                if (export.IsCompleted || timeout.Elapsed > TimeSpan.FromSeconds(30))
                    throw new InvalidOperationException("The encoder did not expose an active partial payload in time.");
                await Task.Delay(1, TestContext.Current.CancellationToken);
            }
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export);
        }
        finally
        {
            cancellation.Cancel();
            try { await export; }
            catch (OperationCanceledException) { }
        }
        Assert.Empty(Directory.GetFiles(_root, "*.png"));
        Assert.False(File.Exists(Path.ChangeExtension(source, ".xmp")));
        NoStaging();
    }

    [Fact]
    public async Task SourceRecipePermissionFailureAfterEncodingDoesNotPublish()
    {
        var source = await SourceAsync();
        await ImageEditService.SaveRawPreviewRecipeAsync(source, new EditParameters());
        var sidecar = Path.ChangeExtension(source, ".xmp");
        var before = await File.ReadAllBytesAsync(sidecar);
        var output = Directory.CreateDirectory(Path.Combine(_root, "output"));
        var sourceFolder = new DirectoryInfo(_root);
        var originalAcl = sourceFolder.GetAccessControl();
        var deniedAcl = sourceFolder.GetAccessControl();
        deniedAcl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
            FileSystemRights.CreateFiles, AccessControlType.Deny));
        try
        {
            sourceFolder.SetAccessControl(deniedAcl);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                RawDerivativeExportService.ExportAsync(source, output.FullName, RawDerivativeFormat.Png, new EditParameters()));
        }
        finally { sourceFolder.SetAccessControl(originalAcl); }
        Assert.Equal(before, await File.ReadAllBytesAsync(sidecar));
        Assert.Empty(output.EnumerateFiles("*.png"));
        NoStaging();
    }

    [Fact]
    public async Task DerivativeIndexesAsSeparateItemAndViewModelKeepsRecipeHistory()
    {
        var source = await SourceAsync();
        var vm = new ImageEditorViewModel(new OriginalBackupService(Path.Combine(_root, "backups")));
        await vm.OpenForEditAsync(new ImageEntry { FilePath = source, FileName = "source.dng" });
        vm.BeginEdit("RAW exposure");
        vm.RawExposureEv = -1;
        vm.EndEdit();
        using var session = new RawPreviewSession();
        var result = await vm.ExportRawDerivativeAsync(_root, RawDerivativeFormat.Png, session.RenderAsync);
        Assert.False(vm.HasChanges);
        Assert.Equal(-1, vm.RawExposureEv);
        Assert.Single(vm.UndoHistory);
        vm.UndoSteps(1);
        Assert.True(vm.HasChanges);
        using var database = new CacheDatabase(Path.Combine(_root, "library.db"));
        await database.InitializeAsync();
        var images = new ImageRepository(database);
        using var scanner = new FolderScannerService();
        var indexer = new LibraryIndexingService(database, images, new TagRepository(database),
            new FaceRepository(database), scanner, new MetadataReaderService(), new FaceMetadataStore());
        await indexer.IndexFileAsync(source);
        await indexer.IndexFileAsync(result.FilePath);
        var rawEntry = await images.GetByPathAsync(source);
        var derivative = await images.GetByPathAsync(result.FilePath);
        Assert.NotNull(rawEntry);
        Assert.NotNull(derivative);
        Assert.NotEqual(rawEntry.Id, derivative.Id);
        Assert.Equal(result.Width, derivative.Width);
        Assert.Equal(result.Height, derivative.Height);
        vm.CloseCommand.Execute(null);
        await vm.OpenForEditAsync(derivative);
        Assert.False(vm.IsRawPreview);
        Assert.False(vm.CanExportRawDerivative);
        Assert.True(vm.CanSavePixels);
        Assert.False(vm.GetCurrentParameters().HasAdjustments);
        NoStaging();
    }

    [Theory]
    [InlineData(1024, 768)]
    [InlineData(4000, 3000)]
    [InlineData(6000, 4000)]
    public async Task ReportsSyntheticEndToEndExportCost(ushort width, ushort height)
    {
        var source = Path.Combine(_root, "benchmark.dng");
        await File.WriteAllBytesAsync(source,
            RawDevelopmentTests.SyntheticDngFixture.Create(false, 1, width, height));
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var before = process.WorkingSet64;
        var peak = before;
        using var sampling = new CancellationTokenSource();
        var sample = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                process.Refresh();
                peak = Math.Max(peak, process.WorkingSet64);
                await Task.Delay(2, TestContext.Current.CancellationToken);
            }
        });
        var clock = Stopwatch.StartNew();
        RawDerivativeResult result;
        try
        {
            result = await RawDerivativeExportService.ExportAsync(source, _root, RawDerivativeFormat.Png, new EditParameters(),
                cancellationToken: TestContext.Current.CancellationToken);
            clock.Stop();
        }
        finally
        {
            sampling.Cancel();
            await sample;
        }
        process.Refresh();
        peak = Math.Max(peak, process.WorkingSet64);
        Console.WriteLine(
            $"{result.Width}x{result.Height}: decode/map/encode/tag/recipe/publish {clock.Elapsed.TotalMilliseconds:F1} ms, " +
            $"sampled peak {peak / 1048576.0:F1} MiB, growth {(peak - before) / 1048576.0:F1} MiB. Synthetic Bayer DNG only.");
    }
}
