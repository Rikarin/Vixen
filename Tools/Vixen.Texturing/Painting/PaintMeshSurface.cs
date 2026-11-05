// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Terrain;

namespace Vixen.Texturing.Painting;

/// <summary>A mesh over an atlas: which texels are surface, and where on the surface each one is.</summary>
/// <remarks>
///     <para>
///         <b>A seam is two places in the atlas that are one place on the mesh, so the mesh is what can paint
///         across one.</b> A stamp in the atlas stops at an island's edge, and the texels that carry on from
///         that edge are somewhere else entirely. Everything here therefore measures a texel by where it is in
///         the mesh's own space. <see cref="Through" /> measures it on the picture a camera sees, so a stamp
///         lands wherever the disc on the screen falls, on any number of islands; <see cref="AcrossSeams" /> carries
///         a stamp made in the atlas over the islands its surface runs on to.
///     </para>
///     <para>
///         ⚠ <b>Two walks and not one, because they answer different questions.</b> A camera paints what is
///         in front of it, connected or not: a bolt is a separate shell and belongs to the stroke over it.
///         An atlas stamp has no camera, so what it may reach is what the surface actually joins, and a
///         flood across shared edges is what keeps it off the far side of a thin wall.
///     </para>
///     <para>
///         ⚠ <b>The cost of one stamp is the texels under it.</b> A triangle bigger than the brush is only
///         scanned inside the brush's reach, so a low-poly prop with a 4K atlas pays what a dense one does.
///         Building the charts and neighbours reads the whole mesh once, on the first stroke.
///     </para>
///     <para>
///         ⚠ <b>Depth is a limit, not an occlusion test.</b> A texel behind other geometry but within the
///         reach of the surface under the brush is painted. Doing better needs a depth test per texel.
///     </para>
/// </remarks>
public sealed class PaintMeshSurface {
    /// <summary>How far outside a triangle a texel may be and still belong to it, in texels.</summary>
    /// <remarks>Half a texel's diagonal, which is what makes this agree with a conservative coverage.</remarks>
    const float Margin = 0.75f;

    /// <summary>The smallest fraction of a brush's depth the surface is given to fall away by.</summary>
    const float DepthReach = 2f;

    /// <summary>The reach of a square alpha's corner, as a share of its radius.</summary>
    const float Corner = 1.4143f;

    readonly int width;
    readonly int height;
    readonly Lazy<Topology> topology;
    readonly Lazy<TriangleTree> layout;

    /// <summary>Lays a mesh over an atlas.</summary>
    /// <param name="projection">The mesh.</param>
    /// <param name="width">The atlas width.</param>
    /// <param name="height">Its height.</param>
    public PaintMeshSurface(PaintProjection projection, int width, int height) {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Projection = projection;
        this.width = width;
        this.height = height;
        Coverage = PaintCoverage.FromTriangles(width, height, Soup(projection));
        topology = new(() => Topology.Of(projection));
        layout = new(() => Layout(projection));
    }

    /// <summary>The mesh.</summary>
    public PaintProjection Projection { get; }

    /// <summary>Which texels are surface.</summary>
    public PaintCoverage Coverage { get; }

    /// <summary>How many islands the atlas holds.</summary>
    public int Charts => topology.Value.Charts;

    /// <summary>Which island a triangle is in.</summary>
    /// <param name="triangle">The triangle.</param>
    /// <returns>Its island.</returns>
    public int ChartOf(int triangle) => topology.Value.Chart[triangle];

    /// <summary>A brush seen through a camera, whose stamps are positions on its picture.</summary>
    /// <param name="view">The camera.</param>
    /// <param name="mirror">A plane the stroke is mirrored through as well, or nothing.</param>
    /// <returns>What a stroke places its stamps with.</returns>
    public IPaintPlacement Through(PaintView view, PaintSymmetry? mirror = null) =>
        new ViewPlacement(this, view, mirror is { } plane ? view.Mirrored(plane) : null);

    /// <summary>A brush in the atlas, which carries on over the seams its surface crosses.</summary>
    /// <returns>What a stroke places its stamps with.</returns>
    public IPaintPlacement AcrossSeams() => new SeamPlacement(this);

