// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>The fixed set of shapes every distortion figure in this assembly is quoted against.</summary>
/// <remarks>
///     <para>
///         docs/plan/42's first exit criterion compares against xatlas's published figures on a
///         500-mesh corpus, and that comparison cannot be run here. What can be run is a <b>baseline
///         the next phase has to move</b>: six shapes chosen so that each one fails a different way —
///         a sphere for area distortion, a cylinder for the developable control, a torus for curvature
///         of both signs, a hemisphere for the pole, a saddle for negative curvature, and a forty-to-one
///         strip for aspect ratio.
///     </para>
///     <para>
///         ⚠ <b>Every shape is built here rather than taken from <c>MeshShapes</c>.</b> The primitives
///         are closed, and a closed surface has no parameterization at all — every one of these is cut
///         open on purpose, and <i>where</i> it is cut is the difference between a disk and an annulus.
///         Two of them are deliberately left uncut, as the input the topology check has to refuse.
///     </para>
///     <para>
///         ⚠ <b>Grid quads are emitted as four-sided faces rather than as two triangles.</b> That is the
///         path where a face's triangles have to be matched back to the face's <i>corners</i>, which is
///         the layer <see cref="UvIsland" /> keeps coordinates in, and a corpus of triangles would never
///         exercise it.
///     </para>
/// </remarks>
static class ShapeCorpus {
    /// <summary>A sphere with both poles cut off and one meridian slit — the area-distortion case.</summary>
    /// <param name="scale">A uniform scale on every position.</param>
    /// <returns>The mesh, which is one disk.</returns>
    public static EditMesh SphereCutOpen(float scale = 1f) =>
        Grid(
            48,
            30,
            false,
            false,
            (u, v) => {
                var phi = u * MathF.Tau;
                var theta = (0.1f + (v * 0.8f)) * MathF.PI;

                return scale
                    * new Vector3(
                        MathF.Sin(theta) * MathF.Cos(phi),
                        MathF.Cos(theta),
                        MathF.Sin(theta) * MathF.Sin(phi)
                    );
            }
        );

    /// <summary>A tube slit along one generator — developable, so the control that should barely distort.</summary>
    /// <param name="scale">A uniform scale on every position.</param>
    /// <returns>The mesh, which is one disk.</returns>
    public static EditMesh CylinderSlit(float scale = 1f) =>
        Grid(
            48,
            12,
            false,
            false,
            (u, v) => scale * new Vector3(MathF.Cos(u * MathF.Tau), (v * 2f) - 1f, MathF.Sin(u * MathF.Tau))
        );

    /// <summary>The same tube with its slit closed — an annulus, which has no injective disk map.</summary>
    /// <param name="scale">A uniform scale on every position.</param>
    /// <returns>The mesh, which is not a disk.</returns>
    public static EditMesh CylinderClosed(float scale = 1f) =>
        Grid(
            48,
            12,
            true,
            false,
            (u, v) => scale * new Vector3(MathF.Cos(u * MathF.Tau), (v * 2f) - 1f, MathF.Sin(u * MathF.Tau))
        );

    /// <summary>A torus slit both ways, which is what it takes to make a genus-one surface a disk.</summary>
    /// <param name="scale">A uniform scale on every position.</param>
    /// <returns>The mesh, which is one disk.</returns>
    /// <remarks>
    ///     ⚠ <b>One slit is not enough and that is the point of having this shape.</b> A torus with a
    ///     single cut is <c>g = 1, b = 1</c>, so <c>χ = −1</c>: it has exactly one boundary loop and is
    ///     still not a disk. A boundary-loop count would pass it.
    /// </remarks>
    public static EditMesh TorusSlit(float scale = 1f) =>
        Grid(48, 24, false, false, (u, v) => scale * TorusPoint(u, v));

    /// <summary>A closed torus — no boundary at all, so nothing has been cut.</summary>
    /// <param name="scale">A uniform scale on every position.</param>
    /// <returns>The mesh, which is closed.</returns>
    public static EditMesh TorusClosed(float scale = 1f) =>
        Grid(48, 24, true, true, (u, v) => scale * TorusPoint(u, v));

