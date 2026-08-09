// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.Editor.Profiler;

/// <summary>A frame's GPU passes, as a bar each.</summary>
/// <remarks>
///     <para>
///         The panel is <c>GpuTimelineView.vxml</c>; this file is the accessibility modifier and the
///         two records the markup's snapshot is made of. The emitter's partial has no modifier, so a
///         type another assembly holds — and <c>Vixen.Editor.Diagnostics</c> and
///         <c>Vixen.Editor.App.Tests</c> both hold this one — needs a declaration that says
///         <c>public</c>. Same arrangement as <c>MemoryView</c> and <c>StatisticsView</c>.
///     </para>
///     <para>
///         <b>Deliberately not a second flame chart.</b> GPU work is a sequence of passes rather than
///         a call tree — a pass has no caller and nothing runs "inside" another pass in any sense a
///         timestamp can see — so a control that drew rows of nesting would be inventing structure
///         that is not there. What a debug group gives is one level of grouping, which is what
///         <see cref="Vixen.Graphics.GpuScope.Level" /> carries.
///     </para>
///     <para>
///         ⚠ <b>It says when the device cannot be timed rather than drawing nothing.</b> Doc 20's B4
///         names timestamp queries as this panel's dependency and several real configurations do not
///         have them — MoltenVK's statistics, GLES, WebGPU without the feature — so an empty chart
///         would be indistinguishable from a frame with no passes in it.
///     </para>
///     <para>
///         ⚠ <b>The frame it shows is several frames old, and the label says which.</b> Reading a
///         query pool the GPU has not finished writing would mean stalling the frame thread on the
///         device once per frame; <see cref="Vixen.Graphics.GpuProfiler" /> refuses to, so what is on
///         screen is the oldest submission that has retired.
///     </para>
/// </remarks>
public sealed partial class GpuTimelineView;

/// <summary>One bar, measured against a width the layout has already settled.</summary>
/// <param name="Slot">Its position in the list, which is what keeps two identical bars apart.</param>
/// <param name="Hue">The class that colours it.</param>
/// <param name="Geometry">Its <c>left</c>, <c>top</c>, <c>width</c> and <c>height</c>, as a declaration list.</param>
/// <param name="Caption">What it says, or <see langword="null" /> when it is too narrow to say anything.</param>
/// <remarks>
///     ⚠ <b>A record struct because the <c>@for</c> keys on it, and the key rule wants a value.</b>
///     Nothing here is signal-backed and nothing mutates: a bar that has moved is a different bar,
///     and that is precisely what makes replacing its region the right reconciliation.
/// </remarks>
internal readonly record struct GpuBar(int Slot, string Hue, string Geometry, string? Caption);

/// <summary>Every bar in a frame, and the height they need between them.</summary>
/// <param name="Bars">The bars, in the order the passes ran.</param>
/// <param name="Height">How tall the lanes have to be for the deepest debug group to fit.</param>
/// <remarks>
///     One snapshot rather than two signals: both come out of one walk of the frame, and splitting
///     them would mean either walking it twice or writing them in an order the reader has to know.
/// </remarks>
internal readonly record struct GpuChart(ImmutableArray<GpuBar> Bars, float Height) {
    /// <summary>Nothing to show — no device, no width, or no retired frame.</summary>
    public static GpuChart Empty { get; } = new([], 0f);
}
