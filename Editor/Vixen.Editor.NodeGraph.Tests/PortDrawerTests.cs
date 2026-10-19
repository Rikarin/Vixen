// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.Editor.Inspector;
using Vixen.Editor.NodeGraph;
using Vixen.Ui;
using Vixen.Ui.Controls.Advanced;
using Xunit;
using PortDirection = Vixen.Editor.NodeGraph.PortDirection;

namespace Tests;

/// <summary>A drawer registered for a port's type shows on the node, and a graph without one pays nothing for it.</summary>
/// <remarks>
///     <para>
///         <b>#1305, decision (b).</b> The on-node editor was the canvas's own <see cref="PortEditor" />
///         whatever the panel would have drawn, so a drawer a plugin registered for a port's type reached
///         the panel beside the canvas and not the node. A third <see cref="PortEditorKind" /> hosts the
///         drawer's element, chosen only when the registry resolves a drawer the built-in set would not.
///     </para>
///     <para>
///         ⚠ <b>The cost is measured as work, not time.</b> <c>StyleTree.Count</c> is every element slot
///         the document ever made, removed ones included, so a pan that built anything moves it. The
///         custom-drawer sweep is the instrument check: the same sweep over the same graph with a drawer
///         registered does move it, so the counter can say no.
///     </para>
/// </remarks>
public class PortDrawerTests : IDisposable {
    readonly ViewFixture fixture = new();
    readonly NodeTypeRegistry registry = new();
    readonly NodeGraphModel graph = new();
    readonly Dial dial = new();

    public PortDrawerTests() => Vixen.Editor.NodeGraph.Tests.NodeTypes.Register(registry);

    public void Dispose() {
        fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_drawer_registered_for_a_ports_type_is_what_the_node_shows_and_writes_through() {
        Show(Custom());

        var node = graph.Add("Test/Combine", new(60f, 60f));
        fixture.Update();

        var fields = fixture.Port(node.Id, "A", PortDirection.Input).Fields;

        Assert.Equal(PortEditorKind.Hosted, fields.Port!.Editor!.Kind);

        var shown = Assert.Single(Inside(fields, "test-dial"));

        Assert.Equal("0.25", shown.Text);
        Assert.All(fields.Boxes, box => Assert.True(box.HasClass("parked")));

        // What the drawer writes is the node's value, recorded as one entry that undoes.
        Assert.True(dial.Built["A"].Write(0.75f));
        dial.Built["A"].Seal();
        fixture.Update();

        Assert.Equal([0.75f], graph.Nodes.Single().Values["A"]);
        // ⚠ The same element, not a rebuilt one: the drawer's write reprojected the graph, and a row
        // that rebuilt here would drop the control somebody is typing into.
        Assert.Same(shown, Assert.Single(Inside(fixture.Port(node.Id, "A", PortDirection.Input).Fields, "test-dial")));
        Assert.Equal("0.75", shown.Text);

        fixture.Stack.Undo();
        fixture.Update();

        Assert.Equal("0.25", Assert.Single(Inside(fixture.Port(node.Id, "A", PortDirection.Input).Fields, "test-dial")).Text);
    }

    [Fact]
    public void A_port_whose_drawer_is_built_in_keeps_the_canvas_shape() {
        Show(DrawerRegistry.CreateDefault());

        var node = graph.Add("Test/Combine", new(60f, 60f));
        fixture.Update();

        var fields = fixture.Port(node.Id, "A", PortDirection.Input).Fields;

        Assert.Equal(PortEditorKind.Number, fields.Port!.Editor!.Kind);
        Assert.Null(fields.Hosted);
    }

    [Fact]
    public void A_graph_with_no_custom_drawers_builds_nothing_while_it_pans() {
        Show(DrawerRegistry.CreateDefault());
        Column();

        var (built, compacted, moved) = Sweep();

        Assert.True(moved, "the sweep never moved a row to another node, so it proves nothing about rebinding");
        Assert.Equal(0, built);
        Assert.False(compacted);
    }

    /// <remarks>
    ///     ⚠ <b>Either half of the counter can move.</b> Removing what a row built leaves tombstones, and
    ///     enough of them compact the store — after which the slot count is renumbered and can fall. So
    ///     "built" here is the slot count rising <i>or</i> a compaction having run, and the drawer's own
    ///     count says the same thing from the other side.
    /// </remarks>
    [Fact]
    public void The_same_sweep_with_a_custom_drawer_does_build_which_is_what_makes_the_counter_an_instrument() {
        Show(Custom());
        Column();

        var builds = dial.Builds;
        var (built, compacted, moved) = Sweep();

        Assert.True(moved);
        Assert.True(built > 0 || compacted, "a hosted drawer moved to another port must build again");
        Assert.True(dial.Builds > builds);
    }

    DrawerRegistry Custom() {
        var drawers = DrawerRegistry.CreateDefault();
        drawers.ForType<float>(dial);

        return drawers;
    }

    void Show(DrawerRegistry drawers) {
        fixture.View.Drawers = drawers;
        fixture.Show(graph, registry);
    }

    /// <summary>Sixty nodes in a column, far taller than the canvas, so a pan changes what is realised.</summary>
    void Column() {
        for (var index = 0; index < 60; index++) {
            graph.Add("Test/Combine", new(60f, index * 160f));
        }

        fixture.Update();
    }

    /// <summary>Pans down the column and back once to warm the pool, then again counting element slots made.</summary>
    (int Built, bool Compacted, bool Moved) Sweep() {
        Pan();

        var before = fixture.Ui.Styles.Tree.Count;
        var compactions = fixture.Ui.StyleCompactions;
        var moved = Pan();

        return (fixture.Ui.Styles.Tree.Count - before, fixture.Ui.StyleCompactions != compactions, moved);
    }

    bool Pan() {
        var first = fixture.Canvas.Items[0].Node;
        var moved = false;

        for (var y = 0f; y <= 9_000f; y += 120f) {
            fixture.Canvas.Pan = new Vector2(0f, y);
            fixture.Update();

            moved |= !ReferenceEquals(fixture.Canvas.Items[0].Node, first);
        }

        for (var y = 9_000f; y >= 0f; y -= 120f) {
            fixture.Canvas.Pan = new Vector2(0f, y);
            fixture.Update();
        }

        return moved;
    }

    static IEnumerable<UiElement> Inside(UiElement root, string tag) {
        foreach (var child in root.Children) {
            if (child.Tag == tag) {
                yield return child;
            }

            foreach (var found in Inside(child, tag)) {
                yield return found;
            }
        }
    }

    /// <summary>A drawer for <c>float</c> that is not the number box: a label reading the value.</summary>
    sealed class Dial : IPropertyDrawer {
        public Dictionary<string, InspectorField> Built { get; } = [];

        public int Builds { get; private set; }

        public UiElement Build(InspectorField field, UiElement parent) {
            Built[field.Member.Name] = field;
            Builds++;

            return parent.Add("test-dial");
        }

        public void Show(InspectorField field, UiElement editor) =>
            editor.Text = Convert.ToSingle(field.Read().Value ?? 0f, CultureInfo.InvariantCulture)
                .ToString(CultureInfo.InvariantCulture);
    }
}
