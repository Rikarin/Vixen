// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Core.Mathematics;
using Vixen.Core.Threading;

namespace Vixen.Geometry.Uv.Flattening;

/// <summary>A chart the ladder would not flatten, and why — which is what U3's recursion reads.</summary>
/// <param name="Chart">Which chart, as the caller numbered it.</param>
/// <param name="Reason">What stopped it.</param>
/// <param name="Flipped">How many triangles were still folded when the repair pass gave up.</param>
readonly record struct ChartRefused(int Chart, ChartRefusal Reason, int Flipped);

/// <summary>Everything one <c>Flatten</c> produced, including what a caller who will recurse needs.</summary>
/// <param name="Islands">One per chart that flattened, in ascending chart order.</param>
/// <param name="ChartOfIsland">Which chart each island came from, parallel to <paramref name="Islands" />.</param>
/// <param name="Distortion">What each island measured on its own, parallel to <paramref name="Islands" />.</param>
/// <param name="Refused">The charts that produced no island at all.</param>
/// <param name="Report">The distortion half of the report.</param>
readonly record struct FlattenOutcome(
    IReadOnlyList<UvIsland> Islands,
    IReadOnlyList<int> ChartOfIsland,
    IReadOnlyList<UvDistortion> Distortion,
    IReadOnlyList<ChartRefused> Refused,
    UvReport Report
);

/// <summary>The three rungs of § D5, each one paid for only when the one below fails its bound.</summary>
/// <remarks>
///     <para>
///         docs/plan/42 § D5 and § Part 5's U2. <see cref="Lscm" /> is one solve and is where most
///         charts stop; <see cref="Arap" /> is a local–global loop over a matrix that is built once;
///         the repair pass is that same loop over the folded neighbourhood alone. A chart that still
///         folds is <b>refused rather than shipped</b>, because § D5 is explicit that a flipped
///         triangle is a correctness failure and the answer to one is to split the chart — which is
///         U3's recursion, and which needs to be told.
///     </para>
///     <para>
///         ⚠ <b>Charts are flattened in parallel and nothing inside one is.</b> Charts share nothing:
///         each writes its own island, its own measures and its own warnings, and the merge afterwards
///         runs in chart order on the calling thread. That is what makes the output byte-identical at
///         any worker count without a single reduction being reordered — and it is why
///         <see cref="Solving.ConjugateGradient" /> is never handed the scheduler here. Two charts
///         solving at once on one instance is what its "not thread-safe" remark is about.
///     </para>
/// </remarks>
static class Flattener {
    /// <summary>How many rings out from a folded triangle the first repair pass frees.</summary>
    const int RepairRing = 2;

    /// <summary>How many times the repair pass grows its neighbourhood before it gives up.</summary>
    /// <remarks>
    ///     ⚠ <b>Bounded, because the alternative is a chart that takes minutes to be refused.</b> Each
    ///     pass costs a matrix build over the freed region, and a region that has grown to the whole
    ///     chart four times over is not going to be fixed by a fifth — at that point the honest answer
    ///     is the one § D5 gives, which is to hand the chart back for splitting.
    /// </remarks>
    const int RepairPasses = 4;

