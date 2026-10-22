// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Diagnostics;

namespace PhotoLibrarian.Core.Services;

/// <summary>
/// Two-pass rigid video stabilization for NV12 frames.
/// <list type="number">
/// <item><see cref="Analyze"/> estimates the camera motion between consecutive frames.</item>
/// <item><see cref="PlanCorrections"/> smooths the whole camera path with a centered Gaussian
/// window (so intentional pans are followed without lag) and keeps every correction inside the
/// available crop budget.</item>
/// <item><see cref="Render"/> warps each frame by its planned correction.</item>
/// </list>
/// </summary>
internal sealed class VideoFrameStabilizer
{
    private const double CropRatio = 0.08;
    private const double MaxTranslationRatio = 0.08;
    private const double MaxAngle = 0.06;
    private const int SearchRadius = 20;
    private const int CoarseRadius = 16;
    /// <summary>Horizontal bands measured for rolling-shutter wobble.</summary>
    internal const int WobbleBands = 8;
    /// <summary>Largest per-band disagreement (alignment pixels) treated as shutter wobble.</summary>
    private const double MaxWobble = 8;
    /// <summary>Largest per-row wobble correction, as a fraction of the frame size.</summary>
    private const double MaxWobbleCorrectionRatio = 0.01;

    private readonly int _width, _height;
    private readonly bool _autoCrop;
    private readonly StabilizationOptions _options;
    private readonly int _cropX, _cropY;
    private readonly int _alignScale;
    private byte[]? _previous;
    private (int X, int Y)? _predicted;
    private AlignmentImage? _previousImage, _keyImage;
    private Motion _pose, _keyPose;
    private Motion? _lastMotion;

    /// <summary>Camera motion from a frame to its predecessor, in full-resolution pixels and radians.</summary>
    public readonly record struct Motion(double X, double Y, double Angle)
    {
        /// <summary>
        /// How much further the bottom row moved than the top row since the previous frame
        /// (full-resolution pixels): the change in rolling-shutter skew. Null when unmeasured.
        /// </summary>
        public (double X, double Y)? Shear { get; init; }
    }

    /// <summary>The rigid warp applied to one output frame.</summary>
    public readonly record struct Correction(double X, double Y, double Angle)
    {
        /// <summary>Extra source offset for each horizontal band (X values, then Y), or null.</summary>
        public double[]? Rows { get; init; }
    }

    private readonly record struct Track(double X, double Y, double Dx, double Dy);

    public VideoFrameStabilizer(int width, int height, bool autoCrop = false)
        : this(width, height, new StabilizationOptions(AutoCrop: autoCrop))
    {
    }

    public VideoFrameStabilizer(int width, int height, StabilizationOptions options)
    {
        _width = width;
        _height = height;
        _options = options;
        _autoCrop = options.AutoCrop && width >= 16 && height >= 16;
        _cropX = _autoCrop ? GetCropOffset(width) : 0;
        _cropY = _autoCrop ? GetCropOffset(height) : 0;
        _alignScale = Math.Max(1, (int)Math.Ceiling(Math.Max(width / 1280.0, height / 720.0)));
    }

    /// <summary>
    /// Estimates the motion from <paramref name="frame"/> to the previously analyzed frame.
    /// Returns zero motion for the first frame and null when the motion cannot be measured
    /// reliably (for example a scene cut, a flat frame or heavy blur).
    /// </summary>
    /// <remarks>
    /// Block matching finds the approximate motion (robust to large and fast moves), dense
    /// alignment refines it to a small fraction of a pixel, and each frame is also registered
    /// directly against a keyframe so measurement errors do not accumulate into slow drift.
    /// </remarks>
    public Motion? Analyze(byte[] frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateFrame(frame);
        var block = EstimateBlockMotion(frame, cancellationToken);
        var image = AlignmentImage.FromNv12(frame, _width, _height, _alignScale, cancellationToken);
        var previousImage = _previousImage;
        _previousImage = image;
        if (previousImage is null)
        {
            StartSegment(image);
            return new Motion(0, 0, 0);
        }

        var local = RefineMotion(image, previousImage, block, cancellationToken);
        if (local is not { } motion)
        {
            StartSegment(image);
            _lastMotion = null;
            return null;
        }
        _lastMotion = motion;

        var chained = Compose(_pose, motion);
        var keyAligned = RegisterToKeyframe(image, previousImage, chained, cancellationToken);
        var pose = keyAligned is { } k ? Compose(_keyPose, k) : chained;
        if (keyAligned is null) (_keyImage, _keyPose) = (image, pose);

        var result = Compose(Inverse(_pose), pose);
        _pose = pose;
        var shear = motion.Shear is { } s ? (s.X * _alignScale, s.Y * _alignScale) : ((double X, double Y)?)null;
        return new Motion(result.X * _alignScale, result.Y * _alignScale, result.Angle) { Shear = shear };
    }

    private void StartSegment(AlignmentImage image)
    {
        _pose = default;
        _keyImage = image;
        _keyPose = default;
    }

    /// <summary>Band center on a -1 (top) to 1 (bottom) scale.</summary>
    private static double BandPosition(int band) => (band + 0.5) * 2 / WobbleBands - 1;

    /// <summary>Precise current-to-previous motion in alignment-image pixels.</summary>
    private Motion? RefineMotion(AlignmentImage image, AlignmentImage previousImage, Motion? block,
        CancellationToken cancellationToken)
    {
        var scaledBlock = block is { } b ? new Motion(b.X / _alignScale, b.Y / _alignScale, b.Angle) : (Motion?)null;
        var guess = scaledBlock ?? _lastMotion ?? default;
        if (RigidAligner.Align(image, previousImage, guess, cancellationToken) is { } aligned &&
            aligned.InlierFraction >= 0.5)
            return aligned.Motion with { Shear = aligned.Shear };
        return scaledBlock;
    }

