// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>Split long, collapse short, flip toward valence six, relax — and reproject every round.</summary>
/// <remarks>
///     <para>
///         <b>Step six of docs/plan/41 § D3, and the step that makes marching-cubes soup workable.</b>
///         Generated meshes carry staircase noise at the voxel frequency. A curvature estimate on a
///         staircase mesh is a curvature estimate <i>of the staircase</i> — the cross field aligns to
///         the cell boundaries, the singularities land on them, and every stage after inherits it.
///         Botsch and Kobbelt, <i>A Remeshing Approach to Multiresolution Modeling</i> (2004), is the
///         four-operator loop; QuadWild's <c>do_remesh</c> exists for the same reason.
///     </para>
///     <para>
///         ⚠ <b>The relaxation is reprojected onto the original surface every round, and without that
///         the step is a shrink.</b> Tangential relaxation moves a vertex toward the centroid of its
///         ring, and on a convex surface that centroid is <i>inside</i> the surface. Five rounds of it
///         visibly deflates a sphere and rounds every hard corner off a boolean result. The mesh
///         becomes beautifully isotropic and stops being the model.
///         <see cref="Run" />'s <c>reproject</c> parameter is how the test that asserts this proves
///         it can fail.
///     </para>
///     <para>
///         ⚠ <b>Boundary vertices are frozen — never collapsed, never relaxed, never reprojected.</b>
///         <see cref="RemeshSettings.FreezeBorder" /> defaults to true, and the rim of an open
///         surface is a feature by construction. Splitting a boundary edge is allowed, because a
///         midpoint of a straight segment is still on the segment; moving one is not, because a
///         projection would pull it onto the nearest interior triangle and eat the rim.
///     </para>
/// </remarks>
static class IsotropicRemesh {
    /// <summary>The upper edge-length band, as a multiple of the target: above this an edge splits.</summary>
    /// <remarks>Four thirds and four fifths are Botsch and Kobbelt's, and the pair is chosen so a
    ///     split cannot immediately produce an edge short enough to be collapsed back.</remarks>
    public const float SplitAbove = 4f / 3f;

    /// <summary>The lower band: below this an edge collapses.</summary>
    public const float CollapseBelow = 4f / 5f;

    /// <summary>How far a relaxed vertex moves toward its ring's centroid each round.</summary>
    public const float Relaxation = 0.5f;

    /// <summary>Runs the loop in place.</summary>
    /// <param name="soup">The mesh, rewritten.</param>
    /// <param name="reference">The surface to reproject onto — the input as it stood before this step.</param>
    /// <param name="target">The edge length to aim for. Zero or less means the input's own mean.</param>
    /// <param name="iterations">How many rounds. Zero does nothing.</param>
    /// <param name="reproject">
    ///     Whether the relaxation is reprojected. ⚠ Always true in the pipeline. It is a parameter so
    ///     that the test asserting the deviation bound can be shown to fail without it — a bound that
    ///     passes whether or not the code under it runs is not a test.
    /// </param>
    /// <returns>The target edge length actually used.</returns>
    public static float Run(
        TriangleSoup soup,
        SurfaceProjector reference,
        float target,
        int iterations,
        bool reproject = true
    ) {
        ArgumentNullException.ThrowIfNull(soup);
        ArgumentNullException.ThrowIfNull(reference);

        if (iterations <= 0 || soup.TriangleCount == 0) {
            return target;
        }

        if (target <= 0f) {
            target = MeanEdgeLength(soup);
        }

        if (target <= 0f) {
            // Every edge has zero length, which is a pile of degenerate triangles and not something
            // a target edge length is a meaningful thing to have for.
            return 0f;
        }

        for (var round = 0; round < iterations; round++) {
            Split(soup, target * SplitAbove);
            Collapse(soup, target * CollapseBelow);
            Flip(soup);
            Relax(soup, reference, reproject);
        }

        return target;
    }

    /// <summary>The mean length of the mesh's edges, counting each once.</summary>
    /// <param name="soup">The mesh.</param>
    /// <returns>The mean, or zero when there are no edges with length.</returns>
    /// <remarks>
    ///     ⚠ The default target when the caller names none, and it is a length taken <i>from the
    ///     mesh</i> — so the whole step is scale-free, which is what lets the same input at a
    ///     thousandth and a thousand times the size condition to the same topology.
    /// </remarks>
    public static float MeanEdgeLength(TriangleSoup soup) {
        ArgumentNullException.ThrowIfNull(soup);

        var view = ManifoldMesh.Build(soup);
        var total = 0d;
        var count = 0;

        for (var vertex = 0; vertex < view.VertexCount; vertex++) {
            foreach (var neighbour in view.Ring(vertex)) {
                if (neighbour > vertex) {
                    total += Vector3.Distance(view.Positions[vertex], view.Positions[neighbour]);
                    count++;
                }
            }
        }

        return count == 0 ? 0f : (float) (total / count);
    }

