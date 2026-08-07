// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Core.Threading;
using Vixen.Geometry.Uv.Flattening;

namespace Vixen.Geometry.Uv.Charting;

/// <summary>What the charter produced, including the two numbers that say which half of § D3 earned it.</summary>
/// <param name="ChartOfFace">Which chart each face belongs to, dense from zero.</param>
/// <param name="ChartCount">How many charts there are.</param>
/// <param name="BeforeMerge">How many there were when the recursion stopped and before anything was merged.</param>
/// <param name="SeamLength">The total world length of every edge two different charts meet along.</param>
/// <param name="Report">The charting half of the report.</param>
readonly record struct ChartOutcome(
    int[] ChartOfFace,
    int ChartCount,
    int BeforeMerge,
    double SeamLength,
    UvReport Report
);

/// <summary>§ D3's four steps: decompose, flatten, accept or recurse, and merge back.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/42 § D3, and the inversion in it is the whole design.</b> Chart count is an
///         <i>outcome of a quality target</i> rather than a knob: nothing here is told how many charts to
///         make, and the recursion splits only what fails <see cref="UvSettings.DistortionThreshold" />.
///         § D3 names the established tools' fragmentation — 51.6 charts where a dozen would do — as the
///         direct consequence of growing regions until a stretch bound trips <i>with nothing that ever
///         puts two back together</i>, and says which half of the fix is which: <b>step 4 is the cheap
///         half and step 3's top-down direction is the expensive half</b>.
///         <see cref="ChartOutcome.BeforeMerge" /> exists so that the two can be measured apart.
///     </para>
///     <para>
///         ⚠ <b>A whole level of the recursion is one <see cref="Flattener" /> call.</b> The regions at
///         one depth are independent, so they are handed over as one chart assignment and flattened
///         across the scheduler's workers together — which is both far faster than a flatten per region
///         and, more importantly, exactly the code path U2's determinism gate already covers. Charting
///         adds no parallelism of its own and therefore no new way to be non-deterministic.
///     </para>
///     <para>
///         ⚠ <b>Two different bounds, because two different things can fail to terminate.</b>
///         <see cref="UvSettings.MaxDepth" /> bounds the <i>distortion</i>-driven recursion — a chart
///         that will not come under τ is eventually accepted as it is. A <i>topological</i> failure is
///         bounded instead by the arithmetic: a split always produces strictly smaller parts and a
///         single face is always a disk, so cutting until every chart can be laid flat terminates on any
///         mesh. Accepting a non-disk at a depth limit would ship a chart that produces no island at
///         all, and § D3's recursion exists precisely to answer that.
///     </para>
/// </remarks>
static class Charter {
    /// <summary>How many merge-back rounds may run before the pass gives up.</summary>
    /// <remarks>
    ///     Each round merges a maximal set of disjoint pairs, so a run of <c>n</c> charts needs about
    ///     <c>log₂ n</c> rounds to reach one chart and this is a long way past that. It is a guard
    ///     against a cycle rather than a budget: a round that merges nothing stops the pass anyway.
    /// </remarks>
    const int MergeRounds = 32;

    /// <summary>Charts a mesh.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="settings">The distortion bound, the depth bound and the seam weights.</param>
    /// <param name="scheduler">Threads to spread each level's regions across, or <c>null</c>.</param>
    /// <param name="batch">How many regions one work item covers, or zero to let the scheduler choose.</param>
    /// <param name="mergeBack">Whether § D3's fourth step runs. ⚠ Off is a measurement, not a setting.</param>
    /// <returns>The chart assignment and what it measured.</returns>
    public static ChartOutcome Run(
        EditMesh mesh,
        UvSettings settings,
        JobScheduler? scheduler,
        int batch,
        bool mergeBack
    ) {
        var clock = Stopwatch.StartNew();
        var warnings = new List<string>();

        if (mesh.FaceCount == 0) {
            return new(
                [],
                0,
                0,
                0d,
                new(0, 0f, 0f, 0f, 0f, 0f, default, 0f, 0f, default, [new(UvStage.Chart, clock.Elapsed, 0)], warnings)
            );
        }

        var graph = SeamGraph.Build(mesh, settings);
        var accepted = Recurse(graph, mesh, settings, scheduler, batch, warnings);
        var before = accepted.Count;

        if (mergeBack) {
            accepted = Merge(graph, mesh, settings, scheduler, batch, accepted, warnings);
        }

        var chartOfFace = Number(mesh, accepted);
        var seam = SeamLength(graph, chartOfFace);

        var report = new UvReport(
            accepted.Count,
            0f,
            0f,
            (float)seam,
            (float)(graph.SurfaceArea > 0d ? seam / Math.Sqrt(graph.SurfaceArea) : 0d),
            0f,
            default,
            0f,
            0f,
            default,
            [new(UvStage.Chart, clock.Elapsed, accepted.Count)],
            warnings
        );

        return new(chartOfFace, accepted.Count, before, seam, report);
    }

