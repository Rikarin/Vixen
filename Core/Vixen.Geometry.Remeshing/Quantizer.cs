// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Geometry.Remeshing;

/// <summary>How hard the quantization tries.</summary>
/// <remarks>
///     ⚠ <b>docs/plan/41 § D7 asks for both and names what the second is for: interactive preview.</b>
///     They are the same formulation and differ only in how a unit of imbalance is routed —
///     <see cref="Exact" /> routes it along the cheapest path there is, <see cref="Approximate" /> along
///     the shortest one. Both are deterministic and both come out feasible; only the first is optimal.
/// </remarks>
enum QuantizeMode : byte {
    /// <summary>Successive shortest paths with node potentials — the minimum-deviation answer.</summary>
    Exact,

    /// <summary>Fewest-arc routing, which is one breadth-first sweep per unit and is not optimal.</summary>
    Approximate
}

/// <summary>How many quads each arc gets, and what that cost.</summary>
/// <remarks>
///     ⚠ <b><see cref="Counts" /> may hold a zero and that is legitimate, which docs/plan/41 § D7 says
///     in as many words</b> — a side that quantizes to nothing collapses, and collapsing one side is how
///     a five-sided patch becomes four-sided. What is <i>not</i> legitimate is a patch whose whole width
///     or whole height comes to zero, because that patch produces no quads at all;
///     <see cref="Quantizer" /> repairs those and <see cref="IsFeasible" /> goes false if it cannot.
/// </remarks>
sealed class Quantization {
    internal Quantization(
        int[] counts,
        (int A, int B, int C)[] spokes,
        double deviation,
        bool feasible,
        string[] warnings
    ) {
        Counts = counts;
        Spokes = spokes;
        Deviation = deviation;
        IsFeasible = feasible;
        Warnings = warnings;
    }

    /// <summary>How many quads run along each arc, by arc index.</summary>
    public IReadOnlyList<int> Counts { get; }

    /// <summary>Per patch, a fan's three spoke counts, or three zeros where the patch is a quad.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A fan's three quad blocks are <c>a × b</c>, <c>b × c</c> and <c>c × a</c>, and its
    ///         three sides come to <c>a + c</c>, <c>a + b</c> and <c>b + c</c>.</b> That is the whole of
    ///         the three-to-quads subdivision written as arithmetic: <i>a</i>, <i>b</i> and <i>c</i> are
    ///         the three spokes running from the centre out to the three side midpoints, every block's
    ///         two opposite sides agree by construction, and the sides' total is <c>2(a + b + c)</c> —
    ///         even, which it has to be, because no all-quad mesh of a disc has an odd boundary.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Solved for rather than derived, and that is why it lives in the flow.</b> Reading
    ///         <c>a = (n₀ + n₁ − n₂) / 2</c> off counts the router chose without the fan in it gives a
    ///         half-integer as often as not, and a negative one whenever one side outruns the other two
    ///         — measured on the sphere at a thousandth, the dropped patch's three sides rounded to 1, 2
    ///         and 2, which is odd and so has no all-quad filling at all. Three extra variables with a
    ///         floor of one and three extra conservation constraints make the parity and the triangle
    ///         inequality things the router <i>satisfies</i> rather than things a later stage discovers.
    ///     </para>
    /// </remarks>
    public IReadOnlyList<(int A, int B, int C)> Spokes { get; }

    /// <summary>The energy — the sum over arcs of the squared distance from what the density field asked.</summary>
    /// <remarks>The number the two modes are compared on, and the reason the exact one is worth having.</remarks>
    public double Deviation { get; }

    /// <summary>Whether every constraint is satisfied and no patch collapsed to nothing.</summary>
    public bool IsFeasible { get; }

    /// <summary>What had to be forced, if anything.</summary>
    public IReadOnlyList<string> Warnings { get; }
}

/// <summary>docs/plan/41 § D7's quantization: a minimum-deviation flow, and never an integer program.</summary>
/// <remarks>
///     <para>
///         <b>Bi-MDF</b> (Heistermann, Warnett and Bommes, SIGGRAPH 2023), read from the paper. Patch
///         sides are the arcs, the consistency constraints are conservation at the nodes, and "as close
///         as possible to the size the density field asked for" is the deviation cost. Three reasons it
///         is this and not an integer program, and all three are constraints rather than preferences:
///         docs/plan/41 Part 1's licence table excludes Gurobi and CoMISo outright, the paper measures
///         two hundred times the speed at better energy, and — the one § D14 needs — <b>a flow solver
///         with an explicit tie-break is deterministic and auditable where an integer program's answer
///         depends on its solver's version and its internal timing</b>.
///     </para>
///     <para>
///         <b>The formulation, in this codebase's terms.</b> One integer variable per
///         <see cref="LayoutArc" />. One node per patch per direction, carrying the equality "this
///         patch's two opposite side groups sum to the same thing" — which is what makes a regular grid
///         exist inside it. An arc shared by two patches is <i>one</i> variable in two nodes, so the
///         across-the-seam constraint costs nothing to state. The cost is
///         <c>(x − target)²</c>, convex and separable.
///     </para>
///     <para>
///         ⚠ <b>Bi-directed means an arc's two nodes may want the <i>same</i> sign, and that is why the
///         search runs over two states per node rather than one.</b> A patch can walk a shared arc the
///         same way its neighbour does, in which case increasing that arc increases both nodes' sums —
///         an arc with two heads, which an ordinary directed flow has no way to write down. Doubling the
///         node set into "this node is one too high" and "this node is one too low" turns it back into
///         an ordinary shortest-path problem, exactly.
///     </para>
///     <para>
///         ⚠ <b>Starting from the per-arc rounded optimum is what makes the search exact rather than
///         greedy.</b> At <c>x = round(t)</c> every single-step move costs <c>±2(x − t) + 1 ≥ 0</c>,
///         because <c>|x − t| ≤ ½</c> — so the residual graph has no negative arc and therefore no
///         negative cycle, which is the precondition the successive-shortest-path theorem needs.
///         Maintaining node potentials keeps it true after every augmentation, and the answer that comes
///         out is the minimum rather than a good one.
///     </para>
/// </remarks>
static class Quantizer {
    /// <summary>How many times a collapsed patch may be forced open before the layout is refused.</summary>
    public const int RepairRounds = 4;

