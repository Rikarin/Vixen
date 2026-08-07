// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>Stage two: five sources into an edge set, an edge set into chains, chains into boundaries.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/41 § D4.</b> Dihedral angle, explicit creases, face-group boundaries, the
///         input's own UV seams and the artist's guides, unioned; chained into polylines; endpoints and
///         junctions promoted to corners; short chains pruned; nearly-straight chains simplified.
///     </para>
///     <para>
///         ⚠ <b>The prune is not a tidying step and it is the one that decides whether any of this
///         runs on generated input.</b> § D4 says it in one clause — "chains shorter than a threshold
///         are pruned (marching-cubes output produces thousands of two-edge 'features' that are
///         noise)" — and the measurement is worse than the clause suggests. On R1's staircase sphere
///         the raw dihedral set chains into hundreds of runs, nearly all of them a single voxel facet
///         boundary; every one of them would be a hard constraint on the field and a boundary of the
///         layout, so the field would align to the voxel grid and the layout would be one patch per
///         facet. <c>FeatureDetectionTests.Pruning_is_what_makes_generated_input_tractable</c> is the
///         number.
///     </para>
///     <para>
///         ⚠ <b>Every threshold here is a fraction of the bounding-box diagonal.</b> Same rule as
///         conditioning, same reason, and <c>FeatureScaleInvarianceTests</c> is the guard.
///     </para>
/// </remarks>
static class FeatureDetector {
    /// <summary>Chains shorter than this fraction of the diagonal are noise.</summary>
    /// <remarks>
    ///     ⚠ <b>An arc length and not an edge count, and a box is why.</b> The obvious prune is "fewer
    ///     than <i>n</i> edges", and on <c>MeshShapes</c>' box every one of the twelve real features
    ///     is exactly <b>one</b> edge long — so an edge-count prune of three deletes a cube's entire
    ///     feature set and leaves a marching-cubes sphere's, which have three or four edges apiece.
    ///     Length relative to the model separates them the right way round: a cube's edge is
    ///     <c>1/√3</c> of its own diagonal, and a voxel facet boundary is a percent or two of the
    ///     sphere's.
    /// </remarks>
    public const float MinFeatureFraction = 0.15f;

    /// <summary>How far a chain may bow from a straight run between two keys, as a fraction of the diagonal.</summary>
    public const float SimplifyFraction = 0.002f;

    /// <summary>How near a handed-in curve has to pass an edge to claim it, as a fraction of the diagonal.</summary>
    /// <remarks>
    ///     A crease or a seam taken off the source mesh lies exactly on a source edge, and stage one
    ///     then welds, cuts and relaxes that surface — so "exactly" is not available by the time this
    ///     runs, and a tolerance is not optional. One percent is loose enough to survive five rounds of
    ///     the pre-remesh and tight enough that a curve does not claim the ring of edges beside it.
    /// </remarks>
    public const float CurveTolerance = 0.01f;

    /// <summary>How many prune-and-rechain rounds run before the set is called stable.</summary>
    /// <remarks>
    ///     ⚠ <b>Pruning changes the chaining, which is why this is a loop.</b> Deleting a stub that
    ///     hung off a junction drops that junction's degree from three to two, so it stops being a
    ///     corner and the two chains that ended there become one longer chain — which may now clear
    ///     the threshold that neither half did. One pass leaves a feature set full of pairs that
    ///     should have been joined.
    /// </remarks>
    public const int PruneRounds = 8;

    /// <summary>Runs § D4 over a conditioned surface.</summary>
    /// <param name="mesh">The conditioned view. Read, never modified.</param>
    /// <param name="settings">Which sources are on, and the dihedral threshold.</param>
    /// <param name="curves">Creases, seams and guides, in the mesh's own space. May be empty.</param>
    /// <returns>The feature graph.</returns>
    public static FeatureGraph Detect(
        ManifoldMesh mesh,
        RemeshSettings settings,
        IReadOnlyList<FeatureCurve>? curves = null
    ) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(settings);