    /// <summary>Inserts a midpoint into every edge longer than the bound, in one pass over the triangles.</summary>
    /// <remarks>
    ///     ⚠ <b>Every long edge is marked first and the triangle table is rebuilt once.</b> Splitting
    ///     one edge at a time means every neighbouring triangle is rewritten as many times as it has
    ///     long edges, and the adjacency has to be valid in between — which is the version of this
    ///     that is three times the code and quadratic.
    /// </remarks>
    static void Split(TriangleSoup soup, float bound) {
        var view = ManifoldMesh.Build(soup);
        var count = view.TriangleCount;
        var midpoints = new int[count * 3];

        Array.Fill(midpoints, -1);

        var any = false;

        for (var half = 0; half < count * 3; half++) {
            var twin = view.Twin(half);

            // One side of each edge decides, so the two triangles get the same new vertex.
            if (twin >= 0 && twin < half) {
                continue;
            }

            var from = view.Triangles[half];
            var to = view.Triangles[ManifoldMesh.Next(half)];

            if (Vector3.Distance(soup.Positions[from], soup.Positions[to]) <= bound) {
                continue;
            }

            var index = soup.Positions.Count;

            soup.Positions.Add((soup.Positions[from] + soup.Positions[to]) * 0.5f);

            midpoints[half] = index;

            if (twin >= 0) {
                midpoints[twin] = index;
            }

            any = true;
        }

        if (!any) {
            return;
        }

        var triangles = soup.Triangles.ToArray();
        var groups = soup.Groups.ToArray();

        soup.Triangles.Clear();
        soup.Groups.Clear();

        for (var triangle = 0; triangle < count; triangle++) {
            Emit(
                soup,
                triangles[(triangle * 3) + 0],
                triangles[(triangle * 3) + 1],
                triangles[(triangle * 3) + 2],
                midpoints[(triangle * 3) + 0],
                midpoints[(triangle * 3) + 1],
                midpoints[(triangle * 3) + 2],
                groups[triangle]
            );
        }
    }

    /// <summary>The one, two, three or four triangles a split triangle becomes.</summary>
    /// <remarks>
    ///     ⚠ <b>Rotated into canonical position rather than written out eight times.</b> Rotating the
    ///     corners <c>(a, b, c) → (b, c, a)</c> rotates the midpoints the same way and preserves the
    ///     winding, so the one-midpoint case and the two-midpoint case each need writing once. Eight
    ///     hand-written cases is where a reversed triangle hides.
    /// </remarks>
    static void Emit(TriangleSoup soup, int a, int b, int c, int ab, int bc, int ca, int group) {
        var marked = (ab >= 0 ? 1 : 0) + (bc >= 0 ? 1 : 0) + (ca >= 0 ? 1 : 0);

        switch (marked) {
            case 0:
                soup.Add(a, b, c, group);
                return;

            case 3:
                soup.Add(a, ab, ca, group);
                soup.Add(ab, b, bc, group);
                soup.Add(ca, bc, c, group);
                soup.Add(ab, bc, ca, group);

                return;

            case 1:
                while (ab < 0) {
                    (a, b, c, ab, bc, ca) = (b, c, a, bc, ca, ab);
                }

                soup.Add(a, ab, c, group);
                soup.Add(ab, b, c, group);

                return;

            default:
                while (ca >= 0) {
                    (a, b, c, ab, bc, ca) = (b, c, a, bc, ca, ab);
                }

                // The corner b is cut off, and what is left is the quad (a, ab, bc, c). Its shorter
                // diagonal is the one that splits it — the longer one produces two slivers, which the
                // very next round would collapse back out.
                soup.Add(ab, b, bc, group);

                if (Vector3.DistanceSquared(soup.Positions[a], soup.Positions[bc])
                    <= Vector3.DistanceSquared(soup.Positions[ab], soup.Positions[c])) {
                    soup.Add(a, ab, bc, group);
                    soup.Add(a, bc, c, group);
                } else {
                    soup.Add(ab, bc, c, group);
                    soup.Add(ab, c, a, group);
                }

                return;
        }
    }