    /// <summary>
    /// Registers the frame directly against the keyframe and returns that keyframe-relative motion
    /// when it succeeds and agrees with the chained estimate; null means the current frame should
    /// become the new keyframe.
    /// </summary>
    private Motion? RegisterToKeyframe(AlignmentImage image, AlignmentImage previousImage, Motion chainedPose,
        CancellationToken cancellationToken)
    {
        if (_keyImage is null) return null;
        var guess = Compose(Inverse(_keyPose), chainedPose);
        // The frame was just aligned with its predecessor; repeating that would change nothing.
        if (ReferenceEquals(_keyImage, previousImage)) return guess;
        bool withinReach = Math.Abs(guess.X) < image.Width * 0.15 && Math.Abs(guess.Y) < image.Height * 0.15 &&
                           Math.Abs(guess.Angle) < 0.06;
        if (withinReach && RigidAligner.Align(image, _keyImage, guess, cancellationToken) is { } aligned &&
            aligned.InlierFraction >= 0.55 &&
            Math.Abs(aligned.Motion.X - guess.X) < 2 && Math.Abs(aligned.Motion.Y - guess.Y) < 2)
            return aligned.Motion;
        return null;
    }

    /// <summary>Applies <paramref name="second"/> and then <paramref name="first"/>.</summary>
    private static Motion Compose(Motion first, Motion second)
    {
        var (sin, cos) = Math.SinCos(first.Angle);
        return new Motion(first.X + cos * second.X - sin * second.Y,
            first.Y + sin * second.X + cos * second.Y, first.Angle + second.Angle);
    }

    private static Motion Inverse(Motion motion)
    {
        var (sin, cos) = Math.SinCos(-motion.Angle);
        return new Motion(-(cos * motion.X - sin * motion.Y), -(sin * motion.X + cos * motion.Y), -motion.Angle);
    }

    /// <summary>Approximate motion from patch block matching on a ~640-pixel-wide image.</summary>
    private Motion? EstimateBlockMotion(byte[] frame, CancellationToken cancellationToken)
    {
        int scale = Math.Max(1, (int)Math.Ceiling(Math.Max(_width / 640.0, _height / 360.0)));
        int smallWidth = _width / scale, smallHeight = _height / scale;
        var luma = Downsample(frame, _width, smallWidth, smallHeight, scale, cancellationToken);

        var previous = _previous;
        _previous = luma;
        if (previous is null)
        {
            _predicted = null;
            return new Motion(0, 0, 0);
        }

        // Search around the previous frame's motion first so steady pans stay inside the search
        // window, then around a coarse whole-frame estimate, then around zero motion.
        var centers = new List<(int X, int Y)>(3);
        if (_predicted is { } predicted) centers.Add(predicted);
        var coarse = EstimateCoarseShift(luma, previous, smallWidth, smallHeight);
        if (!centers.Contains(coarse)) centers.Add(coarse);
        if (!centers.Contains((0, 0))) centers.Add((0, 0));

        foreach (var center in centers)
        {
            var motion = EstimateMotion(luma, previous, smallWidth, smallHeight, center, cancellationToken);
            if (motion is not { } found) continue;
            _predicted = ((int)Math.Round(found.X), (int)Math.Round(found.Y));
            return new Motion(found.X * scale, found.Y * scale, found.Angle);
        }

        // Motion blur during quick camera moves smears fine texture so small patches stop matching.
        // At half the analysis resolution the blur spans fewer pixels and the patches cover more
        // scene, which usually recovers a reliable estimate instead of leaving a gap to guess.
        if (smallWidth >= 128 && smallHeight >= 128)
        {
            int halfWidth = smallWidth / 2, halfHeight = smallHeight / 2;
            var halfLuma = Downsample(luma, smallWidth, halfWidth, halfHeight, 2, cancellationToken);
            var halfPrevious = Downsample(previous, smallWidth, halfWidth, halfHeight, 2, cancellationToken);
            foreach (var center in centers)
            {
                var halfCenter = ((int)Math.Round(center.X / 2.0), (int)Math.Round(center.Y / 2.0));
                var motion = EstimateMotion(halfLuma, halfPrevious, halfWidth, halfHeight, halfCenter,
                    cancellationToken);
                if (motion is not { } found) continue;
                _predicted = ((int)Math.Round(found.X * 2), (int)Math.Round(found.Y * 2));
                return new Motion(found.X * 2 * scale, found.Y * 2 * scale, found.Angle);
            }
        }
        _predicted = null;
        return null;
    }

    /// <summary>
    /// Plans one correction per analyzed frame. Short tracking dropouts are bridged; longer ones
    /// start a new path segment so a scene cut never drags the previous shot's correction along.
    /// </summary>
    public Correction[] PlanCorrections(IReadOnlyList<Motion?> motions, double framesPerSecond)
    {
        int count = motions.Count;
        var corrections = new Correction[count];
        if (count == 0) return corrections;

        double fps = double.IsFinite(framesPerSecond) && framesPerSecond > 0 ? framesPerSecond : 30;
        var filled = BridgeShortGaps(motions, Math.Max(2, (int)Math.Round(fps * 0.2)));
        double smoothingSeconds = _options.LockFrame ? 0.5 : _options.SmoothingSeconds;
        double sigma = Math.Clamp(fps * smoothingSeconds, 2, 120);

        int start = 0;
        for (int i = 1; i <= count; i++)
        {
            if (i < count && filled[i] is not null) continue;
            PlanSegment(filled, start, i, sigma, corrections);
            start = i;
        }

        var rowShifts = PlanRowShifts(motions);
        for (int i = 0; i < count; i++)
            corrections[i] = corrections[i] with { Rows = rowShifts[i] };
        return corrections;
    }

    private double WobbleSlackX => _width * MaxWobbleCorrectionRatio;
    private double WobbleSlackY => _height * MaxWobbleCorrectionRatio;

