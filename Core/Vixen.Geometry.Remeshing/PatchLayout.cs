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
///         naming the stage that refused, and never an exception or a hang". A patch that cannot be
///         made usable in <see cref="RepairRounds" /> rounds is counted in <see cref="Warnings" /> and
///         left out; <see cref="IsUsable" /> goes false only when <i>none</i> of them came out, which is
///         the one case where there is no partition to hand on.
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
    ///     <para>
    ///         Each round either finishes or adds cuts, and adding cuts cannot go on forever because
    ///         every cut is an edge of a finite mesh. The cap is what turns a repair that oscillates
    ///         into a warning.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Reaching the cap is not a refusal, and it used to be.</b> The last round runs no
    ///         repair at all and returns what it built. A layout that has 391 usable patches out of 398
    ///         is a result with seven holes in it and a warning saying so; refusing it costs the whole
    ///         model its remesh over the seven, which is the opposite of what every other repair here
    ///         does with a patch it cannot fix.
    ///     </para>
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

    /// <summary>How many times the layout will try to walk a dangling cut out to existing structure.</summary>
    /// <remarks>
    ///     ⚠ <b>Each round extends at least one loose end or stops, so the bound is a guard rather than
    ///     a tuning number</b> — but a walk that stalls leaves a <i>new</i> loose end behind it, so the
    ///     rounds are not trivially monotone and docs/plan/41's robustness criterion says never a hang.
    /// </remarks>
    public const int ExtendRounds = 4;

    /// <summary>How much longer round than wide a patch may be before the layout cuts it in two.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A ratio of two lengths and never a length, which is the failure this repository
    ///         found five instances of in one day.</b> The measure is a patch's own perimeter against
    ///         its own area — <c>P² / 16A</c>, which is one for a square and grows without bound as a
    ///         region gets snaky — so a model in millimetres and the same model in kilometres cut in
    ///         exactly the same places. <see cref="ScaleSafe" /> is the established fix for the same
    ///         mistake in the maths; this is its layout form.
    ///     </para>
    ///     <para>
    ///         <b>Four, because three is a legitimate shape and five is not.</b> A patch twice as long
    ///         as it is wide scores one; the strip <see cref="Sides" /> is written to protect — 100 × 1,
    ///         whose sides are correctly matched at 100, 1, 100, 1 — scores about 25 and is genuinely
    ///         what the geometry wants. What the bound is aimed at is the patch that scores high because
    ///         its boundary <i>doubles back</i>, and measured on these fixtures those score from 9
    ///         upwards while every compact patch scores under 3.
    ///     </para>
    /// </remarks>
    public const float AspectBound = 4f;

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
            // ⚠ Every round and not once before the loop, because the repairs make their own loose
            // ends: `Merge` dissolves a patch's longest arc and whatever met that arc at its far end
            // is left dangling. Measured on a box, extending only up front left two behind — and two
            // slits are two patches the extractor refuses, which is two holes.
            Extend(mesh, field, features, cut);

            // ⚠ <b>The last round repairs nothing, which is the promise the `cutting` comment below
            // makes and the loop did not keep.</b> `Merge` dissolves a small patch's longest arc and
            // the `Extend` above walks the loose end that leaves straight back out along the same
            // edges, so the two undo each other exactly, every round, while both keep reporting that
            // they repaired something — `cutting` was written for the same oscillation between a cut
            // and a merge and does not catch this one, because this one needs no cut. Measured on a
            // 14 359-triangle generated mesh, rounds four, five and six were identical: 398 patches,
            // 1 659 arcs, 390 of them perfectly usable, and the whole layout refused at the end of
            // round six because `redo` was still true.
            //
            // ⚠ <b>A build-only round rather than "return what the last repairing round built", and
            // the difference is not cosmetic.</b> A repair mutates `cut` after the arcs have been built
            // from it, so a round that repairs cannot also be a round whose layout is returned — the
            // arcs and the partition it is handed on beside would disagree.
            var repairing = round < RepairRounds;

            var split = Splits(mesh, features, cut, forced);
            var (arcs, arcOfHalf, forwardOfHalf) = BuildArcs(mesh, features, density, cut, split);

            // ⚠ An arc that comes back to the vertex it started at is not a side of anything, and it
            // is what a cut loop carrying exactly one split produces. Its two ends are one output
            // vertex, so at one quad it is a zero-area face and at zero quads it is nothing at all —
            // both of which reach `Validate` as a non-manifold edge rather than as an error anybody
            // can attribute. Split it and rebuild.
            if (Reopen(mesh, arcs, forced) && repairing) {
                continue;
            }

            var patches = Flood(mesh, cut);
            var built = new List<LayoutPatch>(patches.Count);
            var redo = false;
            var degenerate = 0;
            var slit = 0;
            var snaky = 0;

            // ⚠ Cutting stops half way through the budget, and that is what makes the loop settle
            // rather than a hope that it will. A cut makes patches, `Merge` dissolves the small ones,
            // and the two undo each other for ever given the chance — measured, a cylinder that cut on
            // every round exhausted all six repairs and the whole layout was refused, which is a
            // fixture that used to come back with 2,958 quads. The rounds after this one only run the
            // repairs that were already convergent, so the last one always produces a layout.
            var cutting = repairing && round < RepairRounds / 2;

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
                    if (repairing && Bridge(mesh, cut, triangles, loops)) {
                        redo = true;
                    } else {
                        degenerate++;
                    }

                    continue;
                }

                var uses = Walk(loops[0], arcOfHalf, forwardOfHalf);

                // ⚠ Caught here rather than left to the extractor, which is the whole difference
                // between a hole and a mesh. A boundary that walks the same arc twice puts one run of
                // output vertices into a grid row twice; `PatchExtractor` sees that as "a side walked
                // the same vertex twice" and skips the patch, and a skipped patch is a hole in the
                // result with no attribution back to the stage that caused it. `Extend` above is the
                // repair; this is what happens when the repair could not land.
                if (Revisits(uses)) {
                    if (cutting && Compact(mesh, field, features, cut, arcs, uses, triangles)) {
                        redo = true;
                    } else {
                        slit++;
                    }

                    continue;
                }

                if (uses.Count < 4) {
                    // ⚠ Divide first and merge only when dividing is impossible. A patch bounded by
                    // three arcs usually wants a fourth corner; one bounded by three arcs that are
                    // each a single edge cannot have one, and § D7's answer for those is that a patch
                    // too small or degenerate merges into a neighbour. Dropping it instead is what
                    // leaves the output full of holes — measured on a box, nineteen arcs with a patch
                    // on only one side of them and seventy boundary edges to show for it.
                    //
                    // ⚠ And there is a third outcome that neither repair reaches, which is the
                    // cylinder's remaining hole. `Merge` will not dissolve a feature arc — rightly,
                    // since that is a crease — so a patch whose every bounding arc is one has nothing
                    // it is allowed to dissolve and comes back false however small it is. Measured on
                    // the cylinder at a 400 budget: one patch, uses=3, triangles=1, features=3, arc
                    // lengths [2,2,2] — a single source triangle with a crease along all three sides,
                    // dropped on every one of the six rounds and worth six boundary edges in the
                    // output. `MergeTriangles` is not the gate there and raising it to sixteen changes
                    // nothing; the gate is the feature test inside `Merge`. The real answer is to
                    // extract a three-sided patch as three quads round a centre point rather than to
                    // dissolve anything, which keeps the crease and fills the hole.
                    if (repairing && Divide(mesh, arcs, uses, forced)) {
                        redo = true;
                    } else if (repairing && triangles.Length <= MergeTriangles && Merge(mesh, features, cut, arcs, uses)) {
                        redo = true;
                    } else {
                        degenerate++;
                    }

                    continue;
                }

                // ⚠ Compaction is attempted on a patch that is *already* usable, which is why it sits
                // after every other test rather than before them. A patch far longer round than it is
                // wide quantizes to a product of two side lengths that its area never asked for, and
                // cutting it across the long axis is what § D7's "patches that are too small or
                // degenerate are merged" looks like from the other end.
                if (Roundness(mesh, arcs, uses, triangles) > AspectBound) {
                    if (cutting && Compact(mesh, field, features, cut, arcs, uses, triangles)) {
                        redo = true;

                        continue;
                    }

                    snaky++;
                }

                built.Add(new() { Triangles = triangles, Sides = Sides(mesh, arcs, uses) });
            }

            if (!redo) {
                if (degenerate > 0) {
                    warnings.Add($"{degenerate} patches could be neither divided nor merged and were dropped.");
                }

                if (slit > 0) {
                    warnings.Add($"{slit} patches were bounded by a slit that could not be cut through.");
                }

                if (snaky > 0) {
                    warnings.Add($"{snaky} patches are longer round than they are wide and could not be cut in two.");
                }

                var dangling = Loose(mesh, cut).Count;

                if (dangling > 0) {
                    warnings.Add($"{dangling} cuts still dead-end, so their patches are bounded by a slit.");
                }

                if (patches.Count > built.Count) {
                    warnings.Add(
                        $"{built.Count} of {patches.Count} patches came out usable after {round} repair round(s)."
                    );
                }

                return new([.. arcs], [.. built], cut, split, [.. warnings], exhausted, built.Count > 0);
            }
        }

        // Unreachable: the last round repairs nothing, so `redo` is false and the block above answers.
        // Kept because the compiler cannot see that, and written as a refusal rather than as a throw
        // because docs/plan/41's robustness criterion has no room for an exception out of a stage.
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

    /// <summary>Walks every dangling cut on out to existing structure, along the field.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the one defect, and the four the report lists are its faces.</b> A cut with a
    ///         loose end is a slit: the flood crosses round it, the same patch is on both sides, and the
    ///         boundary walk traverses that arc once in each direction. <see cref="Prune" /> removes the
    ///         slits it is allowed to and <b>a feature polyline's loose end is not one of them</b> — § D4
    ///         makes a crease a layout boundary whether or not it divides anything, so a crease that runs
    ///         off into a flat region legitimately dead-ends and legitimately cannot be pruned. Measured
    ///         on these fixtures, every duplicated arc in every patch was an opposed pair and almost all
    ///         of them lay on a feature: box 7 loose ends, union 25, and a sphere — the one fixture that
    ///         comes back <see cref="MeshReport.IsSolid" /> — <b>0</b>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So the answer is to finish the cut rather than to remove it.</b> A separatrix walked
    ///         out of the loose end along the field, stopping on whatever it meets, turns the slit into a
    ///         partition edge with a patch on each side. Four defects fall out together: the perimeter
    ///         stops being counted twice, so the budget stops overshooting; the boundary stops revisiting
    ///         a vertex, so <see cref="PatchExtractor" /> stops skipping the patch and leaving a hole; the
    ///         grid stops folding on itself, so the scaled Jacobian comes off zero; and the consistency
    ///         system stops being over-constrained, so the quantizer stops having to let a feature arc
    ///         collapse.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Pruned again after each round, because a walk that stalls leaves a new loose end.</b>
    ///         <see cref="SeparatrixTracer.Trace" /> marks any curve that moved at all, including one that
    ///         ran into a fold and stopped — and half an extension is the same slit one edge further
    ///         along. Pruning removes exactly those and keeps the ones that landed, so the round either
    ///         makes progress or is undone.
    ///     </para>
    /// </remarks>
    static void Extend(ManifoldMesh mesh, CrossField field, FeatureGraph features, bool[] cut) {
        var before = Loose(mesh, cut);

        if (before.Count == 0) {
            return;
        }

        for (var round = 0; round < ExtendRounds; round++) {
            // Ascending, which is what SeparatrixTracer's own contract asks for and is § D14's rule:
            // a motorcycle graph is order-dependent by construction, so the seed order has to be a
            // function of the mesh rather than of whichever walk happened to be enumerated first.
            SeparatrixTracer.Trace(mesh, field, features, before, cut, out _);
            Prune(mesh, features, cut);

            var after = Loose(mesh, cut);

            if (after.Count == 0) {
                return;
            }

            if (after.Count >= before.Count) {
                return;
            }

            before = after;
        }
    }

    /// <summary>Every vertex where exactly one partition edge ends, ascending.</summary>
    static List<int> Loose(ManifoldMesh mesh, bool[] cut) {
        var loose = new List<int>();

        for (var vertex = 0; vertex < mesh.VertexCount; vertex++) {
            var degree = 0;

            foreach (var neighbour in mesh.Ring(vertex)) {
                if (IsCut(mesh, cut, vertex, neighbour)) {
                    degree++;

                    if (degree > 1) {
                        break;
                    }
                }
            }

            if (degree == 1) {
                loose.Add(vertex);
            }
        }

        return loose;
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

    /// <summary>Whether a boundary walk uses any arc more than once.</summary>
    /// <remarks>
    ///     ⚠ <b>The arc and not the vertex, and the arc is the stronger test.</b> Two uses of one arc
    ///     are two traversals of the same run of output positions, so every vertex along it is revisited
    ///     rather than only the ends — and an arc used twice is precisely what a slit produces. Measured
    ///     across the fixtures before <see cref="Extend" /> existed, <i>every</i> duplicated use was an
    ///     opposed pair, which is a slit walked up one side and back down the other and is never anything
    ///     else.
    /// </remarks>
    static bool Revisits(List<ArcUse> uses) {
        var seen = new HashSet<int>(uses.Count);

        foreach (var use in uses) {
            if (!seen.Add(use.Arc)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>How much longer round a patch is than a compact one of the same area would be.</summary>
    /// <remarks>
    ///     ⚠ <b><c>P² / 16A</c>, which is one for a square and has no units at all.</b> That is the point:
    ///     a bound on a <i>length</i> is a claim about how big the model is, and this repository found
    ///     five of those in one day. A patch measured in millimetres and the same patch in kilometres
    ///     score identically, so <see cref="AspectBound" /> cuts in the same places at either size.
    /// </remarks>
    static float Roundness(ManifoldMesh mesh, List<LayoutArc> arcs, List<ArcUse> uses, int[] triangles) {
        var perimeter = 0f;

        foreach (var use in uses) {
            perimeter += arcs[use.Arc].Length;
        }

        var area = 0f;

        foreach (var triangle in triangles) {
            var corners = mesh.Corners(triangle);
            var one = mesh.Positions[corners[1]] - mesh.Positions[corners[0]];
            var two = mesh.Positions[corners[2]] - mesh.Positions[corners[0]];

            area += Vector3.Cross(one, two).Length() * 0.5f;
        }

        return area > 0f ? perimeter * perimeter / (16f * area) : 0f;
    }

    /// <summary>Cuts a patch across its long axis, along the field, landing on existing structure.</summary>
    /// <returns>Whether a cut was added, so the caller knows to rebuild.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The cut is a traced separatrix and not a shortest path, which is what keeps the two
    ///         halves griddable.</b> A patch is cut so that a regular grid fits inside it, and a grid's
    ///         rows follow the cross field — so a cut taken across the field leaves two patches whose
    ///         sides no longer line up with anything. <see cref="SeparatrixTracer" /> is the same walk
    ///         the partition was built out of, so the new boundary is made of the same kind of chain as
    ///         every other one and lands on whatever it meets.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The seed is the arc end half a perimeter round from the lowest-index one</b>, which
    ///         is a function of the mesh and so is the same on every machine — § D14. Reading the
    ///         boundary walk's own first entry instead would seed from wherever the flood happened to
    ///         start, and a re-exported file would be cut somewhere else.
    ///     </para>
    /// </remarks>
    static bool Compact(
        ManifoldMesh mesh,
        CrossField field,
        FeatureGraph features,
        bool[] cut,
        List<LayoutArc> arcs,
        List<ArcUse> uses,
        int[] triangles
    ) {
        if (uses.Count == 0) {
            return false;
        }

        var start = 0;

        for (var at = 1; at < uses.Count; at++) {
            if (Ends(arcs, uses[at]).From < Ends(arcs, uses[start]).From) {
                start = at;
            }
        }

        var perimeter = 0f;

        foreach (var use in uses) {
            perimeter += arcs[use.Arc].Length;
        }

        var walked = 0f;
        var seed = -1;

        for (var step = 0; step < uses.Count; step++) {
            var use = uses[(start + step) % uses.Count];

            walked += arcs[use.Arc].Length;

            if (walked * 2f >= perimeter) {
                seed = Ends(arcs, use).To;

                break;
            }
        }

        if (seed < 0 || (uint) seed >= (uint) mesh.VertexCount) {
            return false;
        }

        var before = Flood(mesh, cut).Count;

        SeparatrixTracer.Trace(mesh, field, features, [seed], cut, out _);
        Prune(mesh, features, cut);

        // ⚠ Only a cut that actually divided something counts as progress. A walk that ran straight
        // back into the boundary it started from has added an edge and no patch, and reporting that as
        // progress is how a repair loop spends its whole budget achieving nothing — which is the
        // split-then-merge oscillation docs/plan/41's robustness criterion is written against.
        return Flood(mesh, cut).Count > before;
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