    /// <summary>Flattens every chart of a mesh.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="chartOfFace">Which chart each face belongs to. Negative excludes the face.</param>
    /// <param name="settings">The iteration counts and the distortion bound.</param>
    /// <param name="scheduler">Threads to spread the charts across, or <c>null</c> for the calling thread.</param>
    /// <param name="batch">How many charts one work item covers, or zero to let the scheduler choose.</param>
    /// <returns>The islands, what they measured, and what was refused.</returns>
    public static FlattenOutcome Run(
        EditMesh mesh,
        IReadOnlyList<int> chartOfFace,
        UvSettings settings,
        JobScheduler? scheduler,
        int batch
    ) {
        var clock = Stopwatch.StartNew();
        var charts = ChartMesh.Extract(mesh, chartOfFace);
        var results = new Attempt[charts.Count];
        var job = new FlattenJob { Charts = charts, Settings = settings, Results = results };

        if (scheduler is not null && charts.Count > 1) {
            scheduler.ParallelFor(job, charts.Count, batch);
        } else {
            for (var index = 0; index < charts.Count; index++) {
                job.Execute(index);
            }
        }

        var islands = new List<UvIsland>(charts.Count);
        var chartOfIsland = new List<int>(charts.Count);
        var distortion = new List<UvDistortion>(charts.Count);
        var measures = new List<Distortion.Measured>(charts.Count);
        var refused = new List<ChartRefused>();
        var warnings = new List<string>();
        var triangles = 0;

        for (var index = 0; index < charts.Count; index++) {
            var chart = charts[index];
            var attempt = results[index];

            triangles += chart.TriangleCount;

            if (attempt.Refusal != ChartRefusal.None) {
                refused.Add(new(chart.Id, attempt.Refusal, attempt.Measure.Distortion.Flipped));
                warnings.Add(Explain(chart, attempt));

                continue;
            }

            if (attempt.Clamped > 0) {
                warnings.Add(
                    $"Chart {chart.Id} has {attempt.Clamped} obtuse triangle corners whose cotangent "
                    + $"weight was negative — the most so at {attempt.Worst:0.####} — and every one of "
                    + "them was raised to a positive floor, so the Laplacian stays positive definite. See "
                    + "CotangentWeights: read the cotangent rather than the count, because it is an angle "
                    + "(-0.4 is 114°, -6 is 170°), and an intrinsic Delaunay flip is the upgrade a large "
                    + "one would justify."
                );
            }

            if (attempt.Degenerate > 0) {
                warnings.Add(
                    $"Chart {chart.Id} carries {attempt.Degenerate} triangles with no area in three "
                    + "dimensions. They were left out of every energy and every measure; their corners "
                    + "took the coordinates their neighbours gave them."
                );
            }

            islands.Add(attempt.Island);
            chartOfIsland.Add(chart.Id);
            distortion.Add(attempt.Measure.Distortion);
            measures.Add(attempt.Measure);
        }

        var report = new UvReport(
            islands.Count,
            0f,
            0f,
            0f,
            0f,
            0f,
            Distortion.Combine(measures),
            0f,
            0f,
            default,
            [new(UvStage.Flatten, clock.Elapsed, triangles)],
            warnings
        );

        return new(islands, chartOfIsland, distortion, refused, report);
    }

    static string Explain(ChartMesh chart, Attempt attempt) =>
        attempt.Refusal switch {
            ChartRefusal.Empty => attempt.Degenerate > 0
                ? $"Chart {chart.Id} has {attempt.Degenerate} triangles and not one of them has any area "
                + "in three dimensions, so there is nothing to lay flat."
                : $"Chart {chart.Id} has no triangles.",
            ChartRefusal.Disconnected =>
                $"Chart {chart.Id} falls into pieces that share no vertex. Flattening is defined on one "
                + "connected chart; the split is the charter's to make.",
            ChartRefusal.NonManifoldEdge =>
                $"Chart {chart.Id} has an edge carrying three or more of its own triangles, so it has no "
                + "consistent orientation to preserve.",
            ChartRefusal.NonManifoldVertex =>
                $"Chart {chart.Id} pinches to a point: a boundary position where more than two boundary "
                + "edges meet. Euler characteristic does not see this — a bowtie counts as a disk — so it "
                + "is checked on its own.",
            ChartRefusal.Closed =>
                $"Chart {chart.Id} is closed and has no boundary, so no cut has been made and nothing can "
                + "be laid flat. docs/plan/42 § D5.",
            ChartRefusal.NotADisk =>
                $"Chart {chart.Id} has {chart.BoundaryLoops} boundary loops and Euler characteristic "
                + $"{chart.EulerCharacteristic}, so it is not a disk — χ = 2 − 2g − b, and a disk is 1. "
                + "⚠ No injective map to the plane exists, so a flattener that produced one produced a "
                + "fold. It needs another cut, which is docs/plan/42 § D3's recursion.",
            _ =>
                $"Chart {chart.Id} still has {attempt.Measure.Distortion.Flipped} folded triangles after "
                + $"{RepairPasses} repair passes. docs/plan/42 § D5 — a flipped triangle is a correctness "
                + "failure, so the chart is refused rather than shipped and is split instead."
        };

