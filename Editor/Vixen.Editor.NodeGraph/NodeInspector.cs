// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Vixen.Editor.Core;
using Vixen.Editor.Inspector;
using Vixen.Ui;

namespace Vixen.Editor.NodeGraph;

/// <summary>The selected nodes' inline values, as rows beside the canvas.</summary>
/// <remarks>
///     <para>
///         <b>A host around the ordinary inspector, not an inspector of its own.</b> Every row below
///         is drawn by <see cref="InspectorView" /> from <see cref="NodePortEditProvider" />, so a
///         port gets the drawer its type deserves, a reset button, a tooltip, the search box, copy
///         and paste, and a multi-node selection — none of which this panel implements, and all of
///         which it used to lack because it built its own controls.
///     </para>
///     <para>
///         ⚠ <b>The reason it could not before is worth keeping in view.</b> A node's numbers live on
///         the <i>graph</i> — in <see cref="GraphNode.Values" />, keyed by port name — because that is
///         what survives a save and an undo, so they are not members of any type and no registry can
///         describe them. An <c>IEditProvider</c> is exactly the seam for that, and until there was
///         one the only way to edit a port was a panel that knew what a port was.
///     </para>
///     <para>
///         ⚠ <b>Only unconnected inputs get a row.</b> A port fed by an edge takes its value from
///         that edge, and an editor showing a number beside it would be showing a value the compiler
///         ignores — which is how somebody comes to spend an afternoon changing a field that does
///         nothing. A connected port keeps its place in the panel and says where the value comes from.
///     </para>
///     <para>
///         ⚠ <b>Writes go through <c>SetPortValueCommand</c>, which is what <c>NodePortMember</c>
///         hands the pipeline</b> — so typing a number here and dragging the same number on the
///         canvas produce the same undo entry and merge with each other.
///     </para>
/// </remarks>
public sealed class NodeInspector : UiElement {
    readonly List<GraphNode> selected = [];

    NodeGraphView? view;
    InspectorView? panel;
    string signature = "";

    /// <inheritdoc />
    protected override string TagName => "node-inspector";

    /// <summary>The view whose selection is shown.</summary>
    /// <remarks>
    ///     ⚠ <b>Assigning subscribes, because the same number can now be typed in two places.</b> A
    ///     port's value is editable on the node as well as here, and a panel that only refreshed when
    ///     the selection changed would sit there showing the number the port had before the field on
    ///     the canvas was dragged.
    /// </remarks>
    public NodeGraphView? View {
        get => view;
        set {
            if (ReferenceEquals(view, value)) {
                return;
            }

            if (view is not null) {
                view.GraphChanged -= Changed;
            }

            view = value;

            if (view is not null) {
                view.GraphChanged += Changed;
            }
        }
    }

    /// <summary>The document edits are recorded against, or <see langword="null" /> for none.</summary>
    public EditorDocument? EditedDocument { get; set; }

    /// <summary>What the panel says when nothing is selected.</summary>
    public string EmptyMessage { get; set; } = "Select a node to edit its settings.";

    /// <summary>The inspector drawing the rows, or <see langword="null" /> before the first rebuild.</summary>
    /// <remarks>
    ///     Exposed because it is the panel: a host that wants to reach the drawers, the clipboard or
    ///     the search box reaches them here rather than through a forwarding property per member.
    /// </remarks>
    public InspectorView? Panel => panel;

    /// <summary>How many rows are showing.</summary>
    public int RowCount => panel?.Rows.Count ?? 0;

    /// <inheritdoc />
    protected override void OnRemoved() {
        View = null;
        base.OnRemoved();
    }

    /// <summary>Re-reads the values of the rows that are already there.</summary>
    /// <remarks>
    ///     ⚠ <b>Not <see cref="Rebuild" />, and the difference matters every frame.</b> Dragging a
    ///     number field on a node raises the model's change once per frame, and rebuilding would
    ///     remove and re-create the rows that many times — in a framework where removal is final, so
    ///     the elements would accumulate for as long as the drag lasted.
    /// </remarks>
    public void Refresh() => panel?.Reload();

