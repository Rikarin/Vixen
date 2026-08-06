// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Uv.Charting;

/// <summary>What an island's outline is like, which is the half of quality distortion does not measure.</summary>
/// <remarks>
///     <para>
///         docs/plan/42 § Part 4 adopts MeshTailor's metric set, and three of the five are about the
///         <i>shape</i> of an island rather than about its mapping: compactness as <c>4πA/P²</c>,
///         convexity as <c>A/A(hull)</c>, and boundary jaggedness as a discrete curvature over resampled
///         boundary loops. § Part 1 notes that nobody else in the field writes the last one down, and
///         that it is exactly what separates <i>"low distortion"</i> from <i>"an artist would accept
///         this"</i>.
///     </para>
///     <para>
///         ⚠ <b>Measured from the mesh and the island together, because an island alone has no
///         topology.</b> <see cref="UvIsland" /> is coordinates per corner and corners in triples, with
///         no statement about which corners are the same point — a seam is precisely two coordinates at
///         one position, so welding by coordinate would be wrong at exactly the vertices that matter.
///         The mesh's corner layer says which corners share a position, and inside one chart that is the
///         answer.
///     </para>
///     <para>
///         ⚠ <b>Jaggedness is normalized so that a convex outline is exactly zero.</b> The total turning
///         of any simple closed curve is <c>2π</c>, so the excess over that is the part a wobble
///         contributed — which makes the figure dimensionless, independent of how finely the boundary
///         happens to be tessellated, and comparable between an island of forty vertices and one of four
///         thousand.
///     </para>
/// </remarks>
static class IslandShape {
    /// <summary>How many points a boundary loop is resampled to before its curvature is summed.</summary>
    /// <remarks>
    ///     ⚠ <b>Resampled at equal arc length rather than measured at the vertices, and that is the
    ///     point of the metric.</b> Turning summed at the vertices reports how the boundary was
    ///     <i>tessellated</i> — a finely divided smooth arc has many tiny turns and a coarse one has few
    ///     large ones, for the same shape. Equal arc length asks the question about the curve.
    /// </remarks>
    const int Samples = 64;

    /// <summary>One island's outline, all three figures dimensionless.</summary>
    /// <param name="Area">The island's area in its own coordinates.</param>
    /// <param name="Compactness">4πA/P² over the outer boundary. One is a disc and zero is a tendril.</param>
    /// <param name="Convexity">A/A(hull). One is convex.</param>
    /// <param name="Jaggedness">Turning in excess of a convex outline's, over 2π. Zero is smooth.</param>
    internal readonly record struct Measured(double Area, double Compactness, double Convexity, double Jaggedness);

    /// <summary>Measures one island's outline.</summary>
    /// <param name="mesh">The mesh the island came from.</param>
    /// <param name="island">The island.</param>
    /// <returns>The three shape figures, and the area they are derived from.</returns>
    public static Measured Measure(EditMesh mesh, UvIsland island) {
        if (island.Coordinates is null || island.Corners is null || island.TriangleCount == 0) {
            return default;
        }

        var coordinate = new Dictionary<int, Vector2>();
        var vertices = new int[island.Corners.Count];

        for (var slot = 0; slot < island.Corners.Count; slot++) {
            var position = mesh.Corners[island.Corners[slot]];

            vertices[slot] = position;
            coordinate.TryAdd(position, island.Coordinates[slot]);
        }

        var area = 0d;
        var sides = new Dictionary<(int, int), int>();

        for (var triangle = 0; triangle < island.TriangleCount; triangle++) {
            var a = vertices[triangle * 3];
            var b = vertices[(triangle * 3) + 1];
            var c = vertices[(triangle * 3) + 2];

            var pa = island.Coordinates[triangle * 3];
            var pb = island.Coordinates[(triangle * 3) + 1];
            var pc = island.Coordinates[(triangle * 3) + 2];

            area += 0.5d
                * Math.Abs(
                    ((pb.X - (double)pa.X) * (pc.Y - (double)pa.Y))
                    - ((pb.Y - (double)pa.Y) * (pc.X - (double)pa.X))
                );

            Count(sides, a, b);
            Count(sides, b, c);
            Count(sides, c, a);
        }

        var loops = Loops(sides, coordinate);

        if (loops.Count == 0 || !(area > 0d)) {
            return new(area, 0d, 0d, 0d);
        }

        var outer = loops[0];
        var longest = Perimeter(outer);

        foreach (var loop in loops) {
            var perimeter = Perimeter(loop);

            if (perimeter > longest) {
                longest = perimeter;
                outer = loop;
            }
        }

        var hull = Hull(loops);
        var compactness = longest > 0d ? 4d * Math.PI * area / (longest * longest) : 0d;
        var convexity = hull > 0d ? area / hull : 0d;

        return new(area, compactness, Math.Min(1d, convexity), Jaggedness(outer, longest));
    }

    static void Count(Dictionary<(int, int), int> sides, int a, int b) {
        var key = a < b ? (a, b) : (b, a);

        sides[key] = sides.GetValueOrDefault(key) + 1;
    }

