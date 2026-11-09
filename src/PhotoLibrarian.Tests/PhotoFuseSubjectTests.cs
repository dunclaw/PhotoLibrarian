// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Diagnostics;
using System.IO.Compression;
using PhotoLibrarian.Core.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class PhotoFuseSubjectTests(ITestOutputHelper output)
{
    private const int Size = 384;
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private static PhotoFuseFaceBounds Face(int x, int y, int size = 48) =>
        new(x / (double)Size, y / (double)Size, size / (double)Size, size / (double)Size);

    [Theory]
    [InlineData(5.2, -3.4, 0.0, 1.0)]
    [InlineData(-4.0, 2.0, 0.07, 1.0)]
    [InlineData(2.0, 1.0, -0.04, 1.08)]
    [InlineData(-3.0, 1.5, 0.02, 0.92)]
    public void IndependentlyMeasuresSubjectSimilarityDespiteUnrelatedBackgroundMotion(
        double dx, double dy, double angle, double scale)
    {
        var basis = PhotoFuseTests.Frame();
        var donor = PhotoFuseTests.Frame(dx, dy, angle, scale);
        Change(donor, 215, 0, 169, Size);
        Assert.False(PhotoFuseRegistration.TryRegister(donor, basis, Size, Size, Token).Accepted);
        var expected = new PhotoFuseTransform(dx, dy, angle, scale - 1);
        var target = Face(70, 70, 64);
        var corners = new[]
        {
            expected.SourceAt(70, 70, Size, Size), expected.SourceAt(134, 70, Size, Size),
            expected.SourceAt(70, 134, Size, Size), expected.SourceAt(134, 134, Size, Size)
        };
        double x = corners.Min(point => point.X), y = corners.Min(point => point.Y);
        var source = new PhotoFuseFaceBounds(x / Size, y / Size,
            (corners.Max(point => point.X) - x) / Size, (corners.Max(point => point.Y) - y) / Size);
        var selection = new PhotoFuseFaceSelection(1, target, source);
        var watch = Stopwatch.StartNew();
        var result = PhotoFuseSubjectComposer.BuildPatch([basis, donor], Size, Size,
            selection, null, [target], Token);
        var patch = Assert.IsType<PhotoFuseSubjectComposer.Patch>(result.Patch);
        double maximumError = corners.Max(point =>
        {
            var a = expected.BaseAt(point.X, point.Y, Size, Size);
            var b = patch.Transform.BaseAt(point.X, point.Y, Size, Size);
            return Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
        });
        output.WriteLine($"Subject {dx},{dy},{angle},{scale}: {maximumError:F3}px, {watch.ElapsedMilliseconds}ms");
        Assert.True(maximumError < 0.5, $"Measured subject landmark error was {maximumError:F3}px.");
    }

    [Fact]
    public void PoseReplacementUsesSurroundingContextAndLeavesOtherFacesExactlyOriginal()
    {
        var basis = PhotoFuseTests.Frame();
        var donor = (byte[])basis.Clone();
        var target = Face(80, 80);
        var neighbor = Face(140, 80);
        Change(donor, 80, 80, 48, 48);
        Change(donor, 140, 80, 48, 48);
        var selection = new PhotoFuseFaceSelection(1, target, target);
        var facial = PhotoFuseSubjectAlignment.Align(donor, basis, Size, Size, selection,
            null, [target, neighbor], true, Token);
        Assert.Null(facial.Transform);
        var result = PhotoFuseSubjectComposer.TryRender([basis, donor], Size, Size, [selection],
            [null, null], [target, neighbor], Token);
        Assert.True(result.Pixels is not null, result.Rejection);
        Assert.Equal(Pixel(donor, 104, 104), Pixel(result.Pixels, 104, 104));
        for (int y = 80; y <= 128; y++)
        for (int x = 140; x <= 188; x++)
            Assert.Equal(Pixel(basis, x, y), Pixel(result.Pixels, x, y));
        CheckMask(result, basis, donor);
    }

    [Fact]
    public void OverlappingCropRectanglesAllowDisjointFaceMasksButActualMaskOverlapRejects()
    {
        var basis = PhotoFuseTests.Frame();
        var donor = PhotoFuseTests.FaceFrame(1);
        Change(donor, 152, 92, 6, 4);
        var first = Face(80, 80);
        var second = Face(140, 80);
        PhotoFuseFaceSelection[] choices = [new(1, first, first), new(1, second, second)];
        var result = PhotoFuseSubjectComposer.TryRender([basis, donor], Size, Size, choices,
            [null, null], [first, second], Token);
        Assert.True(result.Pixels is not null, result.Rejection);
        Assert.Equal(2, result.Regions.Length);
        Assert.True(result.Regions[0].Left + result.Regions[0].Width > result.Regions[1].Left);
        CheckMask(result, basis, donor);
        var overlap = PhotoFuseSubjectComposer.TryRender([basis, donor], Size, Size, [choices[0], choices[0]],
            [null, null], [first, second], Token);
        Assert.Null(overlap.Pixels);
        Assert.Contains("overlap", overlap.Rejection);
    }

    [Fact]
    public void UnsafeGeometryFlatTextureCoverageAndImpossibleSeamsAreExplicitRejections()
    {
        var basis = PhotoFuseTests.Frame();
        var face = Face(80, 80);
        var choice = new PhotoFuseFaceSelection(1, face, face);
        var flat = new byte[basis.Length];
        for (int i = 0; i < flat.Length; i += 4)
        {
            flat[i] = flat[i + 1] = flat[i + 2] = 128;
            flat[i + 3] = 255;
        }
        foreach (var invalid in new[]
        {
            choice with { Frame = 0 }, choice with { Frame = 2 },
            choice with { Target = face with { X = double.NaN } },
            choice with { Donor = Face(300, 300, 20) },
            choice with { Donor = Face(200, 200) }
        })
        {
            var rejected = PhotoFuseSubjectComposer.BuildPatch([basis, basis], Size, Size,
                invalid, null, [face], Token);
            Assert.Null(rejected.Patch);
            Assert.False(string.IsNullOrEmpty(rejected.Rejection));
        }
        Assert.Null(PhotoFuseSubjectComposer.BuildPatch([flat, flat], Size, Size,
            choice, null, [face], Token).Patch);
        var crop = new PhotoFuseRegion(1, 32, 32, 144, 144);
        var clipped = PhotoFuseSeamFinder.Find(basis, basis, new(120, 0, 0), Size, Size,
            crop, choice, [face], Token);
        Assert.Null(clipped.Alpha);
        var incompatible = (byte[])basis.Clone();
        Change(incompatible, 0, 0, Size, Size);
        var badSeam = PhotoFuseSeamFinder.Find(incompatible, basis, default, Size, Size,
            crop, choice, [face], Token);
        Assert.Null(badSeam.Alpha);
        var occluded = PhotoFuseSeamFinder.Find(basis, basis, default, Size, Size,
            crop, choice, [face, Face(92, 92, 24)], Token);
        Assert.Null(occluded.Alpha);
        Assert.Contains("neighboring face", occluded.Rejection);
    }

    [Fact]
    public void RepeatingSubjectTextureRejectsCompetingLocalAlignments()
    {
        var pixels = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            byte value = (byte)Math.Round(128 + 40 * Math.Sin(x * Math.PI / 6) +
                30 * Math.Cos(y * Math.PI / 6) + 12 * Math.Sin((x + y) * Math.PI / 6));
            int offset = (y * Size + x) * 4;
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = value;
            pixels[offset + 3] = 255;
        }
        var face = Face(112, 112, 96);
        var result = PhotoFuseSubjectComposer.BuildPatch([pixels, pixels], Size, Size,
            new(1, face, face), null, [face], Token);
        Assert.Null(result.Patch);
        Assert.Contains("competing alignments", result.Rejection);
    }

    [Fact]
    public void AlignmentAndGraphRespectCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var pixels = PhotoFuseTests.Frame();
        var face = Face(80, 80);
        var choice = new PhotoFuseFaceSelection(1, face, face);
        Assert.Throws<OperationCanceledException>(() => PhotoFuseSubjectAlignment.Align(pixels, pixels,
            Size, Size, choice, null, [face], true, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => PhotoFuseSeamFinder.Find(pixels, pixels, default,
            Size, Size, new(1, 32, 32, 144, 144), choice, [face], cancellation.Token));
    }

    private void CheckMask(PhotoFuseSubjectComposer.Rendering result, byte[] basis, byte[] donor)
    {
        var covered = new bool[Size * Size];
        foreach (var region in result.Regions)
        {
            using var compressed = new MemoryStream(Convert.FromBase64String(region.CompressedMask!));
            using var zipper = new ZLibStream(compressed, CompressionMode.Decompress);
            var alpha = new byte[region.Width * region.Height];
            zipper.ReadExactly(alpha);
            Assert.Equal(-1, zipper.ReadByte());
            var transform = Assert.IsType<PhotoFuseTransform>(region.SubjectTransform);
            int band = 0, inliers = 0;
            for (int y = 0; y < region.Height; y++)
            for (int x = 0; x < region.Width; x++)
            {
                byte weight = alpha[y * region.Width + x];
                if (weight == 0) continue;
                int bx = region.Left + x, by = region.Top + y;
                Assert.False(covered[by * Size + bx]);
                covered[by * Size + bx] = true;
                var p = transform.SourceAt(bx, by, Size, Size);
                Assert.True(PhotoFuseRegistration.Inside(p.X, p.Y, Size, Size));
                if (weight == 255) continue;
                band++;
                double error = 0;
                for (int c = 0; c < 3; c++)
                    error = Math.Max(error, Math.Abs(PhotoFuseRegistration.Sample(donor, Size, p.X, p.Y, c) -
                        basis[(by * Size + bx) * 4 + c]));
                if (error <= 24) inliers++;
            }
            Assert.True(band > 0 && inliers >= band * 0.90);
        }
        for (int i = 0; i < covered.Length; i++)
            if (!covered[i]) Assert.Equal(basis.AsSpan(i * 4, 4).ToArray(), result.Pixels!.AsSpan(i * 4, 4).ToArray());
    }

    private static byte[] Pixel(byte[] pixels, int x, int y) => pixels.AsSpan((y * Size + x) * 4, 4).ToArray();

    private static void Change(byte[] pixels, int left, int top, int width, int height)
    {
        for (int y = top; y < top + height; y++)
        for (int x = left; x < left + width; x++)
        {
            int offset = (y * Size + x) * 4;
            pixels[offset] = (byte)Math.Min(255, pixels[offset] + 65);
            pixels[offset + 1] = (byte)Math.Max(0, pixels[offset + 1] - 55);
            pixels[offset + 2] = (byte)Math.Min(255, pixels[offset + 2] + 40);
        }
    }
}
