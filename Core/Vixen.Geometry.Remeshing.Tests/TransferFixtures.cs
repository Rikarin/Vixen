// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>The surfaces a transfer test needs, and the measurements it makes on the result.</summary>
/// <remarks>
///     ⚠ <b>Every fixture is built at an explicit scale and the tests run each one at three of
///     them.</b> docs/plan/41 § D3's lesson, which this repository has now paid for five times: a
///     tolerance that is absolute where it should be relative is invisible at unit scale and total at
///     any other.
/// </remarks>
static class TransferFixtures {
    /// <summary>A quad grid in the <c>z = 0</c> plane, optionally jittered, with a group rule.</summary>
    /// <param name="cells">How many quads along each side.</param>
    /// <param name="size">How wide the whole sheet is.</param>
    /// <param name="group">What group a face centred at a point belongs to.</param>
    /// <param name="jitter">How far a vertex wanders, as a fraction of a cell.</param>
    /// <returns>The sheet.</returns>
    /// <remarks>
    ///     ⚠ <b>The jitter is what makes a nearest-face rule shred.</b> A boundary crossing a
    ///     <i>regular</i> grid gives a monotone staircase whichever rule decides it, so a regular
    ///     target cannot tell the two apart — the artefact § D12 names needs the decision point to
    ///     wobble across the boundary, which is what a real extraction's quads do and what this
    ///     reproduces on purpose.
    /// </remarks>
    public static EditMesh Grid(int cells, float size, Func<Vector2, int> group, float jitter = 0f) {
        var mesh = new EditMesh();
        var step = size / cells;
        var indices = new int[cells + 1, cells + 1];

        for (var i = 0; i <= cells; i++) {
            for (var j = 0; j <= cells; j++) {
                var x = (-size / 2f) + (i * step);
                var y = (-size / 2f) + (j * step);

                // Interior vertices only, so the sheet stays a square and its outline is comparable
                // between two grids of different densities.
                if (jitter > 0f && i > 0 && i < cells && j > 0 && j < cells) {
                    x += Wobble(i, j, 0) * jitter * step;
                    y += Wobble(i, j, 1) * jitter * step;
                }

                indices[i, j] = mesh.AddPosition(new(x, y, 0f));
            }
        }

        for (var i = 0; i < cells; i++) {
            for (var j = 0; j < cells; j++) {
                var centre = Vector2.Zero;
                Span<int> loop = [indices[i, j], indices[i + 1, j], indices[i + 1, j + 1], indices[i, j + 1]];

                foreach (var corner in loop) {
                    var point = mesh.Positions[corner];

                    centre += new Vector2(point.X, point.Y) * 0.25f;
                }

                mesh.AddFace(loop, group(centre));
            }
        }

        return mesh;
    }

    /// <summary>A deterministic wobble in <c>[-1, 1]</c>, from the integer lattice alone.</summary>
    /// <remarks>
    ///     ⚠ No <see cref="Random" />. A jitter seeded from a clock makes a failing assertion
    ///     unreproducible, and one seeded from a shared instance makes it depend on the order xunit
    ///     ran the tests in.
    /// </remarks>
    static float Wobble(int i, int j, int channel) {
        var hash = (uint) ((i * 73856093) ^ (j * 19349663) ^ (channel * 83492791));

        hash ^= hash >> 15;
        hash *= 2246822519u;
        hash ^= hash >> 13;

        return ((hash % 2001u) / 1000f) - 1f;
    }

    /// <summary>A source sheet whose two groups meet on a straight line.</summary>
    /// <param name="cells">How finely the source resolves the line.</param>
    /// <param name="size">Its width.</param>
    /// <returns>The sheet.</returns>
    /// <remarks>
    ///     ⚠ <b>The line is <c>x = 0</c> and it runs along a grid line, which is not a convenience —
    ///     it is what makes the fixture measure the transfer rule instead of measuring itself.</b> A
    ///     diagonal boundary on a quad grid is a <i>staircase</i> whose steps are one source cell
    ///     tall, and a target quad three cells wide then straddles the steps by about a third of its
    ///     area. Both rules flip on that, so the fixture would report a shred that the source handed
    ///     them. Measured, at a 0.25 slope: 18 boundary edges from the area rule against 17 from the
    ///     rule the plan rejects, both branching twice — the fixture said the two rules were the
    ///     same, and what it was actually measuring was its own staircase.
    /// </remarks>
    public static EditMesh TwoGroups(int cells, float size) => Grid(cells, size, point => point.X < 0f ? 0 : 1);