    /// <summary>
    /// Plans per-band source offsets that undo rolling-shutter distortion. Each sensor row is read
    /// slightly later than the one above, so a row at height s (-0.5 top .. 0.5 bottom) shows the
    /// camera at time t + s·r, where r is the readout time as a fraction of the frame interval.
    /// The precisely measured camera path then predicts every row's displacement, so only r is
    /// estimated from the per-frame skew the aligner measures (one robust fit for the clip).
    /// </summary>
    private double[]?[] PlanRowShifts(IReadOnlyList<Motion?> motions)
    {
        int count = motions.Count;
        var shifts = new double[]?[count];
        var velocity = new (double X, double Y)?[count];
        var acceleration = new (double X, double Y)[count];
        for (int i = 0; i < count; i++)
        {
            var before = i > 0 ? motions[i] : null;
            var after = i + 1 < count ? motions[i + 1] : null;
            if (before is { } b && after is { } a)
            {
                velocity[i] = ((b.X + a.X) / 2, (b.Y + a.Y) / 2);
                acceleration[i] = (a.X - b.X, a.Y - b.Y);
            }
            else if ((before ?? after) is { } only)
            {
                velocity[i] = (only.X, only.Y);
            }
        }

        double readout = EstimateReadout(motions, velocity);
        DebugLog.WriteLine($"Stabilize: rolling-shutter readout {readout:F2} frame");
        var residualX = ResidualSkew(motions, velocity, readout, shear => shear.X, v => v.X);
        var residualY = ResidualSkew(motions, velocity, readout, shear => shear.Y, v => v.Y);
        for (int i = 0; i < count; i++)
        {
            if (velocity[i] is not { } v) continue;
            var rows = new double[WobbleBands * 2];
            bool any = false;
            for (int band = 0; band < WobbleBands; band++)
            {
                double position = BandPosition(band) / 2;
                double delay = position * readout;
                double shiftX = -(delay * v.X + delay * delay / 2 * acceleration[i].X + position * residualX[i]);
                double shiftY = -(delay * v.Y + delay * delay / 2 * acceleration[i].Y + position * residualY[i]);
                rows[band] = Math.Clamp(shiftX, -WobbleSlackX, WobbleSlackX);
                rows[WobbleBands + band] = Math.Clamp(shiftY, -WobbleSlackY, WobbleSlackY);
                any |= Math.Abs(shiftX) >= 0.01 || Math.Abs(shiftY) >= 0.01;
            }
            if (any) shifts[i] = rows;
        }
        return shifts;
    }

    /// <summary>
    /// Least-squares readout time (fraction of a frame interval, 0 to 1). Between consecutive
    /// frames the bottom row moves r·(change in velocity) further than the top row. A second pass
    /// drops frames whose skew disagrees strongly (moving subjects, failed matches).
    /// </summary>
    private static double EstimateReadout(IReadOnlyList<Motion?> motions, (double X, double Y)?[] velocity)
    {
        var pairs = new List<(double Change, double Shear)>();
        for (int i = 1; i < motions.Count; i++)
        {
            if (motions[i]?.Shear is not { } shear || velocity[i] is not { } v || velocity[i - 1] is not { } p)
                continue;
            pairs.Add((v.X - p.X, shear.X));
            pairs.Add((v.Y - p.Y, shear.Y));
        }
        double readout = FitReadout(pairs, double.PositiveInfinity);
        var deviations = pairs.Select(pair => Math.Abs(pair.Shear - readout * pair.Change)).Order().ToList();
        if (deviations.Count == 0) return 0;
        double limit = Math.Max(0.05, 3 * 1.4826 * deviations[deviations.Count / 2]);
        return Math.Clamp(FitReadout(pairs, limit, readout), 0, 1);
    }

    /// <summary>
    /// Per-frame skew the readout model misses (vibration faster than the frame rate, readout
    /// misestimate). The measured frame-to-frame skew minus the model's prediction is summed into
    /// each frame's skew, then only its fast part is kept so slow measurement drift is ignored.
    /// Outliers (moving subjects, failed matches) are dropped and gaps restart the sum.
    /// </summary>
    private static double[] ResidualSkew(IReadOnlyList<Motion?> motions, (double X, double Y)?[] velocity,
        double readout, Func<(double X, double Y), double> shearAxis, Func<(double X, double Y), double> velocityAxis)
    {
        int count = motions.Count;
        var residuals = new double?[count];
        var magnitudes = new List<double>();
        for (int i = 1; i < count; i++)
        {
            if (motions[i]?.Shear is not { } shear || velocity[i] is not { } v || velocity[i - 1] is not { } p)
                continue;
            double residual = shearAxis(shear) - readout * (velocityAxis(v) - velocityAxis(p));
            residuals[i] = residual;
            magnitudes.Add(Math.Abs(residual));
        }

        var skew = new double[count];
        if (magnitudes.Count == 0) return skew;
        magnitudes.Sort();
        double limit = Math.Max(0.05, 3 * 1.4826 * magnitudes[magnitudes.Count / 2]);
        for (int i = 1; i < count; i++)
        {
            if (motions[i] is null) continue;
            double step = residuals[i] is { } r && Math.Abs(r) <= limit ? r : 0;
            skew[i] = skew[i - 1] + step;
        }

        var slow = GaussianSmooth(skew, ResidualSkewSigma);
        for (int i = 0; i < count; i++) skew[i] -= slow[i];
        return skew;
    }

    /// <summary>Frames over which residual skew is treated as slow (and left alone).</summary>
    private const double ResidualSkewSigma = 6;

    private static double FitReadout(List<(double Change, double Shear)> pairs, double limit, double readout = 0)
    {
        double numerator = 0, denominator = 0;
        foreach (var (change, shear) in pairs)
        {
            if (Math.Abs(shear - readout * change) > limit) continue;
            numerator += shear * change;
            denominator += change * change;
        }
        return denominator > 0 ? numerator / denominator : 0;
    }

    /// <summary>Interpolated (or extrapolated) wobble offset for a full-resolution source row.</summary>
    private static (double X, double Y) RowShift(double[]? rows, double row, int height)
    {
        if (rows is null) return (0, 0);
        double band = Math.Clamp(row * WobbleBands / height - 0.5, -0.5, WobbleBands - 0.5);
        int lower = Math.Clamp((int)Math.Floor(band), 0, WobbleBands - 2);
        double t = band - lower;
        return (rows[lower] + (rows[lower + 1] - rows[lower]) * t,
            rows[WobbleBands + lower] + (rows[WobbleBands + lower + 1] - rows[WobbleBands + lower]) * t);
    }

