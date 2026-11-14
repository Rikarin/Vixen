// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Texturing.Painting;

/// <summary>Loop fills: the ring of quads a click runs across.</summary>
public sealed partial class PaintMeshSurface {
    /// <summary>Which triangles a polygon fill takes from one triangle, clicked at a place on it.</summary>
    /// <param name="triangle">The triangle clicked.</param>
    /// <param name="mode">How much of the mesh a click takes.</param>
    /// <param name="near">
    ///     Where on the mesh the click landed, in the mesh's own space: which edge of the quad a loop runs across.
    ///     Every other mode takes the same triangles wherever on the triangle it lands.
    /// </param>
    /// <param name="into">Cleared, then given the triangles, the clicked one first.</param>
    public void Select(int triangle, PaintFillMode mode, Vector3 near, List<int> into) {
        if (mode != PaintFillMode.Loop) {
            Select(triangle, mode, into);

            return;
        }

        ArgumentNullException.ThrowIfNull(into);
        ArgumentOutOfRangeException.ThrowIfNegative(triangle);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(triangle, Projection.Triangles);

        loops.Value.Walk(triangle, near, into);
    }

    /// <summary>
    ///     The quads of a mesh of triangles, and the loops through them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A quad is found, not read, as a polygon is.</b> A mesh here is triangles, so a quad is two triangles
    ///         that share the longest edge of each, the diagonal a quad is cut along, turned less than a third of a turn
    ///         from each other. A triangle with no such partner is a face of its own, and a loop stops before it: a
    ///         loop runs through quads, so it stops at the fan of triangles that caps a cylinder.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Edges are edges of the mesh, not of the layout.</b> Corners are welded by position, so a loop runs
    ///         on across a UV seam, and a quad cut by a seam along its diagonal is still a quad.
    ///     </para>
    /// </remarks>
    sealed class FaceLoops {
        /// <summary>The least cosine between a quad's two halves: a third of a turn.</summary>
        const float Folded = 0.5f;

        /// <summary>How much longer, as a share of its square, a diagonal is than a quad's sides, past rounding.</summary>
        const float Tie = 1e-5f;

        readonly PaintProjection mesh;
        readonly int[] corners;

        /// <summary>For each triangle, the one it makes a quad with, or -1.</summary>
        readonly int[] partner;

        /// <summary>For each edge of the mesh, the triangles on it: the second is -1 for one, -2 for more than two.</summary>
        readonly Dictionary<long, (int First, int Second)> sides;

        FaceLoops(PaintProjection mesh, int[] corners) {
            this.mesh = mesh;
            this.corners = corners;
            sides = new(mesh.Triangles * 3 / 2);

            for (var triangle = 0; triangle < mesh.Triangles; triangle++) {
                for (var side = 0; side < 3; side++) {
                    var key = Key(Edge(triangle, side));

                    sides[key] = sides.TryGetValue(key, out var held)
                        ? held.Second == -1 ? (held.First, triangle) : (held.First, -2)
                        : (triangle, -1);
                }
            }

            partner = new int[mesh.Triangles];

            for (var triangle = 0; triangle < mesh.Triangles; triangle++) {
                partner[triangle] = Partner(triangle);
            }
        }

        public static FaceLoops Of(PaintProjection mesh, int[] corners) => new(mesh, corners);

        /// <summary>The loop through a triangle's quad, across its edge nearest a place.</summary>
        public void Walk(int triangle, Vector3 near, List<int> into) {
            into.Clear();
            into.Add(triangle);

            var mate = partner[triangle];

            if (mate < 0) {
                return;
            }

            into.Add(mate);

            HashSet<int> seen = [triangle, mate];
            Span<(int, int)> edges = stackalloc (int, int)[4];

            Boundary(triangle, edges);

            var across = Nearest(triangle, near);

            if (Opposite(edges, across) is not { } back) {
                return;
            }

            Step(across, seen, into);
            Step(back, seen, into);
        }

        /// <summary>Walks from a quad out through an edge, quad by quad, until the loop closes or ends.</summary>
        void Step((int, int) exit, HashSet<int> seen, List<int> into) {
            Span<(int, int)> edges = stackalloc (int, int)[4];

            while (sides.TryGetValue(Key(exit), out var on) && on.Second >= 0) {
                var next = seen.Contains(on.First) ? on.Second : on.First;
                var mate = partner[next];

                if (seen.Contains(next) || mate < 0 || seen.Contains(mate)) {
                    return;
                }

                seen.Add(next);
                seen.Add(mate);
                into.Add(next);
                into.Add(mate);
                Boundary(next, edges);

                if (Opposite(edges, exit) is not { } onward) {
                    return;
                }

                exit = onward;
            }
        }

