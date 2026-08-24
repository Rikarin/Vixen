// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Inspector;
using Vixen.Editor.NodeGraph;
using Vixen.Ui;
using Vixen.Ui.Testing;
using Xunit;

namespace Tests;

/// <summary>The ported <c>NodeInspector</c>, held to the panel it replaced.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A committed test rather than a wave note.</b> Doc 36 § F7 wave 6 found that "byte
///         identical in N dumped states" was claimed by nine ledger rows and gated by three test
///         files: every other comparison had been run once, eyeballed and deleted, so nothing
///         re-checked it and a later change that moved those pixels moved them past every gate. This
///         is the comparison, kept — <see cref="HandWritten" /> below is <c>NodeInspector.Rebuild</c>
///         as it stood before the port, and every state runs both and compares two strings.
///     </para>
///     <para>
///         ⚠ <b>Two dumps, because a tree dump is blind.</b> <see cref="UiTest.Tree(UiElement)" />
///         sees tags, classes, rectangles and text; it cannot see a <c>Disabled</c>, an
///         <c>IsChecked</c>, a button's <c>Label</c> or a numeric input's <c>Number</c>, all of which
///         a control draws through parts of its own. Wave 7 proved that matters: <c>StandardFrameView</c>
///         matched byte-for-byte in six states while carrying a binding that could not work.
///         <see cref="UiTest.Flags" /> is the second half and is asserted beside the first.
///     </para>
///     <para>
///         ⚠ <b>And the panel is driven from the control as well as from the model.</b> Wave 7's
///         dumps only ever wrote to the panel's model, which is the leg that cannot fail; the tests
///         at the bottom of this file type into a row and wire a port on the graph, which are the two
///         ways a person changes what this panel says.
///     </para>
/// </remarks>
public sealed class NodeInspectorDumpTests : IDisposable {
    readonly ViewFixture fixture = new();
    readonly NodeTypeRegistry registry = new();
    readonly NodeGraphModel graph = new();
    readonly UiTest test;

    public NodeInspectorDumpTests() {
        Vixen.Editor.NodeGraph.Tests.NodeTypes.Register(registry);
        fixture.Show(graph, registry);

        test = UiTest.Adopt(fixture.Ui);
    }

