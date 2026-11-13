// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Terrain;

namespace Vixen.Texturing.Painting;

/// <summary>Polygon fills: which pieces of the mesh a click or an outline takes, and the placement that fills them.</summary>
public sealed partial class PaintMeshSurface {
    /// <summary>
    ///     A polygon fill: each stamp is a click, and what it fills is the piece of the mesh under it, whole; or, with an
    ///     outline, one stamp fills every piece the outline holds.
    /// </summary>
    /// <param name="mode">How much of the mesh a click takes, or each triangle an outline holds.</param>
    /// <param name="view">
    ///     The camera a click was made through, whose ray finds the triangle; null for a click in the atlas, which takes
    ///     the triangle it lands in.
    /// </param>
    /// <param name="symmetry">
    ///     The copies the fill makes of itself on the mesh, or nothing. Through a camera each copy is the camera moved
    ///     with it, as <see cref="Through" /> makes them, so a click or an outline takes what it would on the mesh moved.
    ///     In the atlas each triangle chosen also chooses the one its middle, moved, lands on.
    /// </param>
    /// <param name="outline">
    ///     An outline drawn on the stroke's picture, or null for clicks. With one, a single stamp fills everything
    ///     inside it and where the stamp lands does not matter.
    /// </param>
    /// <returns>
    ///     What a stroke places its clicks with, one stamp each
    ///     (<see cref="PaintStroke.Dab(Vector2, List{PaintRect}, PaintPen)" />).
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every texel of what is chosen, at a weight of one, whatever the brush's radius and falloff.</b>
    ///         The stroke's opacity and flow still cap it, and a stencil or a source still applies, so a fill is a
    ///         stroke like any other to the journal, the gutter and undo.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A polygon is found, not read.</b> A mesh here is triangles, so a quad or an n-gon is the triangles
    ///         joined to the one clicked across shared edges, on the same island and in the same plane. A flat area
    ///         modelled as many quads therefore fills as one polygon.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An outline holds a triangle when one of four points of it falls inside:</b> its middle, or halfway
    ///         from there to a corner. A triangle the outline only grazes at a corner is left, which is what an artist
    ///         dragging a box round a few faces means; and a large triangle the outline cuts through the middle of is
    ///         taken, though its middle may be outside. Unless <see cref="PaintFillOutline.Through" /> says so, such a
    ///         point counts only where the camera sees it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the piece under the outline's own middle is always taken</b>, when that middle is inside it: on
    ///         a low-poly model a box drawn inside one face holds none of that face's points, and an artist who drew it
    ///         there meant that face. Filling through, it is every triangle whose picture covers the middle.
    ///     </para>
    /// </remarks>
    public IPaintPlacement Fill(
        PaintFillMode mode,
        PaintView? view = null,
        PaintSymmetryGroup? symmetry = null,
        PaintFillOutline? outline = null
    ) =>
        new FillPlacement(this, mode, view, symmetry, outline);

    /// <summary>Which triangles a polygon fill takes from one triangle.</summary>
    /// <param name="triangle">The triangle clicked.</param>
    /// <param name="mode">How much of the mesh a click takes.</param>
    /// <param name="into">Cleared, then given the triangles, the clicked one first.</param>
    public void Select(int triangle, PaintFillMode mode, List<int> into) {
        ArgumentNullException.ThrowIfNull(into);
        ArgumentOutOfRangeException.ThrowIfNegative(triangle);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(triangle, Projection.Triangles);

        into.Clear();

        switch (mode) {
            case PaintFillMode.Triangle:
                into.Add(triangle);

                break;
            case PaintFillMode.Shell:
            case PaintFillMode.Part:
                into.Add(triangle);

                for (var other = 0; other < Projection.Triangles; other++) {
                    var same = mode == PaintFillMode.Shell
                        ? ChartOf(other) == ChartOf(triangle)
                        : Projection.PartOf(other) == Projection.PartOf(triangle);

                    if (other != triangle && same) {
                        into.Add(other);
                    }
                }

                break;
            default:
                Projection.Triangle(triangle, out var a, out var b, out var c, out _, out _, out _);

                var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
                var flat = Tolerance;

                Gather(
                    triangle,
                    into,
                    to => {
                        if (ChartOf(to) != ChartOf(triangle)) {
                            return false;
                        }

                        Projection.Triangle(to, out var p, out var q, out var r, out _, out _, out _);

                        return MathF.Abs(Vector3.Dot(p - a, normal)) <= flat
                            && MathF.Abs(Vector3.Dot(q - a, normal)) <= flat
                            && MathF.Abs(Vector3.Dot(r - a, normal)) <= flat;
                    }
                );

                break;
        }
    }

