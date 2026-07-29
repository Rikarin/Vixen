// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Editor.Core;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.NodeGraph;

/// <summary>The selected node's inline values, as rows beside the canvas.</summary>
/// <remarks>
///     <para>
///         <b>Every graph on this framework needs this and none of them should write it twice.</b> A
///         node's numbers live on the <i>graph</i> — in <see cref="GraphNode.Values" />, keyed by
///         port name — because that is what survives a save and an undo, so they are not members of
///         an object and <c>InspectorView</c> cannot draw them. What can is the port list the type
///         registry already carries, which is why this needs nothing from the graph it belongs to.
///     </para>
///     <para>
///         ⚠ <b>Only unconnected inputs get a row.</b> A port fed by an edge takes its value from
///         that edge, and an editor showing a number beside it would be showing a value the compiler
///         ignores — which is how somebody comes to spend an afternoon changing a field that does
///         nothing. A connected port keeps its row's place and says where the value comes from.
///     </para>
///     <para>
///         ⚠ <b>Writes go through <c>SetPortValueCommand</c>, so typing a number is one undo entry
///         rather than one per keystroke.</b> <c>NumericInput</c> commits on blur and on Return —
///         see <c>TextInputs</c> — and that commit is the edit.
///     </para>
/// </remarks>
public sealed class NodeInspector : UiElement {
    readonly Dictionary<UiElement, PortDefinition> editors = [];

    /// <inheritdoc />
    protected override string TagName => "node-inspector";

    /// <summary>The view whose selection is shown.</summary>
    public NodeGraphView? View { get; set; }

    /// <summary>The document edits are recorded against, or <see langword="null" /> for none.</summary>
    public EditorDocument? EditedDocument { get; set; }

    /// <summary>What the panel says when nothing is selected.</summary>
    public string EmptyMessage { get; set; } = "Select a node to edit its settings.";

    /// <summary>How many rows are showing.</summary>
    public int RowCount => editors.Count;

    /// <summary>Rebuilds the rows for whatever is selected.</summary>
    /// <remarks>
    ///     ⚠ <b>One node at a time, which is the inspector's own answer to a mixed selection.</b> Two
    ///     nodes of different types have no set of rows that fits both, and a union of their ports
    ///     would be a panel where half the fields silently apply to one of the two.
    /// </remarks>
    public void Rebuild() {
        while (Children.Count > 0) {
            Children[^1].Remove();
        }

        editors.Clear();

        if (View is not { } view || view.Selection.Count != 1) {
            Add("text").Text = View is { Selection.Count: > 1 } ? "Several nodes selected." : EmptyMessage;

            return;
        }

        var id = view.Selection.First();

        if (!view.Graph.TryGet(id, out var node) || view.Definition(node.Type) is not { } definition) {
            Add("text").Text = $"'{node?.Type ?? "?"}' is not a node type this build has.";

            return;
        }

        Add("node-inspector-title").Text = definition.Title;

        if (definition.Summary.Length > 0) {
            Add("node-inspector-summary").Text = definition.Summary;
        }

        foreach (var port in definition.Ports) {
            if (port.Direction != PortDirection.Input || port.Kind == PortKind.Flow) {
                continue;
            }

            Row(view, node, port);
        }

        if (editors.Count == 0 && Children.Count <= 2) {
            Add("text").Text = "This node has nothing to set.";
        }
    }

    void Row(NodeGraphView view, GraphNode node, PortDefinition port) {
        var row = Add("fact-row");
        row.Add("fact-name").Text = port.Name;

        var slot = row.Add("fact-value");

        if (view.Graph.Source(new(node.Id, port.Name)) is not null) {
            slot.Add("text").Text = "from a connection";

            return;
        }

        var lanes = PortKinds.Lanes(port.Kind);

        if (lanes <= 0) {
            slot.Add("text").Text = port.Kind.ToString();

            return;
        }

        var current = Lanes(node, port, lanes);

        if (port.Kind == PortKind.Bool) {
            var toggle = slot.Add<CheckBox>();
            toggle.IsChecked = current[0] != 0f;

            toggle.CheckedChanged += (_, value) => Write(view, node, port, [value ? 1f : 0f]);
            editors[toggle] = port;

            return;
        }

        List<NumericInput> boxes = [];

        for (var lane = 0; lane < lanes; lane++) {
            if (lanes > 1) {
                slot.Add("lane-name").Text = LaneNames[lane].ToString(CultureInfo.InvariantCulture);
            }

            var box = slot.Add<NumericInput>();

            box.Decimals = port.Kind == PortKind.Int ? 0 : 3;
            box.Number = current[lane];

            boxes.Add(box);
            editors[box] = port;
        }

        foreach (var box in boxes) {
            // ⚠ Every lane is written together rather than the changed one alone, because a value is
            // an array on the node: writing one lane would need a read-modify-write against whatever
            // the *other* boxes are showing, and reading them back at commit time is that read.
            box.NumberChanged += (_, _) => Write(view, node, port, [.. boxes.Select(entry => (float) entry.Number)]);
        }
    }

    /// <summary>What each lane of a vector port is called.</summary>
    const string LaneNames = "XYZW";

    static float[] Lanes(GraphNode node, PortDefinition port, int lanes) {
        var value = new float[lanes];

        var stored = node.Values.TryGetValue(port.Name, out var written) ? written : [];
        var fallback = port.Default;

        for (var lane = 0; lane < lanes; lane++) {
            value[lane] = lane < stored.Length ? stored[lane]
                : lane < fallback.Length ? fallback[lane]
                : 0f;
        }

        return value;
    }

    /// <summary>Records a value against the view's undo stack, or writes it straight through.</summary>
    /// <remarks>
    ///     ⚠ <b>A view with no stack is read-only and says so — <c>NodeGraphView.IsReadOnly</c> — so
    ///     the straight-through branch is not a silent second writer.</b> It is what a graph shown
    ///     outside a document gets, and the alternative is a panel whose fields do nothing.
    /// </remarks>
    void Write(NodeGraphView view, GraphNode node, PortDefinition port, float[] value) {
        if (view.Stack is { } stack) {
            stack.Execute(new SetPortValueCommand(view.Graph, node.Id, port.Name, value, EditedDocument));

            return;
        }

        node.SetValue(port.Name, value);
        view.Graph.Touch();
    }
}
