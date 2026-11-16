// Copyright (c) Duncan Lawler. All Rights Reserved.
namespace PhotoLibrarian.Core.Services;

/// <summary>Copies locally matching, unpainted exemplars into a stroke, from its boundary inward.</summary>
internal static class RetouchPatchSynthesis
{
    private const int PatchRadius = 3;

    public static double[] CreateGuidance(byte[] pixels, int width, int height, int left, int top,
        int maskWidth, int maskHeight, int[] indices, IReadOnlyList<int> selected, int brushRadius,
        CancellationToken cancellationToken)
    {
        var filled = new bool[selected.Count];
        var colors = new double[checked(selected.Count * 3)];
        var donors = new int[selected.Count];
        var front = new HashSet<int>();
        for (var i = 0; i < selected.Count; i++)
        {
            if (i % 256 == 0) cancellationToken.ThrowIfCancellationRequested();
            var x = selected[i] % maskWidth + left;
            var y = selected[i] / maskWidth + top;
            if (IsKnown(x - 1, y) || IsKnown(x + 1, y) ||
                IsKnown(x, y - 1) || IsKnown(x, y + 1))
                front.Add(i);
        }

        while (front.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = -1;
            double priority = -1;
            foreach (var index in front)
            {
                if (filled[index]) continue;
                var x = selected[index] % maskWidth + left;
                var y = selected[index] / maskWidth + top;
                var known = 0;
                double contrast = 0;
                for (var dy = -PatchRadius; dy <= PatchRadius; dy++)
                    for (var dx = -PatchRadius; dx <= PatchRadius; dx++)
                    {
                        if (!IsKnown(x + dx, y + dy)) continue;
                        known++;
                        if (IsKnown(x + dx + 1, y + dy))
                            contrast = Math.Max(contrast, Difference(x + dx, y + dy, x + dx + 1, y + dy));
                        if (IsKnown(x + dx, y + dy + 1))
                            contrast = Math.Max(contrast, Difference(x + dx, y + dy, x + dx, y + dy + 1));
                    }
                // Continue strong structures before filling the less constrained, flat parts.
                var score = known * (1 + contrast / 32);
                if (score > priority || (score == priority && index < target))
                {
                    priority = score;
                    target = index;
                }
            }
            if (target < 0)
                throw new InvalidOperationException("The retouch fill front did not advance.");
            var tx = selected[target] % maskWidth + left;
            var ty = selected[target] / maskWidth + top;
            var patchRadius = PatchRadius;
            var donorX = -1;
            var donorY = -1;
            var adjustment = new double[3];
            // Thin/tiny images or mostly painted surroundings may only offer smaller exemplars.
            for (; patchRadius >= 0; patchRadius--)
            {
                if (FindDonor(tx, ty, patchRadius, out donorX, out donorY, adjustment)) break;
            }
            if (donorX < 0)
                throw new InvalidOperationException("The retouch fill front has no unpainted source pixel.");

            var newlyFilled = new List<int>();
            for (var dy = -patchRadius; dy <= patchRadius; dy++)
                for (var dx = -patchRadius; dx <= patchRadius; dx++)
                {
                    var index = Index(tx + dx, ty + dy);
                    if (index < 0 || filled[index]) continue;
                    var donor = ((donorY + dy) * width + donorX + dx) * 4;
                    donors[index] = donor;
                    for (var channel = 0; channel < 3; channel++)
                        colors[index * 3 + channel] = pixels[donor + channel] + adjustment[channel];
                    filled[index] = true;
                    front.Remove(index);
                    newlyFilled.Add(index);
                }
            foreach (var index in newlyFilled)
            {
                var x = selected[index] % maskWidth + left;
                var y = selected[index] / maskWidth + top;
                AddFront(x - 1, y);
                AddFront(x + 1, y);
                AddFront(x, y - 1);
                AddFront(x, y + 1);
            }
        }

        var guidance = new double[colors.Length];
        for (var i = 0; i < selected.Count; i++)
        {
            if (i % 256 == 0) cancellationToken.ThrowIfCancellationRequested();
            var x = selected[i] % maskWidth + left;
            var y = selected[i] / maskWidth + top;
            AddGradient(i, x - 1, y, -1, 0);
            AddGradient(i, x + 1, y, 1, 0);
            AddGradient(i, x, y - 1, 0, -1);
            AddGradient(i, x, y + 1, 0, 1);
        }
        return guidance;

        int Index(int x, int y)
        {
            if (x < left || y < top || (long)x >= (long)left + maskWidth ||
                (long)y >= (long)top + maskHeight) return -1;
            return indices[(y - top) * maskWidth + x - left];
        }

        bool IsKnown(int x, int y)
        {
            if (x < 0 || y < 0 || x >= width || y >= height) return false;
            var index = Index(x, y);
            return index >= 0 ? filled[index] : pixels[(y * width + x) * 4 + 3] == 255;
        }

        double Color(int x, int y, int channel)
        {
            var index = Index(x, y);
            return index >= 0 ? colors[index * 3 + channel] : pixels[(y * width + x) * 4 + channel];
        }

        double Difference(int x, int y, int nx, int ny)
        {
            double difference = 0;
            for (var channel = 0; channel < 3; channel++)
                difference = Math.Max(difference, Math.Abs(Color(x, y, channel) - Color(nx, ny, channel)));
            return difference;
        }

        void AddFront(int x, int y)
        {
            var index = Index(x, y);
            if (index >= 0 && !filled[index]) front.Add(index);
        }

        bool FindDonor(int tx, int ty, int radius, out int bestX, out int bestY, double[] adjustment)
        {
            bestX = bestY = -1;
            var samples = new List<(int X, int Y, double Weight)>();
            double sampleWeight = 0;
            var gradientX = new double[3];
            var gradientY = new double[3];
            var contextRadius = radius * 2;
            for (var dy = -contextRadius; dy <= contextRadius; dy++)
                for (var dx = -contextRadius; dx <= contextRadius; dx++)
                {
                    if (!IsKnown(tx + dx, ty + dy)) continue;
                    // Observed pixels must outweigh our own earlier guesses.
                    var weight = Index(tx + dx, ty + dy) >= 0 ? 0.1 : 1;
                    samples.Add((dx, dy, weight));
                    sampleWeight += weight;
                    for (var channel = 0; channel < 3; channel++)
                    {
                        if (dx < contextRadius && IsKnown(tx + dx + 1, ty + dy))
                            gradientX[channel] = Math.Max(gradientX[channel],
                                Math.Abs(Color(tx + dx, ty + dy, channel) - Color(tx + dx + 1, ty + dy, channel)));
                        if (dy < contextRadius && IsKnown(tx + dx, ty + dy + 1))
                            gradientY[channel] = Math.Max(gradientY[channel],
                                Math.Abs(Color(tx + dx, ty + dy, channel) - Color(tx + dx, ty + dy + 1, channel)));
                    }
                }

            var edges = new List<(int Offset, int Step, double[] Allowed)>();
            for (var dy = -radius; dy <= radius; dy++)
                for (var dx = -radius; dx <= radius; dx++)
                {
                    var x = tx + dx;
                    var y = ty + dy;
                    if (x < 0 || y < 0 || x >= width || y >= height) continue;
                    var offset = (dy * width + dx) * 4;
                    if (dx < radius && x + 1 < width) edges.Add((offset, 4, gradientX));
                    if (dy < radius && y + 1 < height) edges.Add((offset, width * 4, gradientY));
                }

            var normalization = Math.Max(0.1, sampleWeight);
            // The search is local to each patch, not the stroke's potentially very large extent.
            var reach = brushRadius * 4 + PatchRadius;
            var x0 = Math.Max(0, tx - reach);
            var x1 = (int)Math.Min(width - 1L, (long)tx + reach);
            var y0 = Math.Max(0, ty - reach);
            var y1 = (int)Math.Min(height - 1L, (long)ty + reach);
            double bestError = double.PositiveInfinity;
            long bestDistance = long.MaxValue;
            var means = new double[3];
            for (var search = 0; search < 2; search++)
            {
                for (var sy = y0; sy <= y1; sy++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (var sx = x0; sx <= x1; sx++)
                    {
                        var distance = (long)(sx - tx) * (sx - tx) + (long)(sy - ty) * (sy - ty);
                        var proximity = 0.01 * distance;
                        if (proximity > bestError) continue;
                        var valid = true;
                        for (var dy = -radius; dy <= radius && valid; dy++)
                            for (var dx = -radius; dx <= radius; dx++)
                            {
                                if (tx + dx < 0 || ty + dy < 0 || tx + dx >= width || ty + dy >= height) continue;
                                var x = sx + dx;
                                var y = sy + dy;
                                if (x < 0 || y < 0 || x >= width || y >= height ||
                                    Index(x, y) >= 0 || pixels[(y * width + x) * 4 + 3] != 255)
                                {
                                    valid = false;
                                    break;
                                }
                            }
                        if (!valid) continue;
                        Array.Clear(means);
                        foreach (var sample in samples)
                        {
                            var x = sx + sample.X;
                            var y = sy + sample.Y;
                            if (x < 0 || y < 0 || x >= width || y >= height ||
                                Index(x, y) >= 0 || pixels[(y * width + x) * 4 + 3] != 255)
                            {
                                valid = false;
                                break;
                            }
                            for (var channel = 0; channel < 3; channel++)
                                means[channel] += sample.Weight * (Color(tx + sample.X, ty + sample.Y, channel) -
                                    pixels[(y * width + x) * 4 + channel]);
                        }
                        if (!valid) continue;
                        for (var channel = 0; channel < 3; channel++)
                            means[channel] /= normalization;
                        double error = proximity * normalization;
                        // A large brightness/chroma correction must not make an unrelated object
                        // indistinguishable from the actual background.
                        for (var channel = 0; channel < 3; channel++)
                            error += 0.25 * sampleWeight * means[channel] * means[channel];
                        foreach (var sample in samples)
                        {
                            for (var channel = 0; channel < 3; channel++)
                            {
                                var difference = Color(tx + sample.X, ty + sample.Y, channel) -
                                    pixels[((sy + sample.Y) * width + sx + sample.X) * 4 + channel] - means[channel];
                                error += sample.Weight * difference * difference;
                            }
                            if (error > bestError * normalization) break;
                        }
                        error /= normalization;
                        if (error > bestError) continue;
                        double novelty = 0;
                        // Inspect the copied interior too: a good overlap alone can conceal
                        // another light/logo just beyond it. Keep gradients supported by context.
                        var donor = (sy * width + sx) * 4;
                        foreach (var edge in edges)
                        {
                            for (var channel = 0; channel < 3; channel++)
                            {
                                var offset = donor + edge.Offset + channel;
                                var difference = Math.Abs(pixels[offset] - pixels[offset + edge.Step]);
                                var excess = Math.Max(0, difference - edge.Allowed[channel] - 4);
                                novelty += excess * excess;
                            }
                        }
                        error += novelty / Math.Max(1, edges.Count);
                        if (error > bestError || (error == bestError && distance >= bestDistance)) continue;
                        bestError = error;
                        bestDistance = distance;
                        bestX = sx;
                        bestY = sy;
                        Array.Copy(means, adjustment, 3);
                    }
                }
                if (bestX >= 0) break;
                var propagated = false;
                // Deep inside a wide painted region, propagate an already matched source
                // instead of imposing an area cap or ever using painted pixels as donors.
                for (var dy = -1; dy <= 1 && !propagated; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var index = Index(tx + dx, ty + dy);
                        if (index < 0 || !filled[index]) continue;
                        var sx = donors[index] / 4 % width;
                        var sy = donors[index] / 4 / width;
                        x0 = Math.Max(0, sx - reach);
                        x1 = (int)Math.Min(width - 1L, (long)sx + reach);
                        y0 = Math.Max(0, sy - reach);
                        y1 = (int)Math.Min(height - 1L, (long)sy + reach);
                        propagated = true;
                        break;
                    }
                if (!propagated) break;
            }
            return bestX >= 0;
        }

        void AddGradient(int index, int x, int y, int dx, int dy)
        {
            if (x < 0 || y < 0 || x >= width || y >= height) return;
            var neighbour = Index(x, y);
            var donor = donors[index];
            var sx = donor / 4 % width + dx;
            var sy = donor / 4 / width + dy;
            for (var channel = 0; channel < 3; channel++)
            {
                // Inside the mask use a consistent synthesized gradient field; at its boundary
                // use the donor's outward gradient so the solve blends to the real surroundings.
                var gradient = neighbour >= 0
                    ? colors[index * 3 + channel] - colors[neighbour * 3 + channel]
                    : sx >= 0 && sy >= 0 && sx < width && sy < height &&
                        Index(sx, sy) < 0 && pixels[(sy * width + sx) * 4 + 3] == 255
                        ? pixels[donor + channel] - pixels[(sy * width + sx) * 4 + channel]
                        : 0;
                guidance[index * 3 + channel] += gradient;
            }
        }
    }
}