    /// <summary>How far apart two places on the mesh may be and still be one: a ten-thousandth of its size.</summary>
    float Tolerance {
        get {
            var bounds = Projection.Bounds;

            return Vector3.Distance(bounds.Minimum, bounds.Maximum) * 1e-4f;
        }
    }

    /// <summary>Floods from a triangle across shared edges, wherever a step is allowed.</summary>
    void Gather(int start, List<int> into, Func<int, bool> allowed) {
        var joined = topology.Value;
        HashSet<int> seen = [start];
        Queue<int> waiting = new();

        waiting.Enqueue(start);

        while (waiting.TryDequeue(out var triangle)) {
            into.Add(triangle);

            for (var at = joined.First[triangle]; at < joined.First[triangle + 1]; at++) {
                var next = joined.Neighbours[at];

                if (seen.Add(next) && allowed(next)) {
                    waiting.Enqueue(next);
                }
            }
        }
    }

    /// <summary>The four points of a triangle an outline is tested at: its middle, and halfway from there to each corner.</summary>
    static void Samples(Vector3 a, Vector3 b, Vector3 c, Span<Vector3> into) {
        var middle = (a + b + c) / 3f;

        into[0] = middle;
        into[1] = (middle + a) * 0.5f;
        into[2] = (middle + b) * 0.5f;
        into[3] = (middle + c) * 0.5f;
    }

    /// <summary>A fill: every texel weighs one, and lands on the picture where it projects or at its own centre.</summary>
    readonly struct FillWeigher(PaintView? view) : ITexelWeigher {
        public float Weigh(Vector3 point, Vector2 texel, out Vector2 picture) {
            picture = view is { } camera ? camera.Project(point) : texel;

            return 1f;
        }
    }

    sealed class FillPlacement : IPaintPlacement, IPaintAnchor {
        const int SampleCount = 4;

        readonly PaintMeshSurface surface;
        readonly PaintFillMode mode;
        readonly PaintFillOutline? outline;
        readonly PaintIsometry[] copies;

        /// <summary>The camera and each copy of it, or null in the atlas.</summary>
        readonly PaintView[]? cameras;

        /// <summary>The triangles a click or an outline lands on, each with the camera that found it.</summary>
        readonly List<(int Triangle, int Camera)> seeds = [];

        /// <summary>Every triangle the fill takes, with the camera whose picture its texels are read on.</summary>
        readonly Dictionary<int, int> taken = [];

        readonly List<int> piece = [];

        public FillPlacement(
            PaintMeshSurface surface,
            PaintFillMode mode,
            PaintView? view,
            PaintSymmetryGroup? symmetry,
            PaintFillOutline? outline
        ) {
            this.surface = surface;
            this.mode = mode;
            this.outline = outline;
            copies = [..symmetry?.Copies ?? []];
            cameras = view is { } camera ? [camera, ..copies.Select(camera.Moved)] : null;
        }

        public bool ReplacesStamp => true;

        public bool TryAtlas(Vector2 centre, out Vector2 texel) => Find(0, centre, out _, out texel);

