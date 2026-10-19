// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core.Mathematics;
using Vixen.Editor.NodeGraph;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Xunit;
using PortDirection = Vixen.Editor.NodeGraph.PortDirection;

namespace Tests;

/// <summary>The number on a node and the number in the panel beside it are one field, edited one way.</summary>
/// <remarks>
///     <para>
///         <b>#1305 part (2), doc 36 § Part 6.</b> The boxes on a node were built by hand in
///         <c>NodeGraphView.Inline</c> and written by a <c>SetPortValueCommand</c> the view constructed
///         itself; the panel's rows came from <see cref="NodePortEditProvider" /> through
///         <c>InspectorField</c>. Two constructions of one value, and each test below is a place the two
///         measurably disagreed — asserted on both surfaces, so a fix that moved one of them to the
///         other's mistake would still be red.
///     </para>
/// </remarks>
public class OnNodeFieldTests : IDisposable {
    readonly ViewFixture fixture = new();
    readonly NodeTypeRegistry registry = new();
    readonly NodeGraphModel graph = new();

    public OnNodeFieldTests() {
        Vixen.Editor.NodeGraph.Tests.NodeTypes.Register(registry);

        // ⚠ A wide port with a one-number default. No fixture type has one — every vector default in
        // the tree is written out lane by lane — which is exactly why the disagreement below was
        // never seen: the compiler splats `[0.5]` into a `float3`, and the panel did not.
        registry.Add(new NodeTypeDefinition(
            "Test/Splat",
            [
                new("Tint", PortDirection.Input, PortKind.Float3, ImmutableArray.Create(0.5f)),
                new("Out", PortDirection.Output, PortKind.Float3)
            ],
            static () => new TestVectorNode()
        ));

        fixture.Show(graph, registry);
    }

    public void Dispose() {
        fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    NodePortEditor Fields(NodeId node, string port) => fixture.Port(node, port, PortDirection.Input).Fields;

    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Enter ends an edit in the panel and did not on the node.</b> The panel's number
    ///         drawer seals the stack on <c>Submitted</c>; the node's boxes told the view about every
    ///         keystroke and never about the end of one, so two separate edits of the same port —
    ///         typed, committed, typed again — merged into one undo entry, and Ctrl+Z after the second
    ///         went back past the first.
    ///     </para>
    ///     <para>
    ///         The panel half is the reference and passes either way; the node half is the defect.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Enter_ends_an_edit_on_the_node_as_it_does_in_the_panel() {
        var node = graph.Add("Test/Combine", new(60f, 60f));
        fixture.Update();

        var box = Fields(node.Id, "A").Boxes[0];

        box.Number = 1d;
        Submit(box);
        box.Number = 2d;
        Submit(box);
        fixture.Update();

        fixture.Stack.Undo();
        fixture.Update();

        Assert.Equal([1f], graph.Nodes.Single().Values["A"]);

        // The same two edits through the panel beside the canvas.
        var other = graph.Add("Test/Combine", new(60f, 260f));
        var inspector = Inspector();

        fixture.Update();
        fixture.View.Select([other.Id]);
        inspector.Rebuild();
        fixture.Update();

        var row = Inside<NumericInput>(inspector).First();

        row.Number = 1d;
        Submit(row);
        row.Number = 2d;
        Submit(row);
        fixture.Update();

        fixture.Stack.Undo();
        fixture.Update();

        Assert.Equal([1f], graph.Nodes.Single(found => found.Id == other.Id).Values["A"]);
    }

    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Three readings of one default, and the compiler's is the one that ships.</b>
    ///         <c>NodeGraphCompiler.Value</c> fills every lane of a wide port from a one-number default;
    ///         the node's <c>PortEditor.Set</c> padded with the last lane, which agreed; the panel's
    ///         <c>NodePortMember</c> filled the lanes it had and left the rest at zero — a row showing
    ///         a colour the shader never used.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And an edit made it true.</b> A vector row writes the whole vector, so nudging X in
    ///         the panel wrote the two zeros it was showing into Y and Z, turning a grey into a red
    ///         with one keystroke on a different lane.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_one_number_default_on_a_wide_port_reads_the_same_on_the_node_and_in_the_panel() {
        var node = graph.Add("Test/Splat", new(60f, 60f));
        fixture.Update();

        var editor = Fields(node.Id, "Tint").Port!.Editor!;

        Assert.Equal([0.5f, 0.5f, 0.5f], editor.ToArray());

        var provider = NodePortEditProvider.For(graph, registry.Get("Test/Splat"), node.Id);
        var member = Assert.IsType<NodePortMember>(Assert.Single(provider.Descriptor.Members));

        Assert.Equal(new Vector3(0.5f, 0.5f, 0.5f), member.GetBoxed(node));

        var inspector = Inspector();

        fixture.View.Select([node.Id]);
        inspector.Rebuild();
        fixture.Update();

        Inside<NumericInput>(inspector).First().Number = 0.25d;
        fixture.Update();

        Assert.Equal([0.25f, 0.5f, 0.5f], graph.Nodes.Single().Values["Tint"]);
    }

    /// <summary>Enter in a box, the way a person presses it: the box focused, then the key.</summary>
    void Submit(NumericInput box) {
        fixture.Ui.Focus(box);
        fixture.Type(InputKey.Enter);
    }

    NodeInspector Inspector() {
        var inspector = fixture.Ui.Root.Add<NodeInspector>();

        inspector.View = fixture.View;
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