    /// <summary>What one chart's trip up the ladder produced.</summary>
    readonly record struct Attempt(
        ChartRefusal Refusal,
        UvIsland Island,
        Distortion.Measured Measure,
        int Clamped,
        double Worst,
        int Degenerate
    );

    readonly struct FlattenJob : IJobParallelFor {
        public required List<ChartMesh> Charts { get; init; }
        public required UvSettings Settings { get; init; }
        public required Attempt[] Results { get; init; }

        public void Execute(int index) => Results[index] = Climb(Charts[index], Settings);
    }

    /// <summary>One chart, up as many rungs as it needs.</summary>
    static Attempt Climb(ChartMesh chart, UvSettings settings) {
        if (chart.Topology != ChartRefusal.None) {
            return new(chart.Topology, default, default, 0, 0d, 0);
        }

        var frames = new TriangleFrame[chart.TriangleCount];
        var degenerate = 0;

        for (var triangle = 0; triangle < chart.TriangleCount; triangle++) {
            var a = chart.Positions[chart.Triangles[triangle * 3]];
            var b = chart.Positions[chart.Triangles[(triangle * 3) + 1]];
            var c = chart.Positions[chart.Triangles[(triangle * 3) + 2]];

            frames[triangle] = TriangleFrame.Build(a, b, c);

            if (frames[triangle].IsDegenerate) {
                degenerate++;
            }
        }

        if (degenerate == chart.TriangleCount) {
            return new(ChartRefusal.Empty, default, default, 0, 0d, degenerate);
        }

        var coordinates = new double[chart.VertexCount * 2];
        var rounded = new Vector2[chart.VertexCount];

        Lscm.Solve(chart, frames, settings, coordinates);
        Round(coordinates, rounded);

        var measure = Distortion.Measure(chart, frames, coordinates, rounded);
        var weights = CotangentWeights.Build(chart, frames);
        var anchor = Pins.Choose(chart).First;

        // ⚠ The ladder's rule, and it is a rule about *both* fields. A chart that is inside the
        // distortion bound but has one folded triangle has not passed: docs/plan/42 § D6 — the flip
        // count is a correctness field wearing a metric's clothes, and no threshold applies to it.
        if (!Passes(measure, settings)) {
            var movable = new bool[chart.VertexCount];

            Array.Fill(movable, true);
            movable[anchor] = false;

            Arap.Solve(
                chart,
                frames,
                weights,
                movable,
                coordinates,
                settings.FlattenIterations,
                settings.SolverIterations
            );

            Round(coordinates, rounded);
            measure = Distortion.Measure(chart, frames, coordinates, rounded);
        }

        if (measure.Distortion.Flipped > 0) {
            measure = Repair(chart, frames, weights, settings, anchor, measure, coordinates, rounded);
        }

        if (measure.Distortion.Flipped > 0) {
            return new(ChartRefusal.Folded, default, measure, weights.Clamped, weights.Worst, degenerate);
        }

        return new(
            ChartRefusal.None,
            Island(chart, rounded, measure),
            measure,
            weights.Clamped,
            weights.Worst,
            degenerate
        );
    }

    /// <summary>Whether a rung's output is good enough that the next one need not be paid for.</summary>
    static bool Passes(Distortion.Measured measure, UvSettings settings) =>
        measure.Distortion.Flipped == 0
        && measure.Distortion.StretchL2 <= settings.DistortionThreshold
        && measure.Distortion.Area <= settings.DistortionThreshold;