    /// <summary>Finds the triangle under a position in the atlas.</summary>
    /// <param name="texel">The position.</param>
    /// <param name="triangle">The nearest triangle.</param>
    /// <param name="corners">The weights of its corners at the nearest point.</param>
    /// <param name="nearest">That point, in texels.</param>
    /// <returns>Whether the mesh has any triangle.</returns>
    public bool Locate(Vector2 texel, out int triangle, out Vector3 corners, out Vector2 nearest) {
        var found = layout.Value.Closest(new(texel.X / width, texel.Y / height, 0f));

        triangle = found.Triangle;
        corners = found.Barycentric;
        nearest = new(found.Point.X * width, found.Point.Y * height);

        return found.Triangle >= 0;
    }

    static Vector2[] Soup(PaintProjection projection) {
        var coordinates = new Vector2[projection.Triangles * 3];

        for (var triangle = 0; triangle < projection.Triangles; triangle++) {
            projection.Triangle(
                triangle,
                out _,
                out _,
                out _,
                out coordinates[triangle * 3],
                out coordinates[(triangle * 3) + 1],
                out coordinates[(triangle * 3) + 2]
            );
        }

        return coordinates;
    }

    static TriangleTree Layout(PaintProjection projection) {
        var points = new Vector3[projection.Triangles * 3];
        var order = new int[points.Length];

        for (var triangle = 0; triangle < projection.Triangles; triangle++) {
            projection.Triangle(triangle, out _, out _, out _, out var a, out var b, out var c);

            points[triangle * 3] = new(a.X, a.Y, 0f);
            points[(triangle * 3) + 1] = new(b.X, b.Y, 0f);
            points[(triangle * 3) + 2] = new(c.X, c.Y, 0f);
        }

        for (var at = 0; at < order.Length; at++) {
            order[at] = at;
        }

        return new(points, order);
    }

    /// <summary>
    ///     Walks a triangle's texels inside a rectangle and hands the weigher where on the mesh each one is.
    /// </summary>
    void Scan<TWeigher>(int triangle, PaintRect within, int piece, ref TWeigher weigher, PaintStampTexels into)
        where TWeigher : struct, ITexelWeigher {
        Projection.Triangle(triangle, out var a, out var b, out var c, out var ua, out var ub, out var uc);

        var ta = PaintProjection.Texel(ua, width, height);
        var tb = PaintProjection.Texel(ub, width, height);
        var tc = PaintProjection.Texel(uc, width, height);
        var area = Edge(ta, tb, tc);

        if (area == 0f) {
            return;
        }

        var box = PaintRect.Covering(
                Vector2.Min(ta, Vector2.Min(tb, tc)) - new Vector2(Margin),
                Vector2.Max(ta, Vector2.Max(tb, tc)) + new Vector2(Margin)
            )
            .Clip(width, height)
            .Intersect(within);

        if (box.IsEmpty) {
            return;
        }

        // Each corner's weight goes to zero on the edge across from it, and this far past that edge is the
        // margin in that weight's own units.
        var scale = MathF.Abs(area);
        var slackA = Margin * (tc - tb).Length() / scale;
        var slackB = Margin * (ta - tc).Length() / scale;
        var slackC = Margin * (tb - ta).Length() / scale;
        var minimumX = int.MaxValue;
        var minimumY = int.MaxValue;
        var maximumX = int.MinValue;
        var maximumY = int.MinValue;

        for (var y = box.Y; y < box.EndY; y++) {
            for (var x = box.X; x < box.EndX; x++) {
                var index = (y * width) + x;

                if (!Coverage.IsCovered(index)) {
                    continue;
                }

                Vector2 centre = new(x + 0.5f, y + 0.5f);
                var wa = Edge(tb, tc, centre) / area;
                var wb = Edge(tc, ta, centre) / area;
                var wc = Edge(ta, tb, centre) / area;

                if (wa < -slackA || wb < -slackB || wc < -slackC) {
                    continue;
                }

                wa = MathF.Max(wa, 0f);
                wb = MathF.Max(wb, 0f);
                wc = MathF.Max(wc, 0f);

                var sum = wa + wb + wc;
                var weight = weigher.Weigh(((a * wa) + (b * wb) + (c * wc)) / sum);

                if (!(weight > 0f)) {
                    continue;
                }

                into.Add(index, weight);
                minimumX = Math.Min(minimumX, x);
                minimumY = Math.Min(minimumY, y);
                maximumX = Math.Max(maximumX, x);
                maximumY = Math.Max(maximumY, y);
            }
        }

        if (maximumX >= minimumX) {
            into.Touch(piece, new(minimumX, minimumY, maximumX - minimumX + 1, maximumY - minimumY + 1));
        }
    }