    /// <summary>Collapses every edge shorter than the bound that it is safe to collapse.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The link condition is checked, and it is not optional.</b> Collapsing an edge
    ///         whose two endpoints share a neighbour that is not one of the two opposite corners
    ///         welds two sheets of the surface together — it changes the genus, produces a
    ///         non-manifold edge, and does it silently on geometry that looked fine. Dey et al.,
    ///         <i>Topology preserving edge contraction</i> (1999).
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One collapse per neighbourhood per round.</b> The decision is taken against the
    ///         view built at the start of the round, so a second collapse touching the same one-ring
    ///         would be reasoning about a ring that no longer exists. Locking both endpoints and both
    ///         their rings is what makes the round's result independent of the order it happened to
    ///         visit edges in — which is docs/plan/41 § D14's determinism, one stage early.
    ///     </para>
    /// </remarks>
    static void Collapse(TriangleSoup soup, float bound) {
        var view = ManifoldMesh.Build(soup);
        var count = view.TriangleCount;

        var remap = new int[view.VertexCount];
        var locked = new bool[view.VertexCount];

        for (var vertex = 0; vertex < remap.Length; vertex++) {
            remap[vertex] = vertex;
        }

        var any = false;
        var shared = new HashSet<int>();

        for (var half = 0; half < count * 3; half++) {
            var twin = view.Twin(half);

            if (twin < 0 || twin < half) {
                continue;
            }

            var a = view.Triangles[half];
            var b = view.Triangles[ManifoldMesh.Next(half)];

            if (locked[a] || locked[b] || view.IsBoundary(a) || view.IsBoundary(b)) {
                continue;
            }

            if (Vector3.Distance(soup.Positions[a], soup.Positions[b]) >= bound) {
                continue;
            }

            shared.Clear();

            foreach (var neighbour in view.Ring(a)) {
                shared.Add(neighbour);
            }

            var common = 0;

            foreach (var neighbour in view.Ring(b)) {
                if (shared.Contains(neighbour)) {
                    common++;
                }
            }

            // Two, and exactly two: the opposite corners of the edge's two triangles. Anything else
            // and the collapse would pinch.
            if (common != 2) {
                continue;
            }

            var midpoint = (soup.Positions[a] + soup.Positions[b]) * 0.5f;

            if (!Safe(soup, view, a, b, midpoint) || !Safe(soup, view, b, a, midpoint)) {
                continue;
            }

            soup.Positions[a] = midpoint;
            remap[b] = a;

            locked[a] = true;
            locked[b] = true;

            foreach (var neighbour in view.Ring(a)) {
                locked[neighbour] = true;
            }

            foreach (var neighbour in view.Ring(b)) {
                locked[neighbour] = true;
            }

            any = true;
        }

        if (!any) {
            return;
        }

        Rewrite(soup, remap);
    }

