// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Uv.Flattening;

/// <summary>Why a chart cannot be flattened, or <see cref="None" /> when it can.</summary>
/// <remarks>
///     docs/plan/42 § D5's third rung ends in <i>"split the chart and recurse"</i>, and the recursion
///     is U3's. What U2 owes it is a reason, because the split that fixes an annulus is not the split
///     that fixes a fold: an annulus needs a cut joining its two boundary loops and a fold needs the
///     region round the fold taken out. A single <c>bool</c> would make those the same instruction.
/// </remarks>
enum ChartRefusal : byte {
    /// <summary>The chart is a disk and was flattened.</summary>
    None,

    /// <summary>The chart has no triangles, or none of them has any area in three dimensions.</summary>
    Empty,

    /// <summary>The chart falls into pieces that share no vertex.</summary>
    Disconnected,

    /// <summary>An edge carries three or more of the chart's triangles.</summary>
    NonManifoldEdge,

    /// <summary>Two boundary loops meet at one position — a bowtie, which has no consistent boundary.</summary>
    NonManifoldVertex,

    /// <summary>The chart is closed: it has no boundary at all, so nothing can be laid flat.</summary>
    Closed,

    /// <summary>
    ///     The chart is a surface with boundary but not a disk — an annulus, a handle, or both. ⚠ No
    ///     injective map to the plane exists, so a flattener that produced one produced a fold.
    /// </summary>
    NotADisk,

    /// <summary>Triangles were still folded after the repair pass. U3 splits and recurses.</summary>
    Folded
}

/// <summary>One chart, lifted out of an <see cref="EditMesh" /> and renumbered to its own vertices.</summary>
/// <remarks>
///     <para>
///         docs/plan/42 § D1: flattening takes a mesh and a chart assignment and nothing else. The
///         renumbering is what makes a seam free — a position on the boundary between two charts
///         becomes one vertex in each of them, so the two sides carry different coordinates without
///         anybody splitting a vertex.
///     </para>
///     <para>
///         ⚠ <b><see cref="Corners" /> holds <i>corner</i> indices and <see cref="Triangles" /> holds
///         chart-local <i>vertex</i> indices, and they are parallel.</b> The solve runs on vertices,
///         because that is what a Laplacian is over; the answer is written per corner, because that is
///         where <see cref="UvIsland" /> keeps it and where a seam costs nothing.
///     </para>
/// </remarks>
sealed class ChartMesh {
    /// <summary>Which chart this is, as the caller numbered it.</summary>
    public required int Id { get; init; }

    /// <summary>Three chart-local vertex indices per triangle.</summary>
    public required int[] Triangles { get; init; }

    /// <summary>Three <see cref="EditMesh.Corners" /> indices per triangle, parallel to <see cref="Triangles" />.</summary>
    public required int[] Corners { get; init; }

    /// <summary>Each chart-local vertex's position, in world units.</summary>
    public required Vector3[] Positions { get; init; }

    /// <summary>Each chart-local vertex's index in <see cref="EditMesh.Positions" />.</summary>
    public required int[] Origin { get; init; }

    /// <summary>How many triangles the chart covers.</summary>
    public int TriangleCount => Triangles.Length / 3;

    /// <summary>How many vertices the solve has.</summary>
    public int VertexCount => Positions.Length;

    /// <summary>How many boundary loops the chart has. One is a disk; two is an annulus.</summary>
    public int BoundaryLoops { get; private set; }

    /// <summary><c>V − E + F</c>. One exactly when the chart is a disk.</summary>
    public int EulerCharacteristic { get; private set; }

    /// <summary>Whether the chart can be flattened at all, and why not when it cannot.</summary>
    public ChartRefusal Topology { get; private set; }

    /// <summary>Whether every vertex is reachable from every other along the chart's edges.</summary>
    public bool IsConnected => Topology != ChartRefusal.Disconnected;

    /// <summary>Whether the vertex sits on the chart's boundary.</summary>
    public required bool[] IsBoundary { get; init; }

    /// <summary>Each vertex's neighbours, as a run into <see cref="Adjacent" />.</summary>
    public required int[] AdjacentStart { get; init; }

    /// <summary>The neighbour lists, ascending within each run.</summary>
    public required int[] Adjacent { get; init; }