    /// <summary>How many augmentations one solve may run, as a multiple of the arc count.</summary>
    /// <remarks>
    ///     Each augmentation removes at least one unit of imbalance and the imbalance starts bounded by
    ///     the rounded counts, so this is a guard against a bug rather than a tuning number — but
    ///     docs/plan/41's robustness criterion says never a hang, and a guard that is never hit still has
    ///     to be there.
    /// </remarks>
    public const int AugmentBudget = 64;

    /// <summary>How many augmentations may fail to improve before a component is collapsed.</summary>
    public const int StallLimit = 16;

    /// <summary>The multipliers on every target that are tried, in order, before a feature arc is let go.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A crease is worth more than a quad count, and docs/plan/41 says which of the two exit
    ///         criteria this phase exists to make achievable.</b> The no-collapse floor under the feature
    ///         arcs is a lower bound the router cannot move <i>down</i>, so on a partition whose targets
    ///         are all about one there is often no feasible routing at all — and the answer used to be to
    ///         drop the floor, which deletes a crease and takes a box from <c>5.9e-5</c> to
    ///         <c>5.0e-2</c>. Giving the whole system more quads keeps the relative densities § D9 asked
    ///         for and buys the slack the floor needs.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is the <i>whole</i> system that is scaled and not the feature arcs alone.</b>
    ///         Raising only the creases' targets changes what the router wants without changing what it
    ///         is allowed to do — the floor binds because the arcs around it have nowhere to go, and
    ///         those are the ordinary ones.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Reaching past the first entry costs budget and says so</b>, because a result that is
    ///         three times over its count for a reason a caller cannot see is the failure
    ///         <see cref="RemeshReport.Warnings" /> exists to prevent.
    ///     </para>
    /// </remarks>
    public static ReadOnlySpan<float> FeatureRelief => [1f, 1.5f, 2f, 3f];

    /// <summary>Solves § D7's system over a layout.</summary>
    /// <param name="layout">The partition.</param>
    /// <param name="mode">Which solver.</param>
    /// <param name="scale">
    ///     A multiplier on every arc's target. ⚠ The budget lever: a partition of snaky patches asks for
    ///     far more quads than its area needs, because a patch's quad count is the product of two side
    ///     lengths and a snaky patch's perimeter is long for its area. Scaling every target together
    ///     keeps the relative densities the density field asked for and moves the total.
    /// </param>
    /// <returns>One integer per arc.</returns>
    public static Quantization Solve(
        PatchLayout layout,
        QuantizeMode mode = QuantizeMode.Exact,
        float scale = 1f
    ) {
        ArgumentNullException.ThrowIfNull(layout);

        var solved = Relieved(layout, mode, scale, true);

        // ⚠ <b>The fan constraints are structural and they are still allowed to be given up, for the
        // same reason the feature floor is.</b> Three spokes with a floor of one force every side of a
        // three-sided patch to at least two quads, which is a lower bound the rest of the system may
        // not be able to absorb — and a partition where it cannot has to produce a mesh rather than a
        // refusal. Giving them up costs exactly the fans: `PatchExtractor` refuses a fan whose spokes
        // do not add up and leaves the hole it would have left anyway, and every other patch extracts
        // as it would have.
        if (solved.IsFeasible || !layout.Patches.Any(patch => patch.IsFan)) {
            return solved;
        }

        var without = Relieved(layout, mode, scale, false);

        return without.IsFeasible
            ? new(
                [.. without.Counts],
                [.. without.Spokes],
                without.Deviation,
                true,
                [
                    .. without.Warnings,
                    "The three-sided patches could not be given a centre the rest of the system agreed with, "
                    + "so they were left out."
                ]
            )
            : solved;
    }

    /// <summary>The feature-floor ladder: tried, paid for, and only then given up on.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ All three halves matter. Imposing the floor protects docs/plan/41's second exit
    ///         criterion; it also makes the system strictly harder, and a partition where it cannot be
    ///         met has to produce a mesh rather than a refusal. Measured: a box quantizes with the floor
    ///         on some partitions and not on others, and the fallback is the difference between a result
    ///         and nothing at all.
    ///     </para>
    ///     <para>
    ///         ⚠ It fires far less than it did, and the reason is the layout rather than this solver. A
    ///         partition with slits in it puts one arc into three or four constraints instead of two,
    ///         which is what made the floor unsatisfiable; once PatchLayout walks its loose ends out, a
    ///         union and a difference both quantize with the floor on where neither used to.
    ///     </para>
    /// </remarks>
    static Quantization Relieved(PatchLayout layout, QuantizeMode mode, float scale, bool holdFans) {
        foreach (var relief in FeatureRelief) {
            var solved = Attempt(layout, mode, scale * relief, true, holdFans);

            if (!solved.IsFeasible) {
                continue;
            }

            if (relief > 1f) {
                return new(
                    [.. solved.Counts],
                    [.. solved.Spokes],
                    solved.Deviation,
                    true,
                    [
                        .. solved.Warnings,
                        $"The budget was multiplied by {relief:0.#} so that no feature arc had to collapse."
                    ]
                );
            }

            return solved;
        }

        return Attempt(layout, mode, scale, false, holdFans);
    }