    /// <summary>Whether moving one end of an edge onto the midpoint turns any triangle inside out.</summary>
    /// <remarks>
    ///     ⚠ The link condition is about topology and says nothing about geometry: a collapse that
    ///     preserves the genus can still fold a triangle through its neighbours, which produces a
    ///     surface with the right connectivity and the wrong shape. The normals are compared as
    ///     directions rather than as areas, so a sliver whose area is nearly nothing but whose facing
    ///     is right is not rejected for being small.
    /// </remarks>
    static bool Safe(TriangleSoup soup, ManifoldMesh view, int moving, int other, Vector3 to) {
        foreach (var half in view.Outgoing(moving)) {
            var triangle = half / 3;
            var corners = view.Corners(triangle);

            if (corners[0] == other || corners[1] == other || corners[2] == other) {
                // One of the two triangles the collapse removes.
                continue;
            }

            var a = corners[0] == moving ? to : soup.Positions[corners[0]];
            var b = corners[1] == moving ? to : soup.Positions[corners[1]];
            var c = corners[2] == moving ? to : soup.Positions[corners[2]];

            var after = Vector3.Cross(b - a, c - a);
            var before = view.Cross(triangle);

            if (after.LengthSquared() <= 0f || Vector3.Dot(after, before) <= 0f) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Flips every interior edge that moves its four vertices closer to their ideal valence.</summary>
    /// <remarks>
    ///     ⚠ <b>Six inside and four on a rim.</b> A boundary vertex with six neighbours is a boundary
    ///     vertex whose triangles are crammed into a half-plane; the regular valence for one is four,
    ///     and using six for every vertex is what makes the rim of an open surface visibly ripple.
    /// </remarks>
    static void Flip(TriangleSoup soup) {
        var view = ManifoldMesh.Build(soup);
        var count = view.TriangleCount;

        var triangles = soup.Triangles.ToArray();
        var locked = new bool[view.VertexCount];
        var valence = new int[view.VertexCount];

        for (var vertex = 0; vertex < valence.Length; vertex++) {
            valence[vertex] = view.Valence(vertex);
        }

        var any = false;
        var neighbours = new HashSet<int>();

        for (var half = 0; half < count * 3; half++) {
            var twin = view.Twin(half);

            if (twin < 0 || twin < half) {
                continue;
            }

            var a = view.Triangles[half];
            var b = view.Triangles[ManifoldMesh.Next(half)];
            var c = view.Triangles[ManifoldMesh.Previous(half)];
            var d = view.Triangles[ManifoldMesh.Previous(twin)];

            if (locked[a] || locked[b] || locked[c] || locked[d]) {
                continue;
            }

            if (c == d || valence[a] - 1 < 3 || valence[b] - 1 < 3) {
                continue;
            }

            // An edge between c and d already exists, so flipping onto it would give that edge three
            // faces — a non-manifold edge manufactured by the step that is meant to be tidying up.
            neighbours.Clear();

            foreach (var neighbour in view.Ring(c)) {
                neighbours.Add(neighbour);
            }

            if (neighbours.Contains(d)) {
                continue;
            }

            var before = Deviation(view, valence, a) + Deviation(view, valence, b)
                + Deviation(view, valence, c) + Deviation(view, valence, d);

            var after = Deviation(view, valence, a, -1) + Deviation(view, valence, b, -1)
                + Deviation(view, valence, c, 1) + Deviation(view, valence, d, 1);

            if (after >= before) {
                continue;
            }

            // The quad is (c, a, d, b) and the flip is its other diagonal. Both halves have to keep
            // facing the way the two triangles they replace did.
            var first = Vector3.Cross(
                soup.Positions[a] - soup.Positions[c],
                soup.Positions[d] - soup.Positions[c]
            );

            var second = Vector3.Cross(
                soup.Positions[d] - soup.Positions[c],
                soup.Positions[b] - soup.Positions[c]
            );

            var left = view.Cross(half / 3);
            var right = view.Cross(twin / 3);

            if (first.LengthSquared() <= 0f || second.LengthSquared() <= 0f) {
                continue;
            }

            if (Vector3.Dot(first, left) <= 0f || Vector3.Dot(second, right) <= 0f) {
                continue;
            }

            triangles[(half / 3 * 3) + 0] = c;
            triangles[(half / 3 * 3) + 1] = a;
            triangles[(half / 3 * 3) + 2] = d;

            triangles[(twin / 3 * 3) + 0] = c;
            triangles[(twin / 3 * 3) + 1] = d;
            triangles[(twin / 3 * 3) + 2] = b;

            valence[a]--;
            valence[b]--;
            valence[c]++;
            valence[d]++;

            locked[a] = true;
            locked[b] = true;
            locked[c] = true;
            locked[d] = true;

            any = true;
        }

        if (!any) {
            return;
        }

        soup.Triangles.Clear();
        soup.Triangles.AddRange(triangles);
    }

    static int Deviation(ManifoldMesh view, int[] valence, int vertex, int change = 0) =>
        Math.Abs(valence[vertex] + change - (view.IsBoundary(vertex) ? 4 : 6));

    /// <summary>Moves every interior vertex toward its ring's centroid, in the tangent plane, and reprojects.</summary>
    static void Relax(TriangleSoup soup, SurfaceProjector reference, bool reproject) {
        var view = ManifoldMesh.Build(soup);
        var moved = new Vector3[view.VertexCount];

        for (var vertex = 0; vertex < view.VertexCount; vertex++) {
            var point = view.Positions[vertex];

            moved[vertex] = point;

            if (view.IsBoundary(vertex)) {
                continue;
            }

            var ring = view.Ring(vertex);

            if (ring.Length == 0) {
                continue;
            }

            var centroid = Vector3.Zero;

            foreach (var neighbour in ring) {
                centroid += view.Positions[neighbour];
            }

            centroid /= ring.Length;

            var normal = view.VertexNormal(vertex);
            var offset = (centroid - point) * Relaxation;

            // ⚠ The tangential component only. The normal component of the move toward the centroid
            // *is* the shrink, and taking it out is what makes the step redistribute vertices rather
            // than smooth the surface — Botsch and Kobbelt again.
            if (normal.LengthSquared() > 0f) {
                offset -= normal * Vector3.Dot(offset, normal);
            }

            var candidate = point + offset;

            moved[vertex] = reproject ? reference.Project(candidate) : candidate;
        }

        for (var vertex = 0; vertex < moved.Length; vertex++) {
            soup.Positions[vertex] = moved[vertex];
        }
    }

    /// <summary>Applies a vertex remap and drops the triangles it collapsed.</summary>
    static void Rewrite(TriangleSoup soup, int[] remap) {
        var triangles = soup.Triangles.ToArray();
        var groups = soup.Groups.ToArray();

        soup.Triangles.Clear();
        soup.Groups.Clear();

        for (var triangle = 0; triangle * 3 < triangles.Length; triangle++) {
            var a = Root(remap, triangles[(triangle * 3) + 0]);
            var b = Root(remap, triangles[(triangle * 3) + 1]);
            var c = Root(remap, triangles[(triangle * 3) + 2]);

            if (a != b && b != c && c != a) {
                soup.Add(a, b, c, groups[triangle]);
            }
        }

        soup.Compact();
    }

    static int Root(int[] remap, int vertex) {
        while (remap[vertex] != vertex) {
            vertex = remap[vertex];
        }

        return vertex;
    }

}
