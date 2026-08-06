// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>Why a traced separatrix stopped.</summary>
/// <remarks>
///     ⚠ <b><see cref="Exhausted" /> is the one that is a defect, and it is a value rather than an
///     exception on purpose.</b> docs/plan/41's robustness criterion says a broken input produces a
///     result or a report naming the stage that refused, and <i>never</i> an exception or a hang — so a
///     walk that cycles has to be a number the layout can read and the report can carry.
/// </remarks>
enum TraceStop : byte {
    /// <summary>It ran into another singularity.</summary>
    Singularity,

    /// <summary>It ran into a feature polyline, which is a layout boundary already.</summary>
    Feature,

    /// <summary>It ran off an open rim.</summary>
    Boundary,

    /// <summary>It ran into a curve that had already been traced, which is the motorcycle-graph rule.</summary>
    Traced,

    /// <summary>It closed into a loop.</summary>
    Closed,

    /// <summary>It had nowhere to go — a vertex with no usable ring, or no field.</summary>
    Stalled,

    /// <summary>It hit the step budget. ⚠ A defect, counted and reported.</summary>
    Exhausted
}

/// <summary>One traced curve: a run of vertex indices, each consecutive pair an edge of the mesh.</summary>
/// <param name="Vertices">The walk, in order. A single entry is a walk that never left its seed.</param>
/// <param name="Stop">Why it ended.</param>
/// <remarks>
///     ⚠ <b>Vertices of the mesh and never points inside triangles, and that is what makes the
///     partition exact.</b> A separatrix integrated through triangle interiors has to be intersected
///     against every other one, and two curves that cross at a shared point computed twice do not agree
///     to the last bit — which is how a motorcycle graph acquires a patch that does not close. Walking
///     edges makes every crossing a vertex index, every patch boundary a chain of mesh edges, and the
///     "two patches share a side" test in docs/plan/41 § D8 an integer equality.
/// </remarks>
readonly record struct TracedCurve(int[] Vertices, TraceStop Stop) {
    /// <summary>How many edges it walked.</summary>
    public int EdgeCount => Vertices.Length - 1;
}

/// <summary>docs/plan/41 § D7's tracing: separatrices out of every singularity, along the field.</summary>
/// <remarks>
///     <para>
///         <b>Out of each singularity, in all its directions, until it hits another singularity, a
///         feature line or a boundary.</b> The arrangement of what comes back, plus the feature
///         polylines, is the motorcycle-graph-style partition (Eppstein, Goodrich, Kim and Tamstorf,
///         2008) that <see cref="PatchLayout" /> floods.
///     </para>
///     <para>
///         ⚠ <b>Which ring sector the direction falls into is decided by
///         <see cref="ExactPredicates.Orient2D" /> and never by a float cross product, and docs/plan/41
///         § D14 names this specifically.</b> A cross product compared against zero is not even
///         antisymmetric on near-collinear input — three points on <c>y = 3x</c> with float-exact
///         coordinates evaluate to <c>+16</c> in one argument order and <c>-67108864</c> in another —
///         so a direction lying almost exactly along a ring edge is inside two adjacent sectors, or
///         inside neither, depending on which way the loop happened to walk. A walk that answers "which
///         way now" differently when it arrives from the other end produces a partition whose patches do
///         not close, and no amount of downstream repair puts that right.
///     </para>
///     <para>
///         ⚠ <b>Every walk is bounded and a walk that hits the bound is discarded rather than kept.</b>
///         A field with a closed integral curve that is not a loop of mesh edges makes the walk cycle,
///         and a partially-traced curve left in the cut set is a slit that the boundary walk then has to
///         traverse twice. Discarding costs a coarser layout; keeping costs a wrong one.
///     </para>
/// </remarks>
static class SeparatrixTracer {
    /// <summary>How many edges one walk may cross, as a multiple of the mesh's vertex count.</summary>
    /// <remarks>
    ///     A simple walk cannot pass through more vertices than the mesh has, so anything past this is
    ///     cycling. The multiple is one and not two: doubling it doubles the worst case and finds
    ///     nothing, because a walk that has revisited a vertex once will revisit it again.
    /// </remarks>
    public const int StepBudget = 1;