    /// <summary>One solve, with or without the no-collapse floor and the fans' centres.</summary>
    static Quantization Attempt(
        PatchLayout layout,
        QuantizeMode mode,
        float scale,
        bool holdFeatures,
        bool holdFans
    ) {
        var arcs = layout.Arcs.Count;
        var warnings = new List<string>();
        var fanOf = Fans(layout, holdFans, out var fans);
        var variables = arcs + (fans * 3);
        var lower = new int[variables];
        var targets = new double[variables];

        if (!holdFeatures) {
            warnings.Add("A feature arc had to be allowed to collapse for the layout to quantize.");
        }

        // ⚠ An arc is not a feature arc and still may not collapse, when both of its ends are ends of
        // ones. Collapsing it merges its two ends into a single output vertex — § D7's permitted zero —
        // and where both ends carry a crease that single vertex has to stand for two distinct points of
        // the feature graph, so one of the two creases starts somewhere its own chain never went.
        // Measured on a union: the worst feature arc was a single straight edge with a chord sagitta of
        // exactly zero, reporting 1.66e-2 of the diagonal because a plain neighbour had collapsed across
        // it. § D4 makes a hard edge a chain of output edges by construction, and that is a claim about
        // where the chain *starts* as much as about what runs along it.
        var creased = new HashSet<int>();

        for (var arc = 0; arc < arcs; arc++) {
            if (layout.Arcs[arc].IsFeature) {
                creased.Add(layout.Arcs[arc].Vertices[0]);
                creased.Add(layout.Arcs[arc].Vertices[^1]);
            }
        }

        for (var arc = 0; arc < arcs; arc++) {
            // ⚠ The density field's own number, with no floor under it, and a floor was measured to
            // be the wrong instinct. Forcing every arc to want at least one quad inflates a side made
            // of twenty short traced runs from a true six to a demanded twenty, the balance
            // constraint then drags the opposite side up to meet it, and the budget comes out an order
            // of magnitude over — measured on a box: 4,194 quads against a target of 400. An arc that
            // rounds to nothing collapses, and § D7 says that is legitimate.
            targets[arc] = layout.Arcs[arc].Target * scale;

            // ⚠ A feature arc may not collapse, and this is the one place docs/plan/41 § D7's "a side
            // may quantize to zero" has to be refused. Collapsing an arc merges its two ends into one
            // output vertex, and doing that to a crease deletes the crease — measured, the box's
            // feature reproduction error went from 5.1e-5 to 5.1e-2 the moment the budget scaling
            // pushed a feature arc to nothing. § D4 makes a hard edge a chain of output edges by
            // construction, and a chain of no edges is not one.
            lower[arc] = holdFeatures
                && (layout.Arcs[arc].IsFeature
                    || (creased.Contains(layout.Arcs[arc].Vertices[0])
                        && creased.Contains(layout.Arcs[arc].Vertices[^1])))
                ? 1
                : 0;
        }

        // ⚠ A spoke's floor is one and it is not negotiable within a solve, because the fan's three
        // blocks are `a × b`, `b × c` and `c × a` — a spoke of zero is a block with no quads in it and
        // a boundary the other two blocks cannot close round. It is the whole of the parity and the
        // triangle inequality, stated as a bound the router already knows how to respect.
        for (var patch = 0; patch < layout.Patches.Count; patch++) {
            if (fanOf[patch] < 0) {
                continue;
            }

            var sides = layout.Patches[patch].Sides;
            var slot = arcs + (fanOf[patch] * 3);

            for (var at = 0; at < 3; at++) {
                // a = (n₀ + n₁ − n₂) / 2 and its two rotations, taken on the *targets* so the router
                // starts where the density field would have put the centre if it could count halves.
                targets[slot + at] = Math.Max(
                    1d,
                    (Wanted(layout, sides[at], scale)
                        + Wanted(layout, sides[(at + 1) % 3], scale)
                        - Wanted(layout, sides[(at + 2) % 3], scale)) * 0.5d
                );

                lower[slot + at] = 1;
            }
        }

        var graph = Graph.Build(layout, variables, arcs, fanOf);

        // ⚠ <b>The best answer seen so far, kept because a repair round can make the system
        // unsolvable and used to take the whole result down with it.</b> Forcing a collapsed patch
        // open raises a lower bound, and a lower bound that the consistency constraints cannot absorb
        // turns a routing that <i>was</i> feasible into one that is not — measured on two of sixteen
        // image-to-3D meshes, where round zero solved cleanly with eight collapsed patches out of 312
        // and round one came back "could not be satisfied" and refused all 312.
        //
        // ⚠ <b>And the <i>fewest</i> collapsed patches seen, which is not the same thing and is not the
        // last round either.</b> Each repair raises a floor, and a raised floor can push the routing
        // somewhere far worse rather than nearer — measured on `Solder 4.glb`, where the rounds went 19
        // collapsed, then 13, then 4, then 2, and then <b>375</b> of 378, and returning the last one
        // meant 248 skipped patches and 780 quads against a 5,000 budget where round three would have
        // given 6,197. The repair is a heuristic and a heuristic that is allowed to make things worse
        // has to be allowed to be overruled.
        int[]? best = null;
        var collapsed = 0;
        int[]? fewest = null;
        var fewestCollapsed = int.MaxValue;

        for (var round = 0; round <= RepairRounds; round++) {
            var counts = Route(graph, targets, lower, mode, out var feasible);

            if (!feasible) {
                if (best is null) {
                    warnings.Add("The consistency system could not be satisfied, so the layout was refused.");

                    return Result(layout, arcs, fanOf, counts, targets, false, warnings);
                }

                warnings.Add(
                    $"Forcing the collapsed patches open made the system unsolvable, so {collapsed} were left out."
                );

                return Result(layout, arcs, fanOf, best, targets, true, warnings);
            }

            var forced = Collapsed(layout, counts, lower);

            if (forced == 0) {
                return Result(layout, arcs, fanOf, counts, targets, true, warnings);
            }

            best = counts;
            collapsed = forced;

            if (forced < fewestCollapsed) {
                fewestCollapsed = forced;
                fewest = counts;
            }

            // ⚠ <b>A patch that will not open is a hole and not a refusal, and reading it as one cost
            // every generated mesh in the corpus its remesh.</b> The counts here satisfy every
            // consistency constraint — `Route` said so — so the arcs the rest of the partition shares
            // with this patch are sound and its neighbours extract exactly as they would have.
            // `PatchExtractor.Fill` already skips a patch whose side came out at zero and counts it in
            // `patches were skipped`, which is the same answer `PatchLayout` gives a patch it can
            // neither divide nor merge. Refusing the whole model over four patches out of 398 is the
            // one thing docs/plan/41's robustness criterion is written against: "one such component
            // must not cost the rest of the model its remesh".
            if (round == RepairRounds) {
                var kept = fewest ?? counts;

                warnings.Add(
                    $"{fewestCollapsed} patches collapsed to nothing, could not be forced open and were left out."
                );

                return Result(layout, arcs, fanOf, kept, targets, true, warnings);
            }

            warnings.Add($"{forced} patches quantized to zero in one direction and were forced open.");
        }

        return Result(layout, arcs, fanOf, new int[variables], targets, false, warnings);
    }

