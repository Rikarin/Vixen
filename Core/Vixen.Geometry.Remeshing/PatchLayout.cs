// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>One arc of the partition, read forwards or backwards.</summary>
/// <param name="Arc">Which arc.</param>
/// <param name="Reversed">Whether this patch walks it from its last vertex to its first.</param>
/// <remarks>
///     ⚠ <b>Two patches sharing a side hold the <i>same</i> arc index, and that is the whole of
///     docs/plan/41 § D8's "the seam is an equality rather than a weld".</b> The arc owns one list of
///     output vertices; a reversed use reads the same list backwards. There is no tolerance anywhere in
///     it, so there is no size of model at which the seam opens.
/// </remarks>
readonly record struct ArcUse(int Arc, bool Reversed);

/// <summary>One run of partition edges between two split vertices.</summary>
/// <remarks>
///     ⚠ <b>A chain of mesh vertices and never a chord.</b> Consecutive entries are an edge of the
///     conditioned mesh, so an arc that runs along a feature polyline runs along <i>every</i> vertex of
///     it — which is what lets the extraction place its samples on the source curve rather than near it.
/// </remarks>
sealed class LayoutArc {
    /// <summary>The chain, in the arc's canonical direction.</summary>
    public required int[] Vertices { get; init; }

    /// <summary>Whether every one of its edges is a feature edge.</summary>
    public required bool IsFeature { get; init; }

    /// <summary>Its arc length, in world units.</summary>
    public required float Length { get; init; }

    /// <summary>How many quads the density field would like along it, before rounding.</summary>
    public required float Target { get; init; }

    /// <summary>How many edges it has.</summary>
    public int EdgeCount => Vertices.Length - 1;
}

/// <summary>One patch: a run of triangles, and four sides made of arcs.</summary>
/// <remarks>
///     ⚠ <b>Four sides always, and a side is a <i>list</i> of arcs rather than one.</b> docs/plan/41
///     § D7 wants a patch to be a polygon with <i>k</i> sides and a regular grid to exist inside it, and
///     those two want different things: the grid needs four, the partition produces whatever the
///     separatrices and features left. Grouping the boundary into four super-sides reconciles them
///     without a T-junction — every arc still gets its own integer, and the constraint is that the two
///     opposite groups sum to the same thing.
/// </remarks>
sealed class LayoutPatch {
    /// <summary>Which triangles of the conditioned mesh it covers.</summary>
    public required int[] Triangles { get; init; }

    /// <summary>Its four sides, anticlockwise round the patch, each an ordered run of arcs.</summary>
    public required ArcUse[][] Sides { get; init; }
}

/// <summary>docs/plan/41 § D7's partition: separatrices plus feature polylines, flooded into patches.</summary>
/// <remarks>
///     <para>
///         <b>A motorcycle-graph-style partition</b> (Eppstein, Goodrich, Kim and Tamstorf, 2008). Every
///         feature polyline is a cut by construction — which is § D4's whole thesis and is why a hard
///         edge comes out reproduced rather than approximated — and separatrices traced out of the
///         singularities are cuts as well. What is left between them is the patches.
///     </para>
///     <para>
///         ⚠ <b>Every repair here is bounded and every failure is a warning rather than an
///         exception.</b> docs/plan/41's robustness criterion is "a valid all-quad result or a report
///         naming the stage that refused, and never an exception or a hang". A partition that cannot be
///         made usable in <see cref="RepairRounds" /> rounds says so in
///         <see cref="Warnings" /> and <see cref="IsUsable" /> goes false.
///     </para>
///     <para>
///         ⚠ <b>The zero cases are real inputs and each has an answer.</b> A mesh with no features and
///         no singularities — a torus with a perfect field — has <i>no cuts at all</i> and would flood
///         into one patch with no boundary; <see cref="Seed" /> traces two artificial loops out of its
///         lowest-index vertex instead. A patch bounded by two loops — a cylinder's wall between its two
///         rims — is an annulus and holds no grid; it is cut to a disc. Both are cases a remesher meets
///         on its second day.
///     </para>
/// </remarks>
sealed class PatchLayout {
    /// <summary>How many times the partition may be rebuilt after a repair.</summary>
    /// <remarks>
    ///     Each round either finishes or adds cuts, and adding cuts cannot go on forever because every
    ///     cut is an edge of a finite mesh. The cap is what turns a repair that oscillates into a
    ///     warning.
    /// </remarks>
    public const int RepairRounds = 6;

    /// <summary>The largest patch § D7's merge will dissolve into a neighbour.</summary>
    /// <remarks>
    ///     ⚠ <b>A cap, and without one the merge eats the whole partition.</b> Dissolving an arc makes
    ///     the neighbour bigger, and a rule that merges any patch it cannot divide will then find the
    ///     neighbour wanting too — measured, an unconditioned merge dissolved every cut on a box and the
    ///     layout came back with no partition boundary at all. What actually needs merging is the patch
    ///     of one or two triangles that a separatrix cut off a corner, and those are small.
    /// </remarks>
    public const int MergeTriangles = 4;

    /// <summary>The turn, in degrees, at which a run of partition edges is a corner.</summary>
    /// <remarks>
    ///     ⚠ Half a quarter turn: a cross field's structure is ninety-degree, so a boundary that turns
    ///     by more than forty-five is turning a corner of the grid rather than curving along a side.
    /// </remarks>
    public const float CornerAngle = 45f;

    PatchLayout(
        LayoutArc[] arcs,
        LayoutPatch[] patches,
        bool[] cut,
        bool[] split,
        string[] warnings,
        int exhausted,
        bool usable
    ) {
        Arcs = arcs;
        Patches = patches;
        Cut = cut;
        Split = split;
        Warnings = warnings;
        Exhausted = exhausted;
        IsUsable = usable;
    }

