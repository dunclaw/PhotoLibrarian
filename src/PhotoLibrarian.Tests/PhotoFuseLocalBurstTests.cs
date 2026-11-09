// Copyright (c) Duncan Lawler. All Rights Reserved.
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.InteropServices.WindowsRuntime;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.ML.Services;
using Xunit;
using Windows.Graphics.Imaging;

namespace PhotoLibrarian.Tests;

/// <summary>Opt-in read-only reproduction; no library, metadata writes, exports or photo copies.</summary>
public sealed class PhotoFuseLocalBurstTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ReportLocalBurstRegistration()
    {
        string? path = Environment.GetEnvironmentVariable("PHOTOLIBRARIAN_FUSE_TEST_PHOTO");
        if (string.IsNullOrEmpty(path)) return;
        var token = TestContext.Current.CancellationToken;
        var snapshots = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        Snapshot(path, snapshots);
        var basis = await WindowsImagingCodec.DecodeAsync(path, 1600, token);
        DateTime time = ImageMetadataReader.ReadMetadata(path).OfType<ExifSubIfdDirectory>()
            .Select(directory => directory.GetDateTime(ExifDirectoryBase.TagDateTimeOriginal)).First();
        int accepted = 0, total = 0, previews = 0;
        Assert.True(System.IO.Directory.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoLibrarian", "Models")),
            "The opt-in test requires already-installed local face models; it never installs them.");
        using var models = new OnnxSessionManager();
        await new LocalFaceModelProvider(models).EnsureModelsAsync(token);
        var detector = new FaceDetectionService(models);
        detector.LoadModel();
        var faces = await detector.DetectFacesAsync(path, token);
        output.WriteLine($"Detected {faces.Count} base faces.");
        foreach (var donor in System.IO.Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.JPG"))
        {
            if (string.Equals(path, donor, StringComparison.OrdinalIgnoreCase)) continue;
            var directory = ImageMetadataReader.ReadMetadata(donor).OfType<ExifSubIfdDirectory>().FirstOrDefault();
            if (directory is null || !directory.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var date) ||
                Math.Abs((date - time).TotalMinutes) > 2) continue;
            var pixels = await WindowsImagingCodec.DecodeAsync(donor, 1600, token);
            if (pixels.Width != basis.Width || pixels.Height != basis.Height) continue;
            Snapshot(donor, snapshots);
            var watch = Stopwatch.StartNew();
            var result = PhotoFuseRegistration.TryRegister(pixels.Pixels, basis.Pixels,
                (int)basis.Width, (int)basis.Height, token);
            total++;
            if (result.Accepted) accepted++;
            output.WriteLine($"{Path.GetFileName(donor)} ({(date - time).TotalSeconds:F0}s): " +
                $"{(result.Accepted ? result.Transform.ToString() : result.Reason)} ({watch.ElapsedMilliseconds}ms)");
            if (!result.Accepted) continue;
            var donorFaces = await detector.DetectFacesAsync(donor, token);
            var preparation = await PhotoFuseSession.TryPrepareAsync([path, donor], token);
            using var pair = Assert.IsType<PhotoFuseSession>(preparation.Session);
            int offered = 0, rendered = 0;
            foreach (var detected in faces)
            {
                var face = new FaceRegion { X = detected.X, Y = detected.Y,
                    Width = detected.Width, Height = detected.Height };
                var candidates = PhotoFuseFaceMatcher.Nearby(face, donorFaces.Select(found => new FaceRegion
                    { X = found.X, Y = found.Y, Width = found.Width, Height = found.Height }), pair);
                if (candidates.Count == 0) continue;
                offered++;
                var region = PhotoFuseFaceMatcher.Region(face, 1, pair.Width, pair.Height);
                var preview = await pair.TryPreviewAsync([region], token);
                if (preview.Preview is not null)
                {
                    rendered++;
                    previews++;
                    var composite = preview.Preview.CopyPixels();
                    for (int y = 0; y < pair.Height; y++)
                    for (int x = 0; x < pair.Width; x++)
                    {
                        if (x >= region.Left && x < region.Left + region.Width &&
                            y >= region.Top && y < region.Top + region.Height) continue;
                        int offset = (y * pair.Width + x) * 4;
                        if (!basis.Pixels.AsSpan(offset, 4).SequenceEqual(composite.AsSpan(offset, 4)))
                            Assert.Fail($"Unexpected change outside the selected region at {x},{y}.");
                    }
                    if (previews == 1)
                    {
                        string folder = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-LocalFuse-{Guid.NewGuid():N}");
                        System.IO.Directory.CreateDirectory(folder);
                        try
                        {
                            string outputPath = await pair.ExportAsync(preview.Preview, folder, true, token);
                            await using var stream = File.OpenRead(outputPath);
                            var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream()).AsTask(token);
                            var saved = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8,
                                BitmapAlphaMode.Ignore, new BitmapTransform(),
                                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(token);
                            Assert.Equal(composite, saved.DetachPixelData());
                        }
                        finally { System.IO.Directory.Delete(folder, recursive: true); }
                    }
                }
                else output.WriteLine($"Face region rejected: {preview.Rejection}");
            }
            output.WriteLine($"{Path.GetFileName(donor)}: {offered} face proposals, {rendered} accepted previews.");
        }
        foreach (var snapshot in snapshots)
        {
            if (snapshot.Value is null) Assert.False(File.Exists(snapshot.Key), "A source sidecar was created.");
            else Assert.Equal(snapshot.Value, SHA256.HashData(await File.ReadAllBytesAsync(snapshot.Key, token)));
        }
        output.WriteLine($"Accepted {accepted}/{total} nearby shots; {previews} clean face previews.");
        Assert.True(accepted > 0, "No nearby photo could be registered.");
        Assert.True(previews > 0, "No offered face replacement could actually be rendered.");
    }

    private static void Snapshot(string photo, IDictionary<string, byte[]?> snapshots)
    {
        foreach (string path in new[] { photo, Path.ChangeExtension(photo, ".xmp"), photo + ".xmp" })
            snapshots.TryAdd(path, File.Exists(path) ? SHA256.HashData(File.ReadAllBytes(path)) : null);
    }
}