    /// <summary>Which patches are fans, numbered in patch order, and how many there are.</summary>
    /// <remarks>
    ///     ⚠ In patch order and not in discovery order — § D14. The three spoke variables of fan
    ///     <i>t</i> are <c>arcs + 3t</c> onwards, so their numbering decides which node the router
    ///     reaches first, and that has to be a function of the layout rather than of an enumeration.
    /// </remarks>
    static int[] Fans(PatchLayout layout, bool hold, out int fans) {
        var fanOf = new int[layout.Patches.Count];

        Array.Fill(fanOf, -1);

        fans = 0;

        if (!hold) {
            return fanOf;
        }

        for (var patch = 0; patch < fanOf.Length; patch++) {
            if (layout.Patches[patch].IsFan) {
                fanOf[patch] = fans++;
            }
        }

        return fanOf;
    }

    /// <summary>Splits the router's variables back into the arcs and the fans' three spokes.</summary>
    static Quantization Result(
        PatchLayout layout,
        int arcs,
        int[] fanOf,
        int[] counts,
        double[] targets,
        bool feasible,
        List<string> warnings
    ) {
        var spokes = new (int A, int B, int C)[layout.Patches.Count];

        for (var patch = 0; patch < spokes.Length; patch++) {
            var slot = fanOf[patch] < 0 ? -1 : arcs + (fanOf[patch] * 3);

            if (slot >= 0 && slot + 2 < counts.Length) {
                spokes[patch] = (counts[slot], counts[slot + 1], counts[slot + 2]);
            }
        }

        return new(counts[..arcs], spokes, Energy(counts, targets), feasible, [.. warnings]);
    }

    /// <summary>What the density field asked of one side, before any rounding.</summary>
    static double Wanted(PatchLayout layout, ArcUse[] side, float scale) {
        var total = 0d;

        foreach (var use in side) {
            total += layout.Arcs[use.Arc].Target * scale;
        }

        return total;
    }

    /// <summary>How many quads a quantization would produce, without extracting them.</summary>
    /// <param name="layout">The partition.</param>
    /// <param name="quantization">What the router chose.</param>
    /// <returns>The sum over patches of width times height, and of a fan's three blocks.</returns>
    public static long QuadCount(PatchLayout layout, Quantization quantization) {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(quantization);

        var total = 0L;

        for (var patch = 0; patch < layout.Patches.Count; patch++) {
            if (layout.Patches[patch].IsFan) {
                var (a, b, c) = quantization.Spokes[patch];

                total += ((long) a * b) + ((long) b * c) + ((long) c * a);

                continue;
            }

            total += (long) Sum(layout.Patches[patch].Sides[0], quantization.Counts)
                * Sum(layout.Patches[patch].Sides[1], quantization.Counts);
        }

        return total;
    }