        var halves = mesh.Triangles.Length;
        var sources = new FeatureSource[halves];
        var strength = new float[halves];

        Array.Fill(strength, 1f);

        Intrinsic(mesh, settings, sources);

        if (curves is { Count: > 0 }) {
            Extrinsic(mesh, curves, sources, strength);
        }

        // Below this, every step reads and rewrites `sources` and nothing else — the prune loop is a
        // chain, a keep test and a set of edges to clear.
        var diagonal = mesh.Diagonal;
        var minimum = MinFeatureFraction * diagonal;
        var chains = new List<FeaturePolyline>();
        var pruned = 0;
        var prunedEdges = 0;

        for (var round = 0; round <= PruneRounds; round++) {
            chains.Clear();
            Chain(mesh, sources, strength, chains);

            if (round == PruneRounds) {
                break;
            }

            var cut = 0;

            foreach (var chain in chains) {
                if (Length(mesh, chain) >= minimum) {
                    continue;
                }

                Clear(mesh, sources, chain);

                cut++;
                prunedEdges += chain.EdgeCount;
            }

            if (cut == 0) {
                break;
            }

            pruned += cut;
        }

        var simplified = new List<FeaturePolyline>(chains.Count);

        foreach (var chain in chains) {
            simplified.Add(chain with { Keys = Simplify(mesh, chain.Vertices, SimplifyFraction * diagonal) });
        }

        // Sorted so that two runs hand the layout the same list in the same order. `OrderBy` is
        // stable and the chain order it is given is already deterministic, so the key does not have
        // to separate every pair.
        var ordered = simplified.OrderBy(Lowest).ToArray();
        var (vertexSources, degree, corner, corners) = Vertices(mesh, sources);