        /// <summary>The triangle a triangle makes a quad with, across its longest edge, or -1.</summary>
        int Partner(int triangle) {
            var diagonal = Longest(triangle);

            if (diagonal < 0
                || !sides.TryGetValue(Key(Edge(triangle, diagonal)), out var on)
                || on.Second < 0) {
                return -1;
            }

            var other = on.First == triangle ? on.Second : on.First;
            var across = Longest(other);

            if (across < 0
                || Key(Edge(other, across)) != Key(Edge(triangle, diagonal))
                || corners[triangle * 3 + (diagonal + 2) % 3] == corners[other * 3 + (across + 2) % 3]) {
                return -1;
            }

            return Vector3.Dot(Normal(triangle), Normal(other)) >= Folded ? other : -1;
        }

        /// <summary>The four edges round a quad: the two of each half that are not the diagonal.</summary>
        void Boundary(int triangle, Span<(int, int)> into) {
            var count = 0;

            foreach (var half in (ReadOnlySpan<int>)[triangle, partner[triangle]]) {
                var diagonal = Longest(half);

                for (var side = 0; side < 3; side++) {
                    if (side != diagonal) {
                        into[count++] = Edge(half, side);
                    }
                }
            }
        }

        /// <summary>The edge of a quad across from one, sharing no corner with it, or null for a quad that is not one.</summary>
        static (int, int)? Opposite(ReadOnlySpan<(int, int)> edges, (int, int) edge) {
            foreach (var other in edges) {
                if (other.Item1 != edge.Item1
                    && other.Item1 != edge.Item2
                    && other.Item2 != edge.Item1
                    && other.Item2 != edge.Item2) {
                    return other;
                }
            }

            return null;
        }

        /// <summary>The edge of a triangle's quad nearest a place on the mesh.</summary>
        (int, int) Nearest(int triangle, Vector3 near) {
            var best = (0, 0);
            var least = float.MaxValue;

            foreach (var half in (ReadOnlySpan<int>)[triangle, partner[triangle]]) {
                var diagonal = Longest(half);

                for (var side = 0; side < 3; side++) {
                    if (side == diagonal) {
                        continue;
                    }

                    var (from, to) = Ends(half, side);
                    var distance = Distance(near, from, to);

                    if (distance < least) {
                        (least, best) = (distance, Edge(half, side));
                    }
                }
            }

            return best;
        }

        /// <summary>
        ///     Which side of a triangle is longest: 0 from its first corner, 1 from its second, 2 from its third; or -1
        ///     when two tie for it.
        /// </summary>
        /// <remarks>
        ///     ⚠ <b>A tie is no diagonal.</b> A fan of triangles round a cap has two spokes of one length in each, and
        ///     taking either as the diagonal paired neighbouring triangles of the fan into kites, which a loop then ran
        ///     through and on down the far side. A rectangle's diagonal is always strictly its longest side, by a share of
        ///     its length that falls below <see cref="Tie" /> only for a quad some three hundred times longer than wide.
        /// </remarks>
        int Longest(int triangle) {
            mesh.Triangle(triangle, out var a, out var b, out var c, out _, out _, out _);

            ReadOnlySpan<float> lengths =
                [Vector3.DistanceSquared(a, b), Vector3.DistanceSquared(b, c), Vector3.DistanceSquared(c, a)];
            var longest = lengths[0] >= lengths[1] && lengths[0] >= lengths[2] ? 0 : lengths[1] >= lengths[2] ? 1 : 2;

            for (var side = 0; side < 3; side++) {
                if (side != longest && lengths[side] * (1f + Tie) >= lengths[longest]) {
                    return -1;
                }
            }

            return longest;
        }

        Vector3 Normal(int triangle) {
            mesh.Triangle(triangle, out var a, out var b, out var c, out _, out _, out _);

            var normal = Vector3.Cross(b - a, c - a);
            var length = normal.Length();

            return length > 0f ? normal / length : Vector3.Zero;
        }

        /// <summary>A side's two corners, as welded.</summary>
        (int, int) Edge(int triangle, int side) =>
            (corners[triangle * 3 + side], corners[triangle * 3 + (side + 1) % 3]);

        /// <summary>A side's two ends, in the mesh's own space.</summary>
        (Vector3 From, Vector3 To) Ends(int triangle, int side) {
            mesh.Triangle(triangle, out var a, out var b, out var c, out _, out _, out _);

            return side switch {
                0 => (a, b),
                1 => (b, c),
                _ => (c, a)
            };
        }

        /// <summary>An edge's name, whichever way round its corners come.</summary>
        static long Key((int, int) edge) {
            var (low, high) = edge.Item1 < edge.Item2 ? edge : (edge.Item2, edge.Item1);

            return ((long)low << 32) | (uint)high;
        }

        /// <summary>How far a point is from a segment.</summary>
        static float Distance(Vector3 point, Vector3 from, Vector3 to) {
            var along = to - from;
            var length = along.LengthSquared();
            var share = length > 0f ? Math.Clamp(Vector3.Dot(point - from, along) / length, 0f, 1f) : 0f;

            return Vector3.Distance(point, from + along * share);
        }
    }
}
