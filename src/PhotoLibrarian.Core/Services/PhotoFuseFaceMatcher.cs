// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Core.Services;

/// <summary>Spatial proposals, not person recognition. Every donor is registered independently.</summary>
public static class PhotoFuseFaceMatcher
{
    public static bool IsValid(FaceRegion face) =>
        double.IsFinite(face.X) && double.IsFinite(face.Y) &&
        double.IsFinite(face.Width) && double.IsFinite(face.Height) &&
        face.X >= 0 && face.Y >= 0 && face.Width > 0 && face.Height > 0 &&
        face.X + face.Width <= 1 && face.Y + face.Height <= 1;

    public static IReadOnlyList<FaceRegion> Nearby(FaceRegion target, IEnumerable<FaceRegion> donors,
        PhotoFuseSession pair, int frame = 1)
    {
        if (!IsValid(target)) throw new ArgumentException("The target face is outside the photo.");
        var expected = pair.DonorPointAt(frame, (target.X + target.Width / 2) * pair.Width,
            (target.Y + target.Height / 2) * pair.Height);
        double x = expected.X / pair.Width, y = expected.Y / pair.Height;
        return donors.Where(IsValid).Where(face =>
        {
            double areaRatio = face.Width * face.Height / (target.Width * target.Height);
            double dx = (face.X + face.Width / 2 - x) / target.Width;
            double dy = (face.Y + face.Height / 2 - y) / target.Height;
            return areaRatio is >= 0.5 and <= 2 && dx * dx + dy * dy <= 1.25 * 1.25;
        }).OrderBy(face => Math.Pow(face.X + face.Width / 2 - x, 2) +
            Math.Pow(face.Y + face.Height / 2 - y, 2)).ToArray();
    }

    public static PhotoFuseRegion Region(FaceRegion face, int frame, int width, int height)
    {
        if (!IsValid(face)) throw new ArgumentException("The target face is outside the photo.");
        // Include surrounding stationary pixels for a feathered seam rather than blending through eyes.
        int left = Math.Max(0, (int)Math.Floor((face.X - face.Width * 0.35) * width));
        int top = Math.Max(0, (int)Math.Floor((face.Y - face.Height * 0.35) * height));
        int right = Math.Min(width, (int)Math.Ceiling((face.X + face.Width * 1.35) * width));
        int bottom = Math.Min(height, (int)Math.Ceiling((face.Y + face.Height * 1.35) * height));
        return new PhotoFuseRegion(frame, left, top, right - left, bottom - top);
    }
}