    /// <summary>Warps a frame by a planned correction (and auto-crops when enabled).</summary>
    public byte[] Render(byte[] frame, Correction correction, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateFrame(frame);
        if (!_autoCrop && correction == default) return frame;

        var (sin, cos) = Math.SinCos(correction.Angle);
        var output = new byte[frame.Length];
        // Warp and crop/rescale are composed into one sampling pass to avoid double interpolation.
        // Luma uses Lanczos-3: simpler kernels blur by a different amount at every sub-pixel
        // offset, which makes fine detail visibly pulse from frame to frame.
        WarpLuma(frame, output, _width, _height, cos, sin, correction.X, correction.Y,
            _cropX, _cropY, correction.Rows, cancellationToken);
        WarpPlane(frame, output, _width / 2, _height / 2, _width * _height, 2,
            cos, sin, correction.X / 2, correction.Y / 2, _cropX / 2, _cropY / 2, correction.Rows, cancellationToken);
        return output;
    }

    /// <summary>Runs all three stages over in-memory frames (used for small clips and tests).</summary>
    internal static List<byte[]> StabilizeAll(IReadOnlyList<byte[]> frames, int width, int height,
        bool autoCrop, double framesPerSecond, CancellationToken cancellationToken) =>
        StabilizeAll(frames, width, height, new StabilizationOptions(AutoCrop: autoCrop), framesPerSecond,
            cancellationToken);

    internal static List<byte[]> StabilizeAll(IReadOnlyList<byte[]> frames, int width, int height,
        StabilizationOptions options, double framesPerSecond, CancellationToken cancellationToken)
    {
        var stabilizer = new VideoFrameStabilizer(width, height, options);
        var motions = new List<Motion?>(frames.Count);
        foreach (var frame in frames)
            motions.Add(stabilizer.Analyze(frame, cancellationToken));
        var corrections = stabilizer.PlanCorrections(motions, framesPerSecond);
        var output = new List<byte[]>(frames.Count);
        for (int i = 0; i < frames.Count; i++)
            output.Add(stabilizer.Render(frames[i], corrections[i], cancellationToken));
        return output;
    }

    private void ValidateFrame(byte[] frame)
    {
        if (_width < 2 || _height < 2 || (_width & 1) != 0 || (_height & 1) != 0 ||
            frame.Length != checked(_width * _height * 3 / 2))
            throw new ArgumentException("The decoder must supply an even-sized NV12 frame.");
    }

    private static ParallelOptions Options(CancellationToken cancellationToken) =>
        new() { CancellationToken = cancellationToken };

    private static byte[] Downsample(byte[] frame, int width, int smallWidth, int smallHeight, int scale,
        CancellationToken cancellationToken)
    {
        var luma = new byte[smallWidth * smallHeight];
        Parallel.For(0, smallHeight, Options(cancellationToken), y =>
        {
            for (int x = 0; x < smallWidth; x++)
            {
                int sum = 0;
                for (int row = 0; row < scale; row++)
                    for (int column = 0; column < scale; column++)
                        sum += frame[(y * scale + row) * width + x * scale + column];
                luma[y * smallWidth + x] = (byte)(sum / (scale * scale));
            }
        });
        return luma;
    }

    #region Motion estimation

    /// <summary>
    /// Whole-frame translation search on a 4x-reduced image. Covers large pans that exceed the
    /// fine search window; the result is only used as a search center and is validated later.
    /// </summary>
    private static (int X, int Y) EstimateCoarseShift(byte[] current, byte[] previous, int width, int height)
    {
        const int factor = 4;
        int coarseWidth = width / factor, coarseHeight = height / factor;
        int margin = CoarseRadius + 1;
        if (coarseWidth <= 2 * margin + 4 || coarseHeight <= 2 * margin + 4) return (0, 0);

        var currentCoarse = Downsample(current, width, coarseWidth, coarseHeight, factor, CancellationToken.None);
        var previousCoarse = Downsample(previous, width, coarseWidth, coarseHeight, factor, CancellationToken.None);
        int side = 2 * CoarseRadius + 1;
        var errors = new long[side * side];
        Parallel.For(0, errors.Length, index =>
        {
            int dx = index % side - CoarseRadius, dy = index / side - CoarseRadius;
            long error = 0;
            for (int y = margin; y < coarseHeight - margin; y += 2)
            for (int x = margin; x < coarseWidth - margin; x += 2)
                error += Math.Abs(currentCoarse[y * coarseWidth + x] -
                                  previousCoarse[(y + dy) * coarseWidth + x + dx]);
            errors[index] = error;
        });
        int best = 0;
        for (int i = 1; i < errors.Length; i++)
            if (errors[i] < errors[best]) best = i;
        return ((best % side - CoarseRadius) * factor, (best / side - CoarseRadius) * factor);
    }

