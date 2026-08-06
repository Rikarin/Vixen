// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;
using Vixen.Geometry.Uv.Packing;

namespace Vixen.Geometry.Uv;

/// <summary>The three stages, each callable on its own, because conflating them is the first mistake.</summary>
/// <remarks>
///     <para>
///         docs/plan/42 § D1. Charting asks <i>where do we cut</i>, flattening asks <i>how does a
///         chart become flat</i>, and packing asks <i>where does each island sit</i>. They have
///         different literatures, different failure modes and different runtimes, and a tool that
///         exposes only the fused verb cannot be debugged.
///     </para>
///     <para>
///         ⚠ <b><see cref="Pack(IReadOnlyList{UvIsland}, PackSettings)" /> takes islands rather than a
///         mesh, and that is what makes it a peer of a standalone packer rather than an internal
///         detail.</b> An artist who cut seams by hand and wants them respected is the exact case a
///         fused-verb unwrapper cannot serve, and it is most of why standalone packers are products.
///         Islands can come from this library's charter, from the remesher's patch layout, from
///         another package or from a file — the packer does not ask.
///     </para>
/// </remarks>
public static partial class UvUnwrap {
    /// <summary>Rearranges islands to fill an atlas, leaving their shapes exactly as they arrived.</summary>
    /// <param name="islands">The islands, already flattened. Their coordinates are never rewritten.</param>
    /// <param name="settings">Where the atlas's resolution and the texel margin come from.</param>
    /// <returns>One placement per island, in the order the islands were handed in.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="islands" /> or <paramref name="settings" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The resolution or the margin is unusable.</exception>
    /// <exception cref="ArgumentException">An island's coordinate count does not match its corner count.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The islands did not fit and <see cref="PackSettings.Overflow" /> is
    ///     <see cref="PackOverflow.Refuse" />, or no rescale could make them fit.
    /// </exception>
    public static IReadOnlyList<UvPlacement> Pack(IReadOnlyList<UvIsland> islands, PackSettings settings) =>
        Pack(islands, settings, null, out _);

    /// <summary>The same, and what it measured.</summary>
    /// <param name="islands">The islands.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="report">
    ///     The packing half of the report: the chart count, the efficiency pair, the achieved texel
    ///     density, the stage timing and the warnings. ⚠ The shape and distortion fields belong to the
    ///     two stages this one is deliberately independent of and are left at their defaults.
    /// </param>
    /// <returns>One placement per island.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="islands" /> or <paramref name="settings" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The resolution or the margin is unusable.</exception>
    /// <exception cref="ArgumentException">An island's coordinate count does not match its corner count.</exception>
    /// <exception cref="InvalidOperationException">The islands did not fit and could not be made to.</exception>
    public static IReadOnlyList<UvPlacement> Pack(
        IReadOnlyList<UvIsland> islands,
        PackSettings settings,
        out UvReport report
    ) =>
        Pack(islands, settings, null, out report);

    /// <summary>The same, across a scheduler's workers.</summary>
    /// <param name="islands">The islands.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="scheduler">A scheduler, or <c>null</c> to do everything on the calling thread.</param>
    /// <param name="report">What it measured.</param>
    /// <returns>One placement per island.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The scheduler changes the runtime and cannot change the result.</b> docs/plan/42
    ///         § D12: one worker, four or sixteen, and any batch size, give byte-identical placements.
    ///         The two parallel steps are a rasterization where every island writes only its own slot,
    ///         and a column scan whose answer is the merge of a fixed partition's per-part bests —
    ///         which is the same set however the parts are handed out.
    ///     </para>
    ///     <para>
    ///         That is also why there is no annealing, no genetic search and no random restart
    ///         anywhere below this call. They are the irregular-packing literature's standard answers
    ///         and docs/plan/42 § B6 excludes every one of them, because the content hash has to be a
    ///         function of the input.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="islands" /> or <paramref name="settings" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The resolution or the margin is unusable.</exception>
    /// <exception cref="ArgumentException">An island's coordinate count does not match its corner count.</exception>
    /// <exception cref="InvalidOperationException">The islands did not fit and could not be made to.</exception>
    public static IReadOnlyList<UvPlacement> Pack(
        IReadOnlyList<UvIsland> islands,
        PackSettings settings,
        JobScheduler? scheduler,
        out UvReport report
    ) =>
        Pack(islands, settings, scheduler, 0, out report);

    /// <summary>The same, with the scheduler's batch size pinned.</summary>
    /// <param name="islands">The islands.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="scheduler">A scheduler, or <c>null</c>.</param>
    /// <param name="batch">How many slices one work item covers, or zero to let the scheduler choose.</param>
    /// <param name="report">What it measured.</param>
    /// <returns>One placement per island.</returns>
    /// <remarks>
    ///     ⚠ <b>Internal, and it exists so a test can vary the axis a caller cannot.</b> The batch size
    ///     is a second source of non-determinism independent of the worker count — the same four
    ///     workers handed the same work in different-sized pieces visit it in a different order — and
    ///     a determinism gate that only swept worker counts would not have covered it.
    /// </remarks>
    internal static IReadOnlyList<UvPlacement> Pack(
        IReadOnlyList<UvIsland> islands,
        PackSettings settings,
        JobScheduler? scheduler,
        int batch,
        out UvReport report
    ) {
        ArgumentNullException.ThrowIfNull(islands);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.Resolution);
        ArgumentOutOfRangeException.ThrowIfNegative(settings.Margin);
        ArgumentOutOfRangeException.ThrowIfNegative(settings.CoreLimit);

        if ((2 * settings.Margin) + 1 > settings.Resolution) {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                settings.Margin,
                $"A {settings.Margin}-texel margin leaves nothing of a {settings.Resolution}² atlas. "
                + "docs/plan/42 § D8 — the margin is counted in texels, so it scales with the resolution "
                + "and not with the content."
            );
        }

        for (var index = 0; index < islands.Count; index++) {
            var island = islands[index];

            if (island.Coordinates is not null && island.Corners is not null
                && island.Coordinates.Count != island.Corners.Count) {
                throw new ArgumentException(
                    $"Island {index} carries {island.Coordinates.Count} coordinates for "
                    + $"{island.Corners.Count} corners. There is one coordinate per corner, which is "
                    + "what lets a seam be two coordinates at one position.",
                    nameof(islands)
                );
            }
        }

        var outcome = Packer.Run(islands, settings, scheduler, batch);

        report = outcome.Report;

        return outcome.Placements;
    }
}