    /// <summary>Traces every separatrix, in one deterministic pass over the singularities.</summary>
    /// <param name="mesh">The conditioned surface.</param>
    /// <param name="field">The placed cross field.</param>
    /// <param name="features">The feature graph — its edges stop a walk and are not re-traced.</param>
    /// <param name="seeds">The singular vertices, ascending. See <see cref="SeedsOf" />.</param>
    /// <param name="cut">
    ///     Per half-edge, whether it is a partition edge. Read <i>and</i> written: a walk stops on what
    ///     an earlier walk laid down, which is the motorcycle rule and is what bounds the total work.
    /// </param>
    /// <param name="exhausted">How many walks hit the step budget and were discarded.</param>
    /// <returns>The curves that terminated, in seed order then direction order.</returns>
    public static List<TracedCurve> Trace(
        ManifoldMesh mesh,
        CrossField field,
        FeatureGraph features,
        IReadOnlyList<int> seeds,
        bool[] cut,
        out int exhausted
    ) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(seeds);
        ArgumentNullException.ThrowIfNull(cut);

        var curves = new List<TracedCurve>();
        var singular = new bool[mesh.VertexCount];
        var visited = new int[mesh.VertexCount];

        Array.Fill(visited, -1);

        foreach (var seed in seeds) {
            if ((uint) seed < (uint) singular.Length) {
                singular[seed] = true;
            }
        }

        exhausted = 0;

        // ⚠ Seeds in ascending order and the four directions in a fixed order, because a motorcycle
        // graph is order-dependent by construction: the first walk down a corridor claims it and the
        // second stops on it. Two runs that seeded in different orders would produce two different
        // partitions, both correct and not the same — which is exactly what § D14 forbids.
        var walk = 0;

        foreach (var seed in seeds) {
            var start = Start(mesh, field, seed);

            if (start.LengthSquared() <= 0f) {
                continue;
            }

            var normal = mesh.VertexNormal(seed);

            for (var quarter = 0; quarter < 4; quarter++) {
                var direction = Turn(start, normal, quarter);
                var curve = Walk(mesh, field, features, singular, cut, visited, walk++, seed, direction);

                if (curve.Stop == TraceStop.Exhausted) {
                    exhausted++;

                    continue;
                }

                if (curve.EdgeCount <= 0) {
                    continue;
                }

                Mark(mesh, cut, curve.Vertices);
                curves.Add(curve);
            }
        }