    private static Motion? EstimateMotion(byte[] current, byte[] previous, int width, int height,
        (int X, int Y) center, CancellationToken cancellationToken)
    {
        const int patch = 6;
        int radius = Math.Min(SearchRadius, Math.Min(width, height) / 8);
        // Every sampled pixel, including the +-1 refinement steps, must stay inside both frames.
        int reach = radius + patch + 2;
        int marginLeft = Math.Max(patch + 1, reach - center.X), marginRight = Math.Max(patch + 1, reach + center.X);
        int marginTop = Math.Max(patch + 1, reach - center.Y), marginBottom = Math.Max(patch + 1, reach + center.Y);
        int spanX = width - marginLeft - marginRight, spanY = height - marginTop - marginBottom;
        if (spanX < width / 4 || spanY < height / 4) return null;

        var found = new Track?[40];
        Parallel.For(0, found.Length, Options(cancellationToken), cell =>
        {
            int gx = cell % 8, gy = cell / 8;
            int x = marginLeft + spanX * (2 * gx + 1) / 16;
            int y = marginTop + spanY * (2 * gy + 1) / 10;
            int texture = 0;
            for (int py = -patch; py <= patch; py += 2)
            for (int px = -patch; px <= patch; px += 2)
                texture += Math.Abs(current[(y + py) * width + x + px + 1] -
                                    current[(y + py) * width + x + px - 1]);
            if (texture < 120) return;

            int bestX = center.X, bestY = center.Y;
            double best = double.MaxValue;
            for (int dy = center.Y - radius; dy <= center.Y + radius; dy += 2)
            for (int dx = center.X - radius; dx <= center.X + radius; dx += 2)
            {
                double error = PatchError(current, previous, width, x, y, dx, dy, patch);
                if (error < best)
                {
                    best = error;
                    bestX = dx;
                    bestY = dy;
                }
            }
            int coarseX = bestX, coarseY = bestY;
            for (int dy = coarseY - 1; dy <= coarseY + 1; dy++)
            for (int dx = coarseX - 1; dx <= coarseX + 1; dx++)
            {
                double error = PatchError(current, previous, width, x, y, dx, dy, patch);
                if (error < best)
                {
                    best = error;
                    bestX = dx;
                    bestY = dy;
                }
            }
            if (best > 25 || Math.Abs(bestX - center.X) >= radius || Math.Abs(bestY - center.Y) >= radius)
                return;
            double subX = Refine(best, PatchError(current, previous, width, x, y, bestX - 1, bestY, patch),
                PatchError(current, previous, width, x, y, bestX + 1, bestY, patch));
            double subY = Refine(best, PatchError(current, previous, width, x, y, bestX, bestY - 1, patch),
                PatchError(current, previous, width, x, y, bestX, bestY + 1, patch));
            found[cell] = new Track(x - (width - 1) / 2.0, y - (height - 1) / 2.0, bestX + subX, bestY + subY);
        });
        var tracks = found.Where(track => track.HasValue).Select(track => track!.Value).ToList();
        if (tracks.Count < 6) return null;

        // Robust consensus rejects independently moving subjects and ambiguous low-texture matches.
        Motion bestMotion = default;
        int bestCount = 0;
        foreach (var a in tracks)
        foreach (var b in tracks)
        {
            double x = b.X - a.X, y = b.Y - a.Y;
            double distance = x * x + y * y;
            if (distance < 400) continue;
            double angle = (x * (b.Dy - a.Dy) - y * (b.Dx - a.Dx)) / distance;
            if (Math.Abs(angle) > 0.08) continue;
            var model = new Motion(a.Dx + angle * a.Y, a.Dy - angle * a.X, angle);
            int count = tracks.Count(track => Residual(track, model) < 1.5);
            if (count > bestCount)
            {
                bestCount = count;
                bestMotion = model;
            }
        }
        if (bestCount < Math.Max(6, tracks.Count / 2)) return null;
        var inliers = tracks.Where(track => Residual(track, bestMotion) < 1.5).ToList();
        double meanX = inliers.Average(t => t.X), meanY = inliers.Average(t => t.Y);
        double meanDx = inliers.Average(t => t.Dx), meanDy = inliers.Average(t => t.Dy);
        double numerator = 0, denominator = 0;
        foreach (var track in inliers)
        {
            double x = track.X - meanX, y = track.Y - meanY;
            numerator += x * (track.Dy - meanDy) - y * (track.Dx - meanDx);
            denominator += x * x + y * y;
        }
        double fittedAngle = denominator > 0 ? numerator / denominator : 0;
        return new Motion(meanDx + fittedAngle * meanY, meanDy - fittedAngle * meanX, fittedAngle);
    }

    private static double Residual(Track track, Motion model) =>
        Math.Sqrt(Math.Pow(track.Dx - model.X + model.Angle * track.Y, 2) +
                  Math.Pow(track.Dy - model.Y - model.Angle * track.X, 2));

    private static double Refine(double center, double left, double right)
    {
        double curvature = left + right - 2 * center;
        return curvature > 0 ? Math.Clamp((left - right) / (2 * curvature), -0.5, 0.5) : 0;
    }

    private static double PatchError(byte[] current, byte[] previous, int width,
        int x, int y, int dx, int dy, int patch)
    {
        int error = 0, count = 0;
        for (int py = -patch; py <= patch; py += 2)
        for (int px = -patch; px <= patch; px += 2)
        {
            error += Math.Abs(current[(y + py) * width + x + px] -
                              previous[(y + py + dy) * width + x + px + dx]);
            count++;
        }
        return error / (double)count;
    }

    #endregion

    #region Path planning

    /// <summary>Linearly interpolates motion across dropouts of at most <paramref name="maxGap"/> frames.</summary>
    private static Motion?[] BridgeShortGaps(IReadOnlyList<Motion?> motions, int maxGap)
    {
        var filled = motions.ToArray();
        int i = 1;
        while (i < filled.Length)
        {
            if (filled[i] is not null)
            {
                i++;
                continue;
            }
            int end = i;
            while (end < filled.Length && filled[end] is null) end++;
            var before = filled[i - 1];
            var after = end < filled.Length ? filled[end] : null;
            if (end - i <= maxGap && (before is not null || after is not null))
            {
                var from = before ?? after!.Value;
                var to = after ?? before!.Value;
                for (int k = i; k < end; k++)
                {
                    double t = (k - i + 1) / (double)(end - i + 1);
                    filled[k] = new Motion(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t,
                        from.Angle + (to.Angle - from.Angle) * t);
                }
            }
            i = end;
        }
        return filled;
    }