        public void Place(PaintStamp stamp, in PaintBrush brush, in TerrainBrush kernel, PaintStampTexels into) {
            seeds.Clear();
            taken.Clear();

            if (outline is { } drawn) {
                Outlined(drawn);
            } else {
                Clicked(stamp.Centre);
            }

            // ⚠ A triangle already taken is not grown again: a part or a shell is found by walking the whole mesh,
            // and an outline round a hundred faces of one part would otherwise walk it a hundred times.
            foreach (var (seed, camera) in seeds) {
                if (taken.ContainsKey(seed)) {
                    continue;
                }

                surface.Select(seed, mode, piece);

                foreach (var each in piece) {
                    taken.TryAdd(each, camera);
                }
            }

            var everywhere = new PaintRect(0, 0, surface.width, surface.height);

            foreach (var (triangle, camera) in taken) {
                var weigher = new FillWeigher(cameras?[camera]);

                surface.Scan(triangle, everywhere, surface.ChartOf(triangle), ref weigher, into);
            }
        }

        /// <summary>The triangle under a click through each camera, or in the atlas with each copy of it.</summary>
        void Clicked(Vector2 centre) {
            if (cameras is not null) {
                for (var at = 0; at < cameras.Length; at++) {
                    if (Find(at, centre, out var triangle, out _)) {
                        seeds.Add((triangle, at));
                    }
                }

                return;
            }

            if (!Find(0, centre, out var clicked, out _)) {
                return;
            }

            seeds.Add((clicked, 0));
            surface.Locate(centre, out _, out var weights, out _);
            surface.Projection.Triangle(clicked, out var a, out var b, out var c, out _, out _, out _);
            Copied(a * weights.X + b * weights.Y + c * weights.Z);
        }

        /// <summary>Every triangle the outline holds, through each camera or in the atlas.</summary>
        void Outlined(PaintFillOutline drawn) {
            var projection = surface.Projection;
            var tolerance = surface.Tolerance;
            Span<Vector3> points = stackalloc Vector3[SampleCount];
            var middle = drawn.Middle;

            for (var at = 0; at < (cameras?.Length ?? 1); at++) {
                var camera = cameras?[at];

                // ⚠ The piece under the outline's middle as well, or a box drawn inside one large face holds none of
                // that face's points and fills nothing: a low-poly model's faces are mostly larger than such a box.
                if (middle is { } centre
                    && (camera is null || !drawn.Through)
                    && Find(at, centre, out var under, out _)) {
                    seeds.Add((under, at));
                }

                for (var triangle = 0; triangle < projection.Triangles; triangle++) {
                    projection.Triangle(triangle, out var a, out var b, out var c, out var ua, out var ub, out var uc);

                    var held = camera is { } through
                        ? Holds(drawn, through, triangle, a, b, c, points, tolerance)
                        : Holds(drawn, ua, ub, uc);

                    if (held) {
                        seeds.Add((triangle, at));
                    }
                }
            }

            if (cameras is not null) {
                return;
            }

            // In the atlas each copy is where a chosen triangle's middle goes on the mesh.
            var chosen = seeds.Count;

            for (var at = 0; at < chosen; at++) {
                projection.Triangle(seeds[at].Triangle, out var a, out var b, out var c, out _, out _, out _);
                Copied((a + b + c) / 3f);
            }
        }

