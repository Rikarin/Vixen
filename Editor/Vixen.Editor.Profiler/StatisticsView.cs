// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Profiler;

/// <summary>What is in the scene, against what somebody said should be.</summary>
/// <remarks>
///     <para>
///         <b>The panel is <c>StatisticsView.vxml</c>; this file exists only to make it public.</b>
///         The markup compiler emits a partial class with no accessibility modifier, which is
///         <c>internal</c> — deliberately, so that a component is not public API by accident — and a
///         panel the editor's diagnostics module constructs from another assembly has to be. One
///         hand-written part is the whole of the mechanism.
///     </para>
///     <para>
///         ⚠ <b>It is a <see cref="Vixen.Ui.Composition.Component" /> and no longer a <c>Control</c>
///         — which was forced when this was ported and is now a choice.</b> Wave 1a had no
///         <c>@inherits</c>: the emitter hardcoded the base, so markup could only produce something
///         that <i>builds</i> elements, and every consumer that said
///         <c>panel.Add&lt;StatisticsView&gt;()</c> had to say
///         <c>BuildContext.Build&lt;StatisticsView&gt;(…)</c> instead. Wave 1b added the header, and
///         this panel keeps the component base anyway: the four element properties it used to expose
///         — <c>Toolbar</c>, <c>Refresh</c>, <c>Body</c>, <c>Warnings</c> — are gone rather than
///         reimplemented, because nothing outside the class ever read one. A panel with no public
///         parts is exactly what a component is for.
///     </para>
/// </remarks>
public sealed partial class StatisticsView;