    /// <summary>Steps one to three: candidate regions, flatten, and accept or split.</summary>
    static List<int[]> Recurse(
        SeamGraph graph,
        EditMesh mesh,
        UvSettings settings,
        JobScheduler? scheduler,
        int batch,
        List<string> warnings
    ) {
        var depthLimit = Math.Max(0, settings.MaxDepth);
        var accepted = new List<int[]>();
        var pending = new List<(int[] Faces, int Depth)>();

        foreach (var seed in Seeds(graph, mesh, settings)) {
            pending.Add((seed, 0));
        }

        // A split always produces strictly smaller parts, so the number of levels cannot exceed the
        // number of faces. The guard is what turns a defect in that argument into a warning rather than
        // a hang, which is docs/plan/42's exit criterion 2.
        var rounds = 0;
        var roundLimit = mesh.FaceCount + depthLimit + 8;

        while (pending.Count > 0) {
            if (++rounds > roundLimit) {
                warnings.Add(
                    $"Charting stopped after {roundLimit} levels with {pending.Count} regions still "
                    + "unresolved, and accepted them as they are. docs/plan/42 § D3 — a split always "
                    + "produces strictly smaller parts, so reaching this bound is a defect rather than a "
                    + "hard input."
                );

                foreach (var (faces, _) in pending) {
                    accepted.Add(faces);
                }

                break;
            }

            var chartOfFace = new int[mesh.FaceCount];

            Array.Fill(chartOfFace, -1);

            for (var region = 0; region < pending.Count; region++) {
                foreach (var face in pending[region].Faces) {
                    chartOfFace[face] = region;
                }
            }

            var outcome = Flattener.Run(mesh, chartOfFace, settings, scheduler, batch);
            var flattened = new bool[pending.Count];
            var passed = new bool[pending.Count];

            for (var island = 0; island < outcome.ChartOfIsland.Count; island++) {
                var region = outcome.ChartOfIsland[island];

                flattened[region] = true;
                passed[region] = Passes(outcome.Distortion[island], settings);
            }

            var next = new List<(int[], int)>();

            for (var region = 0; region < pending.Count; region++) {
                var (faces, depth) = pending[region];

                if (passed[region]) {
                    accepted.Add(faces);

                    continue;
                }

                // ⚠ The depth bound governs the distortion recursion only. A region that produced no
                // island is not a quality problem — it is an annulus, a closed surface or a pinch, and
                // accepting one would ship a chart with no coordinates at all.
                if (flattened[region] && depth >= depthLimit) {
                    accepted.Add(faces);

                    continue;
                }

                var parts = Divide(graph, mesh, settings, faces);

                if (parts.Count < 2) {
                    accepted.Add(faces);

                    warnings.Add(
                        $"A region of {faces.Length} faces could not be split any further and was "
                        + (flattened[region]
                            ? "accepted above the distortion threshold."
                            : "accepted although it could not be laid flat, so it produces no island.")
                        + " docs/plan/42 § D3 — the recursion detects a split that fails to reduce rather "
                        + "than repeating it."
                    );

                    continue;
                }

                foreach (var part in parts) {
                    next.Add((part, depth + 1));
                }
            }

            pending = next;
        }

        return accepted;
    }