    /// <remarks>
    ///     ⚠ <b>A value change refreshes and a structural one rebuilds, and telling them apart is
    ///     what this is for.</b> Wiring something into a selected node's port turns that row from a
    ///     field into "from a connection", which no amount of re-reading values will do; dragging a
    ///     number does not, and rebuilding for it once a frame is what <see cref="Refresh" /> exists
    ///     to avoid. The signature is what the rows were built against.
    /// </remarks>
    void Changed(NodeGraphView changed) {
        if (string.Equals(Signature(changed), signature, StringComparison.Ordinal)) {
            Refresh();

            return;
        }

        Rebuild();
    }

    /// <summary>What the rows depend on: the nodes, their ports, and which of them a wire arrives at.</summary>
    static string Signature(NodeGraphView view) {
        var text = new StringBuilder();

        foreach (var id in view.Selection) {
            if (!view.Graph.TryGet(id, out var node)) {
                continue;
            }

            text.Append('#').Append(id.Value.ToString(CultureInfo.InvariantCulture)).Append(node.Type);

            if (view.Definition(node.Type) is not { } definition) {
                continue;
            }

            foreach (var port in definition.Ports) {
                if (port.Direction != PortDirection.Input) {
                    continue;
                }

                text.Append('|')
                    .Append(port.Name)
                    .Append(view.Graph.Source(new(node.Id, port.Name)) is null ? '.' : '*');
            }
        }

        return text.ToString();
    }

    /// <summary>Rebuilds the rows for whatever is selected.</summary>
    /// <remarks>
    ///     ⚠ <b>Several nodes are allowed, and only when they are the same type wired the same
    ///     way.</b> That is the one thing <c>EditTarget</c> cannot check for a graph — every node is a
    ///     <see cref="GraphNode" />, so a selection of an Add and a Multiply looks uniform to it — and
    ///     a union of two types' ports would be a panel where half the fields silently apply to one
    ///     of the two.
    /// </remarks>
    public void Rebuild() {
        while (Children.Count > 0) {
            Children[^1].Remove();
        }

        panel = null;
        selected.Clear();

        signature = View is { } shown ? Signature(shown) : "";

        if (View is not { } view) {
            Add("text").Text = EmptyMessage;

            return;
        }

        foreach (var id in view.Selection) {
            if (view.Graph.TryGet(id, out var node)) {
                selected.Add(node);
            }
        }

        if (selected.Count == 0) {
            Add("text").Text = EmptyMessage;

            return;
        }

        var type = selected[0].Type;

        foreach (var node in selected) {
            if (!string.Equals(node.Type, type, StringComparison.Ordinal)) {
                Add("text").Text = "Several kinds of node selected.";

                return;
            }
        }

        if (view.Definition(type) is not { } definition) {
            Add("text").Text = $"'{type}' is not a node type this build has.";

            return;
        }

        var provider = NodePortEditProvider.For(view.Graph, definition, selected[0].Id, view.IsReadOnly);

        // ⚠ Two nodes of one type can be wired differently, and there is no set of rows that is
        // honest for both: a port that is connected on one and free on the other is a field that
        // would silently apply to half the selection.
        if (!provider.Describes(view.Graph, selected)) {
            Add("node-inspector-title").Text = definition.Title;
            Add("text").Text = "These nodes are wired differently, so there is no set of fields that fits both.";

            return;
        }

        Add("node-inspector-title").Text = selected.Count > 1
            ? $"{definition.Title} ({selected.Count.ToString(CultureInfo.InvariantCulture)})"
            : definition.Title;

        if (definition.Summary.Length > 0) {
            Add("node-inspector-summary").Text = definition.Summary;
        }

        panel = Add<InspectorView>();
        panel.EditedDocument = EditedDocument;

        panel.Inspect(provider.Descriptor, provider, [.. selected]);

        foreach (var port in provider.Connected) {
            var row = Add("fact-row");

            row.Add("fact-name").Text = port.Name;
            row.Add("fact-value").Add("text").Text = "from a connection";
        }

        if (RowCount == 0 && provider.Connected.Count == 0) {
            Add("text").Text = "This node has nothing to set.";
        }
    }
}
