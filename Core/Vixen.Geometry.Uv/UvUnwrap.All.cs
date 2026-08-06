// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Geometry.Uv.Charting;

namespace Vixen.Geometry.Uv;

public static partial class UvUnwrap {
    /// <summary>All three stages: cut, flatten, pack — which is the importer's call and the common case.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="settings">The charter's and the flattener's settings.</param>
    /// <param name="packing">The packer's. ⚠ <see cref="PackSettings.Resolution" /> is required.</param>
    /// <param name="uvs">
    ///     One coordinate per <see cref="EditMesh.Corners" /> entry, in the atlas's unit square. ⚠ A
    ///     corner belonging to a chart that was refused carries <see cref="Vector2.Zero" /> — see the
    ///     remarks.
    /// </param>
    /// <returns>Everything measured about the unwrap, all eleven fields filled in.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A setting is unusable.</exception>
    /// <exception cref="InvalidOperationException">The islands did not fit and could not be made to.</exception>
    /// <remarks>
    ///     <para>
    ///         docs/plan/42 § D1's fourth entry point. The other three exist because <b>conflating the
    ///         stages is the first mistake</b> — they have different literatures, different failure modes
    ///         and different runtimes, and a tool that exposes only the fused verb cannot be debugged.
    ///         This one exists because the importer does not want to care.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A refused chart leaves its corners at zero rather than throwing.</b> Charting cuts
    ///         until every chart is a disk, so a refusal here is a fold the ladder could not undo on a
    ///         chart that could not be split any further — and it is named in
    ///         <see cref="UvReport.Warnings" /> with the reason. Producing coordinates for it would be
    ///         producing a fold, which § D5 is explicit is a correctness failure rather than a quality
    ///         one; refusing the whole mesh over one chart would throw away the other ninety-nine.
    ///         <see cref="UvReport.IsInjective" /> is the field that answers <i>"is all of this
    ///         usable"</i>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="UvReport.SeamLengthNormalized" /> divides by the square root of the
    ///         area.</b> MeshTailor defines it over the area, which is not dimensionless — halve a
    ///         model's scale and the published figure doubles. Both are here, so the number compares
    ///         across models and the published one is still recoverable from
    ///         <see cref="UvReport.SeamLength" />.
    ///     </para>
    /// </remarks>
    public static UvReport All(
        EditMesh mesh,
        UvSettings settings,
        PackSettings packing,
        out IReadOnlyList<Vector2> uvs
    ) =>
        All(mesh, settings, packing, null, 0, out uvs);

    /// <summary>The same, across a scheduler's workers.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="settings">The charter's and the flattener's settings.</param>
    /// <param name="packing">The packer's.</param>
    /// <param name="scheduler">A scheduler, or <c>null</c> to do everything on the calling thread.</param>
    /// <param name="uvs">One coordinate per corner.</param>
    /// <returns>What it measured.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A setting is unusable.</exception>
    /// <exception cref="InvalidOperationException">The islands did not fit and could not be made to.</exception>
    /// <remarks>
    ///     ⚠ <b>The scheduler changes the runtime and cannot change the result.</b> docs/plan/42 § D12:
    ///     one worker, four or sixteen, and any batch size, give byte-identical coordinates through all
    ///     three stages.
    /// </remarks>
    public static UvReport All(
        EditMesh mesh,
        UvSettings settings,
        PackSettings packing,
        JobScheduler? scheduler,
        out IReadOnlyList<Vector2> uvs
    ) =>
        All(mesh, settings, packing, scheduler, 0, out uvs);

    /// <summary>The same, with the scheduler's batch size pinned.</summary>
    /// <remarks>
    ///     ⚠ <b>Internal, and it exists so a test can vary the axis a caller cannot.</b> The batch size
    ///     is a second source of non-determinism independent of the worker count.
    /// </remarks>
    internal static UvReport All(
        EditMesh mesh,
        UvSettings settings,
        PackSettings packing,
        JobScheduler? scheduler,
        int batch,
        out IReadOnlyList<Vector2> uvs
    ) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(packing);

        var charted = Detail(mesh, settings, scheduler, batch, true);
        var flattened = Detail(mesh, charted.ChartOfFace, settings, scheduler, batch);
        var coordinates = new Vector2[mesh.CornerCount];

        if (flattened.Islands.Count == 0) {
            uvs = coordinates;

            return Combine(mesh, charted, flattened.Report, default, [], []);
        }

        var placements = Pack(flattened.Islands, packing, scheduler, batch, out var packed);

        foreach (var placement in placements) {
            var island = flattened.Islands[placement.Island];

            for (var slot = 0; slot < island.Corners.Count; slot++) {
                coordinates[island.Corners[slot]] = placement.Apply(island, island.Coordinates[slot]);
            }
        }

        uvs = coordinates;

        return Combine(mesh, charted, flattened.Report, packed, flattened.Islands, placements);
    }

    /// <summary>Merges the three stages' reports into the one docs/plan/42 § Part 4 describes.</summary>
    /// <remarks>
    ///     ⚠ <b>Each stage fills only what it knows, which is why this is an assembly rather than one
    ///     measurement pass.</b> Charting knows the seam length and nothing about shape; flattening
    ///     knows the four distortion measures and nothing about the atlas; packing knows both
    ///     efficiencies and the achieved density and nothing about the surface. The three shape figures
    ///     are the only ones computed here, because they are the only ones that need the mesh and the
    ///     islands at the same time.
    /// </remarks>
    static UvReport Combine(
        EditMesh mesh,
        ChartOutcome charted,
        UvReport flattened,
        UvReport packed,
        IReadOnlyList<UvIsland> islands,
        IReadOnlyList<UvPlacement> placements
    ) {
        var compactness = 0d;
        var convexity = 0d;
        var jaggedness = 0d;

        foreach (var island in islands) {
            var shape = IslandShape.Measure(mesh, island);

            compactness += shape.Compactness;
            convexity += shape.Convexity;
            jaggedness += shape.Jaggedness;
        }

        var count = Math.Max(1, islands.Count);
        var stages = new List<UvStageTiming>();
        var warnings = new List<string>();

        stages.AddRange(charted.Report.Stages);
        stages.AddRange(flattened.Stages);

        if (placements.Count > 0) {
            stages.AddRange(packed.Stages);
        }

        warnings.AddRange(charted.Report.Warnings);
        warnings.AddRange(flattened.Warnings);

        if (placements.Count > 0) {
            warnings.AddRange(packed.Warnings);
        }

        return new(
            islands.Count,
            (float)(compactness / count),
            (float)(convexity / count),
            charted.Report.SeamLength,
            charted.Report.SeamLengthNormalized,
            (float)(jaggedness / count),
            flattened.Distortion,
            packed.PackingEfficiency,
            packed.EffectiveEfficiency,
            packed.TexelDensity,
            stages,
            warnings
        );
    }
}