    /// <summary>Plans frames [start, end), whose first frame has no usable link to its predecessor.</summary>
    private void PlanSegment(Motion?[] motions, int start, int end, double sigma, Correction[] corrections)
    {
        int length = end - start;
        var pathX = new double[length];
        var pathY = new double[length];
        var pathAngle = new double[length];
        bool correctRotation = _options.CorrectRotation;
        for (int i = 1; i < length; i++)
        {
            var motion = motions[start + i]!.Value;
            // Compose current-to-previous motion with the accumulated camera pose.
            var (sin, cos) = Math.SinCos(pathAngle[i - 1]);
            pathX[i] = pathX[i - 1] + cos * motion.X - sin * motion.Y;
            pathY[i] = pathY[i - 1] + sin * motion.X + cos * motion.Y;
            pathAngle[i] = pathAngle[i - 1] + (correctRotation ? motion.Angle : 0);
        }

        double[] smoothX, smoothY, smoothAngle;
        if (_options.LockFrame)
        {
            // Hold the view perfectly still while the camera stays inside a band within the
            // correction margin; when it drifts further, move at a constant speed only as much
            // as needed, with gently rounded starts and stops.
            double transition = Math.Max(2, sigma / 2);
            smoothX = GaussianSmooth(TautString(pathX, LockBand(_width, _cropX)), transition);
            smoothY = GaussianSmooth(TautString(pathY, LockBand(_height, _cropY)), transition);
            smoothAngle = GaussianSmooth(TautString(pathAngle, MaxAngle * 0.3), transition);
        }
        else
        {
            smoothX = GaussianSmooth(pathX, sigma);
            smoothY = GaussianSmooth(pathY, sigma);
            smoothAngle = GaussianSmooth(pathAngle, sigma);
        }

        // Pull the smooth path toward the raw path wherever the correction would exceed the crop
        // budget, then re-smooth so the limited path stays smooth instead of clipping abruptly.
        for (int iteration = 0; iteration < 8; iteration++)
        {
            bool limited = false;
            for (int i = 0; i < length; i++)
            {
                double factor = LimitFactor(GetCorrection(pathX[i], pathY[i], pathAngle[i],
                    smoothX[i], smoothY[i], smoothAngle[i]));
                if (factor >= 1) continue;
                limited = true;
                smoothX[i] = pathX[i] + (smoothX[i] - pathX[i]) * factor;
                smoothY[i] = pathY[i] + (smoothY[i] - pathY[i]) * factor;
                smoothAngle[i] = pathAngle[i] + (smoothAngle[i] - pathAngle[i]) * factor;
            }
            if (!limited) break;
            smoothX = GaussianSmooth(smoothX, sigma / 3);
            smoothY = GaussianSmooth(smoothY, sigma / 3);
            smoothAngle = GaussianSmooth(smoothAngle, sigma / 3);
        }

        for (int i = 0; i < length; i++)
        {
            var correction = GetCorrection(pathX[i], pathY[i], pathAngle[i], smoothX[i], smoothY[i], smoothAngle[i]);
            double factor = LimitFactor(correction);
            corrections[start + i] = new Correction(correction.X * factor, correction.Y * factor,
                correction.Angle * factor);
        }
    }

    /// <summary>How far the camera may drift from a locked anchor before the anchor follows.</summary>
    private double LockBand(int length, int cropOffset)
    {
        double margin = _autoCrop ? cropOffset : length * MaxTranslationRatio;
        // Leave part of the margin for rotation and for the smoothed transitions.
        return margin * 0.6;
    }

    /// <summary>
    /// The shortest ("taut string") path that stays within ±<paramref name="band"/> of
    /// <paramref name="path"/>: perfectly constant wherever the camera stays within the band,
    /// otherwise straight constant-velocity segments that bend only where they must.
    /// </summary>
    internal static double[] TautString(double[] path, double band)
    {
        int length = path.Length;
        var result = new double[length];
        if (length == 0) return result;

        // Start flat for as long as possible; the first bend touches the band edge exactly.
        double low = path[0] - band, high = path[0] + band;
        int collapse = 1;
        for (; collapse < length; collapse++)
        {
            double nextLow = Math.Max(low, path[collapse] - band), nextHigh = Math.Min(high, path[collapse] + band);
            if (nextLow > nextHigh) break;
            (low, high) = (nextLow, nextHigh);
        }
        if (collapse == length)
        {
            Array.Fill(result, (low + high) / 2);
            return result;
        }

        int origin = 0;
        double value = path[collapse] - band > high ? high : low;
        while (origin < length - 1)
        {
            double minSlope = double.NegativeInfinity, maxSlope = double.PositiveInfinity;
            int minAt = origin, maxAt = origin, bendAt = -1;
            double bendSlope = 0, bendValue = 0;
            for (int t = origin + 1; t < length; t++)
            {
                double lowSlope = (path[t] - band - value) / (t - origin);
                double highSlope = (path[t] + band - value) / (t - origin);
                if (lowSlope > maxSlope)
                {
                    (bendAt, bendSlope, bendValue) = (maxAt, maxSlope, path[maxAt] + band);
                    break;
                }
                if (highSlope < minSlope)
                {
                    (bendAt, bendSlope, bendValue) = (minAt, minSlope, path[minAt] - band);
                    break;
                }
                if (lowSlope >= minSlope) (minSlope, minAt) = (lowSlope, t);
                if (highSlope <= maxSlope) (maxSlope, maxAt) = (highSlope, t);
            }
            if (bendAt < 0)
            {
                // The string is free at the end of the clip: stay as flat as the band allows.
                double slope = Math.Clamp(0, minSlope, maxSlope);
                for (int t = origin; t < length; t++) result[t] = value + slope * (t - origin);
                break;
            }
            for (int t = origin; t < bendAt; t++) result[t] = value + bendSlope * (t - origin);
            (origin, value) = (bendAt, bendValue);
        }
        result[length - 1] = origin == length - 1 ? value : result[length - 1];
        return result;
    }

    private static Correction GetCorrection(double pathX, double pathY, double pathAngle,
        double smoothX, double smoothY, double smoothAngle)
    {
        var (sin, cos) = Math.SinCos(pathAngle);
        double deltaX = smoothX - pathX, deltaY = smoothY - pathY;
        return new Correction(cos * deltaX + sin * deltaY, -sin * deltaX + cos * deltaY, smoothAngle - pathAngle);
    }