    /// <summary>The energy the two modes are compared on.</summary>
    public static double Energy(IReadOnlyList<int> counts, IReadOnlyList<double> targets) {
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentNullException.ThrowIfNull(targets);

        var total = 0d;

        // In index order, which is § D14's rule for every reduction in this assembly.
        for (var arc = 0; arc < counts.Count; arc++) {
            var away = counts[arc] - targets[arc];

            total += away * away;
        }

        return total;
    }

    /// <summary>Forces open every patch that came out with no width or no height.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>§ D7: "a patch that collapses to nothing is a bug, and the validator refuses
    ///         it".</b> The repair raises the floor under the largest arc of the collapsed direction —
    ///         largest by target, tie-broken by index — which is the cheapest place to put the one quad
    ///         that has to exist.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every collapsed patch in one pass, not the first one.</b> Fixing one per round and
    ///         re-solving looks tidier and turns a fragmented layout into a refusal: measured, a box
    ///         whose partition left nine collapsed patches ran out of repair rounds having fixed four of
    ///         them, and the whole remesh came back empty.
    ///     </para>
    /// </remarks>
    static int Collapsed(PatchLayout layout, int[] counts, int[] lower) {
        var forced = 0;

        for (var patch = 0; patch < layout.Patches.Count; patch++) {
            var sides = layout.Patches[patch].Sides;

            // ⚠ A fan has no opposite sides to collapse against, and it cannot collapse anyway: its
            // three spokes carry a floor of one, so each of its three sides comes to at least two
            // whenever the routing is feasible at all. Forcing one open here would raise a bound the
            // fan never asked for.
            if (layout.Patches[patch].IsFan) {
                continue;
            }

            for (var pair = 0; pair < 2; pair++) {
                if (Sum(sides[pair], counts) > 0 || Sum(sides[pair + 2], counts) > 0) {
                    continue;
                }

                forced++;

                var best = -1;
                var score = double.NegativeInfinity;

                foreach (var side in (ArcUse[][]) [sides[pair], sides[pair + 2]]) {
                    foreach (var use in side) {
                        var target = layout.Arcs[use.Arc].Target;

                        if (target > score) {
                            score = target;
                            best = use.Arc;
                        }
                    }
                }

                if (best >= 0) {
                    lower[best] = Math.Max(lower[best], 1);
                }
            }
        }

        return forced;
    }

    static int Sum(ArcUse[] side, IReadOnlyList<int> counts) {
        var total = 0;

        foreach (var use in side) {
            total += counts[use.Arc];
        }

        return total;
    }

    /// <summary>Rounds, then routes the imbalance away.</summary>
    static int[] Route(Graph graph, double[] targets, int[] lower, QuantizeMode mode, out bool feasible) {
        var counts = new int[targets.Length];

        for (var arc = 0; arc < counts.Length; arc++) {
            // ⚠ Away from zero on a tie, which is a rule rather than a default: `MidpointRounding`'s
            // banker's rounding sends exactly-a-half to the *even* integer, so two arcs the density
            // field treated identically would round differently on the strength of their parity.
            counts[arc] = Math.Max(lower[arc], (int) Math.Round(targets[arc], MidpointRounding.AwayFromZero));
        }

        var states = graph.Nodes * 2;
        var potential = new double[states];
        var residual = graph.Residuals(counts);
        var imbalance = Imbalance(residual);

        var best = counts.ToArray();
        var lowest = imbalance;
        var stalled = 0;

        for (var round = 0; round < AugmentBudget * (counts.Length + 1) && imbalance > 0; round++) {
            var source = Source(residual, graph.Ground);

            if (source < 0) {
                break;
            }

            var reached = mode == QuantizeMode.Exact
                ? Cheapest(graph, counts, targets, lower, residual, potential, source)
                : Shortest(graph, counts, lower, residual, source);

            if (reached.Sink >= 0) {
                Apply(graph, counts, lower, reached, source);
            } else {
                Collapse(graph, counts, lower, source / 2);
            }

            residual = graph.Residuals(counts);
            imbalance = Imbalance(residual);

            if (imbalance < lowest) {
                lowest = imbalance;
                stalled = 0;

                Array.Copy(counts, best, counts.Length);

                continue;
            }

            // ⚠ A router that stops improving is collapsed back rather than left to spin, and the
            // collapse is what makes this terminate at all. Every constraint here is homogeneous, so
            // taking a whole component to its lower bounds satisfies all of them — it is a bad answer
            // and it is a guaranteed one, which is the trade docs/plan/41's exit criterion 7 asks for:
            // never an exception, never a hang, and a report that says what happened.
            if (++stalled >= StallLimit) {
                stalled = 0;

                Collapse(graph, counts, lower, source / 2);

                residual = graph.Residuals(counts);
                imbalance = Imbalance(residual);

                if (imbalance < lowest) {
                    lowest = imbalance;

                    Array.Copy(counts, best, counts.Length);
                }
            }
        }

        feasible = lowest == 0;

        return lowest <= imbalance ? best : counts;
    }

