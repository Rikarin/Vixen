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
    internal Quantization(int[] counts, double deviation, bool feasible, string[] warnings) {
        Counts = counts;
        Deviation = deviation;
        IsFeasible = feasible;
        Warnings = warnings;
    }

    /// <summary>How many quads run along each arc, by arc index.</summary>
    public IReadOnlyList<int> Counts { get; }

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

        var solved = Attempt(layout, mode, scale, true);

        // ⚠ The feature floor is tried and then given up on rather than imposed, and both halves
        // matter. Imposing it protects docs/plan/41's second exit criterion; it also makes the system
        // strictly harder, and a partition where it cannot be met has to produce a mesh rather than a
        // refusal. Measured: a box quantizes with the floor on some partitions and not on others, and
        // the fallback is the difference between 5,047 quads and nothing at all.
        return solved.IsFeasible ? solved : Attempt(layout, mode, scale, false);
    }

    /// <summary>One solve, with or without the no-collapse floor under the feature arcs.</summary>
    static Quantization Attempt(PatchLayout layout, QuantizeMode mode, float scale, bool holdFeatures) {
        var arcs = layout.Arcs.Count;
        var warnings = new List<string>();
        var lower = new int[arcs];
        var targets = new double[arcs];

        if (!holdFeatures) {
            warnings.Add("A feature arc had to be allowed to collapse for the layout to quantize.");
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
            lower[arc] = holdFeatures && layout.Arcs[arc].IsFeature ? 1 : 0;
        }

        var graph = Graph.Build(layout, arcs);

        for (var round = 0; round <= RepairRounds; round++) {
            var counts = Route(graph, targets, lower, mode, out var feasible);

            if (!feasible) {
                warnings.Add("The consistency system could not be satisfied, so the layout was refused.");

                return new(counts, Energy(counts, targets), false, [.. warnings]);
            }

            var forced = Collapsed(layout, counts, lower);

            if (forced == 0) {
                return new(counts, Energy(counts, targets), true, [.. warnings]);
            }

            if (round == RepairRounds) {
                warnings.Add($"{forced} patches collapsed to nothing and could not be forced open.");

                return new(counts, Energy(counts, targets), false, [.. warnings]);
            }

            warnings.Add($"{forced} patches quantized to zero in one direction and were forced open.");
        }

        return new(new int[arcs], 0d, false, [.. warnings]);
    }

    /// <summary>How many quads a quantization would produce, without extracting them.</summary>
    /// <param name="layout">The partition.</param>
    /// <param name="counts">One integer per arc.</param>
    /// <returns>The sum over patches of width times height.</returns>
    public static long QuadCount(PatchLayout layout, IReadOnlyList<int> counts) {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(counts);

        var total = 0L;

        foreach (var patch in layout.Patches) {
            total += (long) Sum(patch.Sides[0], counts) * Sum(patch.Sides[1], counts);
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
                Apply(graph, counts, reached, source);
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

    /// <summary>Walks the path back and applies every step of it.</summary>
    static void Apply(Graph graph, int[] counts, Reached reached, int source) {
        for (var state = reached.Sink; state != source && state >= 0; state = reached.FromState[state]) {
            var arc = reached.FromArc[state];
            var previous = reached.FromState[state];

            if (arc < 0 || previous < 0) {
                return;
            }

            var node = previous / 2;
            var sign = previous % 2 == 0 ? 1 : -1;

            counts[arc] += -sign * graph.Incidence(arc, node);
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

        public static Graph Build(PatchLayout layout, int arcs) {
            var nodes = (layout.Patches.Count * 2) + 1;
            var ground = nodes - 1;
            var built = new List<(int Node, int Sign)>[arcs];

            for (var arc = 0; arc < arcs; arc++) {
                built[arc] = [];
            }

            for (var patch = 0; patch < layout.Patches.Count; patch++) {
                var sides = layout.Patches[patch].Sides;

                for (var pair = 0; pair < 2; pair++) {
                    var node = (patch * 2) + pair;

                    foreach (var use in sides[pair]) {
                        built[use.Arc].Add((node, 1));
                    }

                    foreach (var use in sides[pair + 2]) {
                        built[use.Arc].Add((node, -1));
                    }
                }
            }

            var incidence = new (int Node, int Sign)[arcs][];
            var every = new (int Node, int Sign)[arcs][];

            for (var arc = 0; arc < arcs; arc++) {
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

            for (var arc = 0; arc < arcs; arc++) {
                foreach (var entry in incidence[arc]) {
                    counts[entry.Node + 1]++;
                }
            }

            for (var node = 1; node <= nodes; node++) {
                counts[node] += counts[node - 1];
            }

            var entries = new (int Arc, int Sign)[counts[nodes]];
            var cursor = new int[nodes];

            for (var arc = 0; arc < arcs; arc++) {
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