    /// <summary>Every arc, in a fixed order.</summary>
    public IReadOnlyList<LayoutArc> Arcs { get; }

    /// <summary>Every patch, in flood order — which is triangle order, which is the file's.</summary>
    public IReadOnlyList<LayoutPatch> Patches { get; }

    /// <summary>Per half-edge, whether it is a partition edge.</summary>
    public bool[] Cut { get; }

    /// <summary>Per vertex, whether it ends an arc.</summary>
    public bool[] Split { get; }

    /// <summary>What went wrong, if anything.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>How many traced separatrices hit their step budget and were discarded.</summary>
    public int Exhausted { get; }

    /// <summary>Whether every patch came out with four usable sides.</summary>
    public bool IsUsable { get; }

    /// <summary>Builds § D7's partition.</summary>
    /// <param name="mesh">The conditioned surface.</param>
    /// <param name="field">The placed cross field.</param>
    /// <param name="features">The feature graph, whose polylines are cuts by construction.</param>
    /// <param name="density">The one target-edge-length field, which decides every arc's target.</param>
    /// <param name="singularities">What <see cref="SingularityPass.Extract" /> found.</param>
    /// <returns>The layout.</returns>
    public static PatchLayout Build(
        ManifoldMesh mesh,
        CrossField field,
        FeatureGraph features,
        DensityField density,
        IReadOnlyList<FieldSingularity> singularities
    ) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(density);
        ArgumentNullException.ThrowIfNull(singularities);

        var warnings = new List<string>();
        var cut = new bool[mesh.Triangles.Length];

        // Every feature edge and every open rim is a partition edge before anything is traced. § D4:
        // the polylines *are* boundaries of the layout, not something snapped to afterwards.
        for (var half = 0; half < cut.Length; half++) {
            cut[half] = features.IsFeatureEdge(half) || mesh.Twin(half) < 0;
        }

        var seeds = SeparatrixTracer.SeedsOf(mesh, singularities);
        var curves = SeparatrixTracer.Trace(mesh, field, features, seeds, cut, out var exhausted);

        if (exhausted > 0) {
            warnings.Add($"{exhausted} separatrices hit the step budget and were discarded.");
        }

        Seed(mesh, field, features, cut, warnings);
        Prune(mesh, features, cut);
        Redundant(mesh, features, curves, cut, warnings);

        var forced = new HashSet<int>();

        for (var round = 0; round <= RepairRounds; round++) {
            var split = Splits(mesh, features, cut, forced);
            var (arcs, arcOfHalf, forwardOfHalf) = BuildArcs(mesh, features, density, cut, split);

            // ⚠ An arc that comes back to the vertex it started at is not a side of anything, and it
            // is what a cut loop carrying exactly one split produces. Its two ends are one output
            // vertex, so at one quad it is a zero-area face and at zero quads it is nothing at all —
            // both of which reach `Validate` as a non-manifold edge rather than as an error anybody
            // can attribute. Split it and rebuild.
            if (Reopen(mesh, arcs, forced) && round < RepairRounds) {
                continue;
            }

            var patches = Flood(mesh, cut);
            var built = new List<LayoutPatch>(patches.Count);
            var redo = false;
            var degenerate = 0;

            foreach (var triangles in patches) {
                var loops = Loops(mesh, cut, triangles);

                if (loops.Count == 0) {
                    // A closed component with no usable cut on it. There is nothing to bound a grid
                    // with, so it contributes no quads — counted rather than fatal, because one such
                    // component must not cost the rest of the model its remesh.
                    degenerate++;

                    continue;
                }

                if (loops.Count > 1) {
                    if (Bridge(mesh, cut, triangles, loops)) {
                        redo = true;
                    } else {
                        degenerate++;
                    }

                    continue;
                }

                var uses = Walk(loops[0], arcOfHalf, forwardOfHalf);

                if (uses.Count < 4) {
                    // ⚠ Divide first and merge only when dividing is impossible. A patch bounded by
                    // three arcs usually wants a fourth corner; one bounded by three arcs that are
                    // each a single edge cannot have one, and § D7's answer for those is that a patch
                    // too small or degenerate merges into a neighbour. Dropping it instead is what
                    // leaves the output full of holes — measured on a box, nineteen arcs with a patch
                    // on only one side of them and seventy boundary edges to show for it.
                    if (Divide(mesh, arcs, uses, forced)
                        || (triangles.Length <= MergeTriangles && Merge(mesh, features, cut, arcs, uses))) {
                        redo = true;
                    } else {
                        degenerate++;
                    }

                    continue;
                }

                built.Add(new() { Triangles = triangles, Sides = Sides(mesh, arcs, uses) });
            }

            if (!redo) {
                if (degenerate > 0) {
                    warnings.Add($"{degenerate} patches could be neither divided nor merged and were dropped.");
                }

                return new([.. arcs], [.. built], cut, split, [.. warnings], exhausted, built.Count > 0);
            }

            if (round == RepairRounds) {
                warnings.Add($"The partition still had unusable patches after {RepairRounds} repairs.");

                return Refused(arcs, warnings, exhausted, cut, split);
            }
        }