    /// <summary>Cuts every chart out of a mesh, in ascending chart order.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="chartOfFace">Which chart each face belongs to. Negative excludes the face.</param>
    /// <returns>The charts, ordered by <see cref="Id" />.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Faces are triangulated through <see cref="EditMesh.Triangulate" /> rather than
    ///         fanned here.</b> Every face produces exactly <c>Count − 2</c> triangles whatever route it
    ///         took, which is what lets the run of triangles a face owns be found by counting — and the
    ///         ear clipping it uses is the one that already asks <c>ExactPredicates.Orient2D</c> rather
    ///         than a <see langword="float" /> cross product.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Chart ids are read, not assumed dense.</b> A charter that split a chart and threw
    ///         one half away leaves a gap, and a flattener that indexed an array by chart id would
    ///         either crash on it or silently produce an empty island.
    ///     </para>
    /// </remarks>
    public static List<ChartMesh> Extract(EditMesh mesh, IReadOnlyList<int> chartOfFace) {
        var triangulated = mesh.Triangulate();
        var faceStart = new int[mesh.FaceCount + 1];

        for (var face = 0; face < mesh.FaceCount; face++) {
            faceStart[face + 1] = faceStart[face] + Math.Max(mesh.Faces[face].Count - 2, 0);
        }

        var ids = new List<int>();
        var slotOfId = new Dictionary<int, int>();

        for (var face = 0; face < mesh.FaceCount; face++) {
            var id = chartOfFace[face];

            if (id >= 0 && slotOfId.TryAdd(id, ids.Count)) {
                ids.Add(id);
            }
        }

        ids.Sort();

        for (var slot = 0; slot < ids.Count; slot++) {
            slotOfId[ids[slot]] = slot;
        }

        var triangles = new List<int>[ids.Count];
        var corners = new List<int>[ids.Count];
        var local = new Dictionary<int, int>[ids.Count];
        var origin = new List<int>[ids.Count];

        for (var slot = 0; slot < ids.Count; slot++) {
            triangles[slot] = [];
            corners[slot] = [];
            local[slot] = [];
            origin[slot] = [];
        }

        for (var face = 0; face < mesh.FaceCount; face++) {
            var id = chartOfFace[face];

            if (id < 0) {
                continue;
            }

            var slot = slotOfId[id];
            var entry = mesh.Faces[face];
            var loop = mesh.CornersOf(face);

            for (var triangle = faceStart[face]; triangle < faceStart[face + 1]; triangle++) {
                for (var corner = 0; corner < 3; corner++) {
                    var position = triangulated[(triangle * 3) + corner];

                    if (!local[slot].TryGetValue(position, out var vertex)) {
                        vertex = origin[slot].Count;
                        local[slot].Add(position, vertex);
                        origin[slot].Add(position);
                    }

                    triangles[slot].Add(vertex);
                    corners[slot].Add(entry.Start + Slot(loop, position));
                }
            }
        }

        var charts = new List<ChartMesh>(ids.Count);

        for (var slot = 0; slot < ids.Count; slot++) {
            charts.Add(Assemble(mesh, ids[slot], triangles[slot], corners[slot], origin[slot]));
        }

        return charts;
    }

    /// <summary>Which corner of a face sits at a position.</summary>
    /// <remarks>
    ///     ⚠ <b>The first one, and a face that visits a position twice is why this is a search rather
    ///     than an index.</b> <see cref="EditMesh.FacesAt" /> records that a cap made by a boolean
    ///     legitimately can. Both corners carry the same coordinate in that case — the position is
    ///     interior to one chart — so which is chosen changes nothing that is measured.
    /// </remarks>
    static int Slot(ReadOnlySpan<int> loop, int position) {
        for (var corner = 0; corner < loop.Length; corner++) {
            if (loop[corner] == position) {
                return corner;
            }
        }

        return 0;
    }

    static ChartMesh Assemble(EditMesh mesh, int id, List<int> triangles, List<int> corners, List<int> origin) {
        var positions = new Vector3[origin.Count];

        for (var vertex = 0; vertex < origin.Count; vertex++) {
            positions[vertex] = mesh.Positions[origin[vertex]];
        }

        var neighbours = new List<int>[origin.Count];

        for (var vertex = 0; vertex < origin.Count; vertex++) {
            neighbours[vertex] = [];
        }

        // How many of the chart's triangles carry each undirected edge. One is a boundary, two is
        // manifold, and more is the case that has no consistent orientation at all.
        var incident = new Dictionary<(int, int), int>();

        for (var triangle = 0; triangle * 3 < triangles.Count; triangle++) {
            for (var side = 0; side < 3; side++) {
                var a = triangles[(triangle * 3) + side];
                var b = triangles[(triangle * 3) + ((side + 1) % 3)];
                var key = a < b ? (a, b) : (b, a);

                incident[key] = incident.GetValueOrDefault(key) + 1;

                if (!neighbours[a].Contains(b)) {
                    neighbours[a].Add(b);
                    neighbours[b].Add(a);
                }
            }
        }

        var adjacentStart = new int[origin.Count + 1];

        for (var vertex = 0; vertex < origin.Count; vertex++) {
            neighbours[vertex].Sort();
            adjacentStart[vertex + 1] = adjacentStart[vertex] + neighbours[vertex].Count;
        }

        var adjacent = new int[adjacentStart[^1]];

        for (var vertex = 0; vertex < origin.Count; vertex++) {
            neighbours[vertex].CopyTo(adjacent, adjacentStart[vertex]);
        }

        var boundary = new bool[origin.Count];
        var boundaryEdges = new List<(int A, int B)>();
        var boundaryValence = new int[origin.Count];
        var overloaded = false;

        foreach (var (edge, count) in incident) {
            switch (count) {
                case 1:
                    boundary[edge.Item1] = true;
                    boundary[edge.Item2] = true;
                    boundaryValence[edge.Item1]++;
                    boundaryValence[edge.Item2]++;
                    boundaryEdges.Add(edge);

                    break;

                case > 2:
                    overloaded = true;

                    break;
            }
        }

        var chart = new ChartMesh {
            Id = id,
            Triangles = [.. triangles],
            Corners = [.. corners],
            Positions = positions,
            Origin = [.. origin],
            IsBoundary = boundary,
            AdjacentStart = adjacentStart,
            Adjacent = adjacent
        };

        chart.EulerCharacteristic = origin.Count - incident.Count + (triangles.Count / 3);
        chart.BoundaryLoops = Loops(boundaryEdges, boundaryValence);
        chart.Topology = Classify(chart, overloaded, boundaryValence);

        return chart;
    }

