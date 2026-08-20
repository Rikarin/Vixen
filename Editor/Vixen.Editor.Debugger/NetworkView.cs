// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Debugger;

/// <summary>Where a session's bandwidth is going, and what is inside one snapshot.</summary>
/// <remarks>
///     <para>
///         <b>The panel is <c>NetworkView.vxml</c>; this file exists only to make it public.</b> The
///         markup compiler emits a partial class with no accessibility modifier, which is
///         <c>internal</c> — deliberately, so that a component is not public API by accident — and a
///         panel the editor's diagnostics module constructs from another assembly has to be. One
///         hand-written part is the whole of the mechanism.
///     </para>
///     <para>
///         ⚠ <b>A view and not an instrument.</b> Everything it draws is a property
///         <c>Vixen.Net.Diagnostics</c> already exposes — <c>BandwidthLedger</c>'s five tables and
///         its totals, and whatever <c>SnapshotInspector.Inspect</c> makes of a packet — and nothing
///         in <c>Vixen.Net</c> was widened to build it. That is the claim doc 16's diagnostics
///         section rests on, and the panel is what tests it.
///     </para>
///     <para>
///         ⚠ <b>It is a <see cref="Vixen.Ui.Composition.Component" /> rather than a <c>Control</c>,
///         which is a choice and not a constraint.</b> Nothing outside the class reads a part of it:
///         the host wires three delegates and the panel draws itself. A panel with no public parts is
///         exactly what a component is for — see <c>StatisticsView</c>, which made the same call — and
///         a test reaches one through <c>UiDocument.ComponentAt</c> rather than through a tree walk.
///     </para>
/// </remarks>
public sealed partial class NetworkView;
