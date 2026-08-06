// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Geometry.Remeshing;

/// <summary>How the atlas comes off the patch layout, when one is asked for.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/41 § D13, amended by docs/plan/42 § D2 in one half and not the other.</b> A
///         quantized quad patch <i>is</i> a rectangle, so the layout is already a chart decomposition
///         with zero in-chart distortion and re-cutting it with a general charter would be worse in
///         every respect — that half stays here. The packing does not: the merging described below
///         hands its islands to <c>UvUnwrap.Pack</c>, so there is one packer and one margin rule in
///         the engine rather than two.
///     </para>
///     <para>
///         ⚠ <b><see cref="Resolution" /> exists because <see cref="Margin" /> is counted in
///         texels</b>, which is doc 42 § D8's rule and is not this document's to relax. A margin
///         expressed as a fraction of UV space looks right at the resolution it was tuned at and
///         bleeds at half of it, in a build nobody connects to the packing.
///     </para>
/// </remarks>
public sealed record AtlasSettings {
    /// <summary>The atlas's edge length in texels.</summary>
    public int Resolution { get; init; } = 1024;

    /// <summary>How many texels of empty space separate two charts.</summary>
    public int Margin { get; init; } = 4;

    /// <summary>How much seam the result may keep, as a fraction of the output's total edge length.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Merging runs until the seams are under this, and then stops.</b> § D13: one chart
    ///         per patch means hundreds of charts and hundreds of seams, and the merge that fixes it
    ///         is "greedy, seeded from the largest, and bounded by a seam budget the caller sets".
    ///         This is that budget, and lower asks for more merging.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Zero is a legitimate setting and means "merge everything that can be
    ///         merged".</b> It is not reachable as a result — an open boundary is a seam nothing can
    ///         remove, and a closed surface of genus zero cannot be one chart at all — so the merge
    ///         simply runs out of legal moves and says how much seam was left.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Merging is not free and this is the dial that prices it.</b> Two patches whose
    ///         quads are different world sizes — which is exactly what <c>Adaptivity</c> produces —
    ///         share one chart's cell size once merged, so the chart carries the difference as
    ///         distortion. The merge order prefers the pairs that agree, so a low budget spends the
    ///         mismatch it has to and no more.
    ///     </para>
    /// </remarks>
    public float SeamBudget { get; init; } = 0.05f;

    /// <summary>Whether the seams that remain are pushed onto feature lines.</summary>
    /// <remarks>
    ///     ⚠ <b>§ D13: a seam on a hard edge is where an artist would cut anyway and where a
    ///     normal-map discontinuity is invisible.</b> It is implemented as a preference in the merge
    ///     order rather than as a constraint — merging across a <i>non</i>-feature side first leaves
    ///     the feature sides to be the ones still standing when the budget is met.
    /// </remarks>
    public bool PreferFeatureSeams { get; init; } = true;
}