    /// <summary>How many closed loops the boundary edges form, by contracting them.</summary>
    /// <remarks>
    ///     A union-find over the boundary edges rather than a walk, because a walk has to choose a
    ///     direction at a bowtie and the count is wanted <i>before</i> the bowtie is ruled out.
    /// </remarks>
    static int Loops(List<(int A, int B)> edges, int[] valence) {
        if (edges.Count == 0) {
            return 0;
        }

        var parent = new Dictionary<int, int>();

        for (var vertex = 0; vertex < valence.Length; vertex++) {
            if (valence[vertex] > 0) {
                parent[vertex] = vertex;
            }
        }

        foreach (var (a, b) in edges) {
            var left = Find(parent, a);
            var right = Find(parent, b);

            if (left != right) {
                parent[Math.Max(left, right)] = Math.Min(left, right);
            }
        }

        var roots = new HashSet<int>();

        foreach (var vertex in parent.Keys) {
            roots.Add(Find(parent, vertex));
        }

        return roots.Count;
    }

    static int Find(Dictionary<int, int> parent, int vertex) {
        while (parent[vertex] != vertex) {
            vertex = parent[vertex] = parent[parent[vertex]];
        }

        return vertex;
    }

    /// <summary>The one test that decides whether a parameterization can exist.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Euler characteristic rather than a boundary-loop count, because one number catches
    ///         both failures.</b> For a connected surface with genus <c>g</c> and <c>b</c> boundary
    ///         loops, <c>χ = 2 − 2g − b</c>. A disk is <c>g = 0, b = 1</c>, so <c>χ = 1</c>; an annulus
    ///         is <c>0</c> and a torus with one slit is <c>−1</c>. Counting boundary loops alone would
    ///         pass the slit torus, which is genus rather than boundary and folds just as hard.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>χ is blind to a bowtie and that is why the vertex check is separate.</b> Two
    ///         triangles meeting at exactly one vertex give <c>5 − 6 + 2 = 1</c>, which is a disk's
    ///         number for something that is not a surface. The give-away is a boundary vertex with four
    ///         boundary edges rather than two.
    ///     </para>
    /// </remarks>
    static ChartRefusal Classify(ChartMesh chart, bool overloaded, int[] boundaryValence) {
        if (chart.TriangleCount == 0) {
            return ChartRefusal.Empty;
        }

        if (overloaded) {
            return ChartRefusal.NonManifoldEdge;
        }

        if (!Reachable(chart)) {
            return ChartRefusal.Disconnected;
        }

        foreach (var count in boundaryValence) {
            if (count > 2) {
                return ChartRefusal.NonManifoldVertex;
            }
        }

        if (chart.BoundaryLoops == 0) {
            return ChartRefusal.Closed;
        }

        return chart.EulerCharacteristic == 1 ? ChartRefusal.None : ChartRefusal.NotADisk;
    }

    static bool Reachable(ChartMesh chart) {
        var seen = new bool[chart.VertexCount];
        var stack = new Stack<int>();

        stack.Push(0);
        seen[0] = true;
        var found = 1;

        while (stack.Count > 0) {
            var vertex = stack.Pop();

            for (var index = chart.AdjacentStart[vertex]; index < chart.AdjacentStart[vertex + 1]; index++) {
                var next = chart.Adjacent[index];

                if (!seen[next]) {
                    seen[next] = true;
                    found++;
                    stack.Push(next);
                }
            }
        }

        return found == chart.VertexCount;
    }
}
