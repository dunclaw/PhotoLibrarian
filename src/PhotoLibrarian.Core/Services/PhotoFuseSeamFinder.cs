// Copyright (c) Duncan Lawler. All Rights Reserved.
namespace PhotoLibrarian.Core.Services;

/// <summary>Bounded minimum-cut seam: keep facial cores opaque and crop boundaries/neighbors original.</summary>
internal static class PhotoFuseSeamFinder
{
    internal sealed record Result(byte[]? Alpha, string? Rejection);

    internal static Result Find(byte[] donor, byte[] basis, PhotoFuseTransform transform,
        int width, int height, PhotoFuseRegion region, PhotoFuseFaceSelection selection,
        IReadOnlyList<PhotoFuseFaceBounds> originalFaces, CancellationToken token)
    {
        var face = selection.Target;
        int scale = Math.Max(1, (int)Math.Ceiling(Math.Max(region.Width, region.Height) / 192.0));
        int gw = (region.Width + scale - 1) / scale, gh = (region.Height + scale - 1) / scale;
        int nodes = gw * gh;
        var errors = new double[nodes];
        var protectedFace = new bool[nodes];
        var original = new bool[nodes];
        int feather = Math.Clamp((int)Math.Min(face.Width * width, face.Height * height) / 16, 2, 8);
        var others = originalFaces.Where(other => other != face).ToArray();
        for (int y = 0; y < gh; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < gw; x++)
            {
                int index = y * gw + x;
                int bx = Math.Min(region.Left + region.Width - 1, region.Left + x * scale + scale / 2);
                int by = Math.Min(region.Top + region.Height - 1, region.Top + y * scale + scale / 2);
                var p = transform.SourceAt(bx, by, width, height);
                protectedFace[index] = WithinEllipse(bx, by, face, width, height, 0.90) ||
                    WithinEllipse(p.X, p.Y, selection.Donor, width, height, 0.90);
                original[index] = x == 0 || y == 0 || x == gw - 1 || y == gh - 1;
                if (others.Any(other => region.Left + (x + 1) * scale > other.X * width &&
                    region.Left + x * scale < (other.X + other.Width) * width &&
                    region.Top + (y + 1) * scale > other.Y * height &&
                    region.Top + y * scale < (other.Y + other.Height) * height)) original[index] = true;
                for (int cy = region.Top + y * scale; cy <= Math.Min(region.Top + (y + 1) * scale - 1,
                    region.Top + region.Height - 1); cy += Math.Max(1, scale - 1))
                for (int cx = region.Left + x * scale; cx <= Math.Min(region.Left + (x + 1) * scale - 1,
                    region.Left + region.Width - 1); cx += Math.Max(1, scale - 1))
                {
                    var point = transform.SourceAt(cx, cy, width, height);
                    if (!PhotoFuseRegistration.Inside(point.X, point.Y, width, height)) original[index] = true;
                }
                if (original[index] && protectedFace[index])
                {
                    if (WithinEllipse(bx, by, face, width, height, 0.65) ||
                        WithinEllipse(p.X, p.Y, selection.Donor, width, height, 0.65))
                        return new(null, "A neighboring face or image edge prevents a safe seam around this face.");
                    protectedFace[index] = false;
                }
                if (!PhotoFuseRegistration.Inside(p.X, p.Y, width, height)) { errors[index] = 255; continue; }
                double error = 0;
                for (int c = 0; c < 3; c++)
                    error = Math.Max(error, Math.Abs(PhotoFuseRegistration.Sample(donor, width, p.X, p.Y, c) -
                        basis[(by * width + bx) * 4 + c]));
                errors[index] = error;
            }
        }
        // A pointwise cut can sit immediately outside changed skin and put its inward feather
        // back on that skin. Score the whole possible feather neighborhood, not just the cut pixel.
        int radius = (int)Math.Ceiling((feather + 1) / (double)scale);
        var horizontal = new double[nodes];
        var bandErrors = new double[nodes];
        for (int y = 0; y < gh; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < gw; x++)
                for (int near = Math.Max(0, x - radius); near <= Math.Min(gw - 1, x + radius); near++)
                    horizontal[y * gw + x] = Math.Max(horizontal[y * gw + x], errors[y * gw + near]);
        }
        for (int y = 0; y < gh; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < gw; x++)
                for (int near = Math.Max(0, y - radius); near <= Math.Min(gh - 1, y + radius); near++)
                    bandErrors[y * gw + x] = Math.Max(bandErrors[y * gw + x], horizontal[near * gw + x]);
        }
        var graph = new CutGraph(nodes + 2, nodes * 8 + 8, token);
        double finiteCapacity = 0;
        for (int y = 0; y < gh; y++)
        for (int x = 0; x < gw; x++)
        {
            int node = y * gw + x;
            if (x + 1 < gw) AddNeighbor(node, node + 1);
            if (y + 1 < gh) AddNeighbor(node, node + gw);
        }
        void AddNeighbor(int a, int b)
        {
            double error = (bandErrors[a] + bandErrors[b]) / 2;
            double capacity = 4 + Math.Round(error * error);
            graph.Add(a, b, capacity, capacity);
            finiteCapacity += capacity;
        }
        int source = nodes, sink = nodes + 1;
        for (int node = 0; node < nodes; node++)
        {
            if (protectedFace[node]) graph.Add(source, node, finiteCapacity + 1, 0);
            if (original[node]) graph.Add(node, sink, finiteCapacity + 1, 0);
        }
        graph.Cut(source, sink);
        var distance = new byte[region.Width * region.Height];
        for (int y = 0; y < region.Height; y++)
        for (int x = 0; x < region.Width; x++)
            distance[y * region.Width + x] = graph.SourceSide((y / scale) * gw + x / scale) ? byte.MaxValue : (byte)0;
        for (int y = 0; y < region.Height; y++)
        for (int x = 0; x < region.Width; x++)
        {
            int i = y * region.Width + x;
            if (distance[i] == 0) continue;
            int near = Math.Min(x == 0 ? 0 : distance[i - 1], y == 0 ? 0 : distance[i - region.Width]);
            distance[i] = (byte)Math.Min(distance[i], near + 1);
        }
        for (int y = region.Height - 1; y >= 0; y--)
        for (int x = region.Width - 1; x >= 0; x--)
        {
            int i = y * region.Width + x;
            if (distance[i] == 0) continue;
            int near = Math.Min(x == region.Width - 1 ? 0 : distance[i + 1],
                y == region.Height - 1 ? 0 : distance[i + region.Width]);
            distance[i] = (byte)Math.Min(distance[i], near + 1);
        }
        for (int band = feather; band >= 2; band--)
        {
            var alpha = new byte[distance.Length];
            int count = 0, inliers = 0;
            bool coreOpaque = true;
            for (int y = 0; y < region.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < region.Width; x++)
                {
                    int i = y * region.Width + x;
                    alpha[i] = (byte)(255 * Math.Clamp((distance[i] - 1) / (double)band, 0, 1));
                    int bx = region.Left + x, by = region.Top + y;
                    var p = transform.SourceAt(bx, by, width, height);
                    if (alpha[i] != 255 && (WithinEllipse(bx, by, face, width, height, 0.60) ||
                        WithinEllipse(p.X, p.Y, selection.Donor, width, height, 0.60)))
                    {
                        coreOpaque = false;
                        break;
                    }
                    if (alpha[i] == 0) continue;
                    if (!PhotoFuseRegistration.Inside(p.X, p.Y, width, height))
                        return new(null, "The selected face extends outside its replacement photo.");
                    if (alpha[i] == 255) continue;
                    count++;
                    double error = 0;
                    for (int c = 0; c < 3; c++)
                        error = Math.Max(error, Math.Abs(PhotoFuseRegistration.Sample(donor, width, p.X, p.Y, c) -
                            basis[(by * width + bx) * 4 + c]));
                    if (error <= 24) inliers++;
                }
                if (!coreOpaque) break;
            }
            double minimumBandSamples = Math.Ceiling(Math.PI * Math.Min(face.Width * width, face.Height * height) / 2);
            if (coreOpaque && count >= minimumBandSamples && inliers >= count * 0.90) return new(alpha, null);
        }
        return new(null, "No clean seam was found around this face. Choose a different shot.");
    }

    private static bool WithinEllipse(double x, double y, PhotoFuseFaceBounds face, int width, int height, double radius)
    {
        double dx = (x / width - face.X - face.Width / 2) / (face.Width / 2);
        double dy = (y / height - face.Y - face.Height / 2) / (face.Height / 2);
        return dx * dx + dy * dy <= radius * radius;
    }

    private sealed class CutGraph
    {
        private readonly int[] _first, _next, _to, _level, _current, _queue, _path, _pathEdge;
        private readonly double[] _capacity, _bottleneck;
        private readonly CancellationToken _token;
        private int _edges;

        internal CutGraph(int nodes, int edges, CancellationToken token)
        {
            _first = new int[nodes]; Array.Fill(_first, -1);
            _next = new int[edges]; _to = new int[edges]; _capacity = new double[edges];
            _level = new int[nodes]; _current = new int[nodes]; _queue = new int[nodes];
            _path = new int[nodes]; _pathEdge = new int[nodes]; _bottleneck = new double[nodes];
            _token = token;
        }

        internal void Add(int from, int to, double capacity, double reverse)
        {
            _to[_edges] = to; _capacity[_edges] = capacity; _next[_edges] = _first[from]; _first[from] = _edges++;
            _to[_edges] = from; _capacity[_edges] = reverse; _next[_edges] = _first[to]; _first[to] = _edges++;
        }

        internal bool SourceSide(int node) => _level[node] >= 0;

        internal void Cut(int source, int sink)
        {
            while (Levels(source, sink))
            {
                Array.Copy(_first, _current, _first.Length);
                while (Augment(source, sink) > 0) _token.ThrowIfCancellationRequested();
            }
        }

        private bool Levels(int source, int sink)
        {
            _token.ThrowIfCancellationRequested();
            Array.Fill(_level, -1);
            _level[source] = 0;
            int head = 0, tail = 0;
            _queue[tail++] = source;
            while (head < tail)
            {
                if ((head & 1023) == 0) _token.ThrowIfCancellationRequested();
                int node = _queue[head++];
                for (int edge = _first[node]; edge >= 0; edge = _next[edge])
                {
                    int next = _to[edge];
                    if (_capacity[edge] <= 0 || _level[next] >= 0) continue;
                    _level[next] = _level[node] + 1;
                    _queue[tail++] = next;
                }
            }
            return _level[sink] >= 0;
        }

        private double Augment(int source, int sink)
        {
            int depth = 0;
            _path[0] = source; _bottleneck[0] = double.MaxValue;
            while (depth >= 0)
            {
                int node = _path[depth];
                if (node == sink)
                {
                    double flow = _bottleneck[depth];
                    for (int i = 0; i < depth; i++)
                    {
                        int edge = _pathEdge[i];
                        _capacity[edge] -= flow; _capacity[edge ^ 1] += flow;
                    }
                    return flow;
                }
                int candidate = _current[node];
                while (candidate >= 0 && (_capacity[candidate] <= 0 ||
                    _level[_to[candidate]] != _level[node] + 1)) candidate = _next[candidate];
                _current[node] = candidate;
                if (candidate < 0)
                {
                    _level[node] = -1;
                    depth--;
                    if (depth >= 0)
                    {
                        int parent = _path[depth];
                        _current[parent] = _next[_pathEdge[depth]];
                    }
                    continue;
                }
                _pathEdge[depth] = candidate;
                _bottleneck[depth + 1] = Math.Min(_bottleneck[depth], _capacity[candidate]);
                _path[++depth] = _to[candidate];
            }
            return 0;
        }
    }
}