        /// <summary>Whether an outline on a camera's picture holds a triangle at a point of it the camera may use.</summary>
        bool Holds(
            PaintFillOutline drawn,
            PaintView camera,
            int triangle,
            Vector3 a,
            Vector3 b,
            Vector3 c,
            Span<Vector3> points,
            float tolerance
        ) {
            if (drawn is { Through: true, Middle: { } middle } && Covers(camera, a, b, c, middle)) {
                return true;
            }

            Samples(a, b, c, points);

            foreach (var point in points) {
                if (!(camera.Depth(point) > 0f)) {
                    continue;
                }

                var picture = camera.Project(point);

                if (!drawn.Contains(picture)) {
                    continue;
                }

                if (drawn.Through) {
                    return true;
                }

                // Seen when the first thing the line of sight meets is this triangle, or something no nearer.
                if (surface.Projection.TryHit(camera.RayAt(picture), out var hit)
                    && (hit.Triangle == triangle
                        || hit.Distance >= Vector3.Distance(camera.Eye, point) - tolerance)) {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Whether a triangle in front of a camera covers a place on its picture.</summary>
        static bool Covers(PaintView camera, Vector3 a, Vector3 b, Vector3 c, Vector2 place) {
            if (!(camera.Depth(a) > 0f && camera.Depth(b) > 0f && camera.Depth(c) > 0f)) {
                return false;
            }

            var (pa, pb, pc) = (camera.Project(a), camera.Project(b), camera.Project(c));
            var area = Edge(pa, pb, pc);

            return area != 0f
                && Edge(pb, pc, place) / area >= 0f
                && Edge(pc, pa, place) / area >= 0f
                && Edge(pa, pb, place) / area >= 0f;
        }

        /// <summary>Whether an outline in the atlas holds a triangle, tested at the same points of its layout.</summary>
        bool Holds(PaintFillOutline drawn, Vector2 ua, Vector2 ub, Vector2 uc) {
            var (width, height) = (surface.width, surface.height);
            var ta = PaintProjection.Texel(ua, width, height);
            var tb = PaintProjection.Texel(ub, width, height);
            var tc = PaintProjection.Texel(uc, width, height);
            Span<Vector3> points = stackalloc Vector3[SampleCount];

            Samples(new(ta, 0f), new(tb, 0f), new(tc, 0f), points);

            foreach (var point in points) {
                if (drawn.Contains(new(point.X, point.Y))) {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The triangle each copy of a place on the mesh lands on, for a fill in the atlas.</summary>
        void Copied(Vector3 point) {
            foreach (var copy in copies) {
                var landed = surface.Projection.Nearest(copy.Point(point));

                if (landed.Found) {
                    seeds.Add((landed.Triangle, 0));
                }
            }
        }

        /// <summary>The triangle under a click: what its ray hits first, or the one it lands in on the atlas.</summary>
        bool Find(int camera, Vector2 centre, out int triangle, out Vector2 texel) {
            triangle = -1;
            texel = default;

            if (cameras is not null) {
                if (!surface.Projection.TryHit(cameras[camera].RayAt(centre), out var hit)) {
                    return false;
                }

                triangle = hit.Triangle;
                texel = PaintProjection.Texel(hit.Coordinate, surface.width, surface.height);

                return true;
            }

            // ⚠ In a triangle and not near one: a click in the gutter between islands fills nothing.
            if (!surface.Locate(centre, out triangle, out _, out var nearest)
                || Vector2.Distance(nearest, centre) > Margin) {
                return false;
            }

            texel = centre;

            return true;
        }
    }
}

/// <summary>An outline drawn on a fill's picture: the fill takes every piece of the mesh inside it.</summary>
/// <remarks>
///     <para>
///         In the units of the stroke's stamps: positions on the camera's picture for a fill through one, texels for a
///         fill in the atlas. A rectangle is an outline of four corners (<see cref="Rectangle" />), and a lasso is the
///         line the pointer drew, closed from its last point back to its first.
///     </para>
///     <para>
///         ⚠ <b>Inside is by the even-odd rule</b>, so a lasso that crosses itself leaves out the part it went round
///         twice, as a picture editor's lasso does.
///     </para>
/// </remarks>
public sealed class PaintFillOutline {
    /// <summary>The most bands an outline's height is cut into, so a long lasso keeps a small index.</summary>
    const int MostBands = 512;

    readonly Vector2[] corners;
    readonly Vector2 least;
    readonly Vector2 most;

    /// <summary>For each band of the outline's height, the edges that reach into it, each by its first corner.</summary>
    /// <remarks>
    ///     ⚠ <b>A lasso is hundreds of corners, and a fill asks this of every triangle's points.</b> Tested edge by edge
    ///     that costs corners × triangles, which grew with every corner a lasso took. A point's ray crosses only
    ///     the edges whose height spans the point's, so each band keeps those alone and a point tests a few edges.
    /// </remarks>
    readonly int[][] bands;

    readonly float bandHeight;

    /// <summary>An outline through some corners.</summary>
    /// <param name="corners">The corners in order; the outline closes itself. At least three.</param>
    /// <param name="through">
    ///     Whether what the camera cannot see inside the outline is taken as well as what it can: the far side of the
    ///     mesh, and what lies behind something nearer. A fill in the atlas has no camera, so takes everything inside.
    /// </param>
    public PaintFillOutline(IReadOnlyList<Vector2> corners, bool through = false) {
        ArgumentNullException.ThrowIfNull(corners);
        ArgumentOutOfRangeException.ThrowIfLessThan(corners.Count, 3);

        this.corners = [..corners];
        Through = through;
        least = this.corners.Aggregate(new Vector2(float.MaxValue), Vector2.Min);
        most = this.corners.Aggregate(new Vector2(float.MinValue), Vector2.Max);
        (bands, bandHeight) = Banded(this.corners, least.Y, most.Y);

        var mean = this.corners.Aggregate(Vector2.Zero, static (sum, corner) => sum + corner) / this.corners.Length;

        Middle = Contains(mean) ? mean : null;
    }

    /// <summary>The corners, in order.</summary>
    public IReadOnlyList<Vector2> Corners => corners;

    /// <summary>Whether what the camera cannot see inside the outline is taken as well as what it can.</summary>
    public bool Through { get; }

    /// <summary>The mean of the corners when it is inside, which a fill also takes the piece under; otherwise null.</summary>
    internal Vector2? Middle { get; private init; }

    /// <summary>The rectangle between two opposite corners, whichever two they are.</summary>
    /// <param name="from">One corner.</param>
    /// <param name="to">The one across from it.</param>
    /// <param name="through">Whether what the camera cannot see is taken too.</param>
    /// <returns>The outline.</returns>
    public static PaintFillOutline Rectangle(Vector2 from, Vector2 to, bool through = false) =>
        new([from, new(to.X, from.Y), to, new(from.X, to.Y)], through);

    /// <summary>Whether a point is inside, by the even-odd rule.</summary>
    /// <param name="point">The point, in the outline's units.</param>
    /// <returns>Whether it is inside.</returns>
    public bool Contains(Vector2 point) {
        if (point.X < least.X || point.Y < least.Y || point.X > most.X || point.Y > most.Y) {
            return false;
        }

        var inside = false;

        foreach (var at in bands[Band(point.Y, least.Y, bandHeight, bands.Length)]) {
            var (p, q) = (corners[at], corners[at == 0 ? corners.Length - 1 : at - 1]);

            if (p.Y > point.Y != q.Y > point.Y && point.X < (q.X - p.X) * (point.Y - p.Y) / (q.Y - p.Y) + p.X) {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>Which band a height falls in, the outline's top and bottom in the first and last.</summary>
    static int Band(float y, float top, float height, int count) =>
        height > 0f ? Math.Clamp((int)((y - top) / height), 0, count - 1) : 0;

    /// <summary>Cuts the outline's height into bands, about one for each corner, and lists each band's edges.</summary>
    static (int[][] Bands, float Height) Banded(Vector2[] corners, float top, float bottom) {
        var count = Math.Clamp(corners.Length, 1, MostBands);
        var height = (bottom - top) / count;
        var lists = new List<int>[count];

        for (var band = 0; band < count; band++) {
            lists[band] = [];
        }

        for (int at = 0, before = corners.Length - 1; at < corners.Length; before = at++) {
            var (low, high) = (MathF.Min(corners[at].Y, corners[before].Y),
                MathF.Max(corners[at].Y, corners[before].Y));

            for (int band = Band(low, top, height, count), last = Band(high, top, height, count);
                 band <= last;
                 band++) {
                lists[band].Add(at);
            }
        }

        return ([..lists.Select(static list => list.ToArray())], height);
    }
}