    /// <summary>Where in the atlas a square on the picture falls on a triangle's plane.</summary>
    /// <remarks>
    ///     ⚠ <b>The square's own corners on the plane, and not a ball round the hit.</b> A brush at a hard edge asks
    ///     about a face seen nearly side on, whose depth under the brush can be ten radii, and a ball that deep
    ///     scanned tens of thousands of texels of a big triangle per stamp to find a sliver. A plane maps the
    ///     square to a four-sided shape, so its corners bound it.
    /// </remarks>
    PaintRect Seen(int triangle, in PaintView camera, Vector2 centre, float reach) {
        var whole = new PaintRect(0, 0, width, height);
        var density = Projection.Density(triangle, width, height);

        if (!density.IsMeasurable) {
            return whole;
        }

        Projection.Triangle(triangle, out var a, out _, out _, out var ua, out _, out _);

        var normal = density.Normal;
        var height0 = Vector3.Dot(a - camera.Eye, normal);
        var origin = ua * new Vector2(width, height);
        var least = new Vector2(float.PositiveInfinity);
        var most = new Vector2(float.NegativeInfinity);

        for (var corner = 0; corner < 4; corner++) {
            var aim = centre + new Vector2((corner & 1) == 0 ? -reach : reach, (corner & 2) == 0 ? -reach : reach);
            var direction = camera.RayAt(aim).Direction;
            var along = Vector3.Dot(direction, normal);

            // A ray that never reaches the plane leaves the shape unbounded: the triangle's own box says the rest.
            if (MathF.Abs(along) < 1e-6f || height0 / along <= 0f) {
                return whole;
            }

            var offset = camera.Eye + (direction * (height0 / along)) - a;
            var atlas = origin
                + (density.PerTangent * Vector3.Dot(offset, density.Tangent))
                + (density.PerBitangent * Vector3.Dot(offset, density.Bitangent));

            least = Vector2.Min(least, atlas);
            most = Vector2.Max(most, atlas);
        }

        return PaintRect.Covering(least - new Vector2(2f), most + new Vector2(2f));
    }

    /// <summary>Where in the atlas a triangle can hold anything within a distance of a point.</summary>
    PaintRect Reach(int triangle, Vector3 point, float distance) {
        var density = Projection.Density(triangle, width, height);

        if (!density.IsMeasurable) {
            return new(0, 0, width, height);
        }

        Projection.Triangle(triangle, out var a, out _, out _, out var ua, out _, out _);

        // The point dropped onto the triangle's plane, and the map from that plane to the atlas, which
        // holds beyond the triangle's own edges since it is one linear map.
        var offset = point - a;
        var atlas = (ua * new Vector2(width, height))
            + (density.PerTangent * Vector3.Dot(offset, density.Tangent))
            + (density.PerBitangent * Vector3.Dot(offset, density.Bitangent));
        var half = new Vector2((distance * density.Major) + 2f);

        return PaintRect.Covering(atlas - half, atlas + half);
    }

    static float Edge(Vector2 a, Vector2 b, Vector2 point) =>
        ((b.X - a.X) * (point.Y - a.Y)) - ((b.Y - a.Y) * (point.X - a.X));

    /// <summary>What a stamp weighs at a point on the mesh.</summary>
    interface ITexelWeigher {
        float Weigh(Vector3 point);
    }

    /// <summary>A stamp on a camera's picture: a texel weighs what the brush weighs where it projects.</summary>
    readonly struct ViewWeigher(
        PaintView view,
        float depth,
        float reach,
        TerrainBrush kernel,
        PaintStamp stamp,
        IBrushMask? alpha
    ) : ITexelWeigher {
        readonly TerrainBrush kernel = kernel;

        public float Weigh(Vector3 point) =>
            MathF.Abs(view.Depth(point) - depth) > reach
                ? 0f
                : PaintBrush.Weight(kernel, view.Project(point), stamp, alpha);
    }

    /// <summary>
    ///     A stamp in the atlas, carried past its island: a point weighs what the texel would that lies as
    ///     far along the surface in the same direction on the stamp's own side.
    /// </summary>
    readonly struct SeamWeigher(
        Vector3 centre,
        PaintDensity density,
        Vector2 anchor,
        TerrainBrush kernel,
        PaintStamp stamp,
        IBrushMask? alpha
    ) : ITexelWeigher {
        readonly TerrainBrush kernel = kernel;

        public float Weigh(Vector3 point) {
            var offset = point - centre;
            var across = Vector3.Dot(offset, density.Tangent);
            var along = Vector3.Dot(offset, density.Bitangent);
            var flat = MathF.Sqrt((across * across) + (along * along));

            // ⚠ Laid flat at its full length: a point round a right angle projects onto almost nothing,
            // and a stamp must reach as far down the next face as it would have on this one.
            var unfold = flat > 0f ? offset.Length() / flat : 1f;
            var texel = anchor + (((density.PerTangent * across) + (density.PerBitangent * along)) * unfold);

            return PaintBrush.Weight(kernel, texel, stamp, alpha);
        }
    }

