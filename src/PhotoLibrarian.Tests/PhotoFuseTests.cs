// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MetadataExtractor;
using MetadataExtractor.Formats.Xmp;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.ViewModels;
using XmpCore;
using Windows.Graphics.Imaging;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class PhotoFuseTests : IDisposable
{
    private const int Size = 384;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-Fuse-{Guid.NewGuid():N}");
    private readonly ITestOutputHelper _output;
    private CancellationToken Token => TestContext.Current.CancellationToken;

    public PhotoFuseTests(ITestOutputHelper output)
    {
        _output = output;
        System.IO.Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        System.IO.Directory.Delete(_folder, recursive: true);
    }

    private static PhotoFuseTransform Register(byte[] donor, byte[] basis, int width, int height, CancellationToken token)
    {
        var result = PhotoFuseRegistration.TryRegister(donor, basis, width, height, token);
        Assert.True(result.Accepted, result.Reason);
        return result.Transform!.Value;
    }

    private static byte[] Render(IReadOnlyList<byte[]> frames, IReadOnlyList<PhotoFuseTransform> transforms,
        int width, int height, IReadOnlyList<PhotoFuseRegion> regions, CancellationToken token)
    {
        var result = PhotoFuseComposer.TryRender(frames, transforms, width, height, regions, token);
        Assert.Null(result.Rejection);
        return Assert.IsType<byte[]>(result.Pixels);
    }

    private static async Task<PhotoFuseSession> PrepareAsync(IReadOnlyList<string> paths, CancellationToken token)
    {
        var result = await PhotoFuseSession.TryPrepareAsync(paths, token);
        Assert.True(result.Session is not null, result.Rejection);
        return result.Session!;
    }

    private static async Task<PhotoFusePreview> PreviewAsync(PhotoFuseSession session,
        IReadOnlyList<PhotoFuseRegion> regions, CancellationToken token)
    {
        var result = await session.TryPreviewAsync(regions, token);
        Assert.True(result.Preview is not null, result.Rejection);
        return result.Preview!;
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(5.2, -3.4, 0)]
    [InlineData(-4, 2, 0.012)]
    [InlineData(2, 1, -0.02)]
    public void RegistersTranslationAndRotationWithSubpixelAccuracy(double dx, double dy, double angle)
    {
        var basis = Frame();
        var donor = Frame(dx, dy, angle, changed: 1);
        var watch = Stopwatch.StartNew();
        var found = Register(donor, basis, Size, Size, Token);
        double error = 0;
        for (int y = 40; y < Size - 40; y += 16)
        for (int x = 40; x < Size - 40; x += 16)
        {
            var actual = found.SourceAt(x, y, Size, Size);
            var expected = new PhotoFuseTransform(dx, dy, angle).SourceAt(x, y, Size, Size);
            error = Math.Max(error, Math.Sqrt(Math.Pow(actual.X - expected.X, 2) +
                Math.Pow(actual.Y - expected.Y, 2)));
        }
        _output.WriteLine($"Registration {dx},{dy},{angle}: max landmark error {error:F3}px, {watch.ElapsedMilliseconds}ms");
        Assert.True(error < 0.35, $"Maximum landmark error {error:F3}px");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"Registration took {watch.Elapsed}");
    }

    [Theory]
    [InlineData(1.04, 0, 0)]
    [InlineData(1.01, 0, 0)]
    [InlineData(1, 25, 0)]
    [InlineData(1, 0, 0.10)]
    public void RejectsUnsupportedScaleLargeShiftAndRotation(double scale, double dx, double angle) =>
        Assert.False(PhotoFuseRegistration.TryRegister(
            Frame(dx, 0, angle, scale), Frame(), Size, Size, Token).Accepted);

    [Fact]
    public void RegistersStationaryBackgroundWhenMovingPeopleDisagreeInOneTile()
    {
        var basis = Frame();
        var donor = Frame();
        for (int y = 140; y < 220; y++)
        for (int x = 20; x < 110; x++)
        {
            int pixel = (y * Size + x) * 4;
            donor[pixel] = 240;
            donor[pixel + 1] = 50;
            donor[pixel + 2] = 170;
        }
        var result = PhotoFuseRegistration.TryRegister(donor, basis, Size, Size, Token);
        Assert.True(result.Accepted, result.Reason);
        Assert.True(Math.Abs(result.Transform!.Value.X) < 0.1);
        Assert.True(Math.Abs(result.Transform.Value.Y) < 0.1);
    }

    [Fact]
    public async Task ExpectedRejectionsReturnReasonsWithoutThrowingAndReleaseSourceLeases()
    {
        var first = await WriteFrameAsync("base.png", Frame());
        var flat = Enumerable.Repeat((byte)128, Size * Size * 4).ToArray();
        for (int i = 3; i < flat.Length; i += 4) flat[i] = 255;
        var second = await WriteFrameAsync("flat.png", flat);
        int thrown = 0;
        void CountRejection(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
        {
            if (args.Exception is InvalidDataException or NotSupportedException) thrown++;
        }
        PhotoFuseSession.Preparation preparation;
        PhotoFuseComposer.Rendering rendering;
        AppDomain.CurrentDomain.FirstChanceException += CountRejection;
        try
        {
            preparation = await PhotoFuseSession.TryPrepareAsync([first, second], Token);
            rendering = PhotoFuseComposer.TryRender([Frame(), Frame(changed: 1)], [default, default],
                Size, Size, [new(1, 76, 80, 24, 24)], Token);
            var unsupported = await PhotoFuseSession.TryPrepareAsync(["unsupported.dng"], Token);
            Assert.Null(unsupported.Session);
            Assert.NotNull(unsupported.Rejection);
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= CountRejection; }
        Assert.Equal(0, thrown);
        Assert.Null(preparation.Session);
        Assert.NotNull(preparation.Rejection);
        Assert.Null(rendering.Pixels);
        Assert.NotNull(rendering.Rejection);
        using (File.Open(first, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        using (File.Open(second, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }

    [Fact]
    public void RejectsFlatUnrelatedAndPhotometricallyMismatchedFrames()
    {
        var flat = Enumerable.Repeat((byte)128, Size * Size * 4).ToArray();
        Assert.False(PhotoFuseRegistration.TryRegister(flat, flat, Size, Size, Token).Accepted);
        var shiftedColor = Frame();
        for (int i = 0; i < shiftedColor.Length; i += 4)
            shiftedColor[i] = (byte)Math.Min(255, shiftedColor[i] + 45);
        Assert.False(PhotoFuseRegistration.TryRegister(
            shiftedColor, Frame(), Size, Size, Token).Accepted);
        var unrelated = Frame();
        new Random(5821).NextBytes(unrelated);
        Assert.False(PhotoFuseRegistration.TryRegister(
            unrelated, Frame(), Size, Size, Token).Accepted);
    }

    [Fact]
    public void RejectsAmbiguousRepeatingTextureAndLocalizedTextureOnFlatBackground()
    {
        var repeating = Frame();
        var localized = Frame();
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        for (int c = 0; c < 3; c++)
        {
            int i = (y * Size + x) * 4 + c;
            repeating[i] = (byte)(128 + 35 * Math.Sin(x * Math.PI / 6) + 35 * Math.Cos(y * Math.PI / 6));
            if (x < 128 || x > 256 || y < 128 || y > 256) localized[i] = 128;
        }
        Assert.False(PhotoFuseRegistration.TryRegister(repeating, repeating, Size, Size, Token).Accepted);
        Assert.False(PhotoFuseRegistration.TryRegister(localized, localized, Size, Size, Token).Accepted);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task RespectsExifOrientationAndMirroringBeforeRegistration(int orientation)
    {
        var source = Path.Combine(_folder, "oriented.jpg");
        var donor = Path.Combine(_folder, "upright.png");
        await RenderQualityFixtures.WriteImageAsync(source, Size, 256, Frame().AsSpan(0, Size * 256 * 4).ToArray(),
            orientation: (ushort)orientation);
        var oriented = await WindowsImagingCodec.DecodeAsync(source, cancellationToken: Token);
        await RenderQualityFixtures.WriteImageAsync(donor, oriented.Width, oriented.Height, oriented.Pixels);
        using var session = await PrepareAsync([source, donor], Token);
        Assert.Equal((int)oriented.Width, session.Width);
        Assert.Equal((int)oriented.Height, session.Height);
        Assert.Equal(oriented.Pixels, await session.GetAlignedFrameAsync(0, Token));
    }

    [Fact]
    public async Task BoundsFourFrameResolutionAndReportsEndToEndRuntime()
    {
        var watch = Stopwatch.StartNew();
        var paths = new List<string>();
        for (int i = 0; i < 4; i++)
            paths.Add(await WriteFrameAsync($"burst-{i}.png", Frame(changed: i % 3, size: 1601), 1601));
        using var session = await PrepareAsync(paths, Token);
        Assert.Equal(PhotoFuseSession.MaximumDimension, session.Width);
        Assert.Equal(PhotoFuseSession.MaximumDimension, session.Height);
        Assert.Equal(4, session.SourceNames.Count);
        var preview = await PreviewAsync(session, [new(1, 56, 56, 72, 72)], Token);
        Assert.Equal(1600 * 1600 * 4, preview.CopyPixels().Length);
        await session.ExportAsync(preview, _folder, true, Token);
        _output.WriteLine($"Four 1601px inputs, decode/register/preview/export including fixture encoding: " +
            $"{watch.ElapsedMilliseconds}ms; process peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576} MiB");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"End-to-end took {watch.Elapsed}");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            PhotoFuseSession.TryPrepareAsync(paths.Append(paths[0]).ToArray(), Token));
    }

    [Fact]
    public void RejectsLargeOcclusionAndBadRegionSeams()
    {
        var donor = Frame();
        for (int y = 10; y < 130; y++)
        for (int x = 10; x < 130; x++)
            for (int c = 0; c < 3; c++) donor[(y * Size + x) * 4 + c] = 250;
        Assert.False(PhotoFuseRegistration.TryRegister(donor, Frame(), Size, Size, Token).Accepted);
        Assert.NotNull(PhotoFuseComposer.TryRender(
            [Frame(), Frame(changed: 1)], [default, default], Size, Size,
            [new(1, 76, 80, 24, 24)], Token).Rejection);
    }

    [Fact]
    public void FullPreviewRegistrationAccountsForDownsampleCenterAndRejectsParallax()
    {
        const int size = 1600;
        var basis = Frame(size: size);
        var donor = Frame(5.2, -3.4, 0.012, size: size);
        var found = Register(donor, basis, size, size, Token);
        var expected = new PhotoFuseTransform(5.2, -3.4, 0.012);
        double error = 0;
        for (int y = 40; y < size - 40; y += 80)
        for (int x = 40; x < size - 40; x += 80)
        {
            var actualPoint = found.SourceAt(x, y, size, size);
            var expectedPoint = expected.SourceAt(x, y, size, size);
            error = Math.Max(error, Math.Sqrt(Math.Pow(actualPoint.X - expectedPoint.X, 2) +
                Math.Pow(actualPoint.Y - expectedPoint.Y, 2)));
        }
        _output.WriteLine($"1600px downsampled registration: maximum landmark error {error:F3}px");
        Assert.True(error < 0.35, $"Full preview landmark error {error:F3}px");

        var parallax = Frame();
        var shifted = Frame(8, 0);
        Array.Copy(shifted, Size * Size * 2, parallax, Size * Size * 2, Size * Size * 2);
        Assert.False(PhotoFuseRegistration.TryRegister(parallax, Frame(), Size, Size, Token).Accepted);
    }

    [Fact]
    public async Task RejectsTransparentHighBitDepthAndAnimatedPngSources()
    {
        var opaque = await WriteFrameAsync("opaque.png", Frame());
        var transparent = Path.Combine(_folder, "transparent.png");
        var pixels = Frame();
        Array.Clear(pixels, 0, 4);
        await RenderQualityFixtures.WriteImageAsync(transparent, Size, Size, pixels);
        Assert.Null((await PhotoFuseSession.TryPrepareAsync([opaque, transparent], Token)).Session);
        var highDepth = Path.Combine(_folder, "16bit.png");
        await using (var file = new FileStream(highDepth, FileMode.CreateNew, FileAccess.ReadWrite))
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, file.AsRandomAccessStream());
            encoder.SetPixelData(BitmapPixelFormat.Rgba16, BitmapAlphaMode.Ignore, Size, Size, 96, 96,
                new byte[Size * Size * 8]);
            await encoder.FlushAsync();
        }
        Assert.Null((await PhotoFuseSession.TryPrepareAsync([opaque, highDepth], Token)).Session);
        // APNG's control chunk is encountered before WIC can silently decode just its first frame.
        var animated = Path.Combine(_folder, "animated.png");
        var encoded = File.ReadAllBytes(opaque);
        using (var file = File.Create(animated))
        {
            file.Write(encoded.AsSpan(0, 33));
            file.Write(new byte[] { 0, 0, 0, 8, 97, 99, 84, 76, 0, 0, 0, 2, 0, 0, 0, 0, 243, 141, 147, 112 });
            file.Write(encoded.AsSpan(33));
        }
        Assert.Null((await PhotoFuseSession.TryPrepareAsync([opaque, animated], Token)).Session);
    }

    [Fact]
    public void ChoosesIndependentRegionsWithoutChangingOtherPixels()
    {
        var basis = Frame();
        var frames = new[] { basis, Frame(changed: 1), Frame(changed: 2) };
        var regions = new PhotoFuseRegion[] { new(1, 56, 56, 72, 72), new(2, 248, 248, 72, 72) };
        var result = Render(frames, [default, default, default], Size, Size, regions, Token);
        Assert.Equal(Pixel(frames[1], 90, 90), Pixel(result, 90, 90));
        Assert.Equal(Pixel(frames[2], 280, 280), Pixel(result, 280, 280));
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            if (regions.Any(r => x >= r.Left && y >= r.Top && x < r.Left + r.Width && y < r.Top + r.Height))
                continue;
            Assert.Equal(Pixel(basis, x, y), Pixel(result, x, y));
        }
        Assert.Equal(Frame(), basis);
    }

    [Fact]
    public void RejectsOverlapsInvalidFramesBoundsAndUncoveredPixels()
    {
        byte[][] frames = [Frame(), Frame()];
        PhotoFuseTransform[] transforms = [default, new(5, 0, 0)];
        foreach (var region in new PhotoFuseRegion[]
        {
            new(0, 50, 50, 40, 40), new(2, 50, 50, 40, 40), new(1, -1, 50, 40, 40),
            new(1, 0, 50, 40, 40), new(1, 50, 50, 15, 40), new(1, int.MaxValue, 50, 40, 40)
        })
            Assert.NotNull(PhotoFuseComposer.TryRender(frames, transforms, Size, Size, [region], Token).Rejection);
        Assert.NotNull(PhotoFuseComposer.TryRender(frames, transforms, Size, Size,
            [new(1, 50, 50, 40, 40), new(1, 60, 60, 40, 40)], Token).Rejection);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PhotoFuseRegistration.TryRegister(
            frames[1], frames[0], Size, Size, cts.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => PhotoFuseComposer.TryRender(
            frames, transforms, Size, Size, [], cts.Token));
    }

    [Fact]
    public async Task ExportsExactlyReviewedPixelsWithPortableLineageAndNoSourceChanges()
    {
        var first = await WriteFrameAsync("base.png", Frame());
        var second = await WriteFrameAsync("donor.png", Frame(changed: 1));
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(first));
        byte[] donorHash = SHA256.HashData(File.ReadAllBytes(second));
        using (var session = await PrepareAsync([first, second], Token))
        {
            Assert.Equal(Size, session.Width);
            Assert.Throws<IOException>(() => File.Open(first, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
            var preview = await PreviewAsync(session, [new(1, 56, 56, 72, 72)], Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExportAsync(preview, _folder, false, Token));
            string collision = Path.Combine(_folder, "base - Fused.xmp");
            File.WriteAllText(collision, "keep metadata");
            string output = await session.ExportAsync(preview, _folder, true, Token);
            Assert.EndsWith("base - Fused (2).png", output);
            await using var saved = File.OpenRead(output);
            var decoder = await BitmapDecoder.CreateAsync(saved.AsRandomAccessStream());
            var decoded = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                new BitmapTransform(), ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);
            Assert.Equal(preview.CopyPixels(), decoded.DetachPixelData());
            var xmp = ImageMetadataReader.ReadMetadata(output).OfType<XmpDirectory>().Single().XmpMeta!;
            var sources = xmp.GetPropertyString(PhotoFuseSession.ProvenanceNamespace, "Sources");
            Assert.Contains(Convert.ToHexString(sourceHash), sources);
            Assert.Contains("donor.png", sources);
            Assert.Contains("Angle", sources);
            Assert.DoesNotContain(_folder, sources);
            Assert.False(xmp.DoesPropertyExist("http://www.metadataworkinggroup.com/schemas/regions/", "Regions"));
            Assert.False(xmp.DoesPropertyExist("http://ns.adobe.com/exif/1.0/", "GPSLatitude"));
            Assert.Equal("keep metadata", File.ReadAllText(collision));
            Assert.Empty(System.IO.Directory.GetFiles(_folder, "*.tmp"));
        }
        Assert.Equal(sourceHash, SHA256.HashData(File.ReadAllBytes(first)));
        Assert.Equal(donorHash, SHA256.HashData(File.ReadAllBytes(second)));
        Assert.False(File.Exists(first + ".xmp"));
        Assert.False(File.Exists(second + ".xmp"));
    }

    [Fact]
    public async Task RetainsExistingFaceSidecarsButDoesNotPropagateStaleIdentities()
    {
        var first = await WriteFrameAsync("base.png", Frame());
        var second = await WriteFrameAsync("donor.png", Frame(changed: 1));
        var sidecar = first + ".xmp";
        const string packet =
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description xmlns:mwg-rs=\"http://www.metadataworkinggroup.com/schemas/regions/\" " +
            "mwg-rs:Regions=\"Old face coordinates must not be reused\"/></rdf:RDF></x:xmpmeta>";
        File.WriteAllText(sidecar, packet);
        using var session = await PrepareAsync([first, second], Token);
        var preview = await PreviewAsync(session, [new(1, 56, 56, 72, 72)], Token);
        // A caller cannot alter the accepted preview through the public copy accessor.
        Array.Fill(preview.CopyPixels(), (byte)0);
        var output = await session.ExportAsync(preview, _folder, true, Token);
        var xmp = ImageMetadataReader.ReadMetadata(output).OfType<XmpDirectory>().Single().XmpMeta!;
        Assert.False(xmp.DoesPropertyExist("http://www.metadataworkinggroup.com/schemas/regions/", "Regions"));
        Assert.Equal(packet, File.ReadAllText(sidecar));
        Assert.NotEqual(new byte[preview.CopyPixels().Length], preview.CopyPixels());
    }

    [Fact]
    public async Task CleansUpFailedPreparationAndRejectsBadDestinationAndForeignPreview()
    {
        var first = await WriteFrameAsync("base.png", Frame());
        var second = await WriteFrameAsync("donor.png", Frame());
        var invalid = await WriteFrameAsync("invalid.png", Frame());
        File.WriteAllText(invalid, "not an image");
        await Assert.ThrowsAnyAsync<Exception>(() =>
            PhotoFuseSession.TryPrepareAsync([first, invalid], Token));
        // A failed prepare must release every earlier source lease.
        using (File.Open(first, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        using var session = await PrepareAsync([first, second], Token);
        using var other = await PrepareAsync([first, second], Token);
        var preview = await PreviewAsync(session, [new(1, 56, 56, 72, 72)], Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.ExportAsync(preview, _folder, true, Token));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            session.ExportAsync(preview, Path.Combine(_folder, "missing"), true, Token));
        var saves = await Task.WhenAll(session.ExportAsync(preview, _folder, true, Token),
            session.ExportAsync(preview, _folder, true, Token));
        Assert.NotEqual(saves[0], saves[1]);
        Assert.All(saves, path => Assert.True(File.Exists(path)));
        Assert.Empty(System.IO.Directory.GetFiles(_folder, "*.tmp"));
    }

    [Fact]
    public async Task CancelsAndRejectsChangedMetadataWithoutPublishing()
    {
        var first = await WriteFrameAsync("base.png", Frame());
        var second = await WriteFrameAsync("donor.png", Frame(changed: 1));
        using var session = await PrepareAsync([first, second], Token);
        var preview = await PreviewAsync(session, [new(1, 56, 56, 72, 72)], Token);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.ExportAsync(preview, _folder, true, canceled.Token));
        File.WriteAllText(first + ".xmp", "<changed/>");
        await Assert.ThrowsAsync<IOException>(() => session.ExportAsync(preview, _folder, true, Token));
        Assert.Empty(System.IO.Directory.GetFiles(_folder, "*Fused*"));
        Assert.Empty(System.IO.Directory.GetFiles(_folder, "*.tmp"));
    }

    [Fact]
    public async Task RejectsMissingUnsupportedDuplicatedPendingEditsAndEmptyRecipes()
    {
        await Assert.ThrowsAnyAsync<IOException>(() =>
            PhotoFuseSession.TryPrepareAsync([Path.Combine(_folder, "missing.png"), Path.Combine(_folder, "other.png")], Token));
        var first = await WriteFrameAsync("base.png", Frame());
        var second = await WriteFrameAsync("donor.png", Frame());
        await Assert.ThrowsAsync<ArgumentException>(() => PhotoFuseSession.TryPrepareAsync([first, first], Token));
        Assert.Null((await PhotoFuseSession.TryPrepareAsync([Path.Combine(_folder, "raw.dng"), second], Token)).Session);
        File.WriteAllText(Path.ChangeExtension(first, ".xmp"),
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description xmlns:pledit=\"http://ns.photolibrarian.app/edit/1.0/\" pledit:CropLeft=\"0.2\"/>" +
            "</rdf:RDF></x:xmpmeta>");
        Assert.Null((await PhotoFuseSession.TryPrepareAsync([first, second], Token)).Session);
        File.Delete(Path.ChangeExtension(first, ".xmp"));
        using var session = await PrepareAsync([first, second], Token);
        await Assert.ThrowsAsync<ArgumentException>(() => session.TryPreviewAsync([], Token));
    }

    [Fact]
    public async Task NamespaceDeclarationsAndDefaultRecipesDoNotRejectAnUntouchedPhoto()
    {
        var path = await WriteFrameAsync("untouched.png", Frame());
        var xmp = XmpMetaFactory.Create();
        EditParametersSerializer.WriteToXmp(xmp, new EditParameters());
        const string adobe = "http://ns.adobe.com/camera-raw-settings/1.0/";
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(adobe, "crs");
        xmp.SetProperty(adobe, "CameraProfile", "Camera Standard");
        xmp.SetPropertyBoolean(adobe, "HasSettings", false);
        File.WriteAllText(path + ".xmp", XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions()));
        using (var single = await PrepareAsync([path], Token))
            Assert.Equal(Size, single.Width);
        xmp.SetPropertyDouble(EditParameters.XmpNamespace, "Brightness", 0.2);
        File.WriteAllText(path + ".xmp", XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions()));
        var rejection = await PhotoFuseSession.TryPrepareAsync([path], Token);
        Assert.Null(rejection.Session);
        Assert.Contains("unapplied PhotoLibrarian adjustments", rejection.Rejection);
    }

    [Fact]
    public async Task SubjectOutputStoresActualMaskAndTransformAndCannotRenderSearchHints()
    {
        string basis = await WriteFrameAsync("base.png", Frame());
        string donor = await WriteFrameAsync("donor.png", FaceFrame(1));
        var prepared = await PhotoFuseSession.TryPrepareFacesAsync([basis, donor], Token);
        using var session = Assert.IsType<PhotoFuseSession>(prepared.Session);
        var face = PhotoFuseFaceBounds.From(Face(1, 1, 80));
        var rendering = await session.TryPreviewFacesAsync([new(1, face, face)], Token, [face]);
        var preview = Assert.IsType<PhotoFusePreview>(rendering.Preview);
        Assert.Null((await session.TryPreviewAsync([new(1, 56, 56, 72, 72)], Token)).Preview);
        Assert.NotNull(session.CheckRegions([new(1, 56, 56, 72, 72)], Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.GetAlignedFrameAsync(1, Token));
        using (var generic = await PrepareAsync([basis, donor], Token))
        {
            Assert.NotNull(await generic.CheckFaceAsync(new(1, face, face), Token));
            Assert.Null((await generic.TryPreviewFacesAsync([new(1, face, face)], Token)).Preview);
        }
        string saved = await session.ExportAsync(preview, _folder, true, Token);
        Assert.Equal(preview.CopyPixels(), await ReadPixelsAsync(saved));
        var xmp = ImageMetadataReader.ReadMetadata(saved).OfType<XmpDirectory>().Single().XmpMeta!;
        Assert.Equal("2", xmp.GetPropertyString(PhotoFuseSession.ProvenanceNamespace, "Version"));
        Assert.Contains("Scale=1+ScaleDelta", xmp.GetPropertyString(PhotoFuseSession.ProvenanceNamespace, "TransformConvention"));
        Assert.Contains("Search coordinate hint only", xmp.GetPropertyString(PhotoFuseSession.ProvenanceNamespace, "Sources"));
        using var regions = JsonDocument.Parse(xmp.GetPropertyString(PhotoFuseSession.ProvenanceNamespace, "Regions"));
        var stored = regions.RootElement[0];
        var region = Assert.Single(preview.Regions);
        Assert.Equal(region.SubjectTransform!.Value.ScaleDelta, stored.GetProperty("Transform").GetProperty("ScaleDelta").GetDouble());
        using var compressed = new MemoryStream(Convert.FromBase64String(stored.GetProperty("Mask").GetString()!));
        using var zipper = new ZLibStream(compressed, CompressionMode.Decompress);
        var alpha = new byte[region.Width * region.Height];
        zipper.ReadExactly(alpha);
        Assert.Equal(-1, zipper.ReadByte());
        Assert.Equal(255, alpha[(90 - region.Top) * region.Width + 90 - region.Left]);
        Assert.True(Encoding.UTF8.GetByteCount(preview.Provenance) <= PhotoFuseSession.MaximumLineageBytes);
        var oversized = session.AcceptPreview(Frame(), [region with
        {
            CompressedMask = new string('a', PhotoFuseSession.MaximumLineageBytes)
        }]);
        Assert.Null(oversized.Preview);
        Assert.Contains("metadata budget", oversized.Rejection);
    }

    [Fact]
    public async Task FailedReplacementKeepsTheLastAcceptedPreviewSaveableWithoutRerendering()
    {
        var basis = Entry(await WriteFrameAsync("base.png", Frame()), 1);
        var donor = Entry(await WriteFrameAsync("donor.png", FaceFrame(1)), 2);
        var later = Entry(await WriteFrameAsync("later.png", FaceFrame(2)), 3);
        List<FaceRegion> faces = [Face(1, 1, 80), Face(2, 1, 272)];
        using var workspace = new PhotoFuseViewModel(
            (_, _) => Task.FromResult(new List<ImageEntry> { donor, later }),
            (photo, _) => Task.FromResult<IReadOnlyList<FaceRegion>>(photo.Id == 1 ? faces :
                [Face(photo.Id * 10, photo.Id, 80), Face(photo.Id * 10 + 1, photo.Id, 272)]),
            _ => Task.FromResult(faces));
        await workspace.StartAsync(basis);
        await workspace.SelectAsync(faces[0], workspace.GetReplacements(faces[0]).First(choice => choice.Image.Id == donor.Id));
        byte[] accepted = workspace.DisplayPixels!.ToArray();
        var replacement = workspace.GetReplacements(faces[1]).First(choice => choice.Image.Id == later.Id);
        string altered = await WriteFrameAsync("altered.png", Frame(changed: 2));
        File.Move(altered, later.FilePath, overwrite: true);
        await workspace.SelectAsync(faces[1], replacement);
        Assert.Equal(accepted, workspace.DisplayPixels);
        Assert.True(workspace.HasChanges);
        Assert.Contains("changed", workspace.Status, StringComparison.OrdinalIgnoreCase);
        string saved = Assert.IsType<string>(await workspace.FinishAsync());
        Assert.Equal(accepted, await ReadPixelsAsync(saved));
    }

    [Fact]
    public void SmallSubjectBlinkHasAnIndependentlyMeasuredCleanSeam()
    {
        var face = PhotoFuseFaceBounds.From(Face(1, 1, 80));
        var selection = new PhotoFuseFaceSelection(1, face, face);
        var patch = PhotoFuseSubjectComposer.BuildPatch([Frame(), FaceFrame(1)], Size, Size,
            selection, null, [face], Token);
        Assert.True(patch.Patch is not null, patch.Rejection);
        Assert.Equal(255, patch.Patch.Alpha[(90 - patch.Patch.Region.Top) * patch.Patch.Region.Width +
            90 - patch.Patch.Region.Left]);
    }

    [Fact]
    public async Task FullPhotoWorkflowSearchesMoreThanFourShotsAndOffersOnlyNearbyFaces()
    {
        var basis = Entry(await WriteFrameAsync("base.png", Frame()), 1);
        var photos = new List<ImageEntry>();
        for (int i = 0; i < 12; i++)
            photos.Add(Entry(await WriteFrameAsync($"burst-{i}.png", FaceFrame(i % 2 + 1)), i + 2));
        FaceRegion first = Face(1, 1, 80), second = Face(2, 1, 272);
        List<FaceRegion> baseFaces = [first, second];
        var faces = photos.ToDictionary(photo => photo.Id, photo => new List<FaceRegion>
        {
            Face(photo.Id * 10, photo.Id, 80), Face(photo.Id * 10 + 1, photo.Id, 272),
            Face(photo.Id * 10 + 2, photo.Id, 190)
        });
        int scanned = 0;
        using var workspace = new PhotoFuseViewModel((_, _) => Task.FromResult(photos),
            (photo, _) =>
            {
                scanned++;
                return Task.FromResult<IReadOnlyList<FaceRegion>>(photo.Id == 1 ? baseFaces : faces[photo.Id]);
            }, _ => Task.FromResult(baseFaces));
        await workspace.StartAsync(basis);
        Assert.Equal(13, scanned);
        Assert.Equal(12, workspace.GetReplacements(first).Count);
        Assert.All(workspace.GetReplacements(first), choice => Assert.Equal(80.0 / Size, choice.Face.X));
        Assert.Equal(12, workspace.GetReplacements(second).Count);
        using (var manyFrames = await PrepareAsync(
            new[] { basis.FilePath }.Concat(photos.Select(photo => photo.FilePath)).ToArray(), Token))
            Assert.Equal(13, manyFrames.SourceNames.Count);
        await workspace.SelectAsync(first, workspace.GetReplacements(first)[0]);
        Assert.True(workspace.HasChanges, workspace.Status);
        await workspace.SelectAsync(second, workspace.GetReplacements(second)[1]);
        Assert.True(workspace.HasChanges, workspace.Status);
        Assert.Equal(Pixel(FaceFrame(1), 90, 90), Pixel(workspace.DisplayPixels!, 90, 90));
        Assert.Equal(Pixel(FaceFrame(2), 280, 280), Pixel(workspace.DisplayPixels!, 280, 280));
        byte[] accepted = workspace.DisplayPixels!.ToArray();
        workspace.ReleaseForManualFace();
        await workspace.RefreshFacesAsync();
        Assert.Equal(accepted, workspace.DisplayPixels);
        string? saved = await workspace.FinishAsync();
        Assert.NotNull(saved);
        Assert.True(File.Exists(saved));
        Assert.False(workspace.IsActive);
        Assert.Equal(Frame(), await ReadPixelsAsync(basis.FilePath));
    }

    [Fact]
    public async Task MissedFacesCanBeAddedAndStaleReplacementCannotBeApplied()
    {
        var basis = Entry(await WriteFrameAsync("base.png", Frame()), 1);
        var donor = Entry(await WriteFrameAsync("donor.png", FaceFrame(1)), 2);
        List<FaceRegion> baseFaces = [];
        using var workspace = new PhotoFuseViewModel((_, _) => Task.FromResult(new List<ImageEntry> { donor }),
            (photo, _) => Task.FromResult<IReadOnlyList<FaceRegion>>(photo.Id == 1 ? baseFaces : [Face(20, 2, 80)]),
            _ => Task.FromResult(baseFaces));
        await workspace.StartAsync(basis);
        Assert.Empty(workspace.Faces);
        baseFaces.Add(Face(10, 1, 80));
        workspace.ReleaseForManualFace();
        await workspace.RefreshFacesAsync();
        var face = Assert.Single(workspace.Faces);
        var replacement = Assert.Single(workspace.GetReplacements(face));
        var altered = await WriteFrameAsync("altered.png", Frame(changed: 2));
        File.Move(altered, donor.FilePath, true);
        await workspace.SelectAsync(face, replacement);
        Assert.False(workspace.HasChanges);
        Assert.Contains("changed", workspace.Status);
        Assert.Null(await workspace.FinishAsync());
        Assert.Empty(System.IO.Directory.GetFiles(_folder, "*Fused*"));
    }

    [Fact]
    public async Task DiscoverySkipsUnalignablePhotosAndBadFaceSeamsWithoutOfferingBrokenChoices()
    {
        var basis = Entry(await WriteFrameAsync("base.png", Frame()), 1);
        var flat = Frame();
        for (int i = 0; i < flat.Length; i += 4)
            flat[i] = flat[i + 1] = flat[i + 2] = 128;
        var unalignable = Entry(await WriteFrameAsync("flat.png", flat), 2);
        var badSeam = Frame();
        for (int y = 60; y < 124; y++)
        for (int x = 60; x < 124; x++)
            for (int c = 0; c < 3; c++) badSeam[(y * Size + x) * 4 + c] = 250;
        var incompatible = Entry(await WriteFrameAsync("bad-seam.png", badSeam), 3);
        var good = Entry(await WriteFrameAsync("good.png", FaceFrame(1)), 4);
        List<ImageEntry> photos = [unalignable, incompatible, good];
        using var workspace = new PhotoFuseViewModel((_, _) => Task.FromResult(photos),
            (photo, _) => Task.FromResult<IReadOnlyList<FaceRegion>>([Face(photo.Id, photo.Id, 80)]),
            photo => Task.FromResult(new List<FaceRegion> { Face(photo.Id, photo.Id, 80) }));
        await workspace.StartAsync(basis);
        var face = Assert.Single(workspace.Faces);
        var choice = Assert.Single(workspace.GetReplacements(face));
        Assert.Equal(good.Id, choice.Image.Id);
        await workspace.SelectAsync(face, choice);
        Assert.True(workspace.HasChanges, workspace.Status);
        Assert.Equal(Pixel(FaceFrame(1), 90, 90), Pixel(workspace.DisplayPixels!, 90, 90));
        await workspace.DiscardAsync();
        Assert.Empty(System.IO.Directory.GetFiles(_folder, "*Fused*"));
    }

    [Fact]
    public async Task NearbyTimeSearchHasNoCountLimitAndExcludesUnrelatedTimes()
    {
        using var db = new CacheDatabase(Path.Combine(_folder, "index.db"));
        await db.InitializeAsync();
        var repo = new ImageRepository(db);
        DateTime time = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
        var basis = Entry(Path.Combine(_folder, "base.png"), 0);
        basis.DateTaken = time;
        basis.FileSize = 0;
        basis.Id = await repo.UpsertImageAsync(basis);
        for (int i = 0; i < 25; i++)
        {
            var photo = new ImageEntry { FilePath = Path.Combine(_folder, $"photo-{i}.png"),
                FileName = $"photo-{i}.png", DateTaken = time.AddSeconds(i - 12), DateModified = time };
            await repo.UpsertImageAsync(photo);
        }
        await repo.UpsertImageAsync(new ImageEntry { FilePath = Path.Combine(_folder, "later.png"),
            FileName = "later.png", DateTaken = time.AddHours(1), DateModified = time });
        var nearby = await repo.GetNearbyBurstAsync(basis, TimeSpan.FromMinutes(2), Token);
        Assert.Equal(25, nearby.Count);
        Assert.DoesNotContain(nearby, photo => photo.FileName == "later.png");
    }

    [Fact]
    public void NineIndependentRegionsAreNotRejectedByAnArbitraryCountCap()
    {
        var basis = Frame();
        var regions = new List<PhotoFuseRegion>();
        for (int y = 0; y < 3; y++)
        for (int x = 0; x < 3; x++)
            regions.Add(new PhotoFuseRegion(1, 40 + x * 96, 40 + y * 96, 24, 24));
        var result = Render([basis, Frame()], [default, default], Size, Size, regions, Token);
        Assert.Equal(basis, result);
    }

    [Fact]
    public async Task DiscardDuringDiscoveryCancelsWithoutSavingOrRetainingLeases()
    {
        var basis = Entry(await WriteFrameAsync("base.png", Frame()), 1);
        var donor = Entry(await WriteFrameAsync("donor.png", Frame(changed: 1)), 2);
        var entered = new TaskCompletionSource();
        using var workspace = new PhotoFuseViewModel((_, _) => Task.FromResult(new List<ImageEntry> { donor }),
            async (photo, token) =>
            {
                if (photo.Id != 1)
                {
                    entered.TrySetResult();
                    await Task.Delay(TimeSpan.FromSeconds(30), token);
                }
                return new List<FaceRegion> { Face(photo.Id, photo.Id, 80) };
            }, _ => Task.FromResult(new List<FaceRegion> { Face(1, 1, 80) }));
        var started = workspace.StartAsync(basis);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await workspace.DiscardAsync();
        await started;
        Assert.False(workspace.IsActive);
        Assert.Empty(System.IO.Directory.GetFiles(_folder, "*Fused*"));
        using var released = File.Open(basis.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(basis.FileSize, released.Length);
    }

    private static ImageEntry Entry(string path, long id) => new()
    {
        Id = id, FilePath = path, FileName = Path.GetFileName(path),
        FileSize = File.Exists(path) ? new FileInfo(path).Length : 0,
        DateModified = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.UtcNow
    };

    private static FaceRegion Face(long id, long image, int position) => new()
    {
        Id = id, ImageId = image, X = position / (double)Size, Y = position / (double)Size,
        Width = 24.0 / Size, Height = 24.0 / Size
    };

    private static async Task<byte[]> ReadPixelsAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
        return (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            new BitmapTransform(), ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage)).DetachPixelData();
    }

    private async Task<string> WriteFrameAsync(string name, byte[] pixels, uint size = Size)
    {
        var path = Path.Combine(_folder, name);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, file.AsRandomAccessStream());
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, size, size, 96, 96, pixels);
        await encoder.FlushAsync();
        return path;
    }

    private static byte[] Pixel(byte[] pixels, int x, int y) => pixels.AsSpan((y * Size + x) * 4, 4).ToArray();

    internal static byte[] FaceFrame(int changed)
    {
        var pixels = Frame();
        int position = changed == 1 ? 90 : 280;
        for (int y = position; y < position + 4; y++)
        for (int x = position; x < position + 6; x++)
        {
            int i = (y * Size + x) * 4;
            pixels[i] = (byte)Math.Min(255, pixels[i] + 60);
            pixels[i + 1] = (byte)Math.Max(0, pixels[i + 1] - 40);
            pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] + 30);
        }
        return pixels;
    }

    // Original, deterministic synthetic scenery, evaluated continuously so the expected transform
    // is known exactly. Two small foreground regions can change independently of the background.
    internal static byte[] Frame(double dx = 0, double dy = 0, double angle = 0, double scale = 1, int changed = 0,
        int size = Size)
    {
        var pixels = new byte[size * size * 4];
        var (sin, cos) = Math.SinCos(angle);
        double center = (size - 1) / 2.0;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            double px = x - center, py = y - center;
            double qx = scale * (cos * px - sin * py) + center + dx;
            double qy = scale * (sin * px + cos * py) + center + dy;
            double luma = 125 + 28 * Math.Sin(qx * 0.18 + Math.Sin(qy * 0.044) * 4) +
                24 * Math.Cos(qy * 0.23 + Math.Cos(qx * 0.021) * 3) +
                16 * Math.Sin(qx * 0.057 + qy * 0.081) + 10 * Math.Cos(qx * qy * 0.0007);
            bool patch = changed == 1 && qx is > 80 and < 104 && qy is > 80 and < 104 ||
                changed == 2 && qx is > 272 and < 296 && qy is > 272 and < 296;
            int i = (y * size + x) * 4;
            pixels[i] = (byte)Math.Clamp(Math.Round(luma + (patch ? 55 : -12)), 0, 255);
            pixels[i + 1] = (byte)Math.Clamp(Math.Round(luma + (patch ? -35 : 8)), 0, 255);
            pixels[i + 2] = (byte)Math.Clamp(Math.Round(luma + (patch ? 40 : 16)), 0, 255);
            pixels[i + 3] = 255;
        }
        return pixels;
    }
}
