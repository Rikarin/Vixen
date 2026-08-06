// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>An orthonormal basis at a vertex: the surface normal and two directions in its tangent plane.</summary>
/// <param name="Normal">The unit normal, area-weighted over the incident triangles.</param>
/// <param name="Tangent">A unit direction in the tangent plane, derived from the ordered ring.</param>
/// <param name="Bitangent">The third axis, <c>Normal × Tangent</c>.</param>
/// <remarks>
///     ⚠ <b>The tangent is derived from geometry rather than picked, and docs/plan/41 § D14 is why.</b>
///     A 4-RoSy cross is one angle in this frame, so which direction the frame calls zero decides what
///     the field's numbers mean. A frame seeded from an arbitrary world axis flips as a model is
///     rotated; a frame seeded from a random axis is not the same twice. This one is the first
///     neighbour of the ordered one-ring, flattened — the same neighbour on every machine, because the
///     ring order comes from the triangle order and the triangle order comes from the file.
/// </remarks>
readonly record struct TangentFrame(Vector3 Normal, Vector3 Tangent, Vector3 Bitangent);

/// <summary>A manifold triangle surface with tangent frames and ordered one-rings — the field's structure.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/41 § B5, and the resolution it states is that these are two structures for two
///         jobs.</b> Doc 24 chose an indexed face set with an explicit edge table <i>specifically</i>
///         so that non-manifold geometry is reported rather than refused, and that is right for an
///         editable mesh and wrong for a field solve. So the conversion is a stage: conditioning
///         builds this, the solver works here, and extraction hands back an <see cref="EditMesh" /> of
///         quads. <c>EditMesh</c> does not grow a half-edge mode, and the remesher does not run on
///         n-gons.
///     </para>
///     <para>
///         ⚠ <b>The one-ring is in <i>fan</i> order, and that is the whole reason this type exists
///         rather than a handful of helpers over <c>EditMesh</c>.</b>
///         <see cref="EditMesh.EdgesAt" /> returns the edges at a position in the order the edge table
///         discovered them, which is build order — correct for a valence count and useless for
///         anything angular. A cross field integrates a rotation round a vertex; a smoothing step
///         weights neighbours by the angle they subtend; a curvature estimate fits a quadric to
///         consecutive neighbours. All three read a ring that is out of order as a ring with a
///         different shape.
///     </para>
///     <para>
///         ⚠ <b>Immutable, and built in one pass from raw arrays.</b> Every table here is computed
///         once at construction from a position array and an index array. Nothing calls back into
///         <c>EditMesh</c>, whose accessors each trigger a lazy full rebuild of its adjacency — a
///         build loop that alternated <c>AddFace</c> and <c>EdgesAt</c> would pay one rebuild per
///         face.
///     </para>
/// </remarks>
sealed class ManifoldMesh {
    readonly Vector3[] positions;
    readonly int[] triangles;
    readonly int[] groups;

    // Half-edge `h` runs from `triangles[h]` to `triangles[Next(h)]`, so triangle `h / 3` owns the
    // three half-edges `3t`, `3t + 1`, `3t + 2` and no separate corner table is needed.
    readonly int[] twins;

    readonly int[] ring;
    readonly int[] ringStarts;

    readonly int[] outgoing;
    readonly int[] outgoingStarts;

    readonly Vector3[] normals;
    readonly Vector3[] tangents;
    readonly bool[] boundary;

    ManifoldMesh(
        Vector3[] positions,
        int[] triangles,
        int[] groups,
        int[] twins,
        int[] ring,
        int[] ringStarts,
        int[] outgoing,
        int[] outgoingStarts,
        Vector3[] normals,
        Vector3[] tangents,
        bool[] boundary,
        int defects
    ) {
        this.positions = positions;
        this.triangles = triangles;
        this.groups = groups;
        this.twins = twins;
        this.ring = ring;
        this.ringStarts = ringStarts;
        this.outgoing = outgoing;
        this.outgoingStarts = outgoingStarts;
        this.normals = normals;
        this.tangents = tangents;
        this.boundary = boundary;

        Defects = defects;
    }

    /// <summary>The vertices.</summary>
    public ReadOnlySpan<Vector3> Positions => positions;

    /// <summary>Three vertex indices per triangle, in winding order.</summary>
    public ReadOnlySpan<int> Triangles => triangles;