    /// <summary>Takes every movable arc of one node's component down to its lower bound.</summary>
    /// <remarks>
    ///     The escape hatch, and it is exact rather than approximate about what it guarantees: the
    ///     constraints are all "these two side groups sum to the same thing", so an all-lower-bounds
    ///     assignment satisfies every one of them whose bounds are zero.
    /// </remarks>
    static void Collapse(Graph graph, int[] counts, int[] lower, int node) {
        var seen = new bool[graph.Nodes];
        var stack = new Stack<int>();

        seen[node] = true;
        stack.Push(node);

        while (stack.TryPop(out var current)) {
            foreach (var (arc, _) in graph.At(current)) {
                counts[arc] = lower[arc];

                var other = graph.Other(arc, current, out _);

                if (other != graph.Ground && !seen[other]) {
                    seen[other] = true;
                    stack.Push(other);
                }
            }
        }
    }

    /// <summary>How far the whole system is from satisfying its constraints.</summary>
    static long Imbalance(int[] residual) {
        var total = 0L;

        for (var node = 0; node < residual.Length; node++) {
            total += Math.Abs(residual[node]);
        }

        return total;
    }

    /// <summary>The lowest-index node that is out of balance, and the state its excess sits in.</summary>
    static int Source(int[] residual, int ground) {
        for (var node = 0; node < residual.Length; node++) {
            if (node != ground && residual[node] != 0) {
                return (node * 2) + (residual[node] > 0 ? 0 : 1);
            }
        }

        return -1;
    }

    /// <summary>Successive shortest paths with potentials — the exact route.</summary>
    static Reached Cheapest(
        Graph graph,
        int[] counts,
        double[] targets,
        int[] lower,
        int[] residual,
        double[] potential,
        int source
    ) {
        var states = graph.Nodes * 2;
        var distance = new double[states];
        var fromArc = new int[states];
        var fromState = new int[states];

        Array.Fill(distance, double.PositiveInfinity);
        Array.Fill(fromArc, -1);
        Array.Fill(fromState, -1);

        distance[source] = 0d;

        // Priority carries the state index so that two states at the same distance come off in a
        // fixed order — § D14, and the reason a flow solver is auditable where an ILP is not.
        var queue = new PriorityQueue<int, (double Cost, int State)>();

        queue.Enqueue(source, (0d, source));

        var sink = -1;

        while (queue.TryDequeue(out var state, out var priority)) {
            if (priority.Cost > distance[state]) {
                continue;
            }

            if (state != source && IsSink(graph, residual, state, source)) {
                sink = state;

                break;
            }

            var node = state / 2;
            var sign = state % 2 == 0 ? 1 : -1;

            foreach (var (arc, incidence) in graph.At(node)) {
                var step = -sign * incidence;

                if (counts[arc] + step < lower[arc]) {
                    continue;
                }

                var cost = Marginal(counts[arc], targets[arc], step);
                var other = graph.Other(arc, node, out var otherSign);
                var next = (other * 2) + (-sign * otherSign * incidence > 0 ? 0 : 1);
                var reduced = cost + potential[state] - potential[next];

                // Reduced costs are non-negative by construction, and clamping rather than trusting
                // costs nothing and stops one denormal from making Dijkstra wrong.
                reduced = Math.Max(reduced, 0d);

                if (distance[state] + reduced >= distance[next]) {
                    continue;
                }

                distance[next] = distance[state] + reduced;
                fromArc[next] = arc;
                fromState[next] = state;

                queue.Enqueue(next, (distance[next], next));
            }
        }

        if (sink >= 0) {
            // ⚠ The minimum of the state's own distance and the sink's, and not the state's distance
            // on its own. The search stops the moment it pops the sink, so any state further away than
            // that still holds a *tentative* distance rather than a settled one — folding a tentative
            // number into the potentials breaks the invariant that every reduced cost is
            // non-negative, and the next search then walks paths that are not shortest at all. It is
            // the standard successive-shortest-path formula and the reason is exactly this.
            for (var state = 0; state < states; state++) {
                potential[state] += Math.Min(distance[state], distance[sink]);
            }
        }

        return new(sink, fromArc, fromState);
    }

    /// <summary>Fewest-arc routing — the interactive-preview route, feasible and not optimal.</summary>
    static Reached Shortest(Graph graph, int[] counts, int[] lower, int[] residual, int source) {
        var states = graph.Nodes * 2;
        var fromArc = new int[states];
        var fromState = new int[states];
        var seen = new bool[states];

        Array.Fill(fromArc, -1);
        Array.Fill(fromState, -1);

        var queue = new Queue<int>();

        queue.Enqueue(source);
        seen[source] = true;

        while (queue.TryDequeue(out var state)) {
            if (state != source && IsSink(graph, residual, state, source)) {
                return new(state, fromArc, fromState);
            }

            var node = state / 2;
            var sign = state % 2 == 0 ? 1 : -1;

            foreach (var (arc, incidence) in graph.At(node)) {
                var step = -sign * incidence;

                if (counts[arc] + step < lower[arc]) {
                    continue;
                }

                var other = graph.Other(arc, node, out var otherSign);
                var next = (other * 2) + (-sign * otherSign * incidence > 0 ? 0 : 1);

                if (seen[next]) {
                    continue;
                }

                seen[next] = true;
                fromArc[next] = arc;
                fromState[next] = state;
                queue.Enqueue(next);
            }
        }

        return new(-1, fromArc, fromState);
    }

