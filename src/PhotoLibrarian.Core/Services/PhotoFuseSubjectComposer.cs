// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.IO.Compression;

namespace PhotoLibrarian.Core.Services;

internal static class PhotoFuseSubjectComposer
{
    internal sealed record Patch(PhotoFuseRegion Region, PhotoFuseTransform Transform, byte[] Alpha);
    internal sealed record Preparation(Patch? Patch, string? Rejection);
    internal sealed record Rendering(byte[]? Pixels, PhotoFuseRegion[] Regions, string? Rejection);

    internal static Preparation BuildPatch(IReadOnlyList<byte[]> frames, int width, int height,
        PhotoFuseFaceSelection selection, PhotoFuseTransform? searchHint,
        IReadOnlyList<PhotoFuseFaceBounds> originalFaces, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (originalFaces.Any(face => !face.IsValid)) return new(null, "A protected face box is outside the photo.");
        if (selection.Frame < 1 || selection.Frame >= frames.Count)
            return new(null, "Choose a source photo for this face.");
        var face = selection.Target;
        if (!face.IsValid || !selection.Donor.IsValid) return new(null, "A face box is outside its photo.");
        int left = Math.Max(0, (int)Math.Floor((face.X - face.Width) * width));
        int top = Math.Max(0, (int)Math.Floor((face.Y - face.Height) * height));
        int right = Math.Min(width, (int)Math.Ceiling((face.X + face.Width * 2) * width));
        int bottom = Math.Min(height, (int)Math.Ceiling((face.Y + face.Height * 2) * height));
        var region = new PhotoFuseRegion(selection.Frame, left, top, right - left, bottom - top);
        string? rejection = null;
        // Blink/expression edits can be measured on facial detail. A changed pose instead uses
        // surrounding stationary detail; desired facial differences are not alignment failures.
        foreach (bool matchFace in new[] { true, false })
        {
            var alignment = PhotoFuseSubjectAlignment.Align(frames[selection.Frame], frames[0], width, height,
                selection, searchHint, originalFaces, matchFace, token);
            if (alignment.Transform is not { } transform) { rejection = alignment.Rejection; continue; }
            var seam = PhotoFuseSeamFinder.Find(frames[selection.Frame], frames[0], transform, width, height,
                region, selection, originalFaces, token);
            if (seam.Alpha is { } alpha) return new(new(region, transform, alpha), null);
            rejection = seam.Rejection;
        }
        return new(null, rejection);
    }

    internal static Rendering TryRender(IReadOnlyList<byte[]> frames, int width, int height,
        IReadOnlyList<PhotoFuseFaceSelection> selections, IReadOnlyList<PhotoFuseTransform?> searchHints,
        IReadOnlyList<PhotoFuseFaceBounds> originalFaces, CancellationToken token)
    {
        var output = (byte[])frames[0].Clone();
        var occupied = new byte[width * height];
        var regions = new List<PhotoFuseRegion>();
        long maskCharacters = 0;
        foreach (var selection in selections)
        {
            PhotoFuseTransform? hint = selection.Frame >= 0 && selection.Frame < searchHints.Count
                ? searchHints[selection.Frame] : null;
            var prepared = BuildPatch(frames, width, height, selection, hint, originalFaces, token);
            if (prepared.Patch is not { } patch) return new(null, [], prepared.Rejection);
            var region = patch.Region;
            for (int y = 0; y < region.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < region.Width; x++)
                {
                    byte alpha = patch.Alpha[y * region.Width + x];
                    if (alpha == 0) continue;
                    int bx = region.Left + x, by = region.Top + y, pixel = by * width + bx;
                    if (occupied[pixel] != 0)
                        return new(null, [], "These replacement seams overlap. Restore one face to its original.");
                    occupied[pixel] = 1;
                    var point = patch.Transform.SourceAt(bx, by, width, height);
                    for (int c = 0; c < 3; c++)
                    {
                        double donor = PhotoFuseRegistration.Sample(frames[selection.Frame], width, point.X, point.Y, c);
                        double weight = alpha / 255.0;
                        double value = PhotoFuseComposer.ToLinear(donor) * weight +
                            PhotoFuseComposer.ToLinear(output[pixel * 4 + c]) * (1 - weight);
                        output[pixel * 4 + c] = PhotoFuseComposer.FromLinear(value);
                    }
                }
            }
            using var compressed = new MemoryStream();
            using (var zipper = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                zipper.Write(patch.Alpha);
            maskCharacters += (compressed.Length + 2) / 3 * 4;
            if (maskCharacters > PhotoFuseSession.MaximumLineageBytes)
                return new(null, [], "The replacement masks exceed the portable metadata budget. Restore some faces to their originals.");
            regions.Add(region with { SubjectTransform = patch.Transform,
                CompressedMask = Convert.ToBase64String(compressed.ToArray()) });
        }
        return new(output, regions.ToArray(), null);
    }
}