    /// <summary>How many vertices there are.</summary>
    public int VertexCount => positions.Length;

    /// <summary>How many triangles there are.</summary>
    public int TriangleCount => triangles.Length / 3;

    /// <summary>How many edges the twin table could not pair, which on conditioned input is zero.</summary>
    /// <remarks>
    ///     ⚠ <b>Reported rather than thrown, which is doc 24's rule kept.</b> A view built directly
    ///     from unconditioned input is a legitimate thing to want — the conditioner itself builds one
    ///     to measure what it is about to fix. A non-zero here means some edge had three faces or two
    ///     faces walking it the same way, and every such edge was treated as a boundary: the ring
    ///     round it is a fan rather than a cycle, which is wrong but is not a crash.
    /// </remarks>
    public int Defects { get; }

    /// <summary>The box round every vertex.</summary>
    public BoundingBox Bounds => positions.Length == 0 ? default : BoundingBox.FromPoints(positions);

    /// <summary>The bounding box's diagonal — the length every relative tolerance is a fraction of.</summary>
    public float Diagonal {
        get {
            var extent = Bounds;

            return (extent.Maximum - extent.Minimum).Length();
        }
    }

    /// <summary>A vertex's area-weighted unit normal, or zero where every incident triangle is a sliver.</summary>
    /// <param name="vertex">Its index.</param>
    /// <returns>The normal.</returns>
    public Vector3 VertexNormal(int vertex) => normals[vertex];

    /// <summary>A vertex's orthonormal frame.</summary>
    /// <param name="vertex">Its index.</param>
    /// <returns>The frame. See <see cref="TangentFrame" /> for why the tangent is where it is.</returns>
    public TangentFrame Frame(int vertex) {
        var normal = normals[vertex];
        var tangent = tangents[vertex];

        return new(normal, tangent, Vector3.Cross(normal, tangent));
    }

    /// <summary>A vertex's neighbours, in fan order round it.</summary>
    /// <param name="vertex">Its index.</param>
    /// <returns>
    ///     The neighbouring vertex indices. For an interior vertex the ring closes, so the last
    ///     neighbour is adjacent to the first; for a boundary vertex it does not, and the two ends are
    ///     the two boundary edges.
    /// </returns>
    public ReadOnlySpan<int> Ring(int vertex) => ring.AsSpan(ringStarts[vertex], ringStarts[vertex + 1] - ringStarts[vertex]);

    /// <summary>How many neighbours a vertex has.</summary>
    /// <param name="vertex">Its index.</param>
    /// <returns>Its valence. Six is regular on a triangle mesh, which is what the pre-remesh flips toward.</returns>
    public int Valence(int vertex) => ringStarts[vertex + 1] - ringStarts[vertex];

    /// <summary>Whether a vertex sits on an open rim.</summary>
    /// <param name="vertex">Its index.</param>
    /// <returns>Whether any incident edge has one triangle rather than two.</returns>
    public bool IsBoundary(int vertex) => boundary[vertex];

    /// <summary>A triangle's three corners.</summary>
    /// <param name="triangle">Its index.</param>
    /// <returns>Three vertex indices, in winding order.</returns>
    public ReadOnlySpan<int> Corners(int triangle) => triangles.AsSpan(triangle * 3, 3);

    /// <summary>Which group a triangle belongs to, carried from the source.</summary>
    /// <param name="triangle">Its index.</param>
    /// <returns>The group.</returns>
    public int Group(int triangle) => groups[triangle];

    /// <summary>The half-edge running the other way along the same edge.</summary>
    /// <param name="half">A half-edge: triangle <c>h / 3</c>, side <c>h % 3</c>.</param>
    /// <returns>Its twin, or <c>-1</c> where the edge has one triangle.</returns>
    public int Twin(int half) => twins[half];

    /// <summary>Every half-edge leaving a vertex, and so every triangle that touches it.</summary>
    /// <param name="vertex">Its index.</param>
    /// <returns>The half-edge indices; triangle <c>h / 3</c> owns each.</returns>
    /// <remarks>
    ///     ⚠ In build order rather than fan order — <see cref="Ring" /> is the ordered one. This is
    ///     the query a collapse's safety check wants, which asks "every triangle at this vertex" and
    ///     does not care in what sequence; walking the whole triangle table for it instead is what
    ///     makes a pre-remesh quadratic in the mesh.
    /// </remarks>
    public ReadOnlySpan<int> Outgoing(int vertex) =>
        outgoing.AsSpan(outgoingStarts[vertex], outgoingStarts[vertex + 1] - outgoingStarts[vertex]);

