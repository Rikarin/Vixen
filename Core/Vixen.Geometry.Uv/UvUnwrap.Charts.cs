// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;
using Vixen.Geometry.Uv.Charting;

namespace Vixen.Geometry.Uv;

public static partial class UvUnwrap {
    /// <summary>Decides where to cut, and answers with a chart per face.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="settings">The distortion bound, the depth bound and the seam weights.</param>
    /// <returns>Which chart each face belongs to, dense from zero — exactly what <c>Flatten</c> takes.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Chart count is an outcome of a quality target, not a knob, and that inversion is the
    ///         whole design.</b> docs/plan/42 § D3: nothing here is told how many charts to make. A
    ///         region is flattened, measured, and kept if it comes in under
    ///         <see cref="UvSettings.DistortionThreshold" /> — and split and retried if it does not. Then
    ///         a merge-back pass puts adjacent charts together again wherever their union still passes.
    ///         Growing regions until a stretch bound trips, with nothing that ever puts two back
    ///         together, is exactly why the established tools fragment.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A seam is a walk on the mesh's own graph.</b> § D4, taken from MeshTailor directly:
    ///         every cut is a set of <i>existing edges</i>, chosen by search under a seven-term cost —
    ///         concavity, occlusion, feature alignment, material boundary, symmetry, length and any seam
    ///         that was already there. Nothing is ever placed in space and snapped to the mesh
    ///         afterwards, so there is no snapping stage and there are no snapping artefacts.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Material and face-group boundaries partition first and unconditionally.</b> A group
    ///         boundary is somewhere the texture already changes, so a seam there costs nothing that has
    ///         not already been paid — see <see cref="UvSettings.KeepGroups" />, which is the one way to
    ///         turn that off.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<int> Charts(EditMesh mesh, UvSettings settings) =>
        Charts(mesh, settings, null, 0, true, out _);

    /// <summary>The same, and what it measured.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="settings">The distortion bound, the depth bound and the seam weights.</param>
    /// <param name="report">
    ///     The charting half of the report: the chart count, the seam length both raw and over the square
    ///     root of the area, the stage timing and the warnings. ⚠ The shape, distortion and packing
    ///     fields belong to the two stages this one is deliberately independent of and are left at their
    ///     defaults.
    /// </param>
    /// <returns>Which chart each face belongs to.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IReadOnlyList<int> Charts(EditMesh mesh, UvSettings settings, out UvReport report) =>
        Charts(mesh, settings, null, 0, true, out report);

    /// <summary>The same, across a scheduler's workers.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="settings">The distortion bound, the depth bound and the seam weights.</param>
    /// <param name="scheduler">A scheduler, or <c>null</c> to do everything on the calling thread.</param>
    /// <param name="report">What it measured.</param>
    /// <returns>Which chart each face belongs to.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    ///     ⚠ <b>The scheduler changes the runtime and cannot change the result.</b> docs/plan/42 § D12.
    ///     Charting adds no parallelism of its own: a whole level of the recursion is handed to
    ///     <c>Flatten</c> as one chart assignment, so the only threaded work is the same per-chart
    ///     flattening whose determinism U2 already gates. Every decision the charter makes — which seeds,
    ///     which split, which merge — runs in index order on the calling thread.
    /// </remarks>
    public static IReadOnlyList<int> Charts(
        EditMesh mesh,
        UvSettings settings,
        JobScheduler? scheduler,
        out UvReport report
    ) =>
        Charts(mesh, settings, scheduler, 0, true, out report);

    /// <summary>The same, with the batch size pinned and the merge-back pass switchable.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Internal, and the merge switch is a measurement rather than a setting.</b>
    ///         docs/plan/42 § D3 says which half of the fix is which — <i>"step 4 is the cheap half and
    ///         step 3's top-down direction is the expensive half"</i> — and the only way to say what each
    ///         one contributed is to run without one of them. It is not public because turning it off is
    ///         strictly worse: it produces more charts at the same distortion, which is the exact failure
    ///         the design exists to avoid.
    ///     </para>
    ///     <para>
    ///         The batch size is a second axis of non-determinism independent of the worker count, and a
    ///         gate that only swept worker counts would not have covered it.
    ///     </para>
    /// </remarks>
    internal static IReadOnlyList<int> Charts(
        EditMesh mesh,
        UvSettings settings,
        JobScheduler? scheduler,
        int batch,
        bool mergeBack,
        out UvReport report
    ) {
        var outcome = Detail(mesh, settings, scheduler, batch, mergeBack);

        report = outcome.Report;

        return outcome.ChartOfFace;
    }

    /// <summary>Everything the charter knows, including the count before the merge-back pass ran.</summary>
    /// <remarks>
    ///     ⚠ <b>Internal because <see cref="ChartOutcome.BeforeMerge" /> is a measurement of the design
    ///     rather than a fact about the mesh.</b> A caller wants the charts; the difference between the
    ///     count the recursion reached and the count that shipped is what says whether § D3's two halves
    ///     are both pulling their weight, and that belongs in a test rather than in an API.
    /// </remarks>
    internal static ChartOutcome Detail(
        EditMesh mesh,
        UvSettings settings,
        JobScheduler? scheduler,
        int batch,
        bool mergeBack
    ) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegative(settings.MaxDepth);
        ArgumentOutOfRangeException.ThrowIfNegative(settings.FlattenIterations);
        ArgumentOutOfRangeException.ThrowIfNegative(settings.SolverIterations);

        return Charter.Run(mesh, settings, scheduler, batch, mergeBack);
    }
}