    sealed class ViewPlacement(PaintMeshSurface surface, PaintView view, PaintView? mirrored) : IPaintPlacement {
        readonly List<int> near = [];

        public bool ReplacesStamp => true;

        public void Place(PaintStamp stamp, in PaintBrush brush, in TerrainBrush kernel, PaintStampTexels into) {
            One(view, stamp, brush, kernel, into, 0);

            if (mirrored is { } other) {
                One(other, stamp, brush, kernel, into, surface.Charts);
            }
        }

        void One(
            PaintView camera,
            PaintStamp stamp,
            in PaintBrush brush,
            in TerrainBrush kernel,
            PaintStampTexels into,
            int offset
        ) {
            var projection = surface.Projection;
            var ray = camera.RayAt(stamp.Centre);

            if (!projection.TryHit(ray, out var hit)) {
                return;
            }

            var density = projection.Density(hit.Triangle, surface.width, surface.height);
            var depth = camera.Depth(hit.Point);
            var radius = stamp.Radius * Corner * camera.PerPixel(depth);
            var facing = Vector3.Dot(density.Normal, ray.Direction);
            var slope = MathF.Max(MathF.Abs(facing), PaintFootprint.GrazingFloor);
            var reach = radius * MathF.Max(DepthReach, 1f / slope);
            var sphere = MathF.Sqrt((radius * radius) + (reach * reach));
            var weigher = new ViewWeigher(camera, depth, reach, kernel, stamp, brush.Alpha);

            projection.Overlapping(hit.Point, sphere, near);

            foreach (var triangle in near) {
                projection.Triangle(triangle, out var a, out var b, out var c, out _, out _, out _);

                // ⚠ Against the side the ray struck rather than a winding, so a mesh wound the other way
                // or a shell seen from inside paints the same as any other.
                var normal = Vector3.Cross(b - a, c - a);

                if (Vector3.Dot(normal, a - camera.Eye) * facing <= 0f) {
                    continue;
                }

                surface.Scan(
                    triangle,
                    surface.Seen(triangle, camera, stamp.Centre, stamp.Radius * Corner),
                    surface.ChartOf(triangle) + offset,
                    ref weigher,
                    into
                );
            }
        }
    }

    sealed class SeamPlacement(PaintMeshSurface surface) : IPaintPlacement {
        readonly Queue<int> waiting = new();
        readonly HashSet<int> seen = [];

        public bool ReplacesStamp => false;

        public void Place(PaintStamp stamp, in PaintBrush brush, in TerrainBrush kernel, PaintStampTexels into) {
            var projection = surface.Projection;

            if (!surface.Locate(stamp.Centre, out var start, out var corners, out var nearest)) {
                return;
            }

            var reach = stamp.Radius * MathF.Sqrt(stamp.Stretch) * Corner;

            if ((nearest - stamp.Centre).Length() >= reach) {
                return;
            }

            var density = projection.Density(start, surface.width, surface.height);

            if (!density.IsMeasurable) {
                return;
            }

            projection.Triangle(start, out var a, out var b, out var c, out _, out _, out _);

            var centre = (a * corners.X) + (b * corners.Y) + (c * corners.Z);
            var distance = reach / density.Minor;
            var limit = distance * distance;
            var topology = surface.topology.Value;
            var home = topology.Chart[start];
            var weigher = new SeamWeigher(centre, density, nearest, kernel, stamp, brush.Alpha);

            seen.Clear();
            waiting.Clear();
            seen.Add(start);
            waiting.Enqueue(start);

            while (waiting.TryDequeue(out var triangle)) {
                if (topology.Chart[triangle] != home) {
                    surface.Scan(
                        triangle,
                        surface.Reach(triangle, centre, distance),
                        topology.Chart[triangle],
                        ref weigher,
                        into
                    );
                }

                for (var at = topology.First[triangle]; at < topology.First[triangle + 1]; at++) {
                    var next = topology.Neighbours[at];

                    if (!seen.Add(next)) {
                        continue;
                    }

                    projection.Triangle(next, out a, out b, out c, out _, out _, out _);

                    if (Vector3.DistanceSquared(centre, TriangleTree.ClosestPointOnTriangle(centre, a, b, c)) <= limit) {
                        waiting.Enqueue(next);
                    }
                }
            }
        }
    }

