// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.Editor.Blockout;

/// <summary>docs/plan/42 § D13's UV panel, as the half that draws.</summary>
/// <remarks>
///     <para>
///         The panel is <c>BlockoutUvView.vxml</c>; this file is the accessibility modifier and the
///         three records the markup's snapshot is made of. The emitter's partial has no modifier, so
///         a type another assembly holds needs a declaration that says <c>public</c> — the same
///         arrangement <c>GpuTimelineView</c> takes.
///     </para>
///     <para>
///         ⚠ <b>It computes no metric.</b> Every number it draws came out of
///         <see cref="BlockoutUvPanel" />, which was written as "the half that runs without a device"
///         and enumerated the drawing as what it left out. A view that recomputed a stretch would be
///         a second opinion about what a distorted island is, and the two would disagree the first
///         time either changed.
///     </para>
/// </remarks>
public sealed partial class BlockoutUvView;

/// <summary>One island's rectangle, in the atlas square's own pixels.</summary>
/// <param name="Slot">Its island index, which is what keeps two identically placed islands apart.</param>
/// <param name="Grade">The class that colours it: <c>uv-flipped</c>, or a ramp bucket.</param>
/// <param name="Geometry">Its <c>left</c>, <c>top</c>, <c>width</c> and <c>height</c>, as a declaration list.</param>
/// <param name="Caption">What it says, or <see langword="null" /> when there is nothing to say.</param>
/// <remarks>
///     ⚠ <b>A record struct because the <c>@for</c> keys on it, and the key rule wants a value.</b> An
///     island that has moved is a different tile, and replacing its region is the right
///     reconciliation — keying on <see cref="Slot" /> alone would freeze every rectangle at the
///     position the first pack gave it.
/// </remarks>
internal readonly record struct UvTile(int Slot, string Grade, string Geometry, string? Caption);

/// <summary>One line of what the last stage said.</summary>
/// <param name="Slot">Its position in the list.</param>
/// <param name="Text">The sentence.</param>
/// <remarks>
///     ⚠ <b><see cref="Slot" /> is not decoration.</b> Two stages can legitimately produce the same
///     warning, and two equal keys in one loop is not a thing <c>BuildContext.For</c> can be asked to
///     reconcile.
/// </remarks>
internal readonly record struct UvMessage(int Slot, string Text);

/// <summary>Everything on screen, as one immutable snapshot.</summary>
/// <param name="Tiles">One rectangle per island.</param>
/// <param name="Messages">What the last stage said.</param>
/// <param name="Seams">How many edges the current charting cut.</param>
/// <param name="Islands">How many islands there are.</param>
/// <param name="Bad">How many of them <see cref="UvIslandView.IsBad" /> calls bad.</param>
/// <remarks>
///     One signal rather than five: all of it comes out of one walk of the model, and five signals
///     would mean either walking it five times or writing them in an order the reader has to know.
/// </remarks>
internal readonly record struct UvChart(
    ImmutableArray<UvTile> Tiles,
    ImmutableArray<UvMessage> Messages,
    int Seams,
    int Islands,
    int Bad
) {
    /// <summary>Nothing to show — no model, or nothing flattened yet.</summary>
    public static UvChart Empty { get; } = new([], [], 0, 0, 0);
}