    /// <summary>The boundary loops, as coordinate rings.</summary>
    /// <remarks>
    ///     ⚠ <b>Started from the lowest position index and stepped to the lowest available neighbour, so
    ///     that a walk over a dictionary's contents is still a function of the mesh.</b> The dictionary
    ///     is only ever probed by key here; the ordering comes from the sorted adjacency below.
    /// </remarks>
    static List<List<Vector2>> Loops(Dictionary<(int, int), int> sides, Dictionary<int, Vector2> coordinate) {
        var adjacency = new SortedDictionary<int, List<int>>();

        foreach (var (side, count) in sides) {
            if (count != 1) {
                continue;
            }

            Join(adjacency, side.Item1, side.Item2);
            Join(adjacency, side.Item2, side.Item1);
        }

        foreach (var neighbours in adjacency.Values) {
            neighbours.Sort();
        }

        var walked = new HashSet<int>();
        var loops = new List<List<Vector2>>();

        foreach (var start in adjacency.Keys) {
            if (!walked.Add(start)) {
                continue;
            }

            var loop = new List<Vector2> { coordinate[start] };
            var previous = -1;
            var current = start;

            while (true) {
                var next = -1;

                foreach (var neighbour in adjacency[current]) {
                    if (neighbour != previous && (neighbour == start || !walked.Contains(neighbour))) {
                        next = neighbour;

                        break;
                    }
                }

                if (next < 0 || next == start) {
                    break;
                }

                walked.Add(next);
                loop.Add(coordinate[next]);
                previous = current;
                current = next;
            }

            if (loop.Count >= 3) {
                loops.Add(loop);
            }
        }

        return loops;
    }

    static void Join(SortedDictionary<int, List<int>> adjacency, int from, int to) {
        if (!adjacency.TryGetValue(from, out var neighbours)) {
            adjacency[from] = neighbours = [];
        }

        if (!neighbours.Contains(to)) {
            neighbours.Add(to);
        }
    }

    static double Perimeter(List<Vector2> loop) {
        var total = 0d;

        for (var index = 0; index < loop.Count; index++) {
            var a = loop[index];
            var b = loop[(index + 1) % loop.Count];

            total += Math.Sqrt(((b.X - (double)a.X) * (b.X - (double)a.X)) + ((b.Y - (double)a.Y) * (b.Y - (double)a.Y)));
        }

        return total;
    }

    /// <summary>The area of the convex hull of every boundary point.</summary>
    /// <remarks>
    ///     A monotone chain over <see cref="ExactPredicates.Orient2D" /> rather than a float cross
    ///     product, for the reason <c>Distortion</c> gives: three points that are exactly collinear —
    ///     which every straight run of a boundary is full of — are precisely the case a naive test gets
    ///     wrong, and no epsilon scaled to the inputs rescues it because the error is in the
    ///     subtractions.
    /// </remarks>
    static double Hull(List<List<Vector2>> loops) {
        var points = new List<Vector2>();

        foreach (var loop in loops) {
            points.AddRange(loop);
        }

        if (points.Count < 3) {
            return 0d;
        }

        points.Sort(
            static (left, right) => left.X != right.X ? left.X.CompareTo(right.X) : left.Y.CompareTo(right.Y)
        );

        var chain = new Vector2[2 * points.Count];
        var size = 0;

        for (var index = 0; index < points.Count; index++) {
            while (size >= 2 && ExactPredicates.Orient2D(chain[size - 2], chain[size - 1], points[index]) <= 0) {
                size--;
            }

            chain[size++] = points[index];
        }

        for (int index = points.Count - 2, lower = size + 1; index >= 0; index--) {
            while (size >= lower && ExactPredicates.Orient2D(chain[size - 2], chain[size - 1], points[index]) <= 0) {
                size--;
            }

            chain[size++] = points[index];
        }

        var area = 0d;

        for (var index = 0; index < size - 1; index++) {
            var a = chain[index];
            var b = chain[(index + 1) % (size - 1)];

            area += ((double)a.X * b.Y) - ((double)b.X * a.Y);
        }

        return Math.Abs(area) * 0.5d;
    }

    /// <summary>Turning summed over a loop resampled at equal arc length, in excess of a convex outline's.</summary>
    static double Jaggedness(List<Vector2> loop, double perimeter) {
        if (loop.Count < 3 || !(perimeter > 0d)) {
            return 0d;
        }

        var samples = new Vector2[Samples];
        var step = perimeter / Samples;
        var walked = 0d;
        var vertex = 0;
        var along = 0d;

        for (var index = 0; index < Samples; index++) {
            var target = index * step;

            while (vertex < loop.Count) {
                var a = loop[vertex];
                var b = loop[(vertex + 1) % loop.Count];
                var length = Math.Sqrt(
                    ((b.X - (double)a.X) * (b.X - (double)a.X)) + ((b.Y - (double)a.Y) * (b.Y - (double)a.Y))
                );

                if (walked + length >= target || vertex == loop.Count - 1) {
                    along = length > 0d ? Math.Clamp((target - walked) / length, 0d, 1d) : 0d;

                    break;
                }

                walked += length;
                vertex++;
            }

            var from = loop[Math.Min(vertex, loop.Count - 1)];
            var to = loop[(Math.Min(vertex, loop.Count - 1) + 1) % loop.Count];

            samples[index] = new(
                (float)(from.X + ((to.X - (double)from.X) * along)),
                (float)(from.Y + ((to.Y - (double)from.Y) * along))
            );
        }

        var turning = 0d;

        for (var index = 0; index < Samples; index++) {
            var previous = samples[(index + Samples - 1) % Samples];
            var here = samples[index];
            var next = samples[(index + 1) % Samples];

            double ax = here.X - (double)previous.X, ay = here.Y - (double)previous.Y;
            double bx = next.X - (double)here.X, by = next.Y - (double)here.Y;

            var cross = (ax * by) - (ay * bx);
            var dot = (ax * bx) + (ay * by);

            if (cross != 0d || dot != 0d) {
                turning += Math.Abs(Math.Atan2(cross, dot));
            }
        }

        return Math.Max(0d, (turning - (2d * Math.PI)) / (2d * Math.PI));
    }
}