    /// <summary>Step four: adjacent charts whose union still meets τ are merged, greedily, largest first.</summary>
    /// <remarks>
    ///     <para>
    ///         docs/plan/42 § D3's fourth step, and the cheap half of the fix. ⚠ <b>The ordering is
    ///         MeshTailor's canonical one used as a deterministic priority</b> — largest patches first —
    ///         with every tie broken on the chart's own lowest face index, so the pass never depends on
    ///         the order a dictionary happened to enumerate. § D12 gates byte-identical output and a
    ///         greedy merge over a hash set is exactly the way to lose it on somebody else's runtime.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A round proposes a maximal set of <i>disjoint</i> pairs and tests all of them at
    ///         once.</b> Testing one pair at a time would be a flatten per candidate and would make the
    ///         merge pass cost more than the recursion that produced the charts. Disjointness is what
    ///         makes the batch sound: no two proposed unions share a chart, so each one is a question
    ///         about a region that exists.
    ///     </para>
    /// </remarks>
    static List<int[]> Merge(
        SeamGraph graph,
        EditMesh mesh,
        UvSettings settings,
        JobScheduler? scheduler,
        int batch,
        List<int[]> charts,
        List<string> warnings
    ) {
        var merged = 0;

        for (var round = 0; round < MergeRounds && charts.Count > 1; round++) {
            var pairs = Pairs(graph, mesh, settings, charts);

            if (pairs.Count == 0) {
                break;
            }

            var chartOfFace = new int[mesh.FaceCount];

            Array.Fill(chartOfFace, -1);

            for (var pair = 0; pair < pairs.Count; pair++) {
                foreach (var face in charts[pairs[pair].Left]) {
                    chartOfFace[face] = pair;
                }

                foreach (var face in charts[pairs[pair].Right]) {
                    chartOfFace[face] = pair;
                }
            }

            var outcome = Flattener.Run(mesh, chartOfFace, settings, scheduler, batch);
            var accepted = new bool[pairs.Count];
            var any = false;

            for (var island = 0; island < outcome.ChartOfIsland.Count; island++) {
                var pair = outcome.ChartOfIsland[island];

                if (Passes(outcome.Distortion[island], settings)) {
                    accepted[pair] = true;
                    any = true;
                }
            }

            if (!any) {
                break;
            }

            var taken = new bool[charts.Count];
            var next = new List<int[]>();

            for (var pair = 0; pair < pairs.Count; pair++) {
                if (!accepted[pair]) {
                    continue;
                }

                var union = new List<int>(charts[pairs[pair].Left]);

                union.AddRange(charts[pairs[pair].Right]);
                union.Sort();

                taken[pairs[pair].Left] = true;
                taken[pairs[pair].Right] = true;
                next.Add([.. union]);
                merged++;
            }

            for (var chart = 0; chart < charts.Count; chart++) {
                if (!taken[chart]) {
                    next.Add(charts[chart]);
                }
            }

            next.Sort(static (left, right) => left[0].CompareTo(right[0]));
            charts = next;
        }

        if (merged > 0) {
            warnings.Add(
                $"The merge-back pass put {merged} adjacent chart pairs back together, because their "
                + "unions still met the distortion threshold. docs/plan/42 § D3 — chart count is an "
                + "outcome of a quality target, and this is the step that keeps it from being a count of "
                + "how often a bound tripped."
            );
        }

        return charts;
    }