    /// <summary>Half a sphere, wrapped round its pole, whose boundary is the equator.</summary>
    /// <param name="scale">A uniform scale on every position.</param>
    /// <returns>The mesh, which is one disk.</returns>
    /// <remarks>
    ///     ⚠ <b>A genuine pole rather than a cap cut off it.</b> The fan of triangles round the pole is
    ///     where the smallest triangles in the corpus are, which is what makes this the shape that
    ///     catches an absolute epsilon — docs/plan/24 § P1's capsule found the same thing from the other
    ///     direction.
    /// </remarks>
    public static EditMesh Hemisphere(float scale = 1f) {
        var mesh = new EditMesh();
        const int Around = 40;
        const int Rings = 16;

        var pole = mesh.AddPosition(scale * new Vector3(0f, 1f, 0f));
        var ring = new int[Rings, Around];

        for (var index = 0; index < Rings; index++) {
            var theta = (index + 1) * (MathF.PI * 0.5f) / Rings;

            for (var around = 0; around < Around; around++) {
                var phi = around * MathF.Tau / Around;

                ring[index, around] = mesh.AddPosition(
                    scale
                    * new Vector3(
                        MathF.Sin(theta) * MathF.Cos(phi),
                        MathF.Cos(theta),
                        MathF.Sin(theta) * MathF.Sin(phi)
                    )
                );
            }
        }

        Span<int> triangle = stackalloc int[3];
        Span<int> quad = stackalloc int[4];

        for (var around = 0; around < Around; around++) {
            triangle[0] = pole;
            triangle[1] = ring[0, around];
            triangle[2] = ring[0, (around + 1) % Around];
            mesh.AddFace(triangle);
        }

        for (var index = 0; index + 1 < Rings; index++) {
            for (var around = 0; around < Around; around++) {
                var next = (around + 1) % Around;

                quad[0] = ring[index, around];
                quad[1] = ring[index + 1, around];
                quad[2] = ring[index + 1, next];
                quad[3] = ring[index, next];
                mesh.AddFace(quad);
            }
        }

        return mesh;
    }

    /// <summary>A hyperbolic paraboloid patch — curvature of the other sign.</summary>
    /// <param name="scale">A uniform scale on every position.</param>
    /// <returns>The mesh, which is one disk.</returns>
    public static EditMesh Saddle(float scale = 1f) =>
        Grid(
            32,
            32,
            false,
            false,
            (u, v) => {
                var x = (u * 2f) - 1f;
                var y = (v * 2f) - 1f;

                return scale * new Vector3(x, (x * x) - (y * y), y);
            }
        );

    /// <summary>A flat rectangle forty times longer than it is wide.</summary>
    /// <param name="scale">A uniform scale on every position.</param>
    /// <returns>The mesh, which is one disk.</returns>
    /// <remarks>
    ///     Isometric by construction, so its distortion figures are the corpus's zero point — anything
    ///     other than one here is the measurement being wrong rather than the map. It is kept because
    ///     the aspect ratio is what a pin chosen badly shows up on first.
    /// </remarks>
    public static EditMesh Strip(float scale = 1f) =>
        Grid(80, 2, false, false, (u, v) => scale * new Vector3(u, 0f, v * 0.025f));

    /// <summary>A curved patch tessellated so that every triangle has one very obtuse angle.</summary>
    /// <param name="across">How many columns.</param>
    /// <param name="down">How many rows.</param>
    /// <param name="shear">How far each row is shifted along the last. Larger is more obtuse.</param>
    /// <returns>The mesh, which is one disk.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The input the cotangent decision is made for.</b> The rows are sheared hard and
    ///         packed close, so every quad is a sliver parallelogram and every triangle in it has an
    ///         angle around 170°, whose cotangent is roughly <c>−6</c>. A negative weight is what makes
    ///         a cotangent Laplacian indefinite, and conjugate gradient on an indefinite matrix does not
    ///         fail — it converges to a saddle and returns a fold. See <c>CotangentWeights</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It has to be a grid and not a strip, and that was measured the hard way.</b> A
    ///         triangle strip has <i>no interior vertices</i> — every one of them is on the boundary —
    ///         so it is developable whatever it is bent into, the first rung maps it exactly, and the
    ///         obtuse weights are never handed to a Laplacian at all. The first version of this shape
    ///         was a strip and reported a distortion of exactly one on every measure, which is what
    ///         gave it away.
    ///     </para>
    /// </remarks>
    public static EditMesh ObtuseGrid(int across = 24, int down = 12, float shear = 4f) {
        var mesh = new EditMesh();
        var step = 2f / across;
        var rise = 0.06f;
        var index = new int[across + 1, down + 1];

        for (var column = 0; column <= across; column++) {
            for (var row = 0; row <= down; row++) {
                var x = ((column + (shear * row)) * step) - 1f;
                var y = row * rise;

                index[column, row] = mesh.AddPosition(new(x, 0.5f * ((x * x) - (y * y)), y));
            }
        }

        Span<int> quad = stackalloc int[4];

        for (var column = 0; column < across; column++) {
            for (var row = 0; row < down; row++) {
                quad[0] = index[column, row];
                quad[1] = index[column + 1, row];
                quad[2] = index[column + 1, row + 1];
                quad[3] = index[column, row + 1];

                mesh.AddFace(quad);
            }
        }

        return mesh;
    }

