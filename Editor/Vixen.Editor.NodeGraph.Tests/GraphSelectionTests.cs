// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Ui.Reactive;
using Xunit;

namespace Tests;

/// <summary>The graph's selection is the graph's, in the editor's one selection vocabulary (#1305).</summary>
/// <remarks>
///     ⚠ <b>It was a private <c>HashSet&lt;NodeId&gt;</c> on the view</b> — not reactive, not a
///     <c>Selection&lt;T&gt;</c>, and reachable only by whoever held the view, which is why the
///     compositor and the shader graph each read it through their own <c>GraphView</c>. Each test here
///     is red against that: the first two cannot even find the selection on the model, and the third
///     writes one the view never hears about.
/// </remarks>
public sealed class GraphSelectionTests : IDisposable {
    readonly ViewFixture fixture = new();
    readonly NodeTypeRegistry registry = new();
    readonly NodeGraphModel graph = new();

    public GraphSelectionTests() {
        Vixen.Editor.NodeGraph.Tests.NodeTypes.Register(registry);
        fixture.Show(graph, registry);
    }

    public void Dispose() {
        fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    NodeGraphView View => fixture.View;

    /// <summary>A click selects on the graph, which is what any other reader of the graph sees.</summary>
    [Fact]
    public void A_click_selects_on_the_graph_itself() {
        var node = graph.Add("Test/Colour", new(64f, 64f));
        fixture.Update();

        fixture.Click(fixture.Item(node.Id));

        Assert.Equal([node.Id], graph.Selection);
        Assert.Same(graph.Selection, View.Selection);
    }

    /// <summary>The selection is a signal: something that read it is told when a click changes it.</summary>
    [Fact]
    public void A_click_reaches_an_effect_that_read_the_selection() {
        var node = graph.Add("Test/Colour", new(64f, 64f));
        fixture.Update();

        var seen = -1;
        using var watching = new Effect(() => seen = graph.Selection.Count);

        EffectScheduler.Default.Flush();
        Assert.Equal(0, seen);

        fixture.Click(fixture.Item(node.Id));
        EffectScheduler.Default.Flush();

        Assert.Equal(1, seen);
    }

    /// <summary>Selecting on the graph from outside — a palette entry, a script — moves the canvas.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that makes the first test worth anything.</b> A selection a reader can see
    ///     and not set would be a second copy of the view's, and a script selecting a node the canvas
    ///     never highlighted is a selection the author cannot see before pressing Delete.
    /// </remarks>
    [Fact]
    public void Selecting_on_the_graph_is_followed_by_the_canvas() {
        var first = graph.Add("Test/Colour", new(64f, 64f));
        var second = graph.Add("Test/Colour", new(400f, 64f));
        fixture.Update();

        var raised = 0;

        View.SelectionChanged += _ => raised++;

        graph.Selection.Set(second.Id);
        fixture.Update();

        Assert.Equal([fixture.Item(second.Id).Node!], View.Canvas.Selection);
        Assert.Equal(1, raised);

        // And it was not echoed back: the view following a write is not the view writing.
        Assert.Equal([second.Id], graph.Selection);

        graph.Selection.Set(first.Id);
        fixture.Update();

        Assert.Equal([fixture.Item(first.Id).Node!], View.Canvas.Selection);
        Assert.Equal(2, raised);
    }

    /// <summary>A click is not reported twice: once by the gesture and again when the view follows its own write.</summary>
    [Fact]
    public void A_click_raises_the_change_once() {
        var node = graph.Add("Test/Colour", new(64f, 64f));
        fixture.Update();

        var raised = 0;

        View.SelectionChanged += _ => raised++;

        fixture.Click(fixture.Item(node.Id));
        fixture.Update();

        Assert.Equal(1, raised);
    }
}