    /// <summary>The disjoint adjacent pairs a round proposes, largest first.</summary>
    static List<(int Left, int Right)> Pairs(
        SeamGraph graph,
        EditMesh mesh,
        UvSettings settings,
        List<int[]> charts
    ) {
        var chartOfFace = new int[mesh.FaceCount];

        Array.Fill(chartOfFace, -1);

        for (var chart = 0; chart < charts.Count; chart++) {
            foreach (var face in charts[chart]) {
                chartOfFace[face] = chart;
            }
        }

        var seen = new HashSet<(int, int)>();
        var candidates = new List<(int Left, int Right, int Size)>();

        // Collected by walking the edges in index order and de-duplicated through a set that is never
        // itself enumerated — the list below is what gets sorted, and its order comes from the mesh.
        for (var edge = 0; edge < graph.EdgeFaceA.Length; edge++) {
            var left = graph.EdgeFaceA[edge];

            if (left < 0) {
                continue;
            }

            var right = graph.EdgeFaceB[edge];
            var one = chartOfFace[left];
            var other = chartOfFace[right];

            if (one < 0 || other < 0 || one == other) {
                continue;
            }

            var pair = (Math.Min(one, other), Math.Max(one, other));

            if (!seen.Add(pair)) {
                continue;
            }

            // ⚠ A material boundary partitions first and unconditionally, so it is also the one
            // boundary the merge pass may not undo. docs/plan/42 § D3.
            if (Grouped(mesh, settings)
                && mesh.Faces[charts[pair.Item1][0]].Group != mesh.Faces[charts[pair.Item2][0]].Group) {
                continue;
            }

            candidates.Add((pair.Item1, pair.Item2, charts[pair.Item1].Length + charts[pair.Item2].Length));
        }

        candidates.Sort(
            static (left, right) => {
                var size = right.Size.CompareTo(left.Size);

                if (size != 0) {
                    return size;
                }

                var first = left.Left.CompareTo(right.Left);

                return first != 0 ? first : left.Right.CompareTo(right.Right);
            }
        );

        var used = new bool[charts.Count];
        var pairs = new List<(int, int)>();

        foreach (var (left, right, _) in candidates) {
            if (used[left] || used[right]) {
                continue;
            }

            used[left] = true;
            used[right] = true;
            pairs.Add((left, right));
        }

        return pairs;
    }

    /// <summary>Whether this mesh's group boundaries are the ones § D3 partitions on.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The rule is about <i>material</i> boundaries and a group id alone does not say
    ///         whether it is one — which is the whole of <see cref="MeshGroupSource" />.</b> § D3 makes
    ///         a group boundary partition unconditionally because the texture already changes there; a
    ///         group that <see cref="EditMesh.Regroup" /> computed from coplanarity makes no such claim,
    ///         and on a faceted surface it is one group per triangle. Measured on sixteen image-to-3D
    ///         GLBs, honouring the coplanarity guess gave between 13 165 and 24 197 charts, one per
    ///         triangle to within a rounding — every one of them decided before a single distortion
    ///         measurement was taken.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The answer is not to relax the rule.</b> A mesh whose groups <i>were</i> assigned —
    ///         a block-out shape, a file whose materials the reader carried across, a face selection
    ///         somebody put in a group — still partitions first and still may not be merged back, and a
    ///         test holds that. This distinguishes the two cases rather than choosing between them.
    ///     </para>
    /// </remarks>
    static bool Grouped(EditMesh mesh, UvSettings settings) =>
        settings.KeepGroups && mesh.GroupSource is MeshGroupSource.Assigned;

    /// <summary>The candidate regions the recursion starts from.</summary>
    /// <remarks>
    ///     ⚠ <b>Material and face-group boundaries partition first and unconditionally.</b>
    ///     docs/plan/42 § D3, and it is unconditional because a group boundary is somewhere the texture
    ///     already changes — a seam there costs nothing that has not already been paid. Everything else
    ///     the charter does is a trade; this one is not — see <see cref="Grouped" /> for what counts as
    ///     one.
    /// </remarks>
    static List<int[]> Seeds(SeamGraph graph, EditMesh mesh, UvSettings settings) {
        var grouped = Grouped(mesh, settings);
        var seen = new bool[mesh.FaceCount];
        var seeds = new List<int[]>();
        var stack = new Stack<int>();

        for (var start = 0; start < mesh.FaceCount; start++) {
            if (seen[start]) {
                continue;
            }

            var group = mesh.Faces[start].Group;
            var found = new List<int>();

            stack.Push(start);
            seen[start] = true;

            while (stack.Count > 0) {
                var face = stack.Pop();

                found.Add(face);

                for (var link = graph.LinkStart[face]; link < graph.LinkStart[face + 1]; link++) {
                    var neighbour = graph.Links[link];

                    if (seen[neighbour]) {
                        continue;
                    }

                    if (grouped && mesh.Faces[neighbour].Group != group) {
                        continue;
                    }

                    seen[neighbour] = true;
                    stack.Push(neighbour);
                }
            }

            found.Sort();
            seeds.Add([.. found]);
        }

        seeds.Sort(static (left, right) => left[0].CompareTo(right[0]));

        return seeds;
    }