    /// <summary>The triangle across one of a triangle's three sides.</summary>
    /// <param name="triangle">Its index.</param>
    /// <param name="side">Which side: <c>0</c> runs from corner 0 to corner 1, and so on round.</param>
    /// <returns>The neighbouring triangle, or <c>-1</c> where that side is a boundary.</returns>
    public int Adjacent(int triangle, int side) {
        var twin = twins[(triangle * 3) + side];

        return twin < 0 ? -1 : twin / 3;
    }

    /// <summary>A triangle's unit normal, from its winding.</summary>
    /// <param name="triangle">Its index.</param>
    /// <returns>The normal, or zero for a triangle with no area.</returns>
    /// <remarks>
    ///     ⚠ Against zero rather than against an absolute epsilon, which is the guard
    ///     <see cref="EditMesh.Normal" /> records the capsule's poles for: a fixed tolerance is a
    ///     statement about how big a triangle has to be to count, and a primitive built at a tenth of
    ///     the scale would have all of them declared degenerate.
    /// </remarks>
    public Vector3 TriangleNormal(int triangle) {
        var cross = Cross(triangle);

        return cross.LengthSquared() > 0f ? Vector3.Normalize(cross) : Vector3.Zero;
    }

    /// <summary>Twice a triangle's area, as a vector.</summary>
    /// <param name="triangle">Its index.</param>
    /// <returns>The cross product of two of its edges.</returns>
    public Vector3 Cross(int triangle) {
        var a = positions[triangles[(triangle * 3) + 0]];
        var b = positions[triangles[(triangle * 3) + 1]];
        var c = positions[triangles[(triangle * 3) + 2]];

        return Vector3.Cross(b - a, c - a);
    }

    /// <summary>The whole surface's area.</summary>
    /// <returns>The sum over every triangle.</returns>
    public float Area() {
        var total = 0f;

        for (var triangle = 0; triangle < TriangleCount; triangle++) {
            total += Cross(triangle).Length() * 0.5f;
        }

        return total;
    }

    /// <summary>The way back out: an <see cref="EditMesh" /> of the same triangles over the same positions.</summary>
    /// <returns>The mesh, with each triangle in the group it came in with.</returns>
    /// <remarks>
    ///     ⚠ <b>Positions are added one by one rather than through
    ///     <see cref="EditMesh.FromTriangles" />, and the difference is load-bearing.</b>
    ///     <c>FromTriangles</c> welds, and welding is exactly what undoes the repair in
    ///     docs/plan/41 § D3 step 4: a non-manifold edge is repaired by <i>cutting</i>, which puts two
    ///     coincident positions where there was one, and any weld tolerance at all merges them
    ///     straight back into the edge that was just fixed.
    /// </remarks>
    public EditMesh ToEditMesh() {
        var mesh = new EditMesh();

        foreach (var position in positions) {
            mesh.AddPosition(position);
        }

        Span<int> loop = stackalloc int[3];

        for (var triangle = 0; triangle < TriangleCount; triangle++) {
            loop[0] = triangles[(triangle * 3) + 0];
            loop[1] = triangles[(triangle * 3) + 1];
            loop[2] = triangles[(triangle * 3) + 2];

            mesh.AddFace(loop, groups[triangle]);
        }

        return mesh;
    }

    /// <summary>Builds the view from a soup, in one pass over its arrays.</summary>
    /// <param name="soup">The positions and triangles. Neither is retained.</param>
    /// <returns>The view.</returns>
    /// <exception cref="ArgumentException">The index list is not a whole number of triangles.</exception>
    public static ManifoldMesh Build(TriangleSoup soup) {
        ArgumentNullException.ThrowIfNull(soup);

        if (soup.Triangles.Count % 3 != 0) {
            throw new ArgumentException(
                $"A triangle list wants a multiple of three indices and has {soup.Triangles.Count}.",
                nameof(soup)
            );
        }

        return Build([.. soup.Positions], [.. soup.Triangles], [.. soup.Groups]);
    }