    /// <summary>The largest uniform scale (at most 1) that keeps a correction within the budget.</summary>
    private double LimitFactor(Correction correction)
    {
        double factor = 1;
        if (Math.Abs(correction.X) > _width * MaxTranslationRatio)
            factor = Math.Min(factor, _width * MaxTranslationRatio / Math.Abs(correction.X));
        if (Math.Abs(correction.Y) > _height * MaxTranslationRatio)
            factor = Math.Min(factor, _height * MaxTranslationRatio / Math.Abs(correction.Y));
        if (Math.Abs(correction.Angle) > MaxAngle)
            factor = Math.Min(factor, MaxAngle / Math.Abs(correction.Angle));
        // Keep part of the crop margin free for the per-row wobble correction.
        if (_autoCrop)
            factor = Math.Min(factor, CropLimitFactor(_width, _height,
                Math.Max(0, _cropX - (int)Math.Ceiling(WobbleSlackX)), Math.Max(0, _cropY - (int)Math.Ceiling(WobbleSlackY)),
                correction.X * factor, correction.Y * factor, correction.Angle * factor) * factor);
        return factor;
    }

    /// <summary>
    /// Centered Gaussian smoothing. Ends are extended by point reflection about a local linear
    /// fit, which preserves pans at the clip edges while still removing jitter there.
    /// </summary>
    internal static double[] GaussianSmooth(double[] values, double sigma)
    {
        int length = values.Length;
        if (length < 3 || sigma <= 0) return (double[])values.Clone();
        int radius = Math.Min(length - 1, (int)Math.Ceiling(sigma * 3));
        var weights = new double[radius + 1];
        for (int k = 0; k <= radius; k++) weights[k] = Math.Exp(-0.5 * k * k / (sigma * sigma));

        int fitLength = Math.Clamp((int)Math.Ceiling(sigma * 2), 2, length);
        double startAnchor = FitLineAt(values, 0, fitLength, 0);
        double endAnchor = FitLineAt(values, length - fitLength, fitLength, length - 1);

        double Sample(int index)
        {
            if (index < 0) return 2 * startAnchor - values[-index];
            if (index >= length) return 2 * endAnchor - values[2 * (length - 1) - index];
            return values[index];
        }

        var result = new double[length];
        for (int i = 0; i < length; i++)
        {
            double sum = values[i] * weights[0], total = weights[0];
            for (int k = 1; k <= radius; k++)
            {
                sum += (Sample(i - k) + Sample(i + k)) * weights[k];
                total += 2 * weights[k];
            }
            result[i] = sum / total;
        }
        return result;
    }

    /// <summary>Least-squares line through values[start..start+count), evaluated at <paramref name="at"/>.</summary>
    private static double FitLineAt(double[] values, int start, int count, int at)
    {
        double meanT = start + (count - 1) / 2.0, meanV = 0;
        for (int i = 0; i < count; i++) meanV += values[start + i];
        meanV /= count;
        double numerator = 0, denominator = 0;
        for (int i = 0; i < count; i++)
        {
            double t = start + i - meanT;
            numerator += t * (values[start + i] - meanV);
            denominator += t * t;
        }
        double slope = denominator > 0 ? numerator / denominator : 0;
        return meanV + slope * (at - meanT);
    }

    #endregion

    #region Crop limits and warping

    internal static int GetCropOffset(int length)
    {
        int maximum = ((length - 2) / 2) & ~1;
        int requested = ((int)Math.Ceiling(length * CropRatio) + 1) & ~1;
        return Math.Min(maximum, requested);
    }

    internal static (double X, double Y, double Angle) LimitCorrectionToCrop(
        int width, int height, int cropX, int cropY, double offsetX, double offsetY, double angle)
    {
        double factor = CropLimitFactor(width, height, cropX, cropY, offsetX, offsetY, angle);
        return (offsetX * factor, offsetY * factor, angle * factor);
    }

    private static double CropLimitFactor(
        int width, int height, int cropX, int cropY, double offsetX, double offsetY, double angle)
    {
        if (IsCropValid(width, height, cropX, cropY, offsetX, offsetY, angle)) return 1;

        double low = 0, high = 1;
        for (int i = 0; i < 20; i++)
        {
            double factor = (low + high) / 2;
            if (IsCropValid(width, height, cropX, cropY, offsetX * factor, offsetY * factor, angle * factor))
                low = factor;
            else
                high = factor;
        }
        return low;
    }

    internal static bool IsCropValid(
        int width, int height, int cropX, int cropY, double offsetX, double offsetY, double angle)
    {
        double centerX = (width - 1) / 2.0, centerY = (height - 1) / 2.0;
        double halfWidth = centerX - cropX, halfHeight = centerY - cropY;
        var (sin, cos) = Math.SinCos(angle);
        for (int xIndex = 0; xIndex < 2; xIndex++)
        for (int yIndex = 0; yIndex < 2; yIndex++)
        {
            double x = xIndex == 0 ? -halfWidth : halfWidth;
            double y = yIndex == 0 ? -halfHeight : halfHeight;
            double sourceX = centerX + cos * x - sin * y + offsetX;
            double sourceY = centerY + sin * x + cos * y + offsetY;
            if (sourceX < 1 || sourceX > width - 2 || sourceY < 1 || sourceY > height - 2)
                return false;
        }
        return true;
    }