        return new(
            sources,
            vertexSources,
            strength,
            Tangents(mesh, sources, vertexSources, ordered),
            corner,
            degree,
            ordered,
            corners,
            pruned,
            prunedEdges
        );
    }

    /// <summary>The two sources that are read straight off the view: dihedral angle, group boundary — plus the rim.</summary>
    static void Intrinsic(ManifoldMesh mesh, RemeshSettings settings, FeatureSource[] sources) {
        // cos of the threshold, taken once. The comparison is on the dot of the two face normals, so
        // "over the angle" is "under the cosine".
        var limit = MathF.Cos(MathUtil.DegreesToRadians(Math.Clamp(settings.FeatureAngle, 0f, 180f)));

        for (var half = 0; half < sources.Length; half++) {
            var twin = mesh.Twin(half);

            if (twin < 0) {
                // ⚠ The rim is a feature only when Freeze Border is on, which is § D5's rule and not
                // § D4's. An open rim is a boundary of the layout either way — R3 does not need to be
                // told — but pinning the cross to it is a decision the setting owns.
                if (settings.FreezeBorder) {
                    sources[half] |= FeatureSource.Boundary;
                }

                continue;
            }

            if (twin < half) {
                continue;
            }

            var one = half / 3;
            var two = twin / 3;

            // ⚠ A group boundary is a crease only when somebody assigned the groups. Where they came
            // from `EditMesh.Regroup`'s coplanarity guess this test is true across almost every edge of
            // a faceted surface, which declares the whole mesh one enormous feature graph — see
            // MeshGroupSource, and docs/plan/41 § D4 for what the source is meant to mean.
            if (settings.KeepGroups
                && mesh.GroupSource is MeshGroupSource.Assigned
                && mesh.Group(one) != mesh.Group(two)) {
                sources[half] |= FeatureSource.Group;
                sources[twin] |= FeatureSource.Group;
            }

            var a = ScaleSafe.Normal(mesh, one);
            var b = ScaleSafe.Normal(mesh, two);

            // A sliver has no normal at all, and calling the edge beside it a feature is how a
            // degenerate triangle becomes a permanent crease in the output.
            if (a.LengthSquared() <= 0f || b.LengthSquared() <= 0f) {
                continue;
            }

            if (Vector3.Dot(a, b) < limit) {
                sources[half] |= FeatureSource.Dihedral;
                sources[twin] |= FeatureSource.Dihedral;
            }
        }
    }

    /// <summary>The three sources that arrive as curves: creases, UV seams and guides.</summary>
    static void Extrinsic(
        ManifoldMesh mesh,
        IReadOnlyList<FeatureCurve> curves,
        FeatureSource[] sources,
        float[] strength
    ) {
        var tolerance = CurveTolerance * mesh.Diagonal;

        if (tolerance <= 0f) {
            return;
        }

        var grid = new VertexGrid(mesh.Positions, tolerance);
        var stamp = new int[mesh.VertexCount];
        var touched = new List<int>();
        var mark = 0;

        foreach (var curve in curves) {
            if (curve.Points is not { Count: > 1 }) {
                continue;
            }

            mark++;
            touched.Clear();
            grid.Near(curve.Points, tolerance, stamp, mark, touched);

            var soft = curve.Source == FeatureSource.Guide ? Math.Clamp(curve.Strength, 0f, 1f) : 1f;

            foreach (var vertex in touched) {
                foreach (var half in mesh.Outgoing(vertex)) {
                    var other = mesh.Triangles[ManifoldMesh.Next(half)];

                    if (stamp[other] != mark) {
                        continue;
                    }

                    // Both ends near the curve is not enough on its own: a chord across a tight bend
                    // has both, and marking it puts a shortcut into a chain that then has a junction
                    // it should not. The midpoint test is what rules the chord out.
                    var middle = (mesh.Positions[vertex] + mesh.Positions[other]) * 0.5f;

                    if (Distance(curve.Points, middle) > tolerance) {
                        continue;
                    }

                    var twin = mesh.Twin(half);

                    sources[half] |= curve.Source;
                    strength[half] = MathF.Min(strength[half], soft);

                    if (twin >= 0) {
                        sources[twin] |= curve.Source;
                        strength[twin] = MathF.Min(strength[twin], soft);
                    }
                }
            }
        }
    }

    /// <summary>Every feature edge exactly once, walked into chains that end at corners.</summary>
    /// <remarks>
    ///     ⚠ <b>Adjacency is by <i>edge</i> and not by outgoing half-edge, and a rim is why.</b> An
    ///     interior edge has two halves, one leaving each end, so counting outgoing halves gives both
    ///     vertices a degree. A boundary edge has one, leaving one end — so the far end would show a
    ///     degree of zero, be classified as off the feature set entirely, and every chain along an
    ///     open rim would terminate one vertex early.
    /// </remarks>
    static void Chain(ManifoldMesh mesh, FeatureSource[] sources, float[] strength, List<FeaturePolyline> into) {
        var count = mesh.VertexCount;
        var starts = new int[count + 1];

        for (var half = 0; half < sources.Length; half++) {
            if (!Canonical(mesh, sources, half)) {
                continue;
            }

            starts[mesh.Triangles[half] + 1]++;
            starts[mesh.Triangles[ManifoldMesh.Next(half)] + 1]++;
        }

        for (var vertex = 1; vertex <= count; vertex++) {
            starts[vertex] += starts[vertex - 1];
        }

        var neighbours = new int[starts[count]];
        var edges = new int[starts[count]];
        var cursor = new int[count];

        for (var half = 0; half < sources.Length; half++) {
            if (!Canonical(mesh, sources, half)) {
                continue;
            }

            var from = mesh.Triangles[half];
            var to = mesh.Triangles[ManifoldMesh.Next(half)];

            neighbours[starts[from] + cursor[from]] = to;
            edges[starts[from] + cursor[from]] = half;
            cursor[from]++;

            neighbours[starts[to] + cursor[to]] = from;
            edges[starts[to] + cursor[to]] = half;
            cursor[to]++;
        }

        // Ascending within each vertex, so a junction is left by the same edge on every machine.
        for (var vertex = 0; vertex < count; vertex++) {
            Array.Sort(neighbours, edges, starts[vertex], starts[vertex + 1] - starts[vertex]);
        }

        var used = new bool[sources.Length];
        var chain = new List<int>();

        // Open runs first, from every corner in ascending order. What is left over afterwards is
        // exactly the set of closed loops, because a loop has no vertex of degree anything but two.
        for (var pass = 0; pass < 2; pass++) {
            for (var vertex = 0; vertex < count; vertex++) {
                var low = starts[vertex];
                var high = starts[vertex + 1];
                var open = high - low != 2;

                if (low == high || open != (pass == 0)) {
                    continue;
                }

                for (var at = low; at < high; at++) {
                    if (used[edges[at]]) {
                        continue;
                    }

                    Walk(vertex, at, starts, neighbours, edges, used, chain);
                    into.Add(Emit(chain, sources, strength, edges, starts, neighbours));
                }
            }
        }
    }

    /// <summary>Follows a chain from one end until it reaches a corner or comes back.</summary>
    static void Walk(
        int start,
        int first,
        int[] starts,
        int[] neighbours,
        int[] edges,
        bool[] used,
        List<int> chain
    ) {
        chain.Clear();
        chain.Add(start);

        var slot = first;

        while (true) {
            used[edges[slot]] = true;

            var next = neighbours[slot];

            chain.Add(next);

            if (next == start) {
                return;
            }

            var low = starts[next];
            var high = starts[next + 1];

            if (high - low != 2) {
                return;
            }

            var take = used[edges[low]] ? low + 1 : low;

            if (used[edges[take]]) {
                return;
            }

            slot = take;
        }
    }

    static FeaturePolyline Emit(
        List<int> chain,
        FeatureSource[] sources,
        float[] strength,
        int[] edges,
        int[] starts,
        int[] neighbours
    ) {
        var union = FeatureSource.None;
        var softest = 1f;

        for (var at = 0; at + 1 < chain.Count; at++) {
            var half = Edge(chain[at], chain[at + 1], starts, neighbours, edges);

            if (half < 0) {
                continue;
            }

            union |= sources[half];
            softest = MathF.Min(softest, strength[half]);
        }

        var vertices = chain.ToArray();

        return new(vertices, [0, vertices.Length - 1], union, softest, vertices[0] == vertices[^1]);
    }

    static int Edge(int from, int to, int[] starts, int[] neighbours, int[] edges) {
        for (var at = starts[from]; at < starts[from + 1]; at++) {
            if (neighbours[at] == to) {
                return edges[at];
            }
        }

        return -1;
    }

    /// <summary>Whether a half-edge is the one of its pair that stands for the edge.</summary>
    static bool Canonical(ManifoldMesh mesh, FeatureSource[] sources, int half) {
        if (sources[half] == FeatureSource.None) {
            return false;
        }

        var twin = mesh.Twin(half);

        return twin < 0 || half < twin;
    }

    /// <summary>Takes a chain's edges back out of the set, both halves of each.</summary>
    static void Clear(ManifoldMesh mesh, FeatureSource[] sources, FeaturePolyline chain) {
        for (var at = 0; at + 1 < chain.Vertices.Length; at++) {
            var from = chain.Vertices[at];
            var to = chain.Vertices[at + 1];

            foreach (var half in mesh.Outgoing(from)) {
                if (mesh.Triangles[ManifoldMesh.Next(half)] != to) {
                    continue;
                }

                var twin = mesh.Twin(half);

                sources[half] = FeatureSource.None;

                if (twin >= 0) {
                    sources[twin] = FeatureSource.None;
                }
            }

            // The other direction, for the rim: a boundary edge's only half leaves the far end.
            foreach (var half in mesh.Outgoing(to)) {
                if (mesh.Triangles[ManifoldMesh.Next(half)] == from) {
                    sources[half] = FeatureSource.None;
                }
            }
        }
    }

    static float Length(ManifoldMesh mesh, FeaturePolyline chain) {
        var total = 0f;

        for (var at = 0; at + 1 < chain.Vertices.Length; at++) {
            total += Vector3.Distance(mesh.Positions[chain.Vertices[at]], mesh.Positions[chain.Vertices[at + 1]]);
        }

        return total;
    }

    static int Lowest(FeaturePolyline chain) {
        var low = int.MaxValue;

        foreach (var vertex in chain.Vertices) {
            low = Math.Min(low, vertex);
        }

        return low;
    }

    /// <summary>Douglas–Peucker over the chain's own positions, iteratively so a long run cannot recurse deep.</summary>
    static int[] Simplify(ManifoldMesh mesh, int[] chain, float tolerance) {
        if (chain.Length <= 2) {
            return [.. Enumerable.Range(0, chain.Length)];
        }

        var keep = new bool[chain.Length];

        keep[0] = true;
        keep[^1] = true;

        // A closed run starts and ends at the same vertex, so the straight test between them is
        // degenerate. Splitting it at its furthest point first gives the recursion two real spans.
        var stack = new Stack<(int Low, int High)>();

        if (chain[0] == chain[^1] && chain.Length > 3) {
            var split = Furthest(mesh, chain, 0, chain.Length - 1, out _);

            keep[split] = true;
            stack.Push((0, split));
            stack.Push((split, chain.Length - 1));
        } else {
            stack.Push((0, chain.Length - 1));
        }

        while (stack.Count > 0) {
            var (low, high) = stack.Pop();

            if (high - low < 2) {
                continue;
            }

            var at = Furthest(mesh, chain, low, high, out var deviation);

            if (deviation <= tolerance) {
                continue;
            }

            keep[at] = true;
            stack.Push((low, at));
            stack.Push((at, high));
        }

        var keys = new List<int>();

        for (var at = 0; at < keep.Length; at++) {
            if (keep[at]) {
                keys.Add(at);
            }
        }

        return [.. keys];
    }

    static int Furthest(ManifoldMesh mesh, int[] chain, int low, int high, out float deviation) {
        var a = mesh.Positions[chain[low]];
        var b = mesh.Positions[chain[high]];
        var span = b - a;
        var lengthSquared = span.LengthSquared();

        var best = low + 1;
        var worst = -1f;

        for (var at = low + 1; at < high; at++) {
            var point = mesh.Positions[chain[at]];
            float distance;

            if (lengthSquared <= 0f) {
                distance = Vector3.Distance(point, a);
            } else {
                var t = Math.Clamp(Vector3.Dot(point - a, span) / lengthSquared, 0f, 1f);

                distance = Vector3.Distance(point, a + (span * t));
            }

            if (distance > worst) {
                worst = distance;
                best = at;
            }
        }

        deviation = worst;

        return best;
    }

    /// <summary>Per-vertex flags, degree, cornerhood and the corner list, from the surviving edge set.</summary>
    static (FeatureSource[] Sources, int[] Degree, bool[] Corner, int[] Corners) Vertices(
        ManifoldMesh mesh,
        FeatureSource[] sources
    ) {
        var count = mesh.VertexCount;
        var flags = new FeatureSource[count];
        var degree = new int[count];

        for (var half = 0; half < sources.Length; half++) {
            if (!Canonical(mesh, sources, half)) {
                continue;
            }

            var from = mesh.Triangles[half];
            var to = mesh.Triangles[ManifoldMesh.Next(half)];

            flags[from] |= sources[half];
            flags[to] |= sources[half];
            degree[from]++;
            degree[to]++;
        }

        var corner = new bool[count];
        var corners = new List<int>();

        for (var vertex = 0; vertex < count; vertex++) {
            if (degree[vertex] == 0 || degree[vertex] == 2) {
                continue;
            }

            corner[vertex] = true;
            corners.Add(vertex);
        }

        return (flags, degree, corner, [.. corners]);
    }

    /// <summary>The direction the cross is pinned to at each feature vertex.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Taken over a window along the <i>chain</i> and not from the vertex's own two
    ///         edges, and this is the difference between § D6's exit criterion being met and being
    ///         missed by half.</b> A crease that was straight in the source is a zigzag chain of edges
    ///         after conditioning — the pre-remesh moves every vertex — so an edge-to-edge tangent
    ///         alternates by tens of degrees along a feature that is geometrically straight. Pinning
    ///         the cross to <i>that</i> puts a large forced turning directly on the feature line, which
    ///         is precisely the pinch § D6 step two exists to remove, produced by the constraint that
    ///         was meant to prevent it. Measured on a box with a cylindrical bore: singularities on
    ///         feature lines fall from over half of all of them to a handful.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>At a junction the choice is arbitrary and it does not matter, which is a property
    ///         of 4-RoSy.</b> A cross stands for four directions ninety degrees apart, so a junction of
    ///         two perpendicular features is satisfied by either of them. Where they are not
    ///         perpendicular no single direction satisfies both, and the lowest-index incident feature
    ///         neighbour is taken — deterministic, and the residual is what the smoothing spreads.
    ///     </para>
    /// </remarks>
    static Vector3[] Tangents(
        ManifoldMesh mesh,
        FeatureSource[] sources,
        FeatureSource[] flags,
        FeaturePolyline[] chains
    ) {
        var tangents = new Vector3[mesh.VertexCount];

        // The chains first, ascending, so a vertex shared by two of them takes the lower one's
        // direction — the same rule the junction case uses, applied one level up.
        foreach (var chain in chains) {
            var vertices = chain.Vertices;

            for (var at = 0; at < vertices.Length; at++) {
                var vertex = vertices[at];

                if (tangents[vertex].LengthSquared() > 0f) {
                    continue;
                }

                var low = Math.Max(at - TangentWindow, 0);
                var high = Math.Min(at + TangentWindow, vertices.Length - 1);

                // A closed chain repeats its first vertex as its last, so a window that runs off one
                // end wraps into the other rather than shortening — otherwise the seam of a loop is
                // the one place the constraint is noisy again.
                if (chain.IsClosed && vertices.Length > 2) {
                    var span = vertices.Length - 1;

                    low = at - TangentWindow;
                    high = at + TangentWindow;

                    var before = vertices[((low % span) + span) % span];
                    var after = vertices[((high % span) + span) % span];

                    tangents[vertex] = Flatten(mesh, vertex, mesh.Positions[after] - mesh.Positions[before]);

                    continue;
                }

                tangents[vertex] = Flatten(
                    mesh,
                    vertex,
                    mesh.Positions[vertices[high]] - mesh.Positions[vertices[low]]
                );
            }
        }

        // Anything the chains did not reach — a vertex whose only feature edge belongs to a chain
        // that ended at it as a corner, which the window above already covered, or an edge left in
        // the set with no chain at all.
        var scratch = new List<int>();

        for (var vertex = 0; vertex < tangents.Length; vertex++) {
            if (flags[vertex] == FeatureSource.None || tangents[vertex].LengthSquared() > 0f) {
                continue;
            }

            Neighbours(mesh, sources, vertex, scratch);

            var first = -1;

            foreach (var neighbour in scratch) {
                if (first < 0 || neighbour < first) {
                    first = neighbour;
                }
            }

            if (first >= 0) {
                tangents[vertex] = Flatten(mesh, vertex, mesh.Positions[first] - mesh.Positions[vertex]);
            }
        }

        return tangents;
    }

    /// <summary>How wide the window along a chain is, in chain steps either side.</summary>
    /// <remarks>
    ///     Two steps either side is five vertices, which is enough to average out one round of the
    ///     pre-remesh's zigzag and short enough that a genuinely curved crease is still followed.
    /// </remarks>
    public const int TangentWindow = 2;

    /// <summary>A direction projected into a vertex's tangent plane, normalised.</summary>
    static Vector3 Flatten(ManifoldMesh mesh, int vertex, Vector3 along) {
        var flat = ScaleSafe.Flatten(along, mesh.VertexNormal(vertex));

        return flat.LengthSquared() > 0f ? flat : ScaleSafe.Unit(along);
    }

    /// <summary>The feature neighbours of a vertex, both directions of a rim edge included.</summary>
    static void Neighbours(ManifoldMesh mesh, FeatureSource[] sources, int vertex, List<int> into) {
        into.Clear();

        foreach (var half in mesh.Outgoing(vertex)) {
            if (sources[half] != FeatureSource.None) {
                into.Add(mesh.Triangles[ManifoldMesh.Next(half)]);
            }

            // The half running the other way along a rim edge belongs to no triangle at this
            // vertex, so it is found through the previous half-edge of the same triangle instead.
            var back = ManifoldMesh.Previous(half);

            if (mesh.Twin(back) < 0 && sources[back] != FeatureSource.None) {
                into.Add(mesh.Triangles[back]);
            }
        }
    }

    static float Distance(IReadOnlyList<Vector3> curve, Vector3 point) {
        var best = float.MaxValue;

        for (var at = 0; at + 1 < curve.Count; at++) {
            var a = curve[at];
            var span = curve[at + 1] - a;
            var lengthSquared = span.LengthSquared();

            var near = lengthSquared <= 0f
                ? a
                : a + (span * Math.Clamp(Vector3.Dot(point - a, span) / lengthSquared, 0f, 1f));

            best = MathF.Min(best, Vector3.Distance(point, near));
        }

        return best;
    }

    /// <summary>A uniform grid over the vertices, so "everything near this curve" is not a scan.</summary>
    /// <remarks>
    ///     The same shape <c>MeshConditioner.Weld</c> uses, and for the same reason: a hash of the
    ///     rounded position alone misses the pair a hair apart either side of a cell boundary, so the
    ///     twenty-seven cells round each probe are searched.
    /// </remarks>
    sealed class VertexGrid {
        readonly Dictionary<(int X, int Y, int Z), List<int>> cells = [];
        readonly Vector3[] points;
        readonly float size;

        public VertexGrid(ReadOnlySpan<Vector3> positions, float cell) {
            size = cell;
            points = positions.ToArray();

            for (var vertex = 0; vertex < points.Length; vertex++) {
                var key = Cell(points[vertex]);

                if (!cells.TryGetValue(key, out var bucket)) {
                    bucket = [];
                    cells[key] = bucket;
                }

                bucket.Add(vertex);
            }
        }

        /// <summary>Stamps every vertex within a tolerance of a polyline.</summary>
        public void Near(
            IReadOnlyList<Vector3> curve,
            float tolerance,
            int[] stamp,
            int mark,
            List<int> into
        ) {
            for (var at = 0; at + 1 < curve.Count; at++) {
                var a = curve[at];
                var b = curve[at + 1];
                var steps = (int) MathF.Ceiling(Vector3.Distance(a, b) / MathF.Max(size, float.Epsilon));

                for (var step = 0; step <= steps; step++) {
                    var probe = steps == 0 ? a : Vector3.Lerp(a, b, (float) step / steps);

                    Probe(probe, curve, tolerance, stamp, mark, into);
                }
            }
        }

        void Probe(
            Vector3 point,
            IReadOnlyList<Vector3> curve,
            float tolerance,
            int[] stamp,
            int mark,
            List<int> into
        ) {
            var origin = Cell(point);

            for (var x = -1; x <= 1; x++) {
                for (var y = -1; y <= 1; y++) {
                    for (var z = -1; z <= 1; z++) {
                        if (!cells.TryGetValue((origin.X + x, origin.Y + y, origin.Z + z), out var bucket)) {
                            continue;
                        }

                        foreach (var vertex in bucket) {
                            if (stamp[vertex] == mark) {
                                continue;
                            }

                            if (Distance(curve, points[vertex]) > tolerance) {
                                continue;
                            }

                            stamp[vertex] = mark;
                            into.Add(vertex);
                        }
                    }
                }
            }
        }

        (int X, int Y, int Z) Cell(Vector3 point) =>
            ((int) MathF.Floor(point.X / size), (int) MathF.Floor(point.Y / size), (int) MathF.Floor(point.Z / size));
    }
}
