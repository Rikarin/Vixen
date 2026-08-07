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
///         ⚠ <b>It is a <see cref="Vixen.Ui.Composition.Component" /> and no longer a
///         <c>Control</c>, which is the port's one unavoidable consequence.</b> The emitter hardcodes
///         the base type and there is no <c>@inherits</c>, so markup cannot produce a
///         <see cref="Vixen.Ui.UiElement" /> — it can only produce something that builds them. Every
///         consumer that said <c>panel.Add&lt;StatisticsView&gt;()</c> therefore says
///         <c>BuildContext.Build&lt;StatisticsView&gt;(…)</c> instead, and the four element
///         properties this type used to expose — <c>Toolbar</c>, <c>Refresh</c>, <c>Body</c>,
///         <c>Warnings</c> — are gone rather than reimplemented as tree walks, because nothing
///         outside the class ever read one.
///     </para>
/// </remarks>
public sealed partial class StatisticsView;
