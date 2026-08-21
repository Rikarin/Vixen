// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Editor.Inspector;
using Vixen.Editor.NodeGraph;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Tests;

/// <summary>
///     Doc 36 § P1's second <c>IEditProvider</c>, asserted by what a <i>generic</i> surface can do
///     with it.
/// </summary>
/// <remarks>
///     <para>
///         <b>The point of a provider is not that the class exists.</b> A node's ports were always
///         editable — <c>NodeInspector</c> built controls for them by hand. What was missing was
///         everything a panel that had never heard of a port gets for free once the ports are
///         described: binding by name, multi-node editing, the mixed state, <c>Changed</c>, a drawer,
///         a reset button, and a markup tree that names a member in a string.
///     </para>
///     <para>
///         So every test below drives something that knows nothing about graphs —
///         <see cref="EditTarget" />, <see cref="InspectorView" />, <see cref="MarkupBinding" /> —
///         and none of them constructs a control itself.
///     </para>
/// </remarks>
public class NodePortProviderTests : IDisposable {
    readonly ViewFixture fixture = new();
    readonly NodeTypeRegistry registry = new();
    readonly NodeGraphModel graph = new();

    public NodePortProviderTests() {
        Vixen.Editor.NodeGraph.Tests.NodeTypes.Register(registry);
        fixture.Show(graph, registry);

        InspectorTheme.Install(fixture.Ui);
    }