    /// <summary>What shares an edge with what, on the mesh and in the atlas.</summary>
    sealed class Topology {
        Topology(int[] chart, int charts, int[] first, int[] neighbours) {
            Chart = chart;
            Charts = charts;
            First = first;
            Neighbours = neighbours;
        }

        public int[] Chart { get; }

        public int Charts { get; }

        public int[] First { get; }

        public int[] Neighbours { get; }

        public static Topology Of(PaintProjection mesh) {
            var count = mesh.Triangles;
            var edges = new Side[count * 3];
            var bounds = mesh.Bounds;
            var step = MathF.Max(Vector3.Distance(bounds.Minimum, bounds.Maximum), 1e-6f) / (1 << 20);
            Dictionary<(int, int, int), int> welded = new(count);

            int Weld(Vector3 point) {
                var key = (
                    (int)MathF.Round(point.X / step),
                    (int)MathF.Round(point.Y / step),
                    (int)MathF.Round(point.Z / step)
                );

                if (!welded.TryGetValue(key, out var id)) {
                    welded[key] = id = welded.Count;
                }

                return id;
            }

            for (var triangle = 0; triangle < count; triangle++) {
                mesh.Triangle(triangle, out var a, out var b, out var c, out var ua, out var ub, out var uc);

                var (ia, ib, ic) = (Weld(a), Weld(b), Weld(c));

                edges[triangle * 3] = Side.Of(triangle, ia, ib, ua, ub);
                edges[(triangle * 3) + 1] = Side.Of(triangle, ib, ic, ub, uc);
                edges[(triangle * 3) + 2] = Side.Of(triangle, ic, ia, uc, ua);
            }

            Array.Sort(edges, static (left, right) => left.Key.CompareTo(right.Key));

            var parent = new int[count];

            for (var triangle = 0; triangle < count; triangle++) {
                parent[triangle] = triangle;
            }

            int Find(int triangle) {
                while (parent[triangle] != triangle) {
                    parent[triangle] = parent[parent[triangle]];
                    triangle = parent[triangle];
                }

                return triangle;
            }

            var degree = new int[count + 1];
            List<(int, int)> joined = new(count * 3 / 2);

            // ⚠ Each with the next and not each with every other: a fan of a hundred triangles on one edge
            // would be five thousand pairs, and a chain joins the same set.
            for (var at = 1; at < edges.Length; at++) {
                var (left, right) = (edges[at - 1], edges[at]);

                if (left.Key != right.Key || left.Triangle == right.Triangle) {
                    continue;
                }

                joined.Add((left.Triangle, right.Triangle));
                degree[left.Triangle + 1]++;
                degree[right.Triangle + 1]++;

                if (left.SameLayout(right)) {
                    parent[Find(left.Triangle)] = Find(right.Triangle);
                }
            }

            for (var triangle = 0; triangle < count; triangle++) {
                degree[triangle + 1] += degree[triangle];
            }

            var neighbours = new int[joined.Count * 2];
            var filled = new int[count];

            foreach (var (left, right) in joined) {
                neighbours[degree[left] + filled[left]++] = right;
                neighbours[degree[right] + filled[right]++] = left;
            }

            var charts = 0;
            var numbers = new int[count];
            var chart = new int[count];

            Array.Fill(numbers, -1);

            for (var triangle = 0; triangle < count; triangle++) {
                var root = Find(triangle);

                if (numbers[root] < 0) {
                    numbers[root] = charts++;
                }

                chart[triangle] = numbers[root];
            }

            return new(chart, charts, degree, neighbours);
        }

        readonly record struct Side(int Triangle, long Key, Vector2 Low, Vector2 High) {
            const float Tolerance = 1e-5f;

            public static Side Of(int triangle, int from, int to, Vector2 start, Vector2 end) =>
                from < to
                    ? new(triangle, ((long)from << 32) | (uint)to, start, end)
                    : new(triangle, ((long)to << 32) | (uint)from, end, start);

            public bool SameLayout(in Side other) =>
                Vector2.DistanceSquared(Low, other.Low) < Tolerance * Tolerance
                && Vector2.DistanceSquared(High, other.High) < Tolerance * Tolerance;
        }
    }
}
