// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.NodeGraph;

/// <summary>The selected nodes' inline values, as rows beside the canvas.</summary>
/// <remarks>
///     <para>
///         The panel is <c>NodeInspector.vxml</c>; this file is the accessibility modifier, on the
///         same arrangement <c>FactRow</c>, <c>GpuTimelineView</c>, <c>MemoryView</c> and
///         <c>StatisticsView</c> use. The emitter's partial carries no modifier, so a type another
///         assembly holds needs a declaration that says <c>public</c> — and both of this one's
///         callers, <c>ShaderGraphView</c> and <c>VfxGraphView</c>, are in
///         <c>Vixen.Editor.AssetEditors</c>.
///     </para>
///     <para>
///         <b>A host around the ordinary inspector, not an inspector of its own.</b> Every row is
///         drawn by <c>InspectorView</c> from <see cref="NodePortEditProvider" />, so a port gets the
///         drawer its type deserves, a reset button, a tooltip, the search box, copy and paste, and a
///         multi-node selection — none of which this panel implements, and all of which it used to
///         lack because it built its own controls.
///     </para>
///     <para>
///         ⚠ <b>The reason it could not before is worth keeping in view.</b> A node's numbers live on
///         the <i>graph</i> — in <see cref="GraphNode.Values" />, keyed by port name — because that
///         is what survives a save and an undo, so they are not members of any type and no registry
///         can describe them. An <c>IEditProvider</c> is exactly the seam for that, and until there
///         was one the only way to edit a port was a panel that knew what a port was.
///     </para>
///     <para>
///         ⚠ <b>Only unconnected inputs get a row.</b> A port fed by an edge takes its value from
///         that edge, and an editor showing a number beside it would be showing a value the compiler
///         ignores — which is how somebody comes to spend an afternoon changing a field that does
///         nothing. A connected port keeps its place in the panel and says where the value comes
///         from.
///     </para>
///     <para>
///         ⚠ <b>Writes go through <c>SetPortValueCommand</c>, which is what <c>NodePortMember</c>
///         hands the pipeline</b> — so typing a number here and dragging the same number on the
///         canvas produce the same undo entry and merge with each other.
///     </para>
/// </remarks>
public sealed partial class NodeInspector;