    private static void WarpPlane(byte[] input, byte[] output, int width, int height, int start,
        int components, double cos, double sin, double dx, double dy, int cropX, int cropY, double[]? rowShifts,
        CancellationToken cancellationToken)
    {
        double centerX = (width - 1) / 2.0, centerY = (height - 1) / 2.0;
        double scaleX = (width - 2.0 * cropX) / width, scaleY = (height - 2.0 * cropY) / height;
        double firstX = cropX + 0.5 * scaleX - 0.5 - centerX;
        double stepX = cos * scaleX, stepY = sin * scaleX;
        int rowLength = width * components;
        Parallel.For(0, height, Options(cancellationToken), y =>
        {
            double v = cropY + (y + 0.5) * scaleY - 0.5 - centerY;
            // Chroma is subsampled 2x, so look the shift up by luma row and halve it.
            var (shiftX, shiftY) = RowShift(rowShifts, (centerY + v + dy) * 2, height * 2);
            double sourceX = centerX + cos * firstX - sin * v + dx + shiftX / 2;
            double sourceY = centerY + sin * firstX + cos * v + dy + shiftY / 2;
            int destination = start + y * rowLength;
            for (int x = 0; x < width; x++, sourceX += stepX, sourceY += stepY, destination += components)
            {
                int ix = (int)Math.Floor(sourceX), iy = (int)Math.Floor(sourceY);
                double fx = sourceX - ix, fy = sourceY - iy;
                int x0, x1, row0, row1;
                if ((uint)ix < (uint)(width - 1) && (uint)iy < (uint)(height - 1))
                {
                    x0 = ix * components;
                    x1 = x0 + components;
                    row0 = start + iy * rowLength;
                    row1 = row0 + rowLength;
                }
                else
                {
                    x0 = Mirror(ix, width) * components;
                    x1 = Mirror(ix + 1, width) * components;
                    row0 = start + Mirror(iy, height) * rowLength;
                    row1 = start + Mirror(iy + 1, height) * rowLength;
                }
                for (int c = 0; c < components; c++)
                {
                    double top = input[row0 + x0 + c] + (input[row0 + x1 + c] - input[row0 + x0 + c]) * fx;
                    double bottom = input[row1 + x0 + c] + (input[row1 + x1 + c] - input[row1 + x0 + c]) * fx;
                    output[destination + c] = (byte)Math.Clamp((int)(top + (bottom - top) * fy + 0.5), 0, 255);
                }
            }
        });
    }

    /// <summary>Lanczos-3 warp of the luma plane; same geometry as <see cref="WarpPlane"/>.</summary>
    private static void WarpLuma(byte[] input, byte[] output, int width, int height,
        double cos, double sin, double dx, double dy, int cropX, int cropY, double[]? rowShifts,
        CancellationToken cancellationToken)
    {
        double centerX = (width - 1) / 2.0, centerY = (height - 1) / 2.0;
        double scaleX = (width - 2.0 * cropX) / width, scaleY = (height - 2.0 * cropY) / height;
        double firstX = cropX + 0.5 * scaleX - 0.5 - centerX;
        double stepX = cos * scaleX, stepY = sin * scaleX;
        Parallel.For(0, height, Options(cancellationToken), y =>
        {
            Span<int> columns = stackalloc int[LumaTaps];
            Span<int> rows = stackalloc int[LumaTaps];
            Span<double> weightsX = stackalloc double[LumaTaps];
            Span<double> weightsY = stackalloc double[LumaTaps];
            double v = cropY + (y + 0.5) * scaleY - 0.5 - centerY;
            var (shiftX, shiftY) = RowShift(rowShifts, centerY + v + dy, height);
            double sourceX = centerX + cos * firstX - sin * v + dx + shiftX;
            double sourceY = centerY + sin * firstX + cos * v + dy + shiftY;
            int destination = y * width;
            for (int x = 0; x < width; x++, sourceX += stepX, sourceY += stepY, destination++)
            {
                int ix = (int)Math.Floor(sourceX), iy = (int)Math.Floor(sourceY);
                LanczosWeights(sourceX - ix, weightsX);
                LanczosWeights(sourceY - iy, weightsY);
                int firstColumn = ix - LumaTaps / 2 + 1, firstRow = iy - LumaTaps / 2 + 1;
                bool inside = firstColumn >= 0 && firstColumn + LumaTaps <= width &&
                              firstRow >= 0 && firstRow + LumaTaps <= height;
                for (int k = 0; k < LumaTaps; k++)
                {
                    columns[k] = inside ? firstColumn + k : Mirror(firstColumn + k, width);
                    rows[k] = (inside ? firstRow + k : Mirror(firstRow + k, height)) * width;
                }
                double sum = 0;
                for (int row = 0; row < LumaTaps; row++)
                {
                    int offset = rows[row];
                    double line = 0;
                    for (int k = 0; k < LumaTaps; k++) line += input[offset + columns[k]] * weightsX[k];
                    sum += line * weightsY[row];
                }
                output[destination] = (byte)Math.Clamp((int)(sum + 0.5), 0, 255);
            }
        });
    }

    private const int LumaTaps = 6;

    /// <summary>
    /// Normalized Lanczos-3 weights for taps at -2..3 around a fractional offset t. Its sharpness
    /// varies less with the sub-pixel offset than bicubic, so detail pulses less as the correction
    /// moves from frame to frame.
    /// </summary>
    private static void LanczosWeights(double t, Span<double> weights)
    {
        int phase = (int)(t * LanczosPhases + 0.5);
        LanczosTable.AsSpan(phase * LumaTaps, LumaTaps).CopyTo(weights);
    }

    private const int LanczosPhases = 1024;

    /// <summary>Weights for every 1/1024 pixel offset (0 to 1 inclusive), computed once.</summary>
    private static readonly double[] LanczosTable = CreateLanczosTable();

    private static double[] CreateLanczosTable()
    {
        const double a = LumaTaps / 2;
        var table = new double[(LanczosPhases + 1) * LumaTaps];
        for (int phase = 0; phase <= LanczosPhases; phase++)
        {
            double t = phase / (double)LanczosPhases, total = 0;
            var weights = table.AsSpan(phase * LumaTaps, LumaTaps);
            for (int k = 0; k < LumaTaps; k++)
            {
                double distance = Math.Abs(k - (LumaTaps / 2 - 1) - t);
                weights[k] = distance < 1e-9 ? 1
                    : distance >= a ? 0
                    : a * Math.Sin(Math.PI * distance) * Math.Sin(Math.PI * distance / a) /
                      (Math.PI * Math.PI * distance * distance);
                total += weights[k];
            }
            for (int k = 0; k < LumaTaps; k++) weights[k] /= total;
        }
        return table;
    }

    private static int Mirror(int coordinate, int length)
    {
        int wrapped = ((coordinate % (2 * length)) + 2 * length) % (2 * length);
        return wrapped < length ? wrapped : 2 * length - wrapped - 1;
    }

    #endregion
}