    /// <summary>The third rung: the same local–global loop over the folded neighbourhood alone.</summary>
    /// <remarks>
    ///     <para>
    ///         docs/plan/42 § D5. ⚠ <b>The point of restricting the free set is that the rest of the
    ///         chart is already good and re-solving it would trade a fold for stretch everywhere.</b>
    ///         Everything outside the ring is held exactly where the second rung left it, so the pass
    ///         costs a matrix over the region rather than over the chart, and the region's own
    ///         boundary gives the sub-Laplacian the anchor it needs without one being chosen.
    ///     </para>
    ///     <para>
    ///         The ring grows on each pass, because a fold that cannot be undone inside two rings can
    ///         sometimes be undone inside four — and when it cannot, the answer is a smaller chart
    ///         rather than a longer run.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Internal so that a test can reach it, and that is not a convenience.</b> No input in
    ///         the corpus makes the first rung fold — a free-boundary LSCM turns out to be far harder to
    ///         fold than § D5's "no injectivity guarantee" suggests, which is a finding rather than an
    ///         excuse — so the only honest way to cover this pass is to hand it a fold that was made on
    ///         purpose. A rung reached by nothing is a rung nobody has run.
    ///     </para>
    /// </remarks>
    internal static Distortion.Measured Repair(
        ChartMesh chart,
        TriangleFrame[] frames,
        CotangentWeights weights,
        UvSettings settings,
        int anchor,
        Distortion.Measured measure,
        double[] coordinates,
        Vector2[] rounded
    ) {
        for (var pass = 0; pass < RepairPasses && measure.Distortion.Flipped > 0; pass++) {
            var movable = Neighbourhood(chart, rounded, RepairRing + pass);

            movable[anchor] = false;

            Arap.Solve(
                chart,
                frames,
                weights,
                movable,
                coordinates,
                settings.FlattenIterations,
                settings.SolverIterations
            );

            Round(coordinates, rounded);
            measure = Distortion.Measure(chart, frames, coordinates, rounded);
        }

        return measure;
    }

    /// <summary>Which vertices sit within <paramref name="rings" /> edges of a folded triangle.</summary>
    static bool[] Neighbourhood(ChartMesh chart, Vector2[] rounded, int rings) {
        var movable = new bool[chart.VertexCount];
        var front = new List<int>();

        for (var triangle = 0; triangle < chart.TriangleCount; triangle++) {
            var a = chart.Triangles[triangle * 3];
            var b = chart.Triangles[(triangle * 3) + 1];
            var c = chart.Triangles[(triangle * 3) + 2];

            if (ExactPredicates.Orient2D(rounded[a], rounded[b], rounded[c]) > 0) {
                continue;
            }

            for (var corner = 0; corner < 3; corner++) {
                var vertex = chart.Triangles[(triangle * 3) + corner];

                if (!movable[vertex]) {
                    movable[vertex] = true;
                    front.Add(vertex);
                }
            }
        }

        for (var ring = 0; ring < rings; ring++) {
            var next = new List<int>();

            foreach (var vertex in front) {
                for (var index = chart.AdjacentStart[vertex]; index < chart.AdjacentStart[vertex + 1]; index++) {
                    var neighbour = chart.Adjacent[index];

                    if (!movable[neighbour]) {
                        movable[neighbour] = true;
                        next.Add(neighbour);
                    }
                }
            }

            front = next;
        }

        return movable;
    }

    /// <summary>Narrows the solve's coordinates to the ones that ship.</summary>
    static void Round(double[] coordinates, Vector2[] rounded) {
        for (var vertex = 0; vertex < rounded.Length; vertex++) {
            rounded[vertex] = new((float)coordinates[(vertex * 2) + 0], (float)coordinates[(vertex * 2) + 1]);
        }
    }

    /// <summary>Turns a flattened chart into the island a packer takes.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="UvIsland.Scale" /> is coordinates per world unit, and it is derived from the
    ///     areas rather than from a length.</b> docs/plan/42 § D9: the packer is allowed to move an
    ///     island and not to resize it silently, which it can only honour if the island says what its
    ///     own units were. The square root of the area ratio is the right average for a map that is not
    ///     exactly a similarity, which is every map this ladder produces except on a developable chart.
    /// </remarks>
    static UvIsland Island(ChartMesh chart, Vector2[] rounded, Distortion.Measured measure) {
        var coordinates = new Vector2[chart.Corners.Length];
        var lowest = new Vector2(float.MaxValue, float.MaxValue);
        var highest = new Vector2(float.MinValue, float.MinValue);

        for (var corner = 0; corner < chart.Corners.Length; corner++) {
            var coordinate = rounded[chart.Triangles[corner]];

            coordinates[corner] = coordinate;
            lowest = Vector2.Min(lowest, coordinate);
            highest = Vector2.Max(highest, coordinate);
        }

        var scale = measure.SurfaceArea > 0d && measure.ParameterArea > 0d
            ? (float)Math.Sqrt(measure.ParameterArea / measure.SurfaceArea)
            : 0f;

        return new(coordinates, chart.Corners, lowest, highest, scale);
    }
}