    /// <summary>Every edge with two faces of different groups.</summary>
    public static List<MeshEdge> Boundary(EditMesh mesh) {
        var edges = new List<MeshEdge>();

        for (var edge = 0; edge < mesh.Edges.Count; edge++) {
            var faces = mesh.FacesOf(edge);

            if (faces.Length == 2 && mesh.Faces[faces[0]].Group != mesh.Faces[faces[1]].Group) {
                edges.Add(mesh.Edges[edge]);
            }
        }

        return edges;
    }

    /// <summary>The most boundary edges meeting at any one vertex, which is what "a chain" means.</summary>
    /// <remarks>
    ///     ⚠ <b>Two is a chain and three is a shred.</b> A material boundary that walks the mesh
    ///     visits each of its vertices once and leaves, so every interior vertex on it has exactly two
    ///     boundary edges. A quad that flipped to the wrong group on its own is surrounded by four,
    ///     and its corners then carry three each — which is the sawtooth, counted rather than looked
    ///     at.
    /// </remarks>
    public static int Branching(EditMesh mesh, List<MeshEdge> boundary) {
        var degree = new int[mesh.PositionCount];

        foreach (var edge in boundary) {
            degree[edge.A]++;
            degree[edge.B]++;
        }

        return degree.Length == 0 ? 0 : degree.Max();
    }

    /// <summary>The rule § D12 says not to use, so that the two can be compared on one mesh.</summary>
    /// <remarks>
    ///     Each face takes the group of the source face nearest its first corner. This is the
    ///     "nearest face" assignment the plan document rejects, implemented here only so the
    ///     rejection can be a measurement.
    /// </remarks>
    public static void NearestFace(EditMesh source, EditMesh target) {
        var triangles = source.Triangulate();
        var tree = new TriangleTree(source.Positions, triangles);
        var owner = new int[triangles.Length / 3];
        var at = 0;

        for (var face = 0; face < source.FaceCount; face++) {
            for (var step = 0; step < Math.Max(source.Faces[face].Count - 2, 0); step++, at++) {
                owner[at] = source.Faces[face].Group;
            }
        }

        for (var face = 0; face < target.FaceCount; face++) {
            var hit = tree.Closest(target.Positions[target.CornersOf(face)[0]]);

            if (hit.Triangle >= 0) {
                target.SetGroup(face, owner[hit.Triangle]);
            }
        }
    }

    /// <summary>The same mesh at another size, positions and nothing else.</summary>
    public static EditMesh Scaled(EditMesh mesh, float factor) {
        var copy = new EditMesh(mesh);

        for (var vertex = 0; vertex < copy.PositionCount; vertex++) {
            copy.MovePosition(vertex, mesh.Positions[vertex] * factor);
        }

        return copy;
    }

    /// <summary>A binding whose two bones share every vertex by its <c>x</c>, left to right.</summary>
    /// <param name="mesh">The mesh to bind.</param>
    /// <param name="size">The width the ramp runs over.</param>
    /// <returns>The binding, four influences wide with two of them padding.</returns>
    public static SkinBinding Ramp(EditMesh mesh, float size) {
        var influences = new SkinInfluence[mesh.PositionCount * 4];

        for (var vertex = 0; vertex < mesh.PositionCount; vertex++) {
            var t = Math.Clamp((mesh.Positions[vertex].X / size) + 0.5f, 0f, 1f);

            // Descending weight, which is the order a binding is defined to be in.
            influences[vertex * 4] = t >= 0.5f ? new(1, t) : new(0, 1f - t);
            influences[(vertex * 4) + 1] = t >= 0.5f ? new(0, 1f - t) : new(1, t);
        }

        return new() { Influences = influences, Stride = 4 };
    }
}