        return Refused([], warnings, exhausted, cut, new bool[mesh.VertexCount]);
    }

    static PatchLayout Refused(
        IReadOnlyList<LayoutArc> arcs,
        List<string> warnings,
        int exhausted,
        bool[] cut,
        bool[] split
    ) =>
        new([.. arcs], [], cut, split, [.. warnings], exhausted, false);

    /// <summary>Traces two artificial loops on any component the partition never touched.</summary>
    /// <remarks>
    ///     ⚠ <b>A torus with a perfect field has no singularities and a torus with no creases has no
    ///     features, so it has no cuts — and one patch with no boundary is not a patch.</b> That is
    ///     docs/plan/41's "a zero that means off" trap in its layout form, and the answer is to cut it
    ///     somewhere rather than to refuse it: two field-aligned walks out of the component's
    ///     lowest-index vertex, which is a choice derived from the input and so is the same on every
    ///     machine.
    /// </remarks>
    static void Seed(
        ManifoldMesh mesh,
        CrossField field,
        FeatureGraph features,
        bool[] cut,
        List<string> warnings
    ) {
        var component = Components(mesh);
        var touched = new HashSet<int>();

        for (var half = 0; half < cut.Length; half++) {
            if (cut[half]) {
                touched.Add(component[half / 3]);
            }
        }

        var lowest = new Dictionary<int, int>();

        for (var triangle = 0; triangle < mesh.TriangleCount; triangle++) {
            var group = component[triangle];

            if (touched.Contains(group)) {
                continue;
            }

            foreach (var corner in mesh.Corners(triangle)) {
                if (!lowest.TryGetValue(group, out var seen) || corner < seen) {
                    lowest[group] = corner;
                }
            }
        }

        if (lowest.Count == 0) {
            return;
        }

        var seeds = lowest.Values.ToList();

        seeds.Sort();

        SeparatrixTracer.Trace(mesh, field, features, seeds, cut, out var exhausted);

        warnings.Add(
            $"{seeds.Count} components had no feature and no singularity on them, so the layout cut them itself."
        );

        if (exhausted > 0) {
            warnings.Add($"{exhausted} of the seeded walks hit the step budget.");
        }
    }

    /// <summary>Removes traced cuts that dead-end, leaf by leaf, until none is left.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A cut that dead-ends is a slit rather than a cut, and a slit is what makes a patch
    ///         walk the same arc twice.</b> A separatrix that stalls — or one whose seed had only one of
    ///         its four directions succeed — leaves a chain with a loose end, and the flood fill happily
    ///         crosses round it: the same patch is on both sides. The boundary walk then traverses that
    ///         arc once in each direction, which puts one quantization variable into three or four
    ///         constraints and stops the system being a flow problem at all. Measured on a box before
    ///         this existed: seventeen arcs appearing twice in one patch, and the exact solver refusing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A feature polyline's own loose end is not pruned.</b> A crease that runs off into a
    ///         flat region legitimately ends, § D4 makes it a layout boundary regardless, and removing it
    ///         would be removing the thing the whole design exists to reproduce.
    ///     </para>
    /// </remarks>
    static void Prune(ManifoldMesh mesh, FeatureGraph features, bool[] cut) {
        var degree = new int[mesh.VertexCount];

        for (var vertex = 0; vertex < degree.Length; vertex++) {
            foreach (var neighbour in mesh.Ring(vertex)) {
                if (IsCut(mesh, cut, vertex, neighbour)) {
                    degree[vertex]++;
                }
            }
        }

        var queue = new Queue<int>();

        for (var vertex = 0; vertex < degree.Length; vertex++) {
            if (degree[vertex] == 1) {
                queue.Enqueue(vertex);
            }
        }

        while (queue.TryDequeue(out var vertex)) {
            if (degree[vertex] != 1) {
                continue;
            }

            foreach (var neighbour in mesh.Ring(vertex)) {
                if (!IsCut(mesh, cut, vertex, neighbour)) {
                    continue;
                }

                var half = SeparatrixTracer.Half(mesh, vertex, neighbour);
                var twin = SeparatrixTracer.Half(mesh, neighbour, vertex);

                var kept = (half >= 0 && (features.IsFeatureEdge(half) || mesh.Twin(half) < 0))
                    || (twin >= 0 && (features.IsFeatureEdge(twin) || mesh.Twin(twin) < 0));

                if (kept) {
                    degree[vertex] = 2;

                    break;
                }

                if (half >= 0) {
                    cut[half] = false;
                }

                if (twin >= 0) {
                    cut[twin] = false;
                }

                degree[vertex]--;
                degree[neighbour]--;

                if (degree[neighbour] == 1) {
                    queue.Enqueue(neighbour);
                }

                break;
            }
        }
    }

    /// <summary>Drops every traced curve that separates nothing.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A cut that leaves the same patch on both sides is a slit, and a slit is what makes
    ///         an extracted mesh non-manifold.</b> A patch whose boundary walk goes up one side of a
    ///         slit and back down the other puts the same run of vertices into one grid row twice: the
    ///         row folds on itself, the quads on the fold have no area, and their edges come back from
    ///         <c>Validate</c> as non-manifold <i>and</i> inconsistently wound. Measured on a box, five
    ///         of each.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Tested by re-flooding rather than by asking whether the two sides are the same
    ///         patch, and the difference is the torus.</b> Two transverse loops on a torus cut it to a
    ///         disc, and at that point <i>every</i> cut edge has the same patch on both sides — so the
    ///         local test would remove both loops and leave a surface with no boundary at all. Removing
    ///         a curve and counting the patches answers the question that is actually being asked: did
    ///         this curve divide anything.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Feature edges and open rims are never candidates.</b> A crease that divides nothing
    ///         is still a crease, and § D4 makes it a layout boundary regardless of what it separates.
    ///     </para>
    /// </remarks>
    static void Redundant(
        ManifoldMesh mesh,
        FeatureGraph features,
        List<TracedCurve> curves,
        bool[] cut,
        List<string> warnings
    ) {
        if (curves.Count == 0) {
            return;
        }

        var patches = Flood(mesh, cut).Count;
        var dropped = 0;
        var halves = new List<int>();

        foreach (var curve in curves) {
            halves.Clear();

            for (var at = 0; at + 1 < curve.Vertices.Length; at++) {
                foreach (var half in (int[]) [
                    SeparatrixTracer.Half(mesh, curve.Vertices[at], curve.Vertices[at + 1]),
                    SeparatrixTracer.Half(mesh, curve.Vertices[at + 1], curve.Vertices[at])
                ]) {
                    if (half >= 0 && cut[half] && !features.IsFeatureEdge(half) && mesh.Twin(half) >= 0) {
                        halves.Add(half);
                    }
                }
            }

            if (halves.Count == 0) {
                continue;
            }

            foreach (var half in halves) {
                cut[half] = false;
            }

            var without = Flood(mesh, cut).Count;

            if (without == patches) {
                dropped++;

                continue;
            }

            foreach (var half in halves) {
                cut[half] = true;
            }
        }

        if (dropped > 0) {
            warnings.Add($"{dropped} traced separatrices divided nothing and were dropped.");
        }
    }

    /// <summary>Connected components of the triangle graph.</summary>
    static int[] Components(ManifoldMesh mesh) {
        var component = new int[mesh.TriangleCount];

        Array.Fill(component, -1);

        var groups = 0;
        var stack = new Stack<int>();

        for (var triangle = 0; triangle < component.Length; triangle++) {
            if (component[triangle] >= 0) {
                continue;
            }

            var group = groups++;

            component[triangle] = group;
            stack.Push(triangle);

            while (stack.TryPop(out var current)) {
                for (var side = 0; side < 3; side++) {
                    var next = mesh.Adjacent(current, side);

                    if (next >= 0 && component[next] < 0) {
                        component[next] = group;
                        stack.Push(next);
                    }
                }
            }
        }

        return component;
    }

    /// <summary>Which vertices end an arc.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A global property of a vertex rather than a per-patch one, and it has to be.</b> Two
    ///         patches share an arc, so they have to agree on where the arc <i>ends</i> — a split
    ///         computed inside one patch's boundary walk would split the arc for one of them and not for
    ///         the other, and the shared-by-index guarantee would be a shared-by-luck one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A feature polyline's keys are splits, and that is what buys the exit criterion.</b>
    ///         Between two keys the chain is straight to R2's simplification tolerance, so a straight
    ///         output edge across it lies on the source; across a key it does not. Splitting there makes
    ///         every bend of a hard edge an output vertex, which is the difference between
    ///         <c>FeatureReproductionError</c> at a tolerance of exact and one at a tolerance of close.
    ///     </para>
    /// </remarks>
    static bool[] Splits(ManifoldMesh mesh, FeatureGraph features, bool[] cut, HashSet<int> forced) {
        var split = new bool[mesh.VertexCount];
        var turn = MathF.Cos(MathUtil.DegreesToRadians(CornerAngle));

        foreach (var vertex in forced) {
            if ((uint) vertex < (uint) split.Length) {
                split[vertex] = true;
            }
        }

        Span<int> ends = stackalloc int[2];

        for (var vertex = 0; vertex < split.Length; vertex++) {
            var degree = 0;
            var feature = 0;

            foreach (var neighbour in mesh.Ring(vertex)) {
                if (!IsCut(mesh, cut, vertex, neighbour)) {
                    continue;
                }

                if (degree < 2) {
                    ends[degree] = neighbour;
                }

                degree++;

                if (IsFeature(mesh, features, vertex, neighbour)) {
                    feature++;
                }
            }

            if (degree == 0) {
                continue;
            }

            // An end, a junction, a feature corner, or a vertex where a feature meets something that
            // is not one — all four are places a patch corner may sit and an arc must not run through.
            if (degree != 2 || features.IsCorner(vertex) || (feature != 0 && feature != degree)) {
                split[vertex] = true;

                continue;
            }

            var one = ScaleSafe.Unit(mesh.Positions[ends[0]] - mesh.Positions[vertex]);
            var two = ScaleSafe.Unit(mesh.Positions[ends[1]] - mesh.Positions[vertex]);

            // The two ends leave the vertex, so a straight run has them exactly opposed. Anything
            // less opposed than the corner angle is a turn.
            if (one.LengthSquared() > 0f && two.LengthSquared() > 0f && Vector3.Dot(one, two) > -turn) {
                split[vertex] = true;
            }
        }

        Keys(mesh, features, cut, split);
        Closed(mesh, cut, split);

        return split;
    }

    /// <summary>Every key of every feature polyline that still carries a cut becomes a split.</summary>
    static void Keys(ManifoldMesh mesh, FeatureGraph features, bool[] cut, bool[] split) {
        foreach (var polyline in features.Polylines) {
            foreach (var key in polyline.Keys) {
                var vertex = polyline.Vertices[key];

                if ((uint) vertex >= (uint) split.Length) {
                    continue;
                }

                foreach (var neighbour in mesh.Ring(vertex)) {
                    if (IsCut(mesh, cut, vertex, neighbour)) {
                        split[vertex] = true;

                        break;
                    }
                }
            }
        }
    }

    /// <summary>Forces four splits onto any cut loop that carries none.</summary>
    /// <remarks>
    ///     ⚠ <b>A closed cut with no split on it is an arc that starts and ends at the same vertex,
    ///     which is not a side of anything.</b> A cylinder's rim is the ordinary case: a feature loop
    ///     with no corner anywhere on it. Four rather than one, so the disc it bounds has four sides
    ///     without a further repair, and at even arc-length steps from the lowest-index vertex on the
    ///     loop so the choice is a function of the mesh.
    /// </remarks>
    static void Closed(ManifoldMesh mesh, bool[] cut, bool[] split) {
        var seen = new bool[mesh.VertexCount];

        for (var vertex = 0; vertex < split.Length; vertex++) {
            if (seen[vertex] || split[vertex] || !AnyCut(mesh, cut, vertex)) {
                continue;
            }

            var loop = Follow(mesh, cut, split, seen, vertex);

            if (loop.Count < 4) {
                continue;
            }

            var total = 0f;

            for (var at = 0; at + 1 < loop.Count; at++) {
                total += Vector3.Distance(mesh.Positions[loop[at]], mesh.Positions[loop[at + 1]]);
            }

            var walked = 0f;
            var quarter = 1;

            split[loop[0]] = true;

            for (var at = 0; at + 1 < loop.Count && quarter < 4; at++) {
                walked += Vector3.Distance(mesh.Positions[loop[at]], mesh.Positions[loop[at + 1]]);

                if (walked * 4f >= total * quarter) {
                    split[loop[at + 1]] = true;
                    quarter++;
                }
            }
        }
    }

    /// <summary>Walks a cut loop that has no split on it, from a vertex back to itself.</summary>
    static List<int> Follow(ManifoldMesh mesh, bool[] cut, bool[] split, bool[] seen, int start) {
        var loop = new List<int> { start };
        var previous = -1;
        var current = start;

        seen[start] = true;

        for (var step = 0; step <= mesh.VertexCount; step++) {
            var next = -1;

            foreach (var neighbour in mesh.Ring(current)) {
                if (neighbour != previous && IsCut(mesh, cut, current, neighbour)) {
                    next = neighbour;

                    break;
                }
            }

            if (next < 0) {
                return [];
            }

            loop.Add(next);

            if (next == start) {
                return loop;
            }

            if (split[next] || seen[next]) {
                return [];
            }

            seen[next] = true;
            previous = current;
            current = next;
        }

        return [];
    }

    /// <summary>Cuts the arcs of the partition out of the cut set.</summary>
    static (List<LayoutArc> Arcs, int[] ArcOfHalf, bool[] Forward) BuildArcs(
        ManifoldMesh mesh,
        FeatureGraph features,
        DensityField density,
        bool[] cut,
        bool[] split
    ) {
        var arcs = new List<LayoutArc>();
        var arcOfHalf = new int[cut.Length];
        var forward = new bool[cut.Length];

        Array.Fill(arcOfHalf, -1);

        for (var half = 0; half < cut.Length; half++) {
            if (!cut[half] || arcOfHalf[half] >= 0) {
                continue;
            }

            var from = mesh.Triangles[half];

            if (!split[from]) {
                continue;
            }

            var chain = new List<int> { from };
            var previous = -1;
            var current = from;
            var next = mesh.Triangles[ManifoldMesh.Next(half)];

            for (var step = 0; step <= mesh.VertexCount; step++) {
                chain.Add(next);
                Assign(mesh, arcOfHalf, forward, arcs.Count, current, next);

                if (split[next]) {
                    break;
                }

                previous = current;
                current = next;
                next = -1;

                foreach (var neighbour in mesh.Ring(current)) {
                    if (neighbour != previous && IsCut(mesh, cut, current, neighbour)) {
                        next = neighbour;

                        break;
                    }
                }

                if (next < 0) {
                    break;
                }
            }

            arcs.Add(Arc(mesh, features, density, [.. chain]));
        }

        return (arcs, arcOfHalf, forward);
    }

    static void Assign(ManifoldMesh mesh, int[] arcOfHalf, bool[] forward, int arc, int from, int to) {
        var ahead = SeparatrixTracer.Half(mesh, from, to);
        var back = SeparatrixTracer.Half(mesh, to, from);

        if (ahead >= 0) {
            arcOfHalf[ahead] = arc;
            forward[ahead] = true;
        }

        if (back >= 0) {
            arcOfHalf[back] = arc;
            forward[back] = false;
        }
    }

    /// <summary>One arc, with the length and the target the density field asks of it.</summary>
    static LayoutArc Arc(
        ManifoldMesh mesh,
        FeatureGraph features,
        DensityField density,
        int[] chain
    ) {
        var length = 0f;
        var target = 0f;
        var feature = chain.Length > 1;

        for (var at = 0; at + 1 < chain.Length; at++) {
            var step = Vector3.Distance(mesh.Positions[chain[at]], mesh.Positions[chain[at + 1]]);
            var wanted = (density.Target(chain[at]) + density.Target(chain[at + 1])) * 0.5f;

            length += step;
            target += wanted > 0f ? step / wanted : 0f;

            var half = SeparatrixTracer.Half(mesh, chain[at], chain[at + 1]);
            var twin = SeparatrixTracer.Half(mesh, chain[at + 1], chain[at]);

            feature &= (half >= 0 && features.IsFeatureEdge(half)) || (twin >= 0 && features.IsFeatureEdge(twin));
        }

        return new() { Vertices = chain, IsFeature = feature, Length = length, Target = target };
    }

    /// <summary>Floods the triangles into patches, crossing everything that is not a cut.</summary>
    static List<int[]> Flood(ManifoldMesh mesh, bool[] cut) {
        var patch = new int[mesh.TriangleCount];

        Array.Fill(patch, -1);

        var patches = new List<int[]>();
        var stack = new Stack<int>();
        var members = new List<int>();

        for (var triangle = 0; triangle < patch.Length; triangle++) {
            if (patch[triangle] >= 0) {
                continue;
            }

            members.Clear();
            patch[triangle] = patches.Count;
            stack.Push(triangle);

            while (stack.TryPop(out var current)) {
                members.Add(current);

                for (var side = 0; side < 3; side++) {
                    var half = (current * 3) + side;

                    if (cut[half]) {
                        continue;
                    }

                    var next = mesh.Adjacent(current, side);

                    if (next >= 0 && patch[next] < 0) {
                        patch[next] = patches.Count;
                        stack.Push(next);
                    }
                }
            }

            members.Sort();
            patches.Add([.. members]);
        }

        return patches;
    }

    /// <summary>The boundary loops of one patch, as cycles of half-edges with the patch on their left.</summary>
    static List<List<int>> Loops(ManifoldMesh mesh, bool[] cut, int[] triangles) {
        var inside = new HashSet<int>(triangles);
        var seen = new HashSet<int>();
        var loops = new List<List<int>>();

        foreach (var triangle in triangles) {
            for (var side = 0; side < 3; side++) {
                var start = (triangle * 3) + side;

                if (!cut[start] || !seen.Add(start)) {
                    continue;
                }

                var loop = new List<int> { start };
                var half = start;

                for (var step = 0; step <= mesh.Triangles.Length; step++) {
                    half = NextOnLoop(mesh, cut, half);

                    if (half < 0 || !inside.Contains(half / 3)) {
                        loop.Clear();

                        break;
                    }

                    if (half == start) {
                        break;
                    }

                    if (!seen.Add(half)) {
                        loop.Clear();

                        break;
                    }

                    loop.Add(half);
                }

                if (loop.Count > 0) {
                    loops.Add(loop);
                }
            }
        }

        return loops;
    }

    /// <summary>The next boundary half-edge round the patch, rotating through the fan at the far end.</summary>
    static int NextOnLoop(ManifoldMesh mesh, bool[] cut, int half) {
        var next = ManifoldMesh.Next(half);

        for (var step = 0; step <= mesh.Triangles.Length; step++) {
            if (cut[next]) {
                return next;
            }

            var twin = mesh.Twin(next);

            if (twin < 0) {
                return -1;
            }

            next = ManifoldMesh.Next(twin);
        }

        return -1;
    }

    /// <summary>The arcs a boundary loop walks, in order.</summary>
    static List<ArcUse> Walk(List<int> loop, int[] arcOfHalf, bool[] forward) {
        var uses = new List<ArcUse>();

        foreach (var half in loop) {
            var arc = arcOfHalf[half];

            if (arc < 0) {
                continue;
            }

            var use = new ArcUse(arc, !forward[half]);

            if (uses.Count == 0 || uses[^1] != use) {
                uses.Add(use);
            }
        }

        // The loop's first and last half-edge can belong to the same arc, which is one use split
        // across the seam of the list rather than two.
        if (uses.Count > 1 && uses[0] == uses[^1]) {
            uses.RemoveAt(uses.Count - 1);
        }

        return uses;
    }

    /// <summary>Cuts an annulus down to a disc by joining its two boundary loops.</summary>
    /// <remarks>
    ///     ⚠ <b>A patch with two boundary loops holds no regular grid, and a cylinder's wall is the
    ///     ordinary way to get one.</b> The shortest edge path between the loops is the cut, taken
    ///     inside the patch so it does not disturb anything else, and the partition is rebuilt round it.
    /// </remarks>
    static bool Bridge(ManifoldMesh mesh, bool[] cut, int[] triangles, List<List<int>> loops) {
        var inside = new HashSet<int>(triangles);
        var sources = new HashSet<int>();
        var targets = new HashSet<int>();

        foreach (var half in loops[0]) {
            sources.Add(mesh.Triangles[half]);
        }

        foreach (var half in loops[1]) {
            targets.Add(mesh.Triangles[half]);
        }

        var from = new Dictionary<int, int>();
        var queue = new Queue<int>();
        var ordered = sources.ToList();

        ordered.Sort();

        foreach (var vertex in ordered) {
            from[vertex] = -1;
            queue.Enqueue(vertex);
        }

        while (queue.TryDequeue(out var vertex)) {
            if (targets.Contains(vertex) && from[vertex] >= 0) {
                for (var at = vertex; from[at] >= 0; at = from[at]) {
                    var half = SeparatrixTracer.Half(mesh, at, from[at]);
                    var twin = SeparatrixTracer.Half(mesh, from[at], at);

                    if (half >= 0) {
                        cut[half] = true;
                    }

                    if (twin >= 0) {
                        cut[twin] = true;
                    }
                }

                return true;
            }

            foreach (var neighbour in mesh.Ring(vertex)) {
                if (from.ContainsKey(neighbour) || !Touches(mesh, inside, neighbour)) {
                    continue;
                }

                from[neighbour] = vertex;
                queue.Enqueue(neighbour);
            }
        }

        return false;
    }

    static bool Touches(ManifoldMesh mesh, HashSet<int> inside, int vertex) {
        foreach (var half in mesh.Outgoing(vertex)) {
            if (inside.Contains(half / 3)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Marks two more splits on every arc that closed back onto its own start.</summary>
    /// <returns>Whether anything was added, so the caller knows to rebuild.</returns>
    static bool Reopen(ManifoldMesh mesh, List<LayoutArc> arcs, HashSet<int> forced) {
        var added = false;

        foreach (var arc in arcs) {
            var chain = arc.Vertices;

            if (chain[0] != chain[^1] || chain.Length < 4) {
                continue;
            }

            var walked = 0f;
            var third = 1;

            for (var at = 0; at + 1 < chain.Length && third < 3; at++) {
                walked += Vector3.Distance(mesh.Positions[chain[at]], mesh.Positions[chain[at + 1]]);

                if (walked * 3f >= arc.Length * third) {
                    added |= forced.Add(chain[at + 1]);
                    third++;
                }
            }
        }

        return added;
    }

    /// <summary>Adds splits so a patch bounded by fewer than four arcs gets four.</summary>
    static bool Divide(ManifoldMesh mesh, List<LayoutArc> arcs, List<ArcUse> uses, HashSet<int> forced) {
        var added = false;

        foreach (var use in uses) {
            var chain = arcs[use.Arc].Vertices;

            if (chain.Length < 3) {
                continue;
            }

            // The arc-length midpoint, which is a function of the chain and so is the same however
            // the arc was walked.
            var total = arcs[use.Arc].Length;
            var walked = 0f;

            for (var at = 0; at + 1 < chain.Length; at++) {
                walked += Vector3.Distance(mesh.Positions[chain[at]], mesh.Positions[chain[at + 1]]);

                if (walked * 2f >= total) {
                    added |= forced.Add(chain[at + 1]);

                    break;
                }
            }
        }

        return added;
    }

    /// <summary>How much a sharp corner is worth against a mismatch between opposite sides.</summary>
    public const float CornerBias = 0.25f;

    /// <summary>How many split points the corner search will consider on one loop.</summary>
    /// <remarks>
    ///     The search is over three of them with the fourth fixed, so the cost is cubic; a loop with
    ///     more candidates than this keeps the sharpest and lets the rest sit inside a side, which is
    ///     what a side being a <i>list</i> of arcs is for.
    /// </remarks>
    public const int MaxCandidates = 24;

    /// <summary>§ D7's merge: dissolves a patch's longest non-feature arc so it joins its neighbour.</summary>
    /// <returns>Whether an arc was dissolved.</returns>
    /// <remarks>
    ///     ⚠ <b>Never a feature arc, whatever it costs.</b> A patch bounded entirely by creases stays as
    ///     it is and is counted as degenerate — dissolving one of them would put an output face across a
    ///     hard edge, which is the one thing § D4 exists to prevent, and it would trade a hole for a
    ///     wrong answer that nothing downstream can detect.
    /// </remarks>
    static bool Merge(
        ManifoldMesh mesh,
        FeatureGraph features,
        bool[] cut,
        List<LayoutArc> arcs,
        List<ArcUse> uses
    ) {
        var best = -1;
        var score = float.NegativeInfinity;

        foreach (var use in uses) {
            if (arcs[use.Arc].IsFeature || arcs[use.Arc].Length <= score) {
                continue;
            }

            score = arcs[use.Arc].Length;
            best = use.Arc;
        }

        if (best < 0) {
            return false;
        }

        var chain = arcs[best].Vertices;
        var dissolved = false;

        for (var at = 0; at + 1 < chain.Length; at++) {
            foreach (var half in (int[]) [
                SeparatrixTracer.Half(mesh, chain[at], chain[at + 1]),
                SeparatrixTracer.Half(mesh, chain[at + 1], chain[at])
            ]) {
                if (half < 0 || !cut[half] || features.IsFeatureEdge(half) || mesh.Twin(half) < 0) {
                    continue;
                }

                cut[half] = false;
                dissolved = true;
            }
        }

        return dissolved;
    }

    /// <summary>Groups a loop's arcs into four sides, at the corners that make opposite sides agree.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Opposite sides are matched to each other, and the four are emphatically <i>not</i>
    ///         made equal.</b> Splitting a loop into four equal quarters looks like the obvious rule and
    ///         it turns every long thin patch into a square: a strip whose real shape is 100 × 1 gets
    ///         four sides of 50 and quantizes to 2,500 quads instead of 100. Measured, that one mistake
    ///         took a box from a 400-quad budget to 5,966. What the quantization actually needs is
    ///         <c>|L₀ − L₂|</c> and <c>|L₁ − L₃|</c> small, because those are the two equalities it is
    ///         about to enforce — and a strip satisfies them perfectly at 100, 1, 100, 1.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Rotation-independent, and it has to be.</b> The loop's starting half-edge is an
    ///         artefact of the triangle order; the first corner is the sharpest turn on the loop,
    ///         tie-broken by vertex index, and the other three are searched relative to it. A rule that
    ///         read the loop's start would make the four sides of a square patch depend on which corner
    ///         the flood reached first, and the quantization would be solving a different problem on a
    ///         re-exported file — § D14.
    ///     </para>
    /// </remarks>
    static ArcUse[][] Sides(ManifoldMesh mesh, List<LayoutArc> arcs, List<ArcUse> uses) {
        var count = uses.Count;
        var turn = new float[count];
        var vertex = new int[count];

        for (var at = 0; at < count; at++) {
            var previous = uses[(at - 1 + count) % count];

            var incoming = Direction(mesh, arcs, previous, false);
            var outgoing = Direction(mesh, arcs, uses[at], true);

            // Zero along a straight run, a half at a right angle, one where the boundary doubles
            // back — so the score reads as "how much of a corner is this".
            turn[at] = incoming.LengthSquared() > 0f && outgoing.LengthSquared() > 0f
                ? (1f - Vector3.Dot(incoming, outgoing)) * 0.5f
                : 0f;

            vertex[at] = Ends(arcs, uses[at]).From;
        }

        var first = 0;

        for (var at = 1; at < count; at++) {
            if (turn[at] > turn[first] || (turn[at] == turn[first] && vertex[at] < vertex[first])) {
                first = at;
            }
        }

        // Cumulative target from the first corner round the loop, so the three that follow are placed
        // relative to it rather than to whichever half-edge the flood started on.
        var walked = new float[count + 1];

        for (var step = 0; step < count; step++) {
            walked[step + 1] = walked[step] + arcs[uses[(first + step) % count].Arc].Target;
        }

        var total = MathF.Max(walked[count], 1e-6f);
        var candidates = Candidates(count, turn, vertex, first);
        var corners = new int[4];

        corners[0] = first;

        var best = double.PositiveInfinity;

        for (var one = 0; one < candidates.Count - 2; one++) {
            for (var two = one + 1; two < candidates.Count - 1; two++) {
                for (var three = two + 1; three < candidates.Count; three++) {
                    var a = walked[candidates[one]];
                    var b = walked[candidates[two]];
                    var c = walked[candidates[three]];

                    var mismatch = (MathF.Abs(a - (c - b)) + MathF.Abs(b - a - (total - c))) / total;
                    var sharp = turn[first]
                        + turn[(first + candidates[one]) % count]
                        + turn[(first + candidates[two]) % count]
                        + turn[(first + candidates[three]) % count];

                    var score = mismatch - (CornerBias * sharp);

                    if (score >= best) {
                        continue;
                    }

                    best = score;
                    corners[1] = (first + candidates[one]) % count;
                    corners[2] = (first + candidates[two]) % count;
                    corners[3] = (first + candidates[three]) % count;
                }
            }
        }

        if (!double.IsFinite(best)) {
            for (var quarter = 1; quarter < 4; quarter++) {
                corners[quarter] = (first + quarter) % count;
            }
        }

        var sides = new ArcUse[4][];

        for (var side = 0; side < 4; side++) {
            var start = corners[side];
            var end = corners[(side + 1) % 4];
            var run = new List<ArcUse>();

            for (var at = start; at != end; at = (at + 1) % count) {
                run.Add(uses[at]);
            }

            sides[side] = [.. run];
        }

        return sides;
    }

    /// <summary>Which steps round the loop the corner search may pick, as offsets from the first corner.</summary>
    /// <remarks>
    ///     ⚠ <b>Kept in loop order after being chosen by turn, because the search reads them as three
    ///     ascending positions.</b> A candidate set sorted by score would have the triple loop
    ///     enumerating corner orders that do not go round the loop.
    /// </remarks>
    static List<int> Candidates(int count, float[] turn, int[] vertex, int first) {
        var steps = new List<int>(count - 1);

        for (var step = 1; step < count; step++) {
            steps.Add(step);
        }

        if (steps.Count > MaxCandidates) {
            steps.Sort(
                (one, two) => {
                    var byTurn = turn[(first + two) % count].CompareTo(turn[(first + one) % count]);

                    return byTurn != 0
                        ? byTurn
                        : vertex[(first + one) % count].CompareTo(vertex[(first + two) % count]);
                }
            );

            steps.RemoveRange(MaxCandidates, steps.Count - MaxCandidates);
            steps.Sort();
        }

        return steps;
    }

    /// <summary>An arc use's two end vertices, in the direction the patch walks it.</summary>
    public static (int From, int To) Ends(IReadOnlyList<LayoutArc> arcs, ArcUse use) {
        ArgumentNullException.ThrowIfNull(arcs);

        var chain = arcs[use.Arc].Vertices;

        return use.Reversed ? (chain[^1], chain[0]) : (chain[0], chain[^1]);
    }

    /// <summary>The unit direction an arc use leaves or arrives at, for the corner score.</summary>
    static Vector3 Direction(ManifoldMesh mesh, List<LayoutArc> arcs, ArcUse use, bool leaving) {
        var chain = arcs[use.Arc].Vertices;

        if (chain.Length < 2) {
            return Vector3.Zero;
        }

        var (first, second) = (leaving, use.Reversed) switch {
            (true, false) => (chain[0], chain[1]),
            (true, true) => (chain[^1], chain[^2]),
            (false, false) => (chain[^2], chain[^1]),
            _ => (chain[1], chain[0])
        };

        return ScaleSafe.Unit(mesh.Positions[second] - mesh.Positions[first]);
    }

    /// <summary>Whether the edge between two vertices is a feature edge, from either side.</summary>
    /// <remarks>
    ///     ⚠ <b>Both half-edge lookups are checked for <c>-1</c> before they are used as an index, and
    ///     the case is real rather than defensive.</b> A boundary vertex's ring has one more neighbour
    ///     than it has outgoing half-edges — the last edge of an open fan runs <i>into</i> it and never
    ///     out of it — so on an unclosed surface one of the two lookups legitimately finds nothing.
    ///     Measured: an <see cref="IndexOutOfRangeException" /> on the Möbius strip of the broken-mesh
    ///     corpus, which is exactly the input docs/plan/41's robustness criterion says must never throw.
    /// </remarks>
    public static bool IsFeature(ManifoldMesh mesh, FeatureGraph features, int from, int to) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(features);

        var half = SeparatrixTracer.Half(mesh, from, to);

        if (half >= 0 && features.IsFeatureEdge(half)) {
            return true;
        }

        var twin = SeparatrixTracer.Half(mesh, to, from);

        return twin >= 0 && features.IsFeatureEdge(twin);
    }

    /// <summary>Whether the edge between two vertices is a partition edge, from either side.</summary>
    public static bool IsCut(ManifoldMesh mesh, bool[] cut, int from, int to) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(cut);

        var half = SeparatrixTracer.Half(mesh, from, to);

        if (half >= 0 && cut[half]) {
            return true;
        }

        var twin = SeparatrixTracer.Half(mesh, to, from);

        return twin >= 0 && cut[twin];
    }

    static bool AnyCut(ManifoldMesh mesh, bool[] cut, int vertex) {
        foreach (var neighbour in mesh.Ring(vertex)) {
            if (IsCut(mesh, cut, vertex, neighbour)) {
                return true;
            }
        }

        return false;
    }
}