    /// <summary>Splits one region, through the caller's decomposition when there is one.</summary>
    /// <remarks>
    ///     ⚠ <b>A decomposition that declines or answers badly falls back rather than failing.</b>
    ///     § D14's second rule: a proposer proposes and never decides, so the worst a bad one can cost is
    ///     the quality of a chart. Validity is the built-in path's, and it stays there.
    /// </remarks>
    static List<int[]> Divide(SeamGraph graph, EditMesh mesh, UvSettings settings, int[] faces) {
        if (settings.Decomposition is null) {
            return Bisection.Split(graph, faces);
        }

        var proposal = settings.Decomposition.Decompose(mesh, faces, 2);

        if (proposal is null || proposal.Count != faces.Length) {
            return Bisection.Split(graph, faces);
        }

        var parts = new SortedDictionary<int, List<int>>();

        for (var index = 0; index < faces.Length; index++) {
            var part = proposal[index];

            if (!parts.TryGetValue(part, out var slot)) {
                parts[part] = slot = [];
            }

            slot.Add(faces[index]);
        }

        if (parts.Count < 2) {
            return Bisection.Split(graph, faces);
        }

        var split = new List<int[]>();

        foreach (var slot in parts.Values) {
            slot.Sort();
            split.Add([.. slot]);
        }

        split.Sort(static (left, right) => left[0].CompareTo(right[0]));

        return split;
    }

    /// <summary>Numbers the accepted charts, densely and in a canonical order.</summary>
    static int[] Number(EditMesh mesh, List<int[]> charts) {
        var chartOfFace = new int[mesh.FaceCount];

        Array.Fill(chartOfFace, -1);

        charts.Sort(static (left, right) => left[0].CompareTo(right[0]));

        for (var chart = 0; chart < charts.Count; chart++) {
            foreach (var face in charts[chart]) {
                chartOfFace[face] = chart;
            }
        }

        return chartOfFace;
    }

    /// <summary>The world length of every edge two different charts meet along.</summary>
    /// <remarks>
    ///     ⚠ <b>A mesh's own boundary is not a seam.</b> Nothing was cut there, and counting it would
    ///     make an open patch's seam length a statement about its silhouette — so an unwrap of a flat
    ///     square, which needs no cut at all, would report the perimeter of the square.
    /// </remarks>
    static double SeamLength(SeamGraph graph, int[] chartOfFace) {
        var length = 0d;

        for (var edge = 0; edge < graph.EdgeFaceA.Length; edge++) {
            var left = graph.EdgeFaceA[edge];

            if (left >= 0 && chartOfFace[left] != chartOfFace[graph.EdgeFaceB[edge]]) {
                length += graph.EdgeLengths[edge];
            }
        }

        return length;
    }

    /// <summary>The acceptance rule, which is <see cref="Flattener" />'s own and must stay it.</summary>
    /// <remarks>
    ///     ⚠ <b>A rule about <i>both</i> fields.</b> A chart inside the distortion bound with one folded
    ///     triangle has not passed: docs/plan/42 § D6 — the flip count is a correctness field wearing a
    ///     metric's clothes and no threshold applies to it. A chart that produced no island at all never
    ///     reaches here, because the flattener refused it before a solve ran.
    /// </remarks>
    static bool Passes(UvDistortion distortion, UvSettings settings) =>
        distortion.Flipped == 0
        && distortion.StretchL2 <= settings.DistortionThreshold
        && distortion.Area <= settings.DistortionThreshold;
}