    /// <summary>A sphere with only a hairline slit taken out of it — the classic overlapping chart.</summary>
    /// <param name="scale">A uniform scale on every position.</param>
    /// <returns>The mesh, which is a disk on paper and very nearly a sphere in fact.</returns>
    /// <remarks>
    ///     ⚠ <b>What a conformal map is worst at.</b> Almost all of the surface has to reach the plane
    ///     through a boundary that is barely there, so the map compresses one end of the chart towards
    ///     nothing — which is exactly where a conformal energy stops being able to tell a fold from a
    ///     very small triangle. It is the input the third rung exists for.
    /// </remarks>
    public static EditMesh SphereNearlyClosed(float scale = 1f) =>
        Grid(
            64,
            48,
            false,
            false,
            (u, v) => {
                var phi = u * MathF.Tau;
                var theta = (0.004f + (v * 0.992f)) * MathF.PI;

                return scale
                    * new Vector3(
                        MathF.Sin(theta) * MathF.Cos(phi),
                        MathF.Cos(theta),
                        MathF.Sin(theta) * MathF.Sin(phi)
                    );
            }
        );

    /// <summary>Two triangles meeting at exactly one position — a disk by Euler characteristic and not one.</summary>
    /// <returns>The mesh.</returns>
    public static EditMesh Bowtie() {
        var mesh = new EditMesh();

        var centre = mesh.AddPosition(Vector3.Zero);
        var a = mesh.AddPosition(new(1f, 0f, 0f));
        var b = mesh.AddPosition(new(1f, 0f, 1f));
        var c = mesh.AddPosition(new(-1f, 0f, 0f));
        var d = mesh.AddPosition(new(-1f, 0f, -1f));

        Span<int> triangle = stackalloc int[3];

        triangle[0] = centre;
        triangle[1] = a;
        triangle[2] = b;
        mesh.AddFace(triangle);

        triangle[0] = centre;
        triangle[1] = c;
        triangle[2] = d;
        mesh.AddFace(triangle);

        return mesh;
    }

    /// <summary>Two squares that share nothing, both assigned to one chart.</summary>
    /// <returns>The mesh.</returns>
    public static EditMesh TwoIslands() {
        var mesh = new EditMesh();

        Span<int> quad = stackalloc int[4];

        for (var side = 0; side < 2; side++) {
            var shift = side * 4f;

            quad[0] = mesh.AddPosition(new(shift, 0f, 0f));
            quad[1] = mesh.AddPosition(new(shift, 0f, 1f));
            quad[2] = mesh.AddPosition(new(shift + 1f, 0f, 1f));
            quad[3] = mesh.AddPosition(new(shift + 1f, 0f, 0f));

            mesh.AddFace(quad);
        }

        return mesh;
    }

    /// <summary>One triangle, which is the smallest thing a chart can be.</summary>
    /// <param name="scale">A uniform scale on every position.</param>
    /// <returns>The mesh.</returns>
    public static EditMesh OneTriangle(float scale = 1f) {
        var mesh = new EditMesh();

        Span<int> triangle = [
            mesh.AddPosition(scale * new Vector3(0f, 0f, 0f)),
            mesh.AddPosition(scale * new Vector3(1f, 0f, 0f)),
            mesh.AddPosition(scale * new Vector3(0.25f, 0f, 0.75f))
        ];

        mesh.AddFace(triangle);

        return mesh;
    }

    /// <summary>Two triangles sharing one edge.</summary>
    /// <returns>The mesh.</returns>
    public static EditMesh TwoTriangles() {
        var mesh = new EditMesh();

        var a = mesh.AddPosition(new(0f, 0f, 0f));
        var b = mesh.AddPosition(new(1f, 0f, 0f));
        var c = mesh.AddPosition(new(1f, 0f, 1f));
        var d = mesh.AddPosition(new(0f, 0f, 1f));

        Span<int> triangle = stackalloc int[3];

        triangle[0] = a;
        triangle[1] = b;
        triangle[2] = c;
        mesh.AddFace(triangle);

        triangle[0] = a;
        triangle[1] = c;
        triangle[2] = d;
        mesh.AddFace(triangle);

        return mesh;
    }