    public void Dispose() {
        fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── The pipeline reaches a port ──────────────────────────────────────────

    /// <remarks>
    ///     The one thing "no provider" cost that nothing else could work around: a panel holding an
    ///     <c>EditTarget</c> asks for a member by the name a person would type, and until now a graph
    ///     node answered nothing to every name there is.
    /// </remarks>
    [Fact]
    public void A_port_is_reached_by_the_name_a_saved_graph_uses() {
        var (node, target) = Bound("Test/Named");

        Assert.NotNull(target.Find("Base Colour"));
        Assert.Null(target.Find("BaseColour"));

        Assert.Equal(new Vector3(0.1f, 0.2f, 0.3f), target.Find("Base Colour")!.Read().Value);
        Assert.Empty(node.Values);
    }

    /// <remarks>
    ///     ⚠ A texture, a sampler and a flow port take no typed value, so there is no member to make
    ///     of one — and a row for a flow port would be a box beside a wire that means "after".
    /// </remarks>
    [Fact]
    public void Only_the_inputs_that_take_a_typed_value_are_members() {
        var (_, target) = Bound("Test/Settings");

        Assert.Equal(["Enabled", "Count"], target.Members.Select(member => member.Name));
        Assert.Equal([typeof(bool), typeof(int)], target.Members.Select(member => member.ValueType));
    }

    /// <remarks>
    ///     ⚠ A dynamic port takes one number however wide it turned out to be — <c>PortKinds.Fields</c>
    ///     — because the compiler splats a short constant. Three boxes because a <i>different</i> port
    ///     was wired to a <c>float3</c> would make the same graph edit differently.
    /// </remarks>
    [Fact]
    public void A_dynamic_port_is_one_number() {
        var (_, target) = Bound("Test/Combine");

        Assert.Equal(typeof(float), target.Find("A")!.Member.ValueType);
        Assert.Equal(0.25f, target.Find("A")!.Read().Value);
    }

    [Fact]
    public void A_connected_input_is_not_a_member_at_all() {
        var source = graph.Add("Test/Vector", new(40f, 40f));
        var sink = graph.Add("Test/Named", new(320f, 40f));

        graph.Connect(new(source.Id, "Out"), new(sink.Id, "Base Colour"));

        var provider = Provider("Test/Named", sink.Id);

        Assert.Empty(provider.Descriptor.Members);
        Assert.Equal(["Base Colour"], provider.Connected.Select(port => port.Name));
    }

    // ── Writing through it ───────────────────────────────────────────────────

    /// <remarks>
    ///     ⚠ The command is <c>SetPortValueCommand</c> — the graph's own, handed to the pipeline by
    ///     the member — so a number typed in a panel and a number dragged on the canvas produce the
    ///     same entry. Undo restores the *absence* of an inline value, which is what a port that was
    ///     never overridden had.
    /// </remarks>
    [Fact]
    public void A_write_lands_on_the_graphs_own_command_and_undoes_to_nothing() {
        var (node, target) = Bound("Test/Named");

        Assert.True(target.Find("Base Colour")!.Write(new Vector3(0.5f, 0.6f, 0.7f)));
        Assert.Equal([0.5f, 0.6f, 0.7f], node.Values["Base Colour"]);
        Assert.Equal("Set Base Colour", fixture.Stack.History[^1].Name);

        fixture.Stack.Undo();

        Assert.False(node.Values.ContainsKey("Base Colour"));
    }

    /// <remarks>
    ///     ⚠ <b>One entry, and one "before" per node.</b> The whole point of editing a selection is
    ///     that the nodes disagreed; an undo that put them all back to a shared value would be an
    ///     undo that lost one of them.
    /// </remarks>
    [Fact]
    public void One_write_reaches_a_whole_selection_as_one_undo_entry() {
        var first = graph.Add("Test/Combine", new(40f, 40f));
        var second = graph.Add("Test/Combine", new(240f, 40f));

        second.SetValue("A", 0.75f);

        var target = Target([first, second]);
        var depth = fixture.Stack.History.Count;

        Assert.True(target.Find("A")!.Write(0.5f));

        Assert.Equal([0.5f], first.Values["A"]);
        Assert.Equal([0.5f], second.Values["A"]);
        Assert.Equal(depth + 1, fixture.Stack.History.Count);
        Assert.Equal("Set A (2)", fixture.Stack.History[^1].Name);

        fixture.Stack.Undo();

        Assert.False(first.Values.ContainsKey("A"));
        Assert.Equal([0.75f], second.Values["A"]);
    }

    /// <remarks>
    ///     The state a panel has to be able to show rather than invent: two nodes at two values have
    ///     no shared one, and a row that picked the first node's would silently apply it to the rest.
    /// </remarks>
    [Fact]
    public void A_selection_that_disagrees_reads_as_mixed() {
        var first = graph.Add("Test/Combine", new(40f, 40f));
        var second = graph.Add("Test/Combine", new(240f, 40f));

        second.SetValue("A", 0.75f);

        var property = Target([first, second]).Find("A")!;

        Assert.True(property.Read().IsMixed);

        property.Write(0.25f);

        Assert.False(property.Read().IsMixed);
    }

    /// <remarks>
    ///     ⚠ <c>EditTarget</c>'s own check is the CLR type, and every node is a <c>GraphNode</c> — so
    ///     a selection of two kinds of node looks uniform to it and would be given whichever kind's
    ///     rows the provider happened to be built for. The graph is the only thing that knows better.
    /// </remarks>
    [Fact]
    public void A_provider_refuses_a_selection_of_a_different_node_type() {
        var combine = graph.Add("Test/Combine", new(40f, 40f));
        var named = graph.Add("Test/Named", new(240f, 40f));

        var provider = Provider("Test/Combine", combine.Id);

        Assert.True(provider.Describes(graph, [combine]));
        Assert.False(provider.Describes(graph, [combine, named]));
    }

    [Fact]
    public void A_provider_refuses_a_selection_wired_differently() {
        var source = graph.Add("Test/Vector", new(40f, 40f));
        var free = graph.Add("Test/Named", new(240f, 40f));
        var wired = graph.Add("Test/Named", new(440f, 40f));

        graph.Connect(new(source.Id, "Out"), new(wired.Id, "Base Colour"));

        var provider = Provider("Test/Named", free.Id);

        Assert.True(provider.Describes(graph, [free]));
        Assert.False(provider.Describes(graph, [free, wired]));
    }

    // ── What a generic panel does with it ────────────────────────────────────

    /// <remarks>
    ///     <b>The test of the whole task.</b> <see cref="InspectorView" /> is the panel that draws
    ///     components, assets and settings files; it has never heard of a port, and it is now what
    ///     draws one — with the drawer the port's type deserves rather than a number box per lane
    ///     built by hand.
    /// </remarks>
    [Fact]
    public void The_ordinary_inspector_panel_draws_a_ports_own_drawer() {
        var node = graph.Add("Test/Named", new(60f, 60f));
        var provider = Provider("Test/Named", node.Id);

        var panel = fixture.Ui.Root.Add<InspectorView>();

        panel.EditedDocument = fixture.Document;
        panel.Inspect(provider.Descriptor, provider, node);

        fixture.Update();

        var row = Assert.Single(panel.Rows);

        Assert.Equal("Base Colour", row.Label.Text);
        Assert.Equal("What it starts as.", row.Field.Member.Tooltip);

        // Three boxes because the member is a Vector3, which is the vector drawer's doing and not
        // this panel's: the old hand-written one counted lanes itself. `Value` and not `Number`,
        // because a vector row is three text boxes the drawer fills in — see `ComponentDrawer.Show`.
        Assert.Equal(3, Inside<NumericInput>(row).Count());
        Assert.Equal(["0.1", "0.2", "0.3"], Inside<NumericInput>(row).Select(box => box.Value));
    }

    /// <remarks>
    ///     ⚠ <b>The reset button is a question about a default, and a port has one.</b> The
    ///     descriptor's factory makes a detached node, which holds no inline value at all — so a
    ///     member's default is the port's declared default and the button appears exactly when
    ///     somebody has typed over it.
    /// </remarks>
    [Fact]
    public void A_port_that_differs_from_its_declared_default_offers_a_reset() {
        var node = graph.Add("Test/Named", new(60f, 60f));
        var provider = Provider("Test/Named", node.Id);
        var target = new InspectorTarget([node], fixture.Document, null, provider, provider.Descriptor);

        var field = Assert.IsType<InspectorField>(target.Find("Base Colour"));

        Assert.True(field.CanReset);
        Assert.False(field.IsModified);

        field.Write(new Vector3(0.5f, 0.6f, 0.7f));

        Assert.True(field.IsModified);
    }

    /// <remarks>
    ///     <b>Doc 36 § P4, over a member no C# type declares.</b> A <c>&lt;PropertyField&gt;</c> names
    ///     a member in a string the compiler never sees, and the join happens after the tree is built
    ///     — so a node type is now something a <c>.vxml</c> can lay out and group, exactly as
    ///     <c>TerrainBrushInspector.vxml</c> does for a brush.
    /// </remarks>
    [Fact]
    public void Markup_binds_a_port_by_name_and_edits_it() {
        var node = graph.Add("Test/Combine", new(60f, 60f));
        var provider = Provider("Test/Combine", node.Id);
        var target = new InspectorTarget([node], fixture.Document, null, provider, provider.Descriptor);

        var tree = fixture.Ui.Root.Add<UiElement>("markup");

        var drawn = tree.Add<PropertyField>();
        drawn.Path = "A";

        var chosen = tree.Add<Slider>();
        chosen.Minimum = 0f;
        chosen.Maximum = 1f;
        chosen.SetAttribute("binding-path", "B");

        Assert.Equal(2, MarkupBinding.Bind(tree, target));

        fixture.Update();

        Assert.NotNull(drawn.Row);
        Assert.Equal(0.25f, Inside<NumericInput>(drawn).Single().Number);

        chosen.Value = 0.5f;

        Assert.Equal([0.5f], node.Values["B"]);
    }

    /// <remarks>
    ///     ⚠ The panel beside a canvas is now a host rather than an inspector: it decides what may be
    ///     shown and hands it to <see cref="InspectorView" />. A selection of two nodes of one type
    ///     used to say "Several nodes selected" because there was no way to edit both.
    /// </remarks>
    [Fact]
    public void The_panel_beside_the_canvas_edits_several_nodes_of_one_type() {
        var first = graph.Add("Test/Combine", new(40f, 40f));
        var second = graph.Add("Test/Combine", new(240f, 40f));

        var inspector = fixture.Ui.Root.Add<NodeInspector>();

        inspector.View = fixture.View;
        inspector.EditedDocument = fixture.Document;

        fixture.View.Select([first.Id, second.Id]);
        inspector.Rebuild();
        fixture.Update();

        Assert.Equal(2, inspector.RowCount);

        Inside<NumericInput>(inspector).First().Number = 0.5f;

        Assert.Equal([0.5f], first.Values["A"]);
        Assert.Equal([0.5f], second.Values["A"]);
    }

    // ── Fixture ──────────────────────────────────────────────────────────────

    NodePortEditProvider Provider(string type, NodeId node) =>
        NodePortEditProvider.For(graph, registry.Get(type), node);

    (GraphNode Node, EditTarget Target) Bound(string type) {
        var node = graph.Add(type, new(60f, 60f));

        return (node, Target([node]));
    }

    EditTarget Target(IReadOnlyList<GraphNode> nodes) =>
        new(nodes, Provider(nodes[0].Type, nodes[0].Id), fixture.Document);

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
