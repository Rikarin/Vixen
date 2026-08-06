// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Uv.Flattening;

/// <summary>Which two vertices LSCM nails down, chosen so the answer does not depend on the numbering.</summary>
/// <remarks>
///     <para>
///         docs/plan/42 § D5. LSCM's energy has a four-dimensional null space — translate, rotate and
///         scale the whole chart and nothing changes — so two vertices have to be fixed before the
///         least-squares problem has an answer at all. <b>Two vertices far apart is also the quality
///         choice</b>: pinning two neighbours leaves the solve to determine the chart's scale from the
///         gap between them, and a short gap amplifies whatever the solve got wrong about it across
///         the entire chart.
///     </para>
///     <para>
///         ⚠ <b>A pin chosen by "whichever index came first" is a determinism hole that passes every
///         test run on the mesh it was written against.</b> Renumber the vertices — which an importer,
///         a weld or a boolean does routinely — and the pins move, the gauge moves with them, and a
///         golden that should not have changed does. The double sweep below is the standard graph
///         diameter heuristic and it is deterministic; what makes it <i>renumbering-invariant</i> is
///         the seed and the tie-break, and neither of those is graph structure.
///     </para>
///     <para>
///         ⚠ <b>The tie-break is the vertex's position, not its index, and that is a deliberate
///         strengthening of § D5's "index tie-broken".</b> An index tie-break is exactly what a
///         renumbering test catches: two vertices equally far away are ordered by a number the caller
///         is allowed to change. A position is a property of the model, it is identical under any
///         renumbering, and a welded mesh cannot have two of them equal — <c>EditMesh.FromTriangles</c>
///         welds before anything else sees the mesh. The index remains as the last resort, for the
///         unwelded input where two vertices really are at one point.
///     </para>
/// </remarks>
static class Pins {
    /// <summary>Picks the two vertices to fix, and the distance to place them apart.</summary>
    /// <param name="chart">The chart.</param>
    /// <returns>The two vertices, and the world distance between them.</returns>
    /// <remarks>
    ///     ⚠ <b>A chart of one vertex cannot happen and a chart of two can.</b> Two triangles that
    ///     collapsed to an edge would give the same vertex twice, and pinning one vertex to two places
    ///     is a system with no solution rather than a bad one — so the second pin falls back to any
    ///     other vertex, and a chart with only one vertex is refused before it reaches here.
    /// </remarks>
    public static (int First, int Second, double Distance) Choose(ChartMesh chart) {
        var seed = Lowest(chart);
        var first = Farthest(chart, seed);
        var second = Farthest(chart, first);

        if (second == first) {
            second = first == 0 ? Math.Min(1, chart.VertexCount - 1) : 0;
        }

        var a = chart.Positions[first];
        var b = chart.Positions[second];

        double dx = b.X - (double)a.X, dy = b.Y - (double)a.Y, dz = b.Z - (double)a.Z;

        return (first, second, Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz)));
    }

    /// <summary>The lexicographically smallest position, which is where the sweep starts.</summary>
    static int Lowest(ChartMesh chart) {
        var best = 0;

        for (var vertex = 1; vertex < chart.VertexCount; vertex++) {
            if (Before(chart.Positions[vertex], chart.Positions[best])) {
                best = vertex;
            }
        }

        return best;
    }

    /// <summary>The vertex most edges away from <paramref name="from" />, ties broken by position.</summary>
    static int Farthest(ChartMesh chart, int from) {
        var depth = new int[chart.VertexCount];

        Array.Fill(depth, -1);
        depth[from] = 0;

        var queue = new Queue<int>();
        queue.Enqueue(from);

        var best = from;

        while (queue.Count > 0) {
            var vertex = queue.Dequeue();

            if (depth[vertex] > depth[best]
                || (depth[vertex] == depth[best] && Before(chart.Positions[vertex], chart.Positions[best]))) {
                best = vertex;
            }

            for (var index = chart.AdjacentStart[vertex]; index < chart.AdjacentStart[vertex + 1]; index++) {
                var next = chart.Adjacent[index];

                if (depth[next] < 0) {
                    depth[next] = depth[vertex] + 1;
                    queue.Enqueue(next);
                }
            }
        }

        return best;
    }

    /// <summary>Lexicographic order on the coordinates, which is a total order on distinct positions.</summary>
    static bool Before(Vector3 left, Vector3 right) {
        if (left.X != right.X) {
            return left.X < right.X;
        }

        return left.Y != right.Y ? left.Y < right.Y : left.Z < right.Z;
    }
}
