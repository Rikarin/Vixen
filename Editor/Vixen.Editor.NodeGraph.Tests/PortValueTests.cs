// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Xunit;
using PortDirection = Vixen.Editor.NodeGraph.PortDirection;

namespace Tests;

/// <summary>What an unconnected input is worth, on the node and in the panel beside it.</summary>
public class PortValueTests : IDisposable {
    readonly ViewFixture fixture = new();
    readonly NodeTypeRegistry registry = new();
    readonly NodeGraphModel graph = new();

    public PortValueTests() {
        Vixen.Editor.NodeGraph.Tests.NodeTypes.Register(registry);
        fixture.Show(graph, registry);
    }

    public void Dispose() {
        fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    NodeGraphView View => fixture.View;

    NodePortEditor Fields(NodeId node, string port) => fixture.Port(node, port, PortDirection.Input).Fields;

    // ── On the node ──────────────────────────────────────────────────────────

    [Fact]
    public void An_unconnected_input_shows_the_default_its_type_declares() {
        var node = graph.Add("Test/Named", new(60f, 60f));
        fixture.Update();

        var fields = Fields(node.Id, "Base Colour");

        Assert.Equal(PortEditorKind.Number, fields.Port!.Editor!.Kind);
        Assert.Equal(3, fields.Port.Editor.Lanes);
        Assert.Equal([0.1f, 0.2f, 0.3f], Enumerable.Range(0, 3).Select(lane => fields.Port.Editor[lane]));
    }

    [Fact]
    public void An_unconnected_input_shows_the_value_the_author_typed_over_the_default() {
        var node = graph.Add("Test/Named", new(60f, 60f));
        node.SetValue("Base Colour", 0.5f, 0.6f, 0.7f);

        graph.Touch();
        fixture.Update();

        Assert.Equal([0.5f, 0.6f, 0.7f], Fields(node.Id, "Base Colour").Boxes.Select(box => (float) box.Number));
    }

    /// <remarks>
    ///     A port fed by a wire takes its value from the wire, so a number beside it would be one the
    ///     compiler ignores. The editor survives the connection — it is the row that is hidden — so
    ///     pulling the wire off brings the number back rather than a zero.
    /// </remarks>
    [Fact]
    public void A_connected_input_shows_no_value() {
        var source = graph.Add("Test/Vector", new(40f, 40f));
        var sink = graph.Add("Test/Named", new(320f, 40f));

        graph.Connect(new(source.Id, "Out"), new(sink.Id, "Base Colour"));
        fixture.Update();

        Assert.Null(Fields(sink.Id, "Base Colour").Port);

        graph.Disconnect(new(sink.Id, "Base Colour"));
        fixture.Update();

        Assert.NotNull(Fields(sink.Id, "Base Colour").Port);
    }

    [Fact]
    public void An_output_never_shows_a_value() {
        var node = graph.Add("Test/Named", new(60f, 60f));
        fixture.Update();

        Assert.Null(fixture.Port(node.Id, "Out", PortDirection.Output).Fields.Port);
    }

    /// <remarks>
    ///     The regression this whole feature started as. A dynamic port occupies no float lane until
    ///     it is resolved, so an editor that asked <c>PortKinds.Lanes</c> gave every maths node in the
    ///     shader graph — whose inputs are all dynamic — no way to type a number into it at all.
    /// </remarks>
    [Fact]
    public void A_dynamic_input_takes_one_number() {
        var node = graph.Add("Test/Combine", new(60f, 60f));
        fixture.Update();

        var fields = Fields(node.Id, "A");

        Assert.Equal(1, fields.Port!.Editor!.Lanes);
        Assert.Equal(0.25f, fields.Port.Editor[0]);
    }

    [Fact]
    public void A_boolean_input_is_a_tick_and_an_integer_is_a_box() {
        var node = graph.Add("Test/Settings", new(60f, 60f));
        fixture.Update();

        var enabled = Fields(node.Id, "Enabled");
        var count = Fields(node.Id, "Count");

        Assert.Equal(PortEditorKind.Toggle, enabled.Port!.Editor!.Kind);
        Assert.True(enabled.Tick!.IsChecked);

        Assert.Equal(PortEditorKind.Number, count.Port!.Editor!.Kind);
        Assert.Equal(0, count.Boxes[0].Decimals);
        Assert.Equal(3d, count.Boxes[0].Number);
    }

    [Fact]
    public void A_flow_input_takes_nothing() {
        var node = graph.Add("Test/Settings", new(60f, 60f));
        fixture.Update();

        Assert.Null(Fields(node.Id, "After").Port);
    }

    /// <remarks>
    ///     Its ports come off its edges, so nothing knows what kind they are — and a box of digits
    ///     over a value the graph cannot type would write a number into a file the missing plugin owns.
    /// </remarks>
    [Fact]
    public void A_node_whose_type_is_missing_shows_no_values() {
        var known = graph.Add("Test/Colour");
        var missing = graph.Add("Plugin/Gone");

        graph.Connect(new(known.Id, "Out"), new(missing.Id, "Tint"));
        fixture.Update();

        Assert.Null(Fields(missing.Id, "Tint").Port);
    }

    // ── Writing one down ─────────────────────────────────────────────────────

    [Fact]
    public void Typing_a_number_into_a_node_records_it() {
        var node = graph.Add("Test/Named", new(60f, 60f));
        fixture.Update();

        Fields(node.Id, "Base Colour").Boxes[1].Number = 0.75f;
        fixture.Update();

        Assert.Equal([0.1f, 0.75f, 0.3f], graph.Nodes.Single().Values["Base Colour"]);
        Assert.True(fixture.Document.IsDirty.Value);
    }

    /// <remarks>
    ///     ⚠ Back to <i>having no value</i>, not back to a zero. A port that was never typed into
    ///     takes its type's default, and an undo that wrote one back would pin it to a number the node
    ///     type is free to change.
    /// </remarks>
    [Fact]
    public void Undoing_a_typed_number_leaves_the_port_with_none() {
        var node = graph.Add("Test/Named", new(60f, 60f));
        fixture.Update();

        Fields(node.Id, "Base Colour").Boxes[1].Number = 0.75f;
        fixture.Update();

        fixture.Stack.Undo();
        fixture.Update();

        Assert.False(graph.Nodes.Single().Values.ContainsKey("Base Colour"));
        Assert.Equal([0.1f, 0.2f, 0.3f], Fields(node.Id, "Base Colour").Boxes.Select(box => (float) box.Number));
    }

    [Fact]
    public void A_run_of_edits_to_one_port_is_one_undo_entry() {
        var node = graph.Add("Test/Combine", new(60f, 60f));
        fixture.Update();

        var box = Fields(node.Id, "A").Boxes[0];

        box.Number = 1f;
        box.Number = 2f;
        box.Number = 3f;

        fixture.Update();
        fixture.Stack.Undo();

        Assert.False(graph.Nodes.Single().Values.ContainsKey("A"));
    }

    [Fact]
    public void Ticking_a_boolean_records_it() {
        var node = graph.Add("Test/Settings", new(60f, 60f));
        fixture.Update();

        Fields(node.Id, "Enabled").Tick!.IsChecked = false;
        fixture.Update();

        Assert.Equal([0f], graph.Nodes.Single().Values["Enabled"]);
    }

    /// <remarks>
    ///     A graph shown without an undo stack is read-only, and a field that accepted an edit nothing
    ///     recorded would be worse than one that refuses.
    /// </remarks>
    [Fact]
    public void A_view_with_no_stack_shows_its_values_and_refuses_them() {
        var node = graph.Add("Test/Named", new(60f, 60f));

        View.Stack = null;
        View.Project();

        fixture.Update();

        var fields = Fields(node.Id, "Base Colour");

        Assert.True(fields.Port!.Editor!.ReadOnly);
        Assert.True(fields.Boxes[0].ReadOnly);
        Assert.Equal(0.1f, fields.Boxes[0].Number);
        Assert.Empty(node.Values);
    }

    /// <remarks>
    ///     ⚠ <b>A node clips its own contents</b>, so a row wider than the node is a value box that
    ///     is half there and cannot be typed into. Three lanes and a long port name is the widest a
    ///     row gets, and it is the case that has to fit.
    /// </remarks>
    [Fact]
    public void The_boxes_fit_inside_the_node_they_are_on() {
        var node = graph.Add("Test/Named", new(60f, 60f));
        fixture.Update();

        var item = fixture.Item(node.Id);
        var fields = Fields(node.Id, "Base Colour");

        foreach (var box in fields.Boxes) {
            Assert.True(box.Bounds.Width > 0f, "a value box has no room at all");
            Assert.True(box.Bounds.Right <= item.Bounds.Right, $"{box.Bounds} runs past {item.Bounds}");
            Assert.True(box.Bounds.X >= item.Bounds.X, $"{box.Bounds} starts before {item.Bounds}");
        }

        // The name is squeezed rather than deleted: a row of three anonymous numbers says nothing.
        Assert.True(fixture.Port(node.Id, "Base Colour", PortDirection.Input).Label.Bounds.Width > 0f);
    }

    // ── The gestures it has to take away from the canvas ─────────────────────

    /// <remarks>
    ///     ⚠ The press lands on a <c>NodePortView</c>, which is the element a wire is dragged from —
    ///     so without the guard a click into a number box starts a wire from the port it belongs to
    ///     and the field never sees the press it was aimed at.
    /// </remarks>
    [Fact]
    public void Pressing_a_number_box_does_not_start_a_wire() {
        var node = graph.Add("Test/Combine", new(60f, 60f));
        fixture.Update();

        var box = Fields(node.Id, "A").Boxes[0];

        fixture.Click(box);

        Assert.Null(View.Canvas.PendingPort);
        Assert.Empty(View.Selection);
        Assert.Same(box, fixture.Ui.Focused);
    }

    /// <remarks>
    ///     Backspace in a number box has to delete a digit rather than the node the box is on. Both
    ///     the view and the canvas claim the key, and both have to stand down.
    /// </remarks>
    [Fact]
    public void Backspace_in_a_number_box_does_not_delete_the_node() {
        var node = graph.Add("Test/Combine", new(60f, 60f));
        fixture.Update();

        View.Select([node.Id]);

        fixture.Click(Fields(node.Id, "A").Boxes[0]);
        fixture.Type(InputKey.Backspace);

        Assert.Single(graph.Nodes);
    }

    // ── Beside the node ──────────────────────────────────────────────────────

    /// <remarks>
    ///     The same regression as on the node, in the panel that had it first: a boolean, an integer
    ///     and an unresolved dynamic occupy no float lane, and all three take exactly one number.
    /// </remarks>
    [Fact]
    public void The_inspector_has_a_field_for_a_boolean_and_an_integer() {
        var settings = graph.Add("Test/Settings", new(60f, 60f));
        var inspector = Inspector();

        View.Select([settings.Id]);
        inspector.Rebuild();

        // Enabled and Count. A flow port carries no value and is not offered a row at all.
        Assert.Equal(2, inspector.RowCount);
        Assert.Contains(Inside<CheckBox>(inspector), toggle => toggle.IsChecked);
        Assert.Contains(Inside<NumericInput>(inspector), box => box.Number == 3d);
    }

    [Fact]
    public void The_inspector_has_a_field_for_a_dynamic_input() {
        var node = graph.Add("Test/Combine", new(60f, 60f));
        var inspector = Inspector();

        View.Select([node.Id]);
        inspector.Rebuild();

        Assert.Equal(2, inspector.RowCount);
        Assert.Contains(Inside<NumericInput>(inspector), box => box.Number == 0.25d);
    }

    [Fact]
    public void The_inspector_follows_a_number_typed_on_the_node() {
        var node = graph.Add("Test/Combine", new(60f, 60f));
        var inspector = Inspector();

        fixture.Update();

        View.Select([node.Id]);
        inspector.Rebuild();

        Fields(node.Id, "A").Boxes[0].Number = 0.75f;
        fixture.Update();

        Assert.Contains(Inside<NumericInput>(inspector), box => box.Number == 0.75d);
    }

    /// <remarks>
    ///     ⚠ Wiring something into the selected node turns that row from a field into a note saying
    ///     where its value comes from, which no amount of re-reading numbers will do. A refresh that
    ///     never rebuilt would leave a box beside a port the compiler had stopped reading.
    /// </remarks>
    [Fact]
    public void The_inspector_rebuilds_when_the_selected_nodes_wiring_changes() {
        var source = graph.Add("Test/Vector", new(40f, 40f));
        var sink = graph.Add("Test/Named", new(320f, 40f));

        var inspector = Inspector();

        fixture.Update();

        View.Select([sink.Id]);
        inspector.Rebuild();

        Assert.NotEmpty(Inside<NumericInput>(inspector));

        graph.Connect(new(source.Id, "Out"), new(sink.Id, "Base Colour"));
        fixture.Update();

        Assert.Empty(Inside<NumericInput>(inspector));
    }

    NodeInspector Inspector() {
        var inspector = fixture.Ui.Root.Add<NodeInspector>();

        inspector.View = View;
        inspector.EditedDocument = fixture.Document;

        return inspector;
    }

    static IEnumerable<T> Inside<T>(UiElement element) where T : UiElement {
        foreach (var child in element.Children) {
            if (child is T found) {
                yield return found;
            }

            foreach (var deeper in Inside<T>(child)) {
                yield return deeper;
            }
        }
    }
}
