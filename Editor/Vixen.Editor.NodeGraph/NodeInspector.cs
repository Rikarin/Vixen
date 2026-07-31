// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
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
    readonly List<Field> editors = [];

    NodeGraphView? view;
    string signature = "";
    int rows;
    bool writing;

    /// <summary>One editor element, and which lane of which port it stands for.</summary>
    readonly record struct Field(UiElement Element, PortDefinition Port, int Lane);

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

    /// <summary>How many rows are showing.</summary>
    public int RowCount => rows;

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
    public void Refresh() {
        if (View is not { } current
            || current.Selection.Count != 1
            || !current.Graph.TryGet(current.Selection.First(), out var node)) {
            return;
        }

        writing = true;

        try {
            foreach (var (element, port, lane) in editors) {
                var value = Lanes(node, port, PortKinds.Fields(port.Kind));

                switch (element) {
                    case CheckBox toggle:
                        toggle.IsChecked = value[0] != 0f;
                        break;

                    case NumericInput box:
                        box.Number = value[lane];
                        break;
                }
            }
        } finally {
            writing = false;
        }
    }

    /// <remarks>
    ///     ⚠ <b>A value change refreshes and a structural one rebuilds, and telling them apart is
    ///     what this is for.</b> Wiring something into the selected node's port turns that row from a
    ///     field into "from a connection", which no amount of re-reading values will do; dragging a
    ///     number does not, and rebuilding for it once a frame is what <see cref="Refresh" /> exists
    ///     to avoid. The signature is what the rows were built against.
    /// </remarks>
    void Changed(NodeGraphView changed) {
        var current = Signature(changed);

        if (string.Equals(current, signature, StringComparison.Ordinal)) {
            Refresh();

            return;
        }

        Rebuild();
    }

    /// <summary>What the rows depend on: the node, its ports, and which of them a wire arrives at.</summary>
    static string Signature(NodeGraphView view) {
        if (view.Selection.Count != 1 || !view.Graph.TryGet(view.Selection.First(), out var node)) {
            return view.Selection.Count.ToString(CultureInfo.InvariantCulture);
        }

        var text = new StringBuilder(node.Type);

        if (view.Definition(node.Type) is { } definition) {
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
    ///     ⚠ <b>One node at a time, which is the inspector's own answer to a mixed selection.</b> Two
    ///     nodes of different types have no set of rows that fits both, and a union of their ports
    ///     would be a panel where half the fields silently apply to one of the two.
    /// </remarks>
    public void Rebuild() {
        while (Children.Count > 0) {
            Children[^1].Remove();
        }

        editors.Clear();
        rows = 0;

        signature = View is { } shown ? Signature(shown) : "";

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

        if (rows == 0 && Children.Count <= 2) {
            Add("text").Text = "This node has nothing to set.";
        }
    }

    void Row(NodeGraphView view, GraphNode node, PortDefinition port) {
        var row = Add("fact-row");
        row.Add("fact-name").Text = port.Name;

        rows++;

        var slot = row.Add("fact-value");

        if (view.Graph.Source(new(node.Id, port.Name)) is not null) {
            slot.Add("text").Text = "from a connection";

            return;
        }

        // ⚠ Fields rather than lanes. A boolean, an integer and an unresolved dynamic all occupy no
        // float lanes at all — see PortKinds.Lanes — and asking that question here is what left every
        // maths node in the shader graph, whose inputs are dynamic, with the word "Dynamic" where its
        // numbers should have been.
        var fields = PortKinds.Fields(port.Kind);

        if (fields <= 0) {
            slot.Add("text").Text = port.Kind.ToString();

            return;
        }

        var current = Lanes(node, port, fields);

        if (port.Kind == PortKind.Bool) {
            var toggle = slot.Add<CheckBox>();
            toggle.IsChecked = current[0] != 0f;

            toggle.CheckedChanged += (_, value) => {
                if (!writing) {
                    Write(view, node, port, [value ? 1f : 0f]);
                }
            };

            editors.Add(new(toggle, port, 0));

            return;
        }

        List<NumericInput> boxes = [];

        for (var lane = 0; lane < fields; lane++) {
            if (fields > 1) {
                slot.Add("lane-name").Text = LaneNames[lane].ToString(CultureInfo.InvariantCulture);
            }

            var box = slot.Add<NumericInput>();

            box.Decimals = port.Kind == PortKind.Int ? 0 : 3;
            box.Number = current[lane];

            boxes.Add(box);
            editors.Add(new(box, port, lane));
        }

        foreach (var box in boxes) {
            // ⚠ Every lane is written together rather than the changed one alone, because a value is
            // an array on the node: writing one lane would need a read-modify-write against whatever
            // the *other* boxes are showing, and reading them back at commit time is that read.
            box.NumberChanged += (_, _) => {
                if (!writing) {
                    Write(view, node, port, [.. boxes.Select(entry => (float) entry.Number)]);
                }
            };
        }
    }

    /// <summary>What each lane of a vector port is called.</summary>
    const string LaneNames = "XYZW";

    /// <remarks>
    ///     ⚠ <b>A short default fills the lanes it has and no more.</b> A one-number default on a
    ///     three-lane port leaves the other two at zero <i>here</i>, where an author is being shown
    ///     three separate boxes to type into — the compiler's splat is what the same default means
    ///     when it reaches a <c>float3</c> as one value.
    /// </remarks>
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
