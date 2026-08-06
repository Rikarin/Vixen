// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;
using Vixen.Geometry.Uv.Flattening;

namespace Vixen.Geometry.Uv;

public static partial class UvUnwrap {
    /// <summary>Lays every chart of a mesh flat, as an island a packer can take.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="chartOfFace">
    ///     Which chart each face belongs to, one entry per face. A negative entry leaves the face out
    ///     entirely, which is how a caller flattens part of a mesh.
    /// </param>
    /// <param name="settings">The distortion bound and the two iteration budgets.</param>
    /// <returns>
    ///     One island per chart that flattened, in ascending chart order. ⚠ A chart that could not be
    ///     flattened produces no island — see the overload that returns a report, whose
    ///     <see cref="UvReport.Warnings" /> name every one of them and say why.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="chartOfFace" /> is not one entry per face.</exception>
    public static IReadOnlyList<UvIsland> Flatten(
        EditMesh mesh,
        IReadOnlyList<int> chartOfFace,
        UvSettings settings
    ) =>
        Flatten(mesh, chartOfFace, settings, null, 0, out _);

    /// <summary>The same, and what it measured.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="chartOfFace">Which chart each face belongs to. Negative excludes the face.</param>
    /// <param name="settings">The distortion bound and the two iteration budgets.</param>
    /// <param name="report">
    ///     The distortion half of the report: the chart count, the four measures of
    ///     docs/plan/42 § D6, the stage timing and the warnings. ⚠ The shape, seam and packing fields
    ///     belong to the two stages this one is deliberately independent of and are left at their
    ///     defaults.
    /// </param>
    /// <returns>One island per chart that flattened.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="chartOfFace" /> is not one entry per face.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><see cref="UvDistortion.Flipped" /> must be zero and this call guarantees it.</b>
    ///         docs/plan/42 § D5 and § D6: a flipped triangle is a region of the atlas where the
    ///         mapping is not invertible, so a bake writes to the wrong texel and sampling reads from
    ///         it. A chart the ladder cannot bring to zero is <b>refused</b> — it produces no island,
    ///         it is named in <see cref="UvReport.Warnings" />, and the answer to it is a smaller
    ///         chart. Reporting a fold as a quality figure would make the one field that is pass-or-fail
    ///         look like one that could be traded against the others.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A chart that is not a disk is refused before any solve runs.</b> An annulus, a
    ///         handle, a chart in two pieces and a bowtie all have <i>no</i> injective map to the plane,
    ///         so producing coordinates for one would be producing a fold with extra steps. The test is
    ///         the Euler characteristic — <c>χ = 2 − 2g − b</c>, and a disk is the only surface with
    ///         boundary for which it is one — plus a separate check for the pinch that χ cannot see.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<UvIsland> Flatten(
        EditMesh mesh,
        IReadOnlyList<int> chartOfFace,
        UvSettings settings,
        out UvReport report
    ) =>
        Flatten(mesh, chartOfFace, settings, null, 0, out report);

    /// <summary>The same, across a scheduler's workers.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="chartOfFace">Which chart each face belongs to. Negative excludes the face.</param>
    /// <param name="settings">The distortion bound and the two iteration budgets.</param>
    /// <param name="scheduler">A scheduler, or <c>null</c> to do everything on the calling thread.</param>
    /// <param name="report">What it measured.</param>
    /// <returns>One island per chart that flattened.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="chartOfFace" /> is not one entry per face.</exception>
    /// <remarks>
    ///     ⚠ <b>The scheduler changes the runtime and cannot change the result.</b> docs/plan/42 § D12:
    ///     one worker, four or sixteen, and any batch size, give byte-identical coordinates. Charts are
    ///     the unit of parallelism and they share nothing — every reduction inside a solve runs in
    ///     index order on one thread, which is what doc 41 § D14 requires and is why the solver is never
    ///     handed the scheduler from here.
    /// </remarks>
    public static IReadOnlyList<UvIsland> Flatten(
        EditMesh mesh,
        IReadOnlyList<int> chartOfFace,
        UvSettings settings,
        JobScheduler? scheduler,
        out UvReport report
    ) =>
        Flatten(mesh, chartOfFace, settings, scheduler, 0, out report);

    /// <summary>The same, with the scheduler's batch size pinned.</summary>
    /// <remarks>
    ///     ⚠ <b>Internal, and it exists so a test can vary the axis a caller cannot.</b> The batch size
    ///     is a second source of non-determinism independent of the worker count, and a gate that only
    ///     swept worker counts would not have covered it.
    /// </remarks>
    internal static IReadOnlyList<UvIsland> Flatten(
        EditMesh mesh,
        IReadOnlyList<int> chartOfFace,
        UvSettings settings,
        JobScheduler? scheduler,
        int batch,
        out UvReport report
    ) {
        var outcome = Detail(mesh, chartOfFace, settings, scheduler, batch);

        report = outcome.Report;

        return outcome.Islands;
    }

    /// <summary>Everything the flattener knows, including which chart refused and why.</summary>
    /// <remarks>
    ///     ⚠ <b>Internal because it is U3's input rather than a caller's.</b> docs/plan/42 § D5's third
    ///     rung ends in <i>"split the chart and recurse"</i>, and the recursion is the charter's. What
    ///     it needs is the reason — an annulus wants a cut joining its two boundary loops and a fold
    ///     wants the region round the fold taken out, and those are not the same instruction. A public
    ///     surface for it would be a second way to say what <see cref="UvReport.Warnings" /> already
    ///     says to everybody else.
    /// </remarks>
    internal static FlattenOutcome Detail(
        EditMesh mesh,
        IReadOnlyList<int> chartOfFace,
        UvSettings settings,
        JobScheduler? scheduler,
        int batch
    ) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(chartOfFace);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegative(settings.FlattenIterations);
        ArgumentOutOfRangeException.ThrowIfNegative(settings.SolverIterations);

        if (chartOfFace.Count != mesh.FaceCount) {
            throw new ArgumentException(
                $"There are {chartOfFace.Count} chart assignments for {mesh.FaceCount} faces. "
                + "docs/plan/42 § D1 — flattening takes the charting stage's output as a value, one "
                + "entry per face, so that charts cut by hand in another package are as welcome as the "
                + "charter's own.",
                nameof(chartOfFace)
            );
        }

        return Flattener.Run(mesh, chartOfFace, settings, scheduler, batch);
    }
}