    public void Dispose() {
        test.Dispose();
        fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── The states ───────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ A panel that was added and framed and never told anything. The hand-written one built
    ///     nothing at all here — <c>Rebuild</c> had not run — and so does this one, which is the one
    ///     deviation <c>VariationHarnessView</c> had to argue for and this port does not.
    /// </summary>
    [Fact]
    public void A_panel_that_was_never_rebuilt_is_empty() {
        var handWritten = Dump(_ => null);
        var ported = Ported(_ => { });

        Assert.Equal(handWritten, ported);
        Assert.DoesNotContain("\n", ported.Tree, StringComparison.Ordinal);
        Assert.Equal("", ported.Flags);
    }

    /// <summary>A view with nothing selected: the sentence the panel was given.</summary>
    [Fact]
    public void Nothing_selected_is_the_empty_message() => Same(_ => []);

    /// <summary>A view that was never assigned at all, which is the same sentence one arm earlier.</summary>
    [Fact]
    public void No_view_at_all_is_the_same_sentence() {
        var handWritten = Dump(host => {
            host.Add("text").Text = Empty;

            return null;
        });

        var ported = Ported(panel => {
            panel.EmptyMessage = Empty;
            panel.Rebuild();
        });

        Assert.Equal(handWritten, ported);
    }

    /// <summary>One node, free ports: a title, a summary and the inspector's rows.</summary>
    /// <remarks>
    ///     ⚠ <b>And the one state that proves the flags half is not vacuous.</b> An instrument built
    ///     to catch a defect will report success on the day it does not run, so the state with the
    ///     most controls in it asserts that the flags dump said something and named the two things a
    ///     tree dump cannot see: a row's search box has a placeholder, and a port's number box has a
    ///     number.
    /// </remarks>
    [Fact]
    public void One_node_with_free_ports_draws_the_rows() {
        var dump = Same(_ => [graph.Add("Test/Combine", new(60f, 60f))]);

        Assert.Contains("Number=0.25", dump.Flags, StringComparison.Ordinal);
        Assert.Contains("Placeholder=", dump.Flags, StringComparison.Ordinal);
    }

    /// <summary>Two of one type: the count in the heading, and one set of rows for both.</summary>
    [Fact]
    public void Two_nodes_of_one_type_are_counted_in_the_heading() => Same(_ => [
        graph.Add("Test/Combine", new(60f, 60f)),
        graph.Add("Test/Combine", new(300f, 60f))
    ]);

    /// <summary>Two of different types, which is a refusal with no heading.</summary>
    [Fact]
    public void Several_kinds_of_node_is_a_refusal() => Same(_ => [
        graph.Add("Test/Combine", new(60f, 60f)),
        graph.Add("Test/Vector", new(300f, 60f))
    ]);

    /// <summary>A node whose type this build does not have, which is a refusal naming it.</summary>
    [Fact]
    public void A_node_type_this_build_has_never_heard_of_is_named() => Same(_ => [
        graph.Add("Test/Gone Away", new(60f, 60f))
    ]);

    /// <summary>A node with a wire into it: a fact row where a field was.</summary>
    [Fact]
    public void A_connected_input_is_a_fact_row_rather_than_a_field() => Same(_ => {
        var source = graph.Add("Test/Vector", new(40f, 40f));
        var sink = graph.Add("Test/Named", new(320f, 40f));

        graph.Connect(new(source.Id, "Out"), new(sink.Id, "Base Colour"));

        return [sink];
    });

    /// <summary>Two of one type wired differently, which is a heading and a refusal under it.</summary>
    [Fact]
    public void Two_nodes_wired_differently_are_refused_under_their_heading() => Same(_ => {
        var source = graph.Add("Test/Vector", new(40f, 40f));
        var first = graph.Add("Test/Named", new(320f, 40f));
        var second = graph.Add("Test/Named", new(320f, 200f));

        graph.Connect(new(source.Id, "Out"), new(first.Id, "Base Colour"));

        return [first, second];
    });

    /// <summary>A node with no editable port at all, which is the sentence the last four lines wrote.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the state the port had to solve before it could be written.</b> The
    ///     hand-written panel decided it from <c>panel.Rows.Count</c> on the line after
    ///     <c>Inspect</c>; under <c>use</c> the inspect is an effect and has not run on that line, so
    ///     the ported panel decides it from <c>provider.Descriptor.Members</c>. If it had been left
    ///     alone the sentence would appear under every full panel instead of only this one — which is
    ///     what the four preceding tests would catch, and this one is the other half.
    /// </remarks>
    [Fact]
    public void A_node_with_nothing_to_set_says_so() => Same(_ => [graph.Add("Test/Constant", new(60f, 60f))]);

    /// <summary>A node whose settings are names rather than numbers, which is the widest row set.</summary>
    [Fact]
    public void A_node_made_of_names_draws_its_settings() => Same(_ => [
        graph.Add("Test/Named Thing", new(60f, 60f))
    ]);

    // ── Driven from the control, not from the model ──────────────────────────

    /// <summary>
    ///     ⚠ <b>Typing into a row reaches the node and does not rebuild the panel.</b> This is the
    ///     <c>Refresh</c> leg: the graph's change is a value change, so the elements have to survive
    ///     it — under the hand-written panel that was what the signature protected, and under this one
    ///     it is <c>Describes</c>. A port that rebuilt here would drop the caret on every keystroke.
    /// </summary>
    [Fact]
    public void A_number_typed_into_a_row_reaches_the_node_and_keeps_the_row() {
        var node = graph.Add("Test/Combine", new(60f, 60f));
        var panel = Show([node]);

        var box = Boxes(panel)[0];

        box.Number = 0.75d;
        fixture.Update();

        Assert.Equal([0.75f], node.Values["A"]);

        // The same object, so nothing was torn down and rebuilt under the pointer.
        Assert.Same(box, Boxes(panel)[0]);
        Assert.False(box.IsRemoved);
    }

    /// <summary>
    ///     ⚠ <b>And wiring the port the row belongs to <i>does</i> rebuild it.</b> The other half of
    ///     the same decision, and the reason it cannot simply always refresh: a connected port is not
    ///     a member, so re-reading values would leave a number box beside a port the compiler has
    ///     stopped reading.
    /// </summary>
    [Fact]
    public void Wiring_the_port_a_row_belongs_to_replaces_it_with_a_fact_row() {
        var source = graph.Add("Test/Vector", new(40f, 40f));
        var sink = graph.Add("Test/Named", new(320f, 40f));
        var panel = Show([sink]);

        Assert.NotEmpty(Boxes(panel));
        Assert.Empty(Rows(panel, "fact-row"));

        graph.Connect(new(source.Id, "Out"), new(sink.Id, "Base Colour"));
        fixture.Update();

        Assert.Empty(Boxes(panel));
        Assert.Single(Rows(panel, "fact-row"));
    }

    /// <summary>
    ///     ⚠ <b>A second refusal replaces the first, which is the wave-4 trap this panel is shaped
    ///     around.</b> The <c>@if</c>'s arm does not change when one sentence succeeds another — the
    ///     predicate is "is there a notice" — so a readout that had closed over the sentence instead
    ///     of going back to the signal would show the first one for ever. Every state above has a
    ///     different arm; this is the one that stays in the same one.
    /// </summary>
    [Fact]
    public void A_second_refusal_replaces_the_first_within_the_same_arm() {
        var combine = graph.Add("Test/Combine", new(60f, 60f));
        var vector = graph.Add("Test/Vector", new(300f, 60f));
        var missing = graph.Add("Test/Gone Away", new(540f, 60f));

        var panel = Show([combine, vector]);

        Assert.Equal("Several kinds of node selected.", Sentence(panel));

        fixture.View.Select([missing.Id]);
        panel.Rebuild();
        fixture.Update();

        Assert.Equal("'Test/Gone Away' is not a node type this build has.", Sentence(panel));
    }

    /// <summary>
    ///     ⚠ <b>And the heading follows the selection while the arm survives.</b> Same trap, one arm
    ///     over: moving from one node type to another of the same shape keeps the title element and
    ///     has to rewrite it.
    /// </summary>
    [Fact]
    public void The_heading_follows_the_selection_while_the_arm_survives() {
        var combine = graph.Add("Test/Combine", new(60f, 60f));
        var named = graph.Add("Test/Named Thing", new(300f, 60f));

        var panel = Show([combine]);

        Assert.Equal("Two values, one result.", Text(panel, "node-inspector-summary"));

        fixture.View.Select([named.Id]);
        panel.Rebuild();
        fixture.Update();

        Assert.Equal("A name, a renamed name, and a number beside them.", Text(panel, "node-inspector-summary"));
    }

    // ── The comparison ───────────────────────────────────────────────────────

    const string Empty = "Select a node to edit its settings.";

    /// <summary>
    ///     <c>NodeInspector.Rebuild</c> as it stood before doc 36 § F7 wave 8, minus the parts the
    ///     markup now owns.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Kept rather than paraphrased.</b> The value of this file is that the two sides were
    ///     written by different means and agree; a reference that had been "tidied" to match the
    ///     markup would agree with it for the wrong reason. The one thing deliberately dropped is the
    ///     <c>StringBuilder</c> signature, which decided <i>when</i> to run this and never what it
    ///     drew.
    /// </remarks>
    void HandWritten(UiElement into, IReadOnlyList<GraphNode> selected) {
        if (selected.Count == 0) {
            into.Add("text").Text = Empty;

            return;
        }

        var type = selected[0].Type;

        foreach (var node in selected) {
            if (!string.Equals(node.Type, type, StringComparison.Ordinal)) {
                into.Add("text").Text = "Several kinds of node selected.";

                return;
            }
        }

        if (fixture.View.Definition(type) is not { } definition) {
            into.Add("text").Text = $"'{type}' is not a node type this build has.";

            return;
        }

        var provider = NodePortEditProvider.For(graph, definition, selected[0].Id, fixture.View.IsReadOnly);

        if (!provider.Describes(graph, selected)) {
            into.Add("node-inspector-title").Text = definition.Title;
            into.Add("text").Text = "These nodes are wired differently, so there is no set of fields that fits both.";

            return;
        }

        into.Add("node-inspector-title").Text = selected.Count > 1
            ? $"{definition.Title} ({selected.Count})"
            : definition.Title;

        if (definition.Summary.Length > 0) {
            into.Add("node-inspector-summary").Text = definition.Summary;
        }

        var panel = into.Add<InspectorView>();

        panel.EditedDocument = fixture.Document;
        panel.Inspect(provider.Descriptor, provider, [.. selected]);

        foreach (var port in provider.Connected) {
            var row = into.Add("fact-row");

            row.Add("fact-name").Text = port.Name;
            row.Add("fact-value").Add("text").Text = "from a connection";
        }

        if (panel.Rows.Count == 0 && provider.Connected.Count == 0) {
            into.Add("text").Text = "This node has nothing to set.";
        }
    }

    /// <summary>Builds both forms in the same place and asserts the two dumps agree.</summary>
    /// <param name="arrange">Makes the nodes and says which of them is selected.</param>
    /// <returns>What the ported panel drew, for a caller that wants to say more about it.</returns>
    (string Tree, string Flags) Same(Func<NodeGraphModel, IReadOnlyList<GraphNode>> arrange) {
        var selected = arrange(graph);

        fixture.View.Select([.. selected.Select(node => node.Id)]);

        var handWritten = Dump(host => {
            HandWritten(host, selected);

            return null;
        });

        var ported = Ported(panel => {
            panel.View = fixture.View;
            panel.EditedDocument = fixture.Document;
            panel.EmptyMessage = Empty;
            panel.Rebuild();
        });

        Assert.Equal(handWritten, ported);

        // ⚠ A comparison of two empty strings passes and says nothing. That is this file's own
        // version of the trap it exists for, so the dump has to have had something in it.
        Assert.NotEqual("", ported.Tree);

        return ported;
    }

    /// <summary>Builds the hand-written form under the tag the panel answers to, and writes it down.</summary>
    (string Tree, string Flags) Dump(Func<UiElement, object?> build) {
        var host = fixture.Ui.Root.Add("node-inspector");

        build(host);
        fixture.Update();

        var written = (test.Tree(host), test.Flags(host));

        host.Remove();
        fixture.Update();

        return written;
    }

    /// <summary>And the ported panel, in the same place.</summary>
    (string Tree, string Flags) Ported(Action<NodeInspector> arrange) {
        var panel = fixture.Ui.Root.Add<NodeInspector>();

        arrange(panel);
        fixture.Update();

        var written = (test.Tree(panel), test.Flags(panel));

        panel.Remove();
        fixture.Update();

        return written;
    }

    // ── Reading one back ─────────────────────────────────────────────────────

    NodeInspector Show(IReadOnlyList<GraphNode> selected) {
        var panel = fixture.Ui.Root.Add<NodeInspector>();

        panel.View = fixture.View;
        panel.EditedDocument = fixture.Document;

        fixture.View.Select([.. selected.Select(node => node.Id)]);
        panel.Rebuild();
        fixture.Update();

        return panel;
    }

    static IReadOnlyList<Vixen.Ui.Controls.NumericInput> Boxes(UiElement panel) =>
        [.. Inside<Vixen.Ui.Controls.NumericInput>(panel)];

    static IReadOnlyList<UiElement> Rows(UiElement panel, string tag) =>
        [.. panel.Children.Where(child => string.Equals(child.Tag, tag, StringComparison.Ordinal))];

    static string? Sentence(UiElement panel) => Text(panel, "text");

    static string? Text(UiElement panel, string tag) =>
        panel.Children.FirstOrDefault(child => string.Equals(child.Tag, tag, StringComparison.Ordinal))?.Text;

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