    /// <summary>Whether arriving in a state cancels an imbalance rather than creating one.</summary>
    /// <remarks>
    ///     <para>
    ///         Arriving in the "one too high" state means a unit of positive excess was just added
    ///         there, so it cancels only where the node was already negative — and the other way round.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The source's own node stays eligible, and excluding it is what leaves a lone
    ///         imbalance of two permanently stuck.</b> A node whose residual is <c>+2</c> with every
    ///         other node balanced has no partner anywhere: the only way to fix it is a path that leaves
    ///         it and returns through a two-headed arc, taking a unit off at each end. The sink test
    ///         already tells that apart from a path that comes back and undoes itself — returning in the
    ///         "too low" state removes a second unit and qualifies, returning in the "too high" state
    ///         puts the first one back and does not, and the residual's sign decides which. Measured on
    ///         a sphere: the router reached an imbalance of two and then could not move at all.
    ///     </para>
    /// </remarks>
    static bool IsSink(Graph graph, int[] residual, int state, int source) {
        var node = state / 2;

        if (node == graph.Ground) {
            return true;
        }

        if (state % 2 == 0 ? residual[node] >= 0 : residual[node] <= 0) {
            return false;
        }

        // ⚠ A path that returns to the node it left takes a second unit off the *same* residual, so
        // there has to be a second unit there. Without this the router flips a residual of one between
        // <c>+1</c> and <c>-1</c> for ever: each augmentation changes it by two, the sign alternates,
        // the magnitude does not, and the total imbalance never moves. Measured on a box, thirteen
        // consecutive augmentations at the same node with the imbalance stuck at twenty-four.
        return node != source / 2 || Math.Abs(residual[node]) >= 2;
    }

    /// <summary>Walks the path back and applies every step of it, or none of them.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A path may cross the same arc twice, and the search checks one step at a time — so
    ///         the lower bound has to be re-checked <i>after</i> the whole path is laid down.</b> The
    ///         states are <c>(node, sign)</c> pairs and the search tree is over states, so no state
    ///         repeats on a path; an <i>arc</i> repeats whenever the path leaves a node in one state and
    ///         comes back to it in the other, which is exactly the two-headed case the bi-directed
    ///         formulation exists for. Each of the two visits sees the count as it was before either
    ///         move, both pass, and an arc sitting one above its bound goes one below it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Measured on § D9's solved base, where it is reachable and on the naive one it was
    ///         not.</b> A box and a sphere both quantized a side to <c>−1</c>; that side's patch then
    ///         reported its two opposite sides disagreeing, <see cref="PatchExtractor" /> skipped it, and
    ///         the box came back with 30 boundary edges and a feature reproduction error of
    ///         <c>2.8e-2</c> — three orders past docs/plan/41's second exit criterion, from one integer.
    ///         The longer base is what made the counts small enough to reach zero; the fault is this,
    ///         and it was always here.
    ///     </para>
    /// </remarks>
    static void Apply(Graph graph, int[] counts, int[] lower, Reached reached, int source) {
        for (var state = reached.Sink; state != source && state >= 0; state = reached.FromState[state]) {
            var arc = reached.FromArc[state];
            var previous = reached.FromState[state];

            if (arc < 0 || previous < 0) {
                return;
            }

            var node = previous / 2;
            var sign = previous % 2 == 0 ? 1 : -1;

            // ⚠ Clamped step by step rather than the path abandoned whole, and the difference is three
            // orders of magnitude. The search already proved every *single* step legal against the
            // counts as they stood, so this is a no-op on every path that crosses each arc once — which
            // is nearly all of them, and the router's behaviour is unchanged there. Refusing the whole
            // path instead was measured and is a catastrophe: the router stalls, `Collapse` takes whole
            // components down to their lower bounds, and the real corpus came back with 5 quads on one
            // mesh and 68 on another against a budget of 5,000.
            counts[arc] = Math.Max(lower[arc], counts[arc] + (-sign * graph.Incidence(arc, node)));
        }
    }

    /// <summary>What one step of an arc costs, on the convex deviation.</summary>
    static double Marginal(int count, double target, int step) {
        var here = count - target;
        var there = count + step - target;

        return (there * there) - (here * here);
    }

    readonly record struct Reached(int Sink, int[] FromArc, int[] FromState);

    /// <summary>The bi-directed graph: arcs, and the nodes they carry a sign at.</summary>
    /// <remarks>
    ///     ⚠ <b>An arc in more than two nodes is not a flow arc at all, and it is pinned rather than
    ///     approximated.</b> A patch that walks the same arc twice — which happens where an annulus was
    ///     cut to a disc — puts one variable into three or four constraints, and the shortest-path
    ///     machinery below has no meaning there. Pinning it to its rounded target keeps the rest of the
    ///     system a genuine flow problem and keeps the answer exact <i>for that system</i>, which is
    ///     better than an approximate answer to the right one with no way to tell.
    /// </remarks>
    sealed class Graph {
        int[] starts = [];
        (int Arc, int Sign)[] entries = [];
        (int Node, int Sign)[][] incidence = [];
        (int Node, int Sign)[][] every = [];

        /// <summary>How many nodes, including the ground node.</summary>
        public int Nodes { get; private init; }

        /// <summary>The node that absorbs anything — where an arc with only one constraint ends.</summary>
        public int Ground { get; private init; }

        /// <summary>Every arc at a node, with the sign it carries there.</summary>
        public ReadOnlySpan<(int Arc, int Sign)> At(int node) =>
            entries.AsSpan(starts[node], starts[node + 1] - starts[node]);

        /// <summary>An arc's sign at a node, or zero where it does not touch it.</summary>
        public int Incidence(int arc, int node) {
            foreach (var entry in incidence[arc]) {
                if (entry.Node == node) {
                    return entry.Sign;
                }
            }

            return 0;
        }

