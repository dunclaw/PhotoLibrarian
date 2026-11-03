// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using MetadataExtractor;
using MetadataExtractor.Formats.Xmp;
using PhotoLibrarian.Core.Services;
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

    public void Dispose() => System.IO.Directory.Delete(_folder, recursive: true);

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
        var found = PhotoFuseRegistration.Register(donor, basis, Size, Size, Token);
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
        Assert.Throws<InvalidDataException>(() => PhotoFuseRegistration.Register(
            Frame(dx, 0, angle, scale), Frame(), Size, Size, Token));

    [Fact]
    public void RejectsFlatUnrelatedAndPhotometricallyMismatchedFrames()
    {
        var flat = Enumerable.Repeat((byte)128, Size * Size * 4).ToArray();
        Assert.Throws<InvalidDataException>(() => PhotoFuseRegistration.Register(flat, flat, Size, Size, Token));
        var shiftedColor = Frame();
        for (int i = 0; i < shiftedColor.Length; i += 4)
            shiftedColor[i] = (byte)Math.Min(255, shiftedColor[i] + 45);
        Assert.Throws<InvalidDataException>(() => PhotoFuseRegistration.Register(
            shiftedColor, Frame(), Size, Size, Token));
        var unrelated = Frame();
        new Random(5821).NextBytes(unrelated);
        Assert.Throws<InvalidDataException>(() => PhotoFuseRegistration.Register(
            unrelated, Frame(), Size, Size, Token));
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
        Assert.Throws<InvalidDataException>(() =>
            PhotoFuseRegistration.Register(repeating, repeating, Size, Size, Token));
        Assert.Throws<InvalidDataException>(() =>
            PhotoFuseRegistration.Register(localized, localized, Size, Size, Token));
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
        using var session = await PhotoFuseSession.PrepareAsync([source, donor], Token);
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
        using var session = await PhotoFuseSession.PrepareAsync(paths, Token);
        Assert.Equal(PhotoFuseSession.MaximumDimension, session.Width);
        Assert.Equal(PhotoFuseSession.MaximumDimension, session.Height);
        Assert.Equal(4, session.SourceNames.Count);
        var preview = await session.PreviewAsync([new(1, 56, 56, 72, 72)], Token);
        Assert.Equal(1600 * 1600 * 4, preview.CopyPixels().Length);
        await session.ExportAsync(preview, _folder, true, Token);
        _output.WriteLine($"Four 1601px inputs, decode/register/preview/export including fixture encoding: " +
            $"{watch.ElapsedMilliseconds}ms; process peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576} MiB");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"End-to-end took {watch.Elapsed}");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            PhotoFuseSession.PrepareAsync(paths.Append(paths[0]).ToArray(), Token));
    }

    [Fact]
    public void RejectsLargeOcclusionAndBadRegionSeams()
    {
        var donor = Frame();
        for (int y = 10; y < 130; y++)
        for (int x = 10; x < 130; x++)
            for (int c = 0; c < 3; c++) donor[(y * Size + x) * 4 + c] = 250;
        Assert.Throws<InvalidDataException>(() => PhotoFuseRegistration.Register(donor, Frame(), Size, Size, Token));
        Assert.Throws<InvalidDataException>(() => PhotoFuseComposer.Render(
            [Frame(), Frame(changed: 1)], [default, default], Size, Size,
            [new(1, 76, 80, 24, 24)], Token));
    }

    [Fact]
    public void FullPreviewRegistrationAccountsForDownsampleCenterAndRejectsParallax()
    {
        const int size = 1600;
        var basis = Frame(size: size);
        var donor = Frame(5.2, -3.4, 0.012, size: size);
        var found = PhotoFuseRegistration.Register(donor, basis, size, size, Token);
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
        Assert.Throws<InvalidDataException>(() =>
            PhotoFuseRegistration.Register(parallax, Frame(), Size, Size, Token));
    }

    [Fact]
    public async Task RejectsTransparentHighBitDepthAndAnimatedPngSources()
    {
        var opaque = await WriteFrameAsync("opaque.png", Frame());
        var transparent = Path.Combine(_folder, "transparent.png");
        var pixels = Frame();
        Array.Clear(pixels, 0, 4);
        await RenderQualityFixtures.WriteImageAsync(transparent, Size, Size, pixels);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            PhotoFuseSession.PrepareAsync([opaque, transparent], Token));
        var highDepth = Path.Combine(_folder, "16bit.png");
        await using (var file = new FileStream(highDepth, FileMode.CreateNew, FileAccess.ReadWrite))
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, file.AsRandomAccessStream());
            encoder.SetPixelData(BitmapPixelFormat.Rgba16, BitmapAlphaMode.Ignore, Size, Size, 96, 96,
                new byte[Size * Size * 8]);
            await encoder.FlushAsync();
        }
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            PhotoFuseSession.PrepareAsync([opaque, highDepth], Token));
        // APNG's control chunk is encountered before WIC can silently decode just its first frame.
        var animated = Path.Combine(_folder, "animated.png");
        var encoded = File.ReadAllBytes(opaque);
        using (var file = File.Create(animated))
        {
            file.Write(encoded.AsSpan(0, 33));
            file.Write(new byte[] { 0, 0, 0, 8, 97, 99, 84, 76, 0, 0, 0, 2, 0, 0, 0, 0, 243, 141, 147, 112 });
            file.Write(encoded.AsSpan(33));
        }
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            PhotoFuseSession.PrepareAsync([opaque, animated], Token));
    }

    [Fact]
    public void ChoosesIndependentRegionsWithoutChangingOtherPixels()
    {
        var basis = Frame();
        var frames = new[] { basis, Frame(changed: 1), Frame(changed: 2) };
        var regions = new PhotoFuseRegion[] { new(1, 56, 56, 72, 72), new(2, 248, 248, 72, 72) };
        var result = PhotoFuseComposer.Render(frames, [default, default, default], Size, Size, regions, Token);
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
            Assert.Throws<ArgumentException>(() =>
                PhotoFuseComposer.Render(frames, transforms, Size, Size, [region], Token));
        Assert.Throws<ArgumentException>(() => PhotoFuseComposer.Render(frames, transforms, Size, Size,
            [new(1, 50, 50, 40, 40), new(1, 60, 60, 40, 40)], Token));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PhotoFuseRegistration.Register(
            frames[1], frames[0], Size, Size, cts.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => PhotoFuseComposer.Render(
            frames, transforms, Size, Size, [], cts.Token));
    }

    [Fact]
    public async Task ExportsExactlyReviewedPixelsWithPortableLineageAndNoSourceChanges()
    {
        var first = await WriteFrameAsync("base.png", Frame());
        var second = await WriteFrameAsync("donor.png", Frame(changed: 1));
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(first));
        byte[] donorHash = SHA256.HashData(File.ReadAllBytes(second));
        using (var session = await PhotoFuseSession.PrepareAsync([first, second], Token))
        {
            Assert.Equal(Size, session.Width);
            Assert.Throws<IOException>(() => File.Open(first, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
            var preview = await session.PreviewAsync([new(1, 56, 56, 72, 72)], Token);
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
        using var session = await PhotoFuseSession.PrepareAsync([first, second], Token);
        var preview = await session.PreviewAsync([new(1, 56, 56, 72, 72)], Token);
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
            PhotoFuseSession.PrepareAsync([first, invalid], Token));
        // A failed prepare must release every earlier source lease.
        using (File.Open(first, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        using var session = await PhotoFuseSession.PrepareAsync([first, second], Token);
        using var other = await PhotoFuseSession.PrepareAsync([first, second], Token);
        var preview = await session.PreviewAsync([new(1, 56, 56, 72, 72)], Token);
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
        using var session = await PhotoFuseSession.PrepareAsync([first, second], Token);
        var preview = await session.PreviewAsync([new(1, 56, 56, 72, 72)], Token);
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
            PhotoFuseSession.PrepareAsync([Path.Combine(_folder, "missing.png"), Path.Combine(_folder, "other.png")], Token));
        var first = await WriteFrameAsync("base.png", Frame());
        var second = await WriteFrameAsync("donor.png", Frame());
        await Assert.ThrowsAsync<ArgumentException>(() => PhotoFuseSession.PrepareAsync([first, first], Token));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            PhotoFuseSession.PrepareAsync([Path.Combine(_folder, "raw.dng"), second], Token));
        File.WriteAllText(Path.ChangeExtension(first, ".xmp"),
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description xmlns:pledit=\"http://ns.photolibrarian.app/edit/1.0/\" pledit:CropLeft=\"0.2\"/>" +
            "</rdf:RDF></x:xmpmeta>");
        await Assert.ThrowsAsync<NotSupportedException>(() => PhotoFuseSession.PrepareAsync([first, second], Token));
        File.Delete(Path.ChangeExtension(first, ".xmp"));
        using var session = await PhotoFuseSession.PrepareAsync([first, second], Token);
        await Assert.ThrowsAsync<ArgumentException>(() => session.PreviewAsync([], Token));
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

    // Original, deterministic synthetic scenery, evaluated continuously so the expected transform
    // is known exactly. Two small foreground regions can change independently of the background.
    private static byte[] Frame(double dx = 0, double dy = 0, double angle = 0, double scale = 1, int changed = 0,
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