    /// <summary>Builds the view from raw arrays, which the constructor takes ownership of.</summary>
    /// <param name="points">The vertices.</param>
    /// <param name="indices">Three per triangle.</param>
    /// <param name="faceGroups">One per triangle, or empty for all-zero.</param>
    /// <returns>The view.</returns>
    public static ManifoldMesh Build(Vector3[] points, int[] indices, int[] faceGroups) {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(indices);
        ArgumentNullException.ThrowIfNull(faceGroups);

        var count = indices.Length / 3;
        var groups = faceGroups.Length == count ? faceGroups : new int[count];

        var twins = Twins(points.Length, indices, out var defects);
        var built = Rings(points.Length, indices, twins);
        var (normals, tangents) = Frames(points, indices, built.Ring, built.Starts);

        return new(
            points,
            indices,
            groups,
            twins,
            built.Ring,
            built.Starts,
            built.Outgoing,
            built.OutgoingStarts,
            normals,
            tangents,
            built.Boundary,
            defects
        );
    }

    /// <summary>The half-edge opposite each half-edge, or <c>-1</c>.</summary>
    /// <remarks>
    ///     Keyed on the ordered pair the half-edge runs <i>backwards</i> along, so two triangles are
    ///     joined only when they walk their shared edge in opposite directions. That makes an
    ///     inconsistently wound pair report as two boundaries rather than pair up into a ring that
    ///     turns inside out halfway round — which is a failure a valence count cannot see and a
    ///     smoothing pass turns into noise.
    /// </remarks>
    static int[] Twins(int vertexCount, int[] indices, out int defects) {
        var twins = new int[indices.Length];

        Array.Fill(twins, -1);

        var seen = new Dictionary<long, int>(indices.Length);
        var unpaired = 0;

        for (var half = 0; half < indices.Length; half++) {
            var from = indices[half];
            var to = indices[Next(half)];

            if (from == to || (uint) from >= (uint) vertexCount || (uint) to >= (uint) vertexCount) {
                unpaired++;
                continue;
            }

            // The opposite direction is what a manifold neighbour walks, so look for it rather than
            // for the same one.
            if (seen.Remove(Key(to, from), out var other)) {
                twins[half] = other;
                twins[other] = half;

                continue;
            }

            if (!seen.TryAdd(Key(from, to), half)) {
                // A third face on the edge, or a second walking it the same way. Left as a boundary.
                unpaired++;
            }
        }

        defects = unpaired + seen.Count;
        return twins;
    }

    /// <summary>The ordered one-ring of every vertex, flattened, and which vertices sit on a rim.</summary>
    /// <remarks>
    ///     ⚠ <b>The walk is <c>twin(prev(h))</c>, and a boundary fan is started from its open end.</b>
    ///     For a half-edge <c>h</c> running <c>v → a</c> in the triangle <c>(v, a, b)</c>,
    ///     <c>prev(h)</c> runs <c>b → v</c> and its twin runs <c>v → b</c> — one step round the fan,
    ///     in the winding's own direction. Rotating <i>backwards</i> is <c>next(twin(h))</c> and stops
    ///     where <c>h</c> has no twin, so a vertex with an open side is started from the half-edge
    ///     that has none. Starting anywhere else on an open fan yields a ring that begins in the
    ///     middle and is missing its far half.
    /// </remarks>
    static (int[] Ring, int[] Starts, int[] Outgoing, int[] OutgoingStarts, bool[] Boundary) Rings(
        int vertexCount,
        int[] indices,
        int[] twins
    ) {
        var boundary = new bool[vertexCount];

        for (var half = 0; half < indices.Length; half++) {
            if (twins[half] >= 0) {
                continue;
            }

            var from = indices[half];
            var to = indices[Next(half)];

            if ((uint) from < (uint) vertexCount) {
                boundary[from] = true;
            }

            if ((uint) to < (uint) vertexCount) {
                boundary[to] = true;
            }
        }

        // Outgoing half-edges per vertex, as a counting sort into flat runs — the same shape
        // `EditMesh.Incidence` uses and for the same reason: a list per vertex is one allocation per
        // vertex on a mesh that has two million of them.
        var outStarts = new int[vertexCount + 1];

        foreach (var index in indices) {
            if ((uint) index < (uint) vertexCount) {
                outStarts[index + 1]++;
            }
        }

        for (var index = 1; index <= vertexCount; index++) {
            outStarts[index] += outStarts[index - 1];
        }

        var outgoing = new int[outStarts[vertexCount]];
        var cursor = new int[vertexCount];

        for (var half = 0; half < indices.Length; half++) {
            var from = indices[half];

            if ((uint) from >= (uint) vertexCount) {
                continue;
            }

            outgoing[outStarts[from] + cursor[from]] = half;
            cursor[from]++;
        }

        var ring = new List<int>(indices.Length);
        var starts = new int[vertexCount + 1];

        for (var vertex = 0; vertex < vertexCount; vertex++) {
            starts[vertex] = ring.Count;

            var low = outStarts[vertex];
            var high = outStarts[vertex + 1];

            if (low == high) {
                continue;
            }

            var start = outgoing[low];

            for (var at = low; at < high; at++) {
                if (twins[outgoing[at]] < 0) {
                    start = outgoing[at];
                    break;
                }
            }

            var half = start;
            var steps = 0;

            while (true) {
                ring.Add(indices[Next(half)]);

                var back = Previous(half);
                var twin = twins[back];

                if (twin < 0) {
                    // The fan is open, and the last triangle's third corner is the far end of it.
                    ring.Add(indices[back]);
                    break;
                }

                half = twin;

                // A closed fan comes back to where it started. The step count is the guard for a
                // vertex whose fans do not agree — a bowtie the repair step has not run on yet.
                if (half == start || ++steps > high - low) {
                    break;
                }
            }
        }

        starts[vertexCount] = ring.Count;
        return ([.. ring], starts, outgoing, outStarts, boundary);
    }