        /// <summary>The other node an arc touches, and the sign it carries there.</summary>
        public int Other(int arc, int node, out int sign) {
            foreach (var entry in incidence[arc]) {
                if (entry.Node != node) {
                    sign = entry.Sign;

                    return entry.Node;
                }
            }

            sign = 0;

            return Ground;
        }

        /// <summary>How far out of balance each node is, in index order.</summary>
        /// <remarks>
        ///     ⚠ <b>Over <i>every</i> arc and not only the ones the flow may move.</b> A pinned arc is
        ///     still a side of its patch and still counts toward that patch's sums; leaving it out of
        ///     the balance would have the solver satisfy a constraint that is not the one the geometry
        ///     imposes, and the extraction would then find two sides of a patch disagreeing.
        /// </remarks>
        public int[] Residuals(int[] counts) {
            var residual = new int[Nodes];

            for (var arc = 0; arc < every.Length; arc++) {
                foreach (var entry in every[arc]) {
                    residual[entry.Node] += entry.Sign * counts[arc];
                }
            }

            residual[Ground] = 0;

            return residual;
        }

        /// <summary>Builds the constraints: two nodes per quad patch, three per fan, one ground.</summary>
        /// <remarks>
        ///     ⚠ <b>A fan's three constraints are the <i>same</i> conservation law the quad patches
        ///     carry, and that is why the router needs no new machinery for it.</b> Side <i>k</i> of a
        ///     fan equals two of its spokes — <c>n₀ = a + c</c>, <c>n₁ = a + b</c>, <c>n₂ = b + c</c> —
        ///     which is one group of variables summing to another, exactly like "these two opposite side
        ///     groups agree". A spoke appears in two nodes with the <i>same</i> sign, which is the
        ///     two-headed arc the bi-directed formulation above exists for and is why it is stated in
        ///     spokes rather than in halves of sides.
        /// </remarks>
        public static Graph Build(PatchLayout layout, int variables, int arcs, int[] fanOf) {
            // A fan the caller chose not to hold contributes no node at all — it has no opposite sides
            // to constrain and no spokes to constrain them against, so it is simply not in the system.
            var offset = new int[layout.Patches.Count + 1];

            for (var patch = 0; patch < layout.Patches.Count; patch++) {
                offset[patch + 1] = offset[patch]
                    + (layout.Patches[patch].IsFan ? fanOf[patch] >= 0 ? 3 : 0 : 2);
            }

            var nodes = offset[^1] + 1;
            var ground = nodes - 1;
            var built = new List<(int Node, int Sign)>[variables];

            for (var arc = 0; arc < variables; arc++) {
                built[arc] = [];
            }

            for (var patch = 0; patch < layout.Patches.Count; patch++) {
                var sides = layout.Patches[patch].Sides;

                if (layout.Patches[patch].IsFan) {
                    if (fanOf[patch] < 0) {
                        continue;
                    }

                    var slot = arcs + (fanOf[patch] * 3);

                    for (var at = 0; at < 3; at++) {
                        var node = offset[patch] + at;

                        foreach (var use in sides[at]) {
                            built[use.Arc].Add((node, 1));
                        }

                        built[slot + at].Add((node, -1));
                        built[slot + ((at + 2) % 3)].Add((node, -1));
                    }

                    continue;
                }

                for (var pair = 0; pair < 2; pair++) {
                    var node = offset[patch] + pair;

                    foreach (var use in sides[pair]) {
                        built[use.Arc].Add((node, 1));
                    }

                    foreach (var use in sides[pair + 2]) {
                        built[use.Arc].Add((node, -1));
                    }
                }
            }

            var incidence = new (int Node, int Sign)[variables][];
            var every = new (int Node, int Sign)[variables][];

            for (var arc = 0; arc < variables; arc++) {
                var merged = new List<(int Node, int Sign)>();

                foreach (var entry in built[arc]) {
                    var at = merged.FindIndex(existing => existing.Node == entry.Node);

                    if (at < 0) {
                        merged.Add(entry);
                    } else {
                        merged[at] = (entry.Node, merged[at].Sign + entry.Sign);
                    }
                }

                merged.RemoveAll(entry => entry.Sign == 0);

                every[arc] = [.. merged];

                // Pinned: not a flow arc, so the search never moves it — but it still counts toward
                // its patches' sums, which is what `every` is for.
                incidence[arc] = merged.Count > 2 || merged.Exists(entry => Math.Abs(entry.Sign) != 1)
                    ? []
                    : [.. merged];
            }

            var counts = new int[nodes + 1];

            for (var arc = 0; arc < variables; arc++) {
                foreach (var entry in incidence[arc]) {
                    counts[entry.Node + 1]++;
                }
            }

            for (var node = 1; node <= nodes; node++) {
                counts[node] += counts[node - 1];
            }

            var entries = new (int Arc, int Sign)[counts[nodes]];
            var cursor = new int[nodes];

            for (var arc = 0; arc < variables; arc++) {
                foreach (var entry in incidence[arc]) {
                    entries[counts[entry.Node] + cursor[entry.Node]] = (arc, entry.Sign);
                    cursor[entry.Node]++;
                }
            }

            return new() {
                Nodes = nodes,
                Ground = ground,
                starts = counts,
                entries = entries,
                incidence = incidence,
                every = every
            };
        }
    }
}