        return curves;
    }

    /// <summary>The vertices a singularity list seeds walks from, ascending and without repeats.</summary>
    /// <param name="mesh">The surface.</param>
    /// <param name="singularities">What <see cref="SingularityPass.Extract" /> found.</param>
    /// <returns>One vertex per singular triangle, deduplicated.</returns>
    /// <remarks>
    ///     ⚠ <b>A singularity is a property of a <i>triangle</i> here and a separatrix leaves a
    ///     <i>vertex</i>, and the lowest-index corner is the bridge.</b> The turning a cross field
    ///     accumulates round a triangle is not attributable to any one of its three corners, so any
    ///     choice is arbitrary — and an arbitrary choice that is a function of the input alone is all
    ///     § D14 asks for. Picking the corner with the largest angle defect instead reads better and is
    ///     a float comparison between three nearly-equal numbers, which is a tie-break that moves.
    /// </remarks>
    public static List<int> SeedsOf(ManifoldMesh mesh, IReadOnlyList<FieldSingularity> singularities) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(singularities);

        var seen = new bool[mesh.VertexCount];
        var seeds = new List<int>();

        foreach (var singularity in singularities) {
            var corners = mesh.Corners(singularity.Triangle);
            var lowest = Math.Min(corners[0], Math.Min(corners[1], corners[2]));

            if ((uint) lowest < (uint) seen.Length && !seen[lowest]) {
                seen[lowest] = true;
                seeds.Add(lowest);
            }
        }

        seeds.Sort();

        return seeds;
    }

    /// <summary>One walk, from a vertex along a direction, until it stops.</summary>
    static TracedCurve Walk(
        ManifoldMesh mesh,
        CrossField field,
        FeatureGraph features,
        bool[] singular,
        bool[] cut,
        int[] visited,
        int walk,
        int seed,
        Vector3 direction
    ) {
        var chain = new List<int> { seed };
        var budget = (StepBudget * mesh.VertexCount) + 4;
        var previous = -1;
        var current = seed;

        visited[seed] = walk;

        for (var step = 0; step < budget; step++) {
            var next = Step(mesh, current, previous, direction, out var half);

            if (next < 0) {
                return new([.. chain], TraceStop.Stalled);
            }

            chain.Add(next);

            // ⚠ The edge is *not* marked here. A walk that turns out to be exhausted must leave no
            // trace at all, and marking as it goes would leave half a separatrix in the cut set —
            // which is a slit in the partition rather than a cut through it.
            if (next == seed) {
                return new([.. chain], TraceStop.Closed);
            }

            if (visited[next] == walk) {
                // It came back onto itself without closing on the seed, which is a cycle by any other
                // name. Reported as exhausted so the layout counts it with the other defects.
                return new([.. chain], TraceStop.Exhausted);
            }

            visited[next] = walk;

            if (features.IsFeatureVertex(next)) {
                return new([.. chain], TraceStop.Feature);
            }

            if (mesh.IsBoundary(next)) {
                return new([.. chain], TraceStop.Boundary);
            }

            if (singular[next]) {
                return new([.. chain], TraceStop.Singularity);
            }

            if (OnCut(mesh, cut, next)) {
                return new([.. chain], TraceStop.Traced);
            }

            // The field is followed rather than the straight line: transport what we were going into
            // the new vertex's plane, then take whichever of that vertex's four cross directions is
            // nearest it. That is what makes the curve a separatrix and not a geodesic.
            direction = CrossField.Align(
                field.Direction(next),
                CrossField.Transport(direction, mesh.VertexNormal(current), mesh.VertexNormal(next)),
                mesh.VertexNormal(next)
            );

            if (direction.LengthSquared() <= 0f) {
                return new([.. chain], TraceStop.Stalled);
            }

            previous = current;
            current = next;

            _ = half;
        }

        return new([.. chain], TraceStop.Exhausted);
    }

    /// <summary>One step: which ring neighbour the direction points at.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The sector test is <see cref="ExactPredicates.Orient2D" /> and the choice
    ///         <i>within</i> a sector is a dot product, and the split is deliberate.</b> "Which of the
    ///         ring's wedges contains this direction" is a question with a right answer that a float
    ///         cross product gets wrong on collinear input; "which of this wedge's two edges is nearer"
    ///         is a question about quality where either answer leaves the walk on the surface. Only the
    ///         first is a predicate, and only the first is exact.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Exactly along a ring edge is answered by the predicate's zero rather than by an
    ///         epsilon</b>, which is what makes a walk down a straight crease take the same edge every
    ///         time regardless of which end it started from.
    ///     </para>
    /// </remarks>
    static int Step(ManifoldMesh mesh, int vertex, int previous, Vector3 direction, out int half) {
        half = -1;

        var ring = mesh.Ring(vertex);

        if (ring.Length == 0) {
            return -1;
        }

        var frame = mesh.Frame(vertex);
        var target = Chart(frame, direction);

        if (target == Vector2.Zero) {
            return -1;
        }

        Span<Vector2> chart = ring.Length <= 32 ? stackalloc Vector2[ring.Length] : new Vector2[ring.Length];

        for (var at = 0; at < ring.Length; at++) {
            chart[at] = Chart(frame, mesh.Positions[ring[at]] - mesh.Positions[vertex]);
        }

        var best = -1;
        var score = float.NegativeInfinity;

        for (var at = 0; at < ring.Length; at++) {
            var next = (at + 1) % ring.Length;

            // A boundary vertex's ring is a fan rather than a cycle, so the wedge from its last
            // neighbour back to its first is the open side and is not a wedge at all.
            if (next == 0 && mesh.IsBoundary(vertex)) {
                continue;
            }

            if (!InWedge(chart[at], chart[next], target)) {
                continue;
            }

            foreach (var corner in (int[]) [at, next]) {
                if (ring[corner] == previous || chart[corner] == Vector2.Zero) {
                    continue;
                }

                var towards = Vector2.Dot(Vector2.Normalize(chart[corner]), Vector2.Normalize(target));

                // Ties by ring index, ascending, so the walk does not depend on which wedge found the
                // direction first — a direction exactly along a ring edge is in two wedges.
                if (towards > score) {
                    score = towards;
                    best = corner;
                }
            }
        }

        if (best < 0) {
            // Nothing claimed it, which happens where the ring does not close — a defect the twin
            // table already reported. Fall back to the plain nearest neighbour, which is a walk that
            // still terminates.
            for (var at = 0; at < ring.Length; at++) {
                if (ring[at] == previous || chart[at] == Vector2.Zero) {
                    continue;
                }

                var towards = Vector2.Dot(Vector2.Normalize(chart[at]), Vector2.Normalize(target));

                if (towards > score) {
                    score = towards;
                    best = at;
                }
            }
        }

        if (best < 0 || score <= 0f) {
            // Every candidate is behind the direction, so the walk has run into a fold rather than
            // across it. Stopping is right; carrying on would double back.
            return -1;
        }

        half = -1;

        return ring[best];
    }

    /// <summary>Whether a chart direction lies in the wedge swept from one ring edge to the next.</summary>
    /// <remarks>
    ///     The wedge's own turn decides which inequality is which, so a ring charted clockwise and one
    ///     charted anticlockwise are both handled without the caller knowing which it has. A wedge whose
    ///     two edges are exactly collinear is not a wedge and claims nothing.
    /// </remarks>
    static bool InWedge(Vector2 from, Vector2 to, Vector2 direction) {
        var turn = ExactPredicates.Orient2D(Vector2.Zero, from, to);

        if (turn == 0) {
            return false;
        }

        var first = ExactPredicates.Orient2D(Vector2.Zero, from, direction);
        var second = ExactPredicates.Orient2D(Vector2.Zero, direction, to);

        return turn > 0 ? first >= 0 && second >= 0 : first <= 0 && second <= 0;
    }

    /// <summary>A world direction in a vertex's own two-dimensional chart.</summary>
    static Vector2 Chart(TangentFrame frame, Vector3 direction) {
        var flat = ScaleSafe.Flatten(direction, frame.Normal);

        return flat.LengthSquared() <= 0f
            ? Vector2.Zero
            : new(Vector3.Dot(flat, frame.Tangent), Vector3.Dot(flat, frame.Bitangent));
    }

    /// <summary>The seed direction: the field's own representative at the singular vertex.</summary>
    static Vector3 Start(ManifoldMesh mesh, CrossField field, int vertex) =>
        (uint) vertex >= (uint) mesh.VertexCount ? Vector3.Zero : field.Direction(vertex);

    /// <summary>One of the four directions a cross stands for.</summary>
    static Vector3 Turn(Vector3 direction, Vector3 normal, int quarter) => quarter switch {
        0 => direction,
        1 => Vector3.Cross(normal, direction),
        2 => -direction,
        _ => -Vector3.Cross(normal, direction)
    };

    /// <summary>Whether any half-edge at a vertex has been cut.</summary>
    static bool OnCut(ManifoldMesh mesh, bool[] cut, int vertex) {
        foreach (var half in mesh.Outgoing(vertex)) {
            if (cut[half]) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Marks a chain's edges as partition edges, both halves.</summary>
    static void Mark(ManifoldMesh mesh, bool[] cut, int[] chain) {
        for (var at = 0; at + 1 < chain.Length; at++) {
            var half = Half(mesh, chain[at], chain[at + 1]);

            if (half < 0) {
                continue;
            }

            cut[half] = true;

            var twin = mesh.Twin(half);

            if (twin >= 0) {
                cut[twin] = true;
            }
        }
    }

    /// <summary>The half-edge running from one vertex to another, or <c>-1</c>.</summary>
    public static int Half(ManifoldMesh mesh, int from, int to) {
        ArgumentNullException.ThrowIfNull(mesh);

        foreach (var half in mesh.Outgoing(from)) {
            if (mesh.Triangles[ManifoldMesh.Next(half)] == to) {
                return half;
            }
        }

        return -1;
    }
}
