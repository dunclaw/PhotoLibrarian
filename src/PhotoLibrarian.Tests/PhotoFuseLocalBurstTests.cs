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

/// <summary>Opt-in local reproduction; originals stay read-only and outputs use temporary/test directories.</summary>
public sealed class PhotoFuseLocalBurstTests(ITestOutputHelper output)
{
    private const int ReviewCell = 256;
    private const int ReviewWidth = ReviewCell * 3;
    [Fact]
    public async Task ReportLocalBurstRegistration()
    {
        string? path = Environment.GetEnvironmentVariable("PHOTOLIBRARIAN_FUSE_TEST_PHOTO");
        if (string.IsNullOrEmpty(path)) return;
        string? reviewFolder = Environment.GetEnvironmentVariable("PHOTOLIBRARIAN_FUSE_TEST_REVIEW");
        if (!string.IsNullOrEmpty(reviewFolder)) Assert.True(IsReviewOutsideSourceTree(path, reviewFolder));
        var token = TestContext.Current.CancellationToken;
        var snapshots = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        Snapshot(path, snapshots);
        var basis = await WindowsImagingCodec.DecodeAsync(path, 1600, token);
        DateTime time = ImageMetadataReader.ReadMetadata(path).OfType<ExifSubIfdDirectory>()
            .Select(directory => directory.GetDateTime(ExifDirectoryBase.TagDateTimeOriginal)).First();
        int accepted = 0, total = 0, previews = 0;
        bool checkedCombined = false;
        var coveredFaces = new HashSet<int>();
        Assert.True(System.IO.Directory.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoLibrarian", "Models")),
            "The opt-in test requires already-installed local face models; it never installs them.");
        using var models = new OnnxSessionManager();
        await new LocalFaceModelProvider(models).EnsureModelsAsync(token);
        var detector = new FaceDetectionService(models);
        detector.LoadModel();
        var faces = await detector.DetectFacesAsync(path, token);
        var originalFaces = faces.Select(face => new PhotoFuseFaceBounds(face.X, face.Y, face.Width, face.Height)).ToArray();
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
            output.WriteLine($"{Path.GetFileName(donor)} ({(date - time).TotalSeconds:F0}s): " +
                $"{(result.Accepted ? result.Transform.ToString() : result.Reason)} ({watch.ElapsedMilliseconds}ms)");
            var donorFaces = await detector.DetectFacesAsync(donor, token);
            var preparationWatch = Stopwatch.StartNew();
            var preparation = await PhotoFuseSession.TryPrepareFacesAsync([path, donor], token);
            using var pair = Assert.IsType<PhotoFuseSession>(preparation.Session);
            long preparationMs = preparationWatch.ElapsedMilliseconds, subjectMs = 0;
            int offered = 0, rendered = 0;
            var acceptedSelections = new List<PhotoFuseFaceSelection>();
            int faceIndex = 0;
            byte[]? review = string.IsNullOrEmpty(reviewFolder) ? null : new byte[ReviewWidth * faces.Count * ReviewCell * 4];
            foreach (var detected in faces)
            {
                int index = faceIndex++;
                var face = new FaceRegion { X = detected.X, Y = detected.Y,
                    Width = detected.Width, Height = detected.Height };
                var candidates = PhotoFuseFaceMatcher.Nearby(face, donorFaces.Select(found => new FaceRegion
                    { X = found.X, Y = found.Y, Width = found.Width, Height = found.Height }), pair);
                if (review is not null)
                    DrawFace(review, index, 0, basis.Pixels, (int)basis.Width, (int)basis.Height, PhotoFuseFaceBounds.From(face));
                if (candidates.Count == 0) continue;
                if (review is not null)
                    DrawFace(review, index, 1, pixels.Pixels, (int)pixels.Width, (int)pixels.Height,
                        PhotoFuseFaceBounds.From(candidates[0]));
                offered++;
                var selection = new PhotoFuseFaceSelection(1, PhotoFuseFaceBounds.From(face),
                    PhotoFuseFaceBounds.From(candidates[0]));
                var subjectWatch = Stopwatch.StartNew();
                var preview = await pair.TryPreviewFacesAsync([selection], token, originalFaces);
                subjectMs += subjectWatch.ElapsedMilliseconds;
                if (preview.Preview is not null)
                {
                    var region = Assert.Single(preview.Preview.Regions);
                    rendered++;
                    previews++;
                    acceptedSelections.Add(selection);
                    coveredFaces.Add(index);
                    var composite = preview.Preview.CopyPixels();
                    if (review is not null)
                        DrawFace(review, index, 2, composite, pair.Width, pair.Height, PhotoFuseFaceBounds.From(face));
                    for (int y = 0; y < pair.Height; y++)
                    for (int x = 0; x < pair.Width; x++)
                    {
                        if (x >= region.Left && x < region.Left + region.Width &&
                            y >= region.Top && y < region.Top + region.Height) continue;
                        int offset = (y * pair.Width + x) * 4;
                        if (!basis.Pixels.AsSpan(offset, 4).SequenceEqual(composite.AsSpan(offset, 4)))
                            Assert.Fail($"Unexpected change outside the selected region or on a protected face at {x},{y}.");
                    }
                    foreach (var other in originalFaces.Where(other => other != selection.Target))
                    {
                        int left = (int)Math.Ceiling(other.X * pair.Width);
                        int right = (int)Math.Floor((other.X + other.Width) * pair.Width);
                        int top = (int)Math.Ceiling(other.Y * pair.Height);
                        int bottom = (int)Math.Floor((other.Y + other.Height) * pair.Height);
                        for (int y = top; y <= bottom; y++)
                        for (int x = left; x <= right; x++)
                        {
                            int offset = (y * pair.Width + x) * 4;
                            if (!basis.Pixels.AsSpan(offset, 4).SequenceEqual(composite.AsSpan(offset, 4)))
                                Assert.Fail($"Unexpected change on a protected neighboring face at {x},{y}.");
                        }
                    }
                    if (!checkedCombined && acceptedSelections.Count >= 2)
                    {
                        var combined = await pair.TryPreviewFacesAsync(acceptedSelections.Take(2).ToArray(), token, originalFaces);
                        Assert.True(combined.Preview is not null, combined.Rejection);
                        Assert.Equal(2, combined.Preview.Regions.Length);
                        checkedCombined = true;
                        output.WriteLine("Two independently chosen faces render together without overlapping masks.");
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
                            if (Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
                                Assert.Equal(composite, saved.DetachPixelData());
                            else
                                PhotoFuseTests.AssertJpegMatchesPreview(composite, saved.DetachPixelData());
                            Assert.Equal(Path.GetExtension(path), Path.GetExtension(outputPath));
                        }
                        finally { System.IO.Directory.Delete(folder, recursive: true); }
                    }
                }
                else output.WriteLine($"Face {index} region rejected: {preview.Rejection}");
            }
            if (rendered > 0) accepted++;
            output.WriteLine($"{Path.GetFileName(donor)}: {offered} face proposals, {rendered} accepted previews; " +
                $"pair preparation {preparationMs}ms, subject renders {subjectMs}ms.");
            if (review is not null && !string.IsNullOrEmpty(reviewFolder))
            {
                var encoded = await WindowsImagingCodec.EncodeAsync("review.png", review, ReviewWidth,
                    (uint)faces.Count * ReviewCell, token);
                string reviewPath = Path.Combine(reviewFolder, $"fuse-local-{Path.GetFileNameWithoutExtension(donor)}.png");
                await using var stream = new FileStream(reviewPath, FileMode.CreateNew, FileAccess.Write);
                await stream.WriteAsync(encoded, token);
            }
        }
        foreach (var snapshot in snapshots)
        {
            if (snapshot.Value is null) Assert.False(File.Exists(snapshot.Key), "A source sidecar was created.");
            else Assert.Equal(snapshot.Value, SHA256.HashData(await File.ReadAllBytesAsync(snapshot.Key, token)));
        }
        output.WriteLine($"Accepted {accepted}/{total} nearby shots; {previews} clean face previews; " +
            $"{coveredFaces.Count}/{faces.Count} base faces have replacements.");
        Assert.True(accepted > 0, "No nearby photo could be registered.");
        Assert.True(previews > 0, "No offered face replacement could actually be rendered.");
        Assert.Equal(faces.Count, coveredFaces.Count);
        Assert.True(checkedCombined, "No combined face replacements were verified.");
    }

    [Theory]
    [InlineData("D:\\photos\\burst", false)]
    [InlineData("d:\\PHOTOS\\BURST\\review", false)]
    [InlineData("D:\\photos\\burst\\..\\burst\\review", false)]
    [InlineData("D:\\photos\\burst-review", true)]
    [InlineData("D:\\test-review", true)]
    public void ReviewOutputCannotBeWrittenInTheSourceTree(string review, bool allowed) =>
        Assert.Equal(allowed, IsReviewOutsideSourceTree("D:\\photos\\burst\\base.jpg", review));

    private static bool IsReviewOutsideSourceTree(string source, string review)
    {
        string tree = Path.GetDirectoryName(Path.GetFullPath(source))!
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string destination = Path.GetFullPath(review).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !string.Equals(tree, destination, StringComparison.OrdinalIgnoreCase) &&
            !destination.StartsWith(tree + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void Snapshot(string photo, IDictionary<string, byte[]?> snapshots)
    {
        foreach (string path in new[] { photo, Path.ChangeExtension(photo, ".xmp"), photo + ".xmp" })
            snapshots.TryAdd(path, File.Exists(path) ? SHA256.HashData(File.ReadAllBytes(path)) : null);
    }

    private static void DrawFace(byte[] sheet, int row, int column, byte[] pixels, int width, int height,
        PhotoFuseFaceBounds face)
    {
        for (int y = 0; y < ReviewCell; y++)
        for (int x = 0; x < ReviewCell; x++)
        {
            double sx = (face.X - face.Width * 0.35 + (x + 0.5) / ReviewCell * face.Width * 1.7) * width;
            double sy = (face.Y - face.Height * 0.35 + (y + 0.5) / ReviewCell * face.Height * 1.7) * height;
            sx = Math.Clamp(sx, 0, width - 1.001);
            sy = Math.Clamp(sy, 0, height - 1.001);
            int index = ((row * ReviewCell + y) * ReviewWidth + column * ReviewCell + x) * 4;
            for (int c = 0; c < 3; c++)
                sheet[index + c] = (byte)Math.Round(PhotoFuseRegistration.Sample(pixels, width, sx, sy, c));
            sheet[index + 3] = 255;
        }
    }
}