    /// <summary>A square, plus one triangle whose three corners are collinear.</summary>
    /// <returns>The mesh, which is still a disk: <c>5 − 7 + 3 = 1</c>, one boundary loop.</returns>
    /// <remarks>
    ///     ⚠ <b>A zero that means "off".</b> A triangle with no area in three dimensions has an infinite
    ///     cotangent at every corner and no plane to take a Jacobian in, so it belongs in no energy and
    ///     no measure — and it has no orientation to reverse either, which is why it must not count
    ///     towards a flip. The degenerate triangle here shares a whole edge with the square rather than
    ///     a vertex, so the chart stays manifold and the topology check has nothing to say about it.
    /// </remarks>
    public static EditMesh WithDegenerateTriangle() {
        var mesh = new EditMesh();

        var a = mesh.AddPosition(new(0f, 0f, 0f));
        var b = mesh.AddPosition(new(1f, 0f, 0f));
        var c = mesh.AddPosition(new(1f, 0f, 1f));
        var d = mesh.AddPosition(new(0f, 0f, 1f));
        var e = mesh.AddPosition(new(1f, 0f, 2f));

        Span<int> triangle = stackalloc int[3];

        triangle[0] = a;
        triangle[1] = b;
        triangle[2] = c;
        mesh.AddFace(triangle);

        triangle[0] = a;
        triangle[1] = c;
        triangle[2] = d;
        mesh.AddFace(triangle);

        // b, c and e all sit on x = 1, so this one has no area at all.
        triangle[0] = b;
        triangle[1] = e;
        triangle[2] = c;
        mesh.AddFace(triangle);

        return mesh;
    }

    /// <summary>Every face in one chart.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="id">Which chart.</param>
    /// <returns>One entry per face.</returns>
    public static int[] OneChart(EditMesh mesh, int id = 0) {
        var charts = new int[mesh.FaceCount];

        Array.Fill(charts, id);

        return charts;
    }

    /// <summary>Splits a grid mesh's faces into a fixed number of contiguous charts.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="charts">How many.</param>
    /// <returns>One entry per face.</returns>
    /// <remarks>
    ///     For the determinism sweep, which needs more charts than there are workers before comparing
    ///     one worker count against another proves anything.
    /// </remarks>
    public static int[] Strips(EditMesh mesh, int charts) {
        var assignment = new int[mesh.FaceCount];
        var perChart = Math.Max(1, (mesh.FaceCount + charts - 1) / charts);

        for (var face = 0; face < mesh.FaceCount; face++) {
            assignment[face] = Math.Min(charts - 1, face / perChart);
        }

        return assignment;
    }

    /// <summary>Rebuilds a mesh with its positions renumbered by a fixed permutation.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="seed">Which permutation.</param>
    /// <returns>A mesh with the same geometry and different indices.</returns>
    /// <remarks>
    ///     ⚠ <b>The test a pin chosen by index fails.</b> Nothing about the surface has changed, so
    ///     nothing about the map may — and an importer, a weld or a boolean renumbers a mesh routinely.
    /// </remarks>
    public static EditMesh Renumber(EditMesh mesh, uint seed) {
        var order = new int[mesh.PositionCount];

        for (var index = 0; index < order.Length; index++) {
            order[index] = index;
        }

        var state = seed;

        for (var index = order.Length - 1; index > 0; index--) {
            var swap = (int)(Next(ref state) % (uint)(index + 1));

            (order[index], order[swap]) = (order[swap], order[index]);
        }

        var moved = new EditMesh();
        var slot = new int[mesh.PositionCount];

        for (var index = 0; index < order.Length; index++) {
            slot[order[index]] = moved.AddPosition(mesh.Positions[order[index]]);
        }

        for (var face = 0; face < mesh.FaceCount; face++) {
            var loop = mesh.CornersOf(face);
            var mapped = new int[loop.Length];

            for (var corner = 0; corner < loop.Length; corner++) {
                mapped[corner] = slot[loop[corner]];
            }

            moved.AddFace(mapped);
        }

        return moved;
    }

    static Vector3 TorusPoint(float u, float v) {
        var around = u * MathF.Tau;
        var through = v * MathF.Tau;
        var radius = 2f + (0.6f * MathF.Cos(through));

        return new(radius * MathF.Cos(around), 0.6f * MathF.Sin(through), radius * MathF.Sin(around));
    }

    /// <summary>A quad grid over a parametric surface, wrapped in either direction or not.</summary>
    static EditMesh Grid(int across, int down, bool wrapAcross, bool wrapDown, Func<float, float, Vector3> surface) {
        var mesh = new EditMesh();
        var columns = wrapAcross ? across : across + 1;
        var rows = wrapDown ? down : down + 1;
        var index = new int[columns, rows];

        for (var column = 0; column < columns; column++) {
            for (var row = 0; row < rows; row++) {
                index[column, row] = mesh.AddPosition(surface((float)column / across, (float)row / down));
            }
        }

        Span<int> quad = stackalloc int[4];

        for (var column = 0; column < across; column++) {
            for (var row = 0; row < down; row++) {
                var right = (column + 1) % columns;
                var below = (row + 1) % rows;

                quad[0] = index[column, row];
                quad[1] = index[right, row];
                quad[2] = index[right, below];
                quad[3] = index[column, below];

                mesh.AddFace(quad);
            }
        }

        return mesh;
    }

    static uint Next(ref uint state) => state = (state * 1664525u) + 1013904223u;
}