    /// <summary>A normal and a tangent per vertex.</summary>
    static (Vector3[] Normals, Vector3[] Tangents) Frames(
        Vector3[] points,
        int[] indices,
        int[] ring,
        int[] ringStarts
    ) {
        var normals = new Vector3[points.Length];
        var tangents = new Vector3[points.Length];

        // ⚠ Area-weighted, by summing the raw cross products rather than the normalised ones. On
        // marching-cubes output a large share of the triangles are slivers whose normal is noise; an
        // unweighted average gives each of them the same vote as the real surface round them, and the
        // frame the field is solved in inherits the noise.
        for (var triangle = 0; triangle * 3 < indices.Length; triangle++) {
            var ia = indices[(triangle * 3) + 0];
            var ib = indices[(triangle * 3) + 1];
            var ic = indices[(triangle * 3) + 2];

            var cross = Vector3.Cross(points[ib] - points[ia], points[ic] - points[ia]);

            normals[ia] += cross;
            normals[ib] += cross;
            normals[ic] += cross;
        }

        for (var vertex = 0; vertex < points.Length; vertex++) {
            normals[vertex] = normals[vertex].LengthSquared() > 0f
                ? Vector3.Normalize(normals[vertex])
                : Vector3.Zero;

            tangents[vertex] = Tangent(points, ring, ringStarts, normals[vertex], vertex);
        }

        return (normals, tangents);
    }

    /// <summary>The first ring neighbour, flattened into the tangent plane and normalised.</summary>
    static Vector3 Tangent(Vector3[] points, int[] ring, int[] ringStarts, Vector3 normal, int vertex) {
        if (normal.LengthSquared() <= 0f) {
            return Vector3.UnitX;
        }

        if (ringStarts[vertex + 1] > ringStarts[vertex]) {
            var along = points[ring[ringStarts[vertex]]] - points[vertex];
            var flat = along - (normal * Vector3.Dot(along, normal));

            if (flat.LengthSquared() > 0f) {
                return Vector3.Normalize(flat);
            }
        }

        // An isolated vertex, or one whose first neighbour is exactly along the normal. The world
        // axis least parallel to the normal cannot collapse under the cross, which is the same guard
        // `EditMesh.Triangulate` uses to pick a plane.
        var guide = MathF.Abs(normal.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;

        return Vector3.Normalize(Vector3.Cross(normal, guide));
    }

    /// <summary>The next half-edge round the same triangle.</summary>
    internal static int Next(int half) => half - (half % 3) + ((half + 1) % 3);

    /// <summary>The previous one.</summary>
    internal static int Previous(int half) => half - (half % 3) + ((half + 2) % 3);

    /// <summary>An ordered vertex pair as one key, so the twin lookup is a dictionary of longs.</summary>
    static long Key(int from, int to) => ((long) from << 32) | (uint) to;
}
