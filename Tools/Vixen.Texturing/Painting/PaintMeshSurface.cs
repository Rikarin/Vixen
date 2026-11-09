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
///         ⚠ <b>Depth is a limit and then an occlusion test.</b> The limit keeps a stamp near the surface under its
///         centre, and <see cref="PaintOcclusion" /> keeps it off what the camera cannot see within that: a strap
///         lying close over a face, a lip over a recess, the far side of a silhouette the disc straddles. The test
///         is against the depth the camera sees along each line of sight, cast once per cell of the picture for
///         the whole stroke, since a stroke keeps its camera.
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

    /// <summary>The most corners a triangle can have after five planes have each added one.</summary>
    const int MaximumClipped = 8;

    readonly int width;
    readonly int height;
    readonly Lazy<Topology> topology;
    readonly Lazy<TriangleTree> layout;
    readonly Lazy<float> texelsPerUnit;

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
        texelsPerUnit = new(() => MeanDensity(projection, width, height));
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
    /// <param name="symmetry">The copies the stroke makes of itself, or nothing.</param>
    /// <param name="occlusion">
    ///     Whether a texel the camera cannot see is left alone, and how close in front of it something may be and
    ///     not hide it; null is <see cref="PaintOcclusion.Default" />.
    /// </param>
    /// <returns>What a stroke places its stamps with.</returns>
    /// <remarks>
    ///     Each copy is the camera moved with it: the picture of a point through a moved camera is the picture of
    ///     the point moved, so a stamp at one position on the picture lands on every copy at once. Each copy sees
    ///     the mesh through its own camera, so each tests what it hides on its own.
    /// </remarks>
    public IPaintPlacement Through(
        PaintView view,
        PaintSymmetryGroup? symmetry = null,
        PaintOcclusion? occlusion = null
    ) =>
        new ViewPlacement(
            this,
            [view, .. symmetry?.Copies.Select(view.Moved) ?? []],
            occlusion ?? PaintOcclusion.Default
        );

    /// <summary>A brush in the atlas, which carries on over the seams its surface crosses.</summary>
    /// <param name="symmetry">
    ///     The copies the stroke makes of itself on the mesh, or nothing. Each stands where the stamp's place on the
    ///     surface goes, on whichever island that is.
    /// </param>
    /// <returns>What a stroke places its stamps with.</returns>
    public IPaintPlacement AcrossSeams(PaintSymmetryGroup? symmetry = null) => new SeamPlacement(this, symmetry);

    /// <summary>A brush's radius in the units of a camera's picture, measured where a position on it lands.</summary>
    /// <param name="view">The camera.</param>
    /// <param name="position">Where on its picture, which is where a stroke starts.</param>
    /// <param name="radius">The radius, in <paramref name="space" />.</param>
    /// <param name="space">What the radius is measured in.</param>
    /// <returns>The radius on the picture: the radius itself for <see cref="PaintSizeSpace.Picture" />.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Measured once, where the stroke starts, and the stroke keeps it.</b> A radius worked out again per
    ///         stamp is a brush whose spacing and footprint change as it crosses a chart or a slope, which is
    ///         <c>PaintProjector</c>'s reason for deciding the radius at pointer-down.
    ///     </para>
    ///     <para>
    ///         A position that misses the mesh is measured at the depth of the mesh's centre and at its mean texel
    ///         density, so a stroke started just off the silhouette is still about the size asked for.
    ///     </para>
    /// </remarks>
    public float PictureRadius(PaintView view, Vector2 position, float radius, PaintSizeSpace space) {
        if (space == PaintSizeSpace.Picture) {
            return radius;
        }

        var ray = view.RayAt(position);
        var hit = Projection.TryHit(ray, out var found);
        var depth = hit ? view.Depth(found.Point) : view.Depth(Projection.Bounds.Center);
        var perPixel = view.PerPixel(MathF.Max(depth, 1e-6f));
        var units = space == PaintSizeSpace.Model ? radius : radius / TexelsPerUnit(hit ? found.Triangle : -1);

        return units / perPixel;
    }

    /// <summary>A brush's radius in texels, for a stroke in the atlas that starts at a texel.</summary>
    /// <param name="texel">Where the stroke starts.</param>
    /// <param name="radius">The radius, in <paramref name="space" />.</param>
    /// <param name="space">
    ///     What the radius is measured in. An atlas has no camera, so <see cref="PaintSizeSpace.Picture" /> is the
    ///     atlas itself, as for <see cref="PaintSizeSpace.Texels" />.
    /// </param>
    /// <returns>The radius in texels.</returns>
    public float AtlasRadius(Vector2 texel, float radius, PaintSizeSpace space) {
        if (space != PaintSizeSpace.Model) {
            return radius;
        }

        return radius * TexelsPerUnit(Locate(texel, out var triangle, out _, out _) ? triangle : -1);
    }

    /// <summary>How many texels one unit of the mesh spans on a triangle, or on the mesh as a whole.</summary>
    float TexelsPerUnit(int triangle) {
        if (triangle >= 0 && Projection.Density(triangle, width, height) is { IsMeasurable: true } density) {
            return density.Area;
        }

        return texelsPerUnit.Value;
    }

    /// <summary>The atlas area the mesh covers over its surface area, as a length: one for a mesh with none.</summary>
    static float MeanDensity(PaintProjection projection, int width, int height) {
        double atlas = 0;
        double surface = 0;

        for (var triangle = 0; triangle < projection.Triangles; triangle++) {
            projection.Triangle(triangle, out var a, out var b, out var c, out var ua, out var ub, out var uc);

            surface += Vector3.Cross(b - a, c - a).Length();
            atlas += MathF.Abs(Edge(ua * new Vector2(width, height), ub * new Vector2(width, height), uc * new Vector2(width, height)));
        }

        return surface > 0 && atlas > 0 ? (float)Math.Sqrt(atlas / surface) : 1f;
    }

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

                if (wa < -slackA || wb < -slackB || wc < -slackC || into.IsFull(x, y)) {
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

    /// <summary>What of a triangle a square on the picture sees, as a rectangle of the atlas.</summary>
    /// <param name="triangle">The triangle.</param>
    /// <param name="sides">The inward normals of the pyramid from the eye through the square.</param>
    /// <param name="eye">Where the eye is.</param>
    /// <param name="forward">Which way it looks.</param>
    /// <returns>The texels to scan, empty when the square sees none of it.</returns>
    /// <remarks>
    ///     ⚠ <b>The triangle clipped by the pyramid, and not a ball round the hit or the square's corners on the
    ///     plane.</b> A brush at a hard edge asks about a face seen nearly side on, which is ten radii deep under
    ///     it, and a ball that deep scanned tens of thousands of texels of a big triangle per stamp to find a
    ///     sliver. A corner of the square can look past the horizon of that same plane, so corners on the plane
    ///     are no bound there either. Clipping has no such case, and the near plane is what keeps out what is behind.
    /// </remarks>
    PaintRect Seen(int triangle, ReadOnlySpan<Vector3> sides, Vector3 eye, Vector3 forward) {
        Projection.Triangle(triangle, out var a, out var b, out var c, out var ua, out var ub, out var uc);

        Span<Clipped> polygon = stackalloc Clipped[MaximumClipped];
        Span<Clipped> next = stackalloc Clipped[MaximumClipped];

        polygon[0] = new(a, PaintProjection.Texel(ua, width, height));
        polygon[1] = new(b, PaintProjection.Texel(ub, width, height));
        polygon[2] = new(c, PaintProjection.Texel(uc, width, height));

        var count = 3;

        for (var side = 0; side <= sides.Length && count > 0; side++) {
            var inward = side < sides.Length ? sides[side] : forward;
            var written = 0;

            for (var at = 0; at < count; at++) {
                var from = polygon[at];
                var to = polygon[(at + 1) % count];
                var start = Vector3.Dot(from.Point - eye, inward);
                var end = Vector3.Dot(to.Point - eye, inward);

                if (start >= 0f) {
                    next[written++] = from;
                }

                if ((start >= 0f) != (end >= 0f)) {
                    var share = start / (start - end);

                    next[written++] = new(
                        from.Point + ((to.Point - from.Point) * share),
                        from.Texel + ((to.Texel - from.Texel) * share)
                    );
                }
            }

            count = written;
            next[..count].CopyTo(polygon);
        }

        if (count == 0) {
            return PaintRect.Empty;
        }

        var least = polygon[0].Texel;
        var most = least;

        for (var at = 1; at < count; at++) {
            least = Vector2.Min(least, polygon[at].Texel);
            most = Vector2.Max(most, polygon[at].Texel);
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

    /// <summary>A corner of a clipped triangle, on the mesh and in the atlas.</summary>
    readonly record struct Clipped(Vector3 Point, Vector2 Texel);

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
        IBrushMask? alpha,
        DepthCells? seen
    ) : ITexelWeigher {
        readonly TerrainBrush kernel = kernel;
        readonly float tangent = view.Tangent;

        public float Weigh(Vector3 point) {
            var at = view.Depth(point);

            if (MathF.Abs(at - depth) > reach) {
                return 0f;
            }

            var picture = view.Project(point, tangent);
            var weight = PaintBrush.Weight(kernel, picture, stamp, alpha);

            // Weighed first, since most of a stamp's square is outside its disc and the test costs more.
            return weight > 0f && seen is not null && !seen.Sees(picture, at) ? 0f : weight;
        }
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

    sealed class ViewPlacement(PaintMeshSurface surface, PaintView[] views, PaintOcclusion occlusion)
        : IPaintPlacement, IPaintRays {
        readonly List<int> near = [];
        readonly DepthCells?[] cells = new DepthCells?[views.Length];

        public bool ReplacesStamp => true;

        public long Rays => cells.Sum(static held => held?.Rays ?? 0L);

        public void Place(PaintStamp stamp, in PaintBrush brush, in TerrainBrush kernel, PaintStampTexels into) {
            // Each camera's pieces apart from the others', so a copy on the same island dilates on its own.
            for (var at = 0; at < views.Length; at++) {
                One(at, stamp, brush, kernel, into, surface.Charts * at);
            }
        }

        void One(
            int index,
            PaintStamp stamp,
            in PaintBrush brush,
            in TerrainBrush kernel,
            PaintStampTexels into,
            int offset
        ) {
            var camera = views[index];
            var projection = surface.Projection;
            var ray = camera.RayAt(stamp.Centre);

            if (!projection.TryHit(ray, out var hit)) {
                return;
            }

            var density = projection.Density(hit.Triangle, surface.width, surface.height);
            var depth = camera.Depth(hit.Point);
            // ⚠ The long semi-axis: an author's stretched stamp reaches √aspect radii along it, and a square on the
            // picture only a radius across clipped it to a disc's width.
            var across = stamp.Radius * MathF.Sqrt(stamp.Stretch);
            var radius = across * Corner * camera.PerPixel(depth);
            var facing = Vector3.Dot(density.Normal, ray.Direction);
            var slope = MathF.Max(MathF.Abs(facing), PaintFootprint.GrazingFloor);
            var reach = radius * MathF.Max(DepthReach, 1f / slope);
            var sphere = MathF.Sqrt((radius * radius) + (reach * reach));
            var seen = occlusion.Tests ? Cells(index, stamp.Radius) : null;

            seen?.Cover(stamp.Centre, across * Corner);

            var weigher = new ViewWeigher(camera, depth, reach, kernel, stamp, brush.Alpha, seen);

            Span<Vector3> sides = stackalloc Vector3[4];

            Pyramid(camera, stamp.Centre, across, ray.Direction, sides);
            projection.Overlapping(hit.Point, sphere, near);

            foreach (var triangle in near) {
                projection.Triangle(triangle, out var a, out var b, out var c, out _, out _, out _);

                // ⚠ Against the side the ray struck rather than a winding, so a mesh wound the other way
                // or a shell seen from inside paints the same as any other.
                var normal = Vector3.Cross(b - a, c - a);

                if (Vector3.Dot(normal, a - camera.Eye) * facing <= 0f) {
                    continue;
                }

                var visible = surface.Seen(triangle, sides, camera.Eye, camera.Forward);

                if (visible.IsEmpty) {
                    continue;
                }

                surface.Scan(
                    triangle,
                    visible,
                    surface.ChartOf(triangle) + offset,
                    ref weigher,
                    into
                );
            }
        }

        /// <summary>
        ///     The depths a camera sees, made at the stroke's first stamp. ⚠ The cell is sized once, from that stamp's
        ///     radius, and kept: a cell that changed with pressure would cast the same line again at another place.
        ///     A replay starts at the same stamp, so it sizes the same cell and agrees with itself.
        /// </summary>
        DepthCells Cells(int index, float radius) {
            var bounds = surface.Projection.Bounds;

            return cells[index] ??= new(
                surface.Projection,
                views[index],
                Math.Clamp(radius / DepthCells.CellsPerRadius, DepthCells.FinestCell, DepthCells.CoarsestCell),
                occlusion.Tolerance * Vector3.Distance(bounds.Minimum, bounds.Maximum)
            );
        }
    }

    /// <summary>
    ///     The depth of the nearest surface along the line of sight through each cell of a camera's picture, cast
    ///     once per stroke for the cells its stamps reach.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A texel is seen when it is no deeper than the deepest of the four cells around it.</b> On one
    ///         flat face the depth at a point lies between the depths at the cell centres around it, so the deepest
    ///         of them is never in front of the face. Across a silhouette the four disagree, and the test lets the
    ///         far side through within one cell of the edge, which is the cost of not casting a line per texel. The
    ///         shallowest of the four instead would hide a slope's own texels behind itself.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Kept per stroke and not per stamp.</b> Stamps a sixth of a radius apart cover each cell a dozen
    ///         times and the camera does not move during a stroke, so each line is cast once. A stamp's own square
    ///         is copied into a dense grid, so a texel's four lookups are array reads.
    ///     </para>
    /// </remarks>
    sealed class DepthCells(PaintProjection projection, PaintView view, float cell, float tolerance) {
        /// <summary>How many cells a stamp's radius spans, so a small brush is tested as finely as a big one.</summary>
        public const float CellsPerRadius = 16f;

        /// <summary>The smallest cell, in picture units, below which the lines cost more than the edges gain.</summary>
        public const float FinestCell = 0.25f;

        /// <summary>The largest cell, so a big brush still holds a silhouette to a few picture units.</summary>
        public const float CoarsestCell = 4f;

        readonly Dictionary<long, float> cast = [];
        float[] grid = [];
        int left;
        int top;
        int columns;
        int rows;

        /// <summary>How many lines have been cast.</summary>
        public long Rays { get; private set; }

        /// <summary>Makes the cells under a square of the picture ready to read.</summary>
        /// <param name="centre">The square's middle.</param>
        /// <param name="half">Half its side.</param>
        public void Cover(Vector2 centre, float half) {
            left = (int)MathF.Floor(((centre.X - half) / cell) - 1.5f);
            top = (int)MathF.Floor(((centre.Y - half) / cell) - 1.5f);
            columns = (int)MathF.Ceiling(((centre.X + half) / cell) + 1.5f) - left + 1;
            rows = (int)MathF.Ceiling(((centre.Y + half) / cell) + 1.5f) - top + 1;

            if (grid.Length < columns * rows) {
                grid = new float[columns * rows];
            }

            for (var row = 0; row < rows; row++) {
                for (var column = 0; column < columns; column++) {
                    grid[(row * columns) + column] = Depth(left + column, top + row);
                }
            }
        }

        /// <summary>Whether a point at a depth, at a place on the picture, is what the camera sees there.</summary>
        /// <param name="picture">Where it lands on the picture.</param>
        /// <param name="depth">How far along the camera's forward it is.</param>
        /// <returns>Whether nothing but the tolerance is in front of it.</returns>
        public bool Sees(Vector2 picture, float depth) {
            // The four cell centres round the point: cell n's centre is half a cell into it.
            var column = (int)MathF.Floor((picture.X / cell) - 0.5f) - left;
            var row = (int)MathF.Floor((picture.Y / cell) - 0.5f) - top;

            if (column < 0 || row < 0 || column + 1 >= columns || row + 1 >= rows) {
                return true;
            }

            var at = (row * columns) + column;
            var deepest = MathF.Max(
                MathF.Max(grid[at], grid[at + 1]),
                MathF.Max(grid[at + columns], grid[at + columns + 1])
            );

            return depth <= deepest + tolerance;
        }

        float Depth(int column, int row) {
            var key = ((long)column << 32) | (uint)row;

            if (cast.TryGetValue(key, out var held)) {
                return held;
            }

            Rays++;

            var ray = view.RayAt(new((column + 0.5f) * cell, (row + 0.5f) * cell));
            var depth = projection.TryHit(ray, out var hit) ? view.Depth(hit.Point) : float.PositiveInfinity;

            cast[key] = depth;

            return depth;
        }
    }

    /// <summary>The inward normals of the four planes from the eye through the sides of a square on the picture.</summary>
    static void Pyramid(in PaintView camera, Vector2 centre, float radius, Vector3 middle, Span<Vector3> sides) {
        Span<Vector3> corners = [
            camera.RayAt(centre + new Vector2(-radius, -radius)).Direction,
            camera.RayAt(centre + new Vector2(radius, -radius)).Direction,
            camera.RayAt(centre + new Vector2(radius, radius)).Direction,
            camera.RayAt(centre + new Vector2(-radius, radius)).Direction
        ];

        for (var side = 0; side < sides.Length; side++) {
            var normal = Vector3.Cross(corners[side], corners[(side + 1) % corners.Length]);

            sides[side] = Vector3.Dot(normal, middle) < 0f ? -normal : normal;
        }
    }

    sealed class SeamPlacement(PaintMeshSurface surface, PaintSymmetryGroup? symmetry) : IPaintPlacement {
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
            var weigher = new SeamWeigher(centre, density, nearest, kernel, stamp, brush.Alpha);

            // The stamp's own island is the stamp's, painted under it in the atlas; this carries on past its edges.
            Walk(start, centre, distance, surface.topology.Value.Chart[start], 0, ref weigher, into);

            if (symmetry is null) {
                return;
            }

            var copies = symmetry.Copies;

            for (var at = 0; at < copies.Count; at++) {
                var copy = copies[at];
                var moved = copy.Point(centre);
                var landed = projection.Nearest(moved);

                // Nothing within reach is a mesh that is not symmetric here. The walk would weigh every texel of it
                // at nothing, since a weight is measured from the copy's place and not from the surface found, so
                // this only saves the walk.
                if (!landed.Found || landed.Distance > distance) {
                    continue;
                }

                // ⚠ The frame turned rather than the point turned back: the copy weighs a point by where the
                // stroke's own stamp would have it, which keeps a mirrored alpha mirrored.
                var turned = density with {
                    Tangent = copy.Direction(density.Tangent),
                    Bitangent = copy.Direction(density.Bitangent)
                };
                var mirrored = new SeamWeigher(moved, turned, nearest, kernel, stamp, brush.Alpha);

                Walk(landed.Triangle, moved, distance, -1, surface.Charts * (at + 1), ref mirrored, into);
            }
        }

        /// <summary>
        ///     Scans the triangles joined to one within a distance of a point, every chart but <paramref name="home" />.
        /// </summary>
        void Walk(
            int start,
            Vector3 centre,
            float distance,
            int home,
            int offset,
            ref SeamWeigher weigher,
            PaintStampTexels into
        ) {
            var projection = surface.Projection;
            var limit = distance * distance;
            var topology = surface.topology.Value;

            seen.Clear();
            waiting.Clear();
            seen.Add(start);
            waiting.Enqueue(start);

            while (waiting.TryDequeue(out var triangle)) {
                if (topology.Chart[triangle] != home) {
                    surface.Scan(
                        triangle,
                        surface.Reach(triangle, centre, distance),
                        topology.Chart[triangle] + offset,
                        ref weigher,
                        into
                    );
                }

                for (var at = topology.First[triangle]; at < topology.First[triangle + 1]; at++) {
                    var next = topology.Neighbours[at];

                    if (!seen.Add(next)) {
                        continue;
                    }

                    projection.Triangle(next, out var a, out var b, out var c, out _, out _, out _);

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

/// <summary>How many lines of sight a placement has cast, which is what its occlusion test costs.</summary>
internal interface IPaintRays {
    /// <summary>The lines cast so far.</summary>
    long Rays { get; }
}

/// <summary>Whether a stamp through a camera paints only what that camera sees, and how strictly.</summary>
/// <param name="Tests">Whether a texel hidden behind other geometry is left alone.</param>
/// <param name="Tolerance">
///     How far in front of a texel something may be and still not hide it, as a share of the mesh's bounding diagonal:
///     a decal floating a hair over a face, or two faces that touch, are not each other's occluders.
/// </param>
public readonly record struct PaintOcclusion(bool Tests, float Tolerance) {
    /// <summary>What a stroke does unless asked otherwise: only what is seen, to a five-hundredth of the model's size.</summary>
    public static PaintOcclusion Default { get; } = new(true, 0.002f);

    /// <summary>Every texel within the depth under the brush, seen or not, which is what a stroke did before the test.</summary>
    public static PaintOcclusion None { get; } = new(false, 0f);
}

/// <summary>What a brush's radius is measured in.</summary>
public enum PaintSizeSpace {
    /// <summary>The units of the picture the stroke is made on: a camera's, or the atlas's for a stroke with none.</summary>
    Picture,

    /// <summary>Texels of the atlas, whatever the camera: the same brush paints the same number of texels at any zoom.</summary>
    Texels,

    /// <summary>The mesh's own units: the same brush covers the same part of the model at any zoom or density.</summary>
    Model
}
