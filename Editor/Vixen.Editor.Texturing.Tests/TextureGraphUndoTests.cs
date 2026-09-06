// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.NodeGraph;
using Vixen.Ui;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>What an undo taken outside the texture-graph pane does to the pane.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/965">#965</a> predicted
///         <a href="https://github.com/Rikarin/Vixen/issues/933">#933</a>'s gap one pane along, and
///         these two tests refute it.</b> The prediction was drawn from a grep — nothing in
///         <c>Vixen.Editor.NodeGraph</c> reads <c>CommandStack.Depth</c>, which is still true — and
///         the inference from it was that the canvas therefore cannot hear an undo. It does, by a
///         route the grep could not see: <c>NodeGraphCommand.Undo</c> calls
///         <c>NodeGraphModel.Touch</c>, the model raises <c>Changed</c>, and
///         <c>NodeGraphView.OnGraphChanged</c> reprojects and then raises <c>GraphChanged</c> — which
///         is what <c>TextureGraphView.Edited</c> is wired to, so the picture is recomputed too.
///     </para>
///     <para>
///         ⚠ <b>The difference from the layers panel is the model and not the panel.</b>
///         <c>LayerStackAsset</c> is a tree of records that <c>LayerStackEdit</c> replaces wholesale,
///         with no change event anywhere on it, so the only thing a layers row could follow was the
///         stack's depth. A <c>NodeGraphModel</c> is mutable and observable, and every graph command
///         in the tree ends in <c>Touch</c>. Two panels with the same shape and different answers,
///         which is why the issue asked for a test rather than the same one line.
///     </para>
///     <para>
///         ⚠ <b>These are kept rather than deleted with the issue.</b> Nothing anywhere states the
///         property they rest on — a command that reverted the model without telling anybody would
///         leave both panes stale and every existing test green, because every existing test reads
///         the model.
///     </para>
///     <para>
///         ⚠ <b>The notification is doubly redundant, which a sabotage has to know.</b> Taking
///         <c>Changed</c> out of <c>NodeGraphModel.Remove</c> leaves both of these green because
///         <c>NodeGraphCommand.Touch</c> raises it again afterwards; taking <c>Graph.Touch()</c> out
///         of that leaves them green because <c>Remove</c> already raised it. Only removing
///         <em>both</em> turns them red — and it turns them red on the undo assertions alone, with
///         the instrument halves above them still passing, which is what says these measure the
///         undo rather than the edit.
///     </para>
/// </remarks>
public class TextureGraphUndoTests {
    /// <summary>The panel these tests open, which is their own rather than the module's.</summary>
    const string Panel = "texturing.graph.undo-test";

    /// <summary>⚠ An undo taken outside the pane takes the node off the canvas, not just out of the model.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The canvas is read and the model is not, which is the whole point.</b> #933 survived
    ///         because every test drove a control and then asserted on the document; an assertion on
    ///         <c>canvas.Graph.Nodes</c> here would be an assertion about
    ///         <c>AddNodeCommand.Revert</c> and would pass against a pane that never redrew.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The instrument is the same lookup before the undo.</b> A search that could never
    ///         find the element would make the second half trivially true — which is the shape of
    ///         pass this repository calls a silent success — so the element is required to be there
    ///         first.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_undo_taken_outside_the_pane_reaches_the_canvas() {
        using var fixture = new TexturingFixture();
        var (_, canvas, document) = Opened(fixture);

        var add = new AddNodeCommand(document.Graph, "Filters/Blur", new(240f, 240f), document);

        document.Stack.Execute(add);
        Settle(fixture);

        var placed = add.Node.Id;

        Assert.True(OnCanvas(canvas, placed), "the node was never drawn, so this test can prove nothing.");

        Assert.True(document.Stack.Undo());
        fixture.Shell.Document.Effects.Flush();
        Settle(fixture);

        Assert.False(
            OnCanvas(canvas, placed),
            "the canvas is still drawing a node the document no longer has, which is #965 as filed."
        );
    }

    /// <summary>⚠ And the picture is told, so the pane beside the canvas catches up too.</summary>
    /// <remarks>
    ///     <b>The half that is not about the canvas at all.</b> <c>TextureGraphView.Edited</c> is what
    ///     the module compiles and re-evaluates on, so a canvas that reprojected without raising it
    ///     would leave the author looking at the picture their undone edit made. It is raised from
    ///     <c>NodeGraphView.OnGraphChanged</c>, which is the same notification the projection above
    ///     rides on — so this asserts the second consumer of it rather than a second mechanism.
    /// </remarks>
    [Fact]
    public void An_undo_taken_outside_the_pane_asks_for_a_new_picture() {
        using var fixture = new TexturingFixture();
        var (view, _, document) = Opened(fixture);

        var edits = 0;

        view.Edited = () => edits++;

        document.Stack.Execute(new AddNodeCommand(document.Graph, "Filters/Blur", new(240f, 240f), document));
        Settle(fixture);

        Assert.True(edits > 0, "the edit itself did not ask for a picture, so the wire is dead either way.");

        var made = edits;

        Assert.True(document.Stack.Undo());
        fixture.Shell.Document.Effects.Flush();
        Settle(fixture);

        Assert.True(edits > made, "the undo changed the graph and nothing asked for the picture to be redrawn.");
    }

    /// <summary>Whether the canvas is drawing an element for a node.</summary>
    /// <param name="canvas">The canvas.</param>
    /// <param name="node">The node's identity.</param>
    /// <returns>Whether an item on the canvas is tagged with it.</returns>
    static bool OnCanvas(NodeGraphView canvas, NodeId node) =>
        canvas.Canvas.Items.Any(item => item.Node?.Tag is NodeId tag && tag == node);

    /// <summary>Opens a docked panel with a graph on it, and the document that graph belongs to.</summary>
    /// <remarks>
    ///     ⚠ <b>A real <c>DockPanel</c> rather than a loose element</b>, for
    ///     <c>TextureGraphPanelTests.Opened</c>'s reason: a canvas outside a panel's layout measures
    ///     zero, and a canvas with no height draws no items — which would make
    ///     <see cref="OnCanvas" /> answer false throughout and the first assertion catch it.
    /// </remarks>
    static (TextureGraphView View, NodeGraphView Canvas, TextureGraphDocument Document) Opened(
        TexturingFixture fixture
    ) {
        TextureGraphView? built = null;

        fixture.Shell.RegisterPanel(
            Panel,
            new StringId("editor.panel." + Panel, "Texture Graph"),
            panel => built = new TextureGraphView(panel)
        );

        fixture.Shell.Workspace.Open(Panel);

        Assert.NotNull(built);

        var document = new TextureGraphDocument(
            fixture.Project,
            fixture.AddGraph("Bricks"),
            fixture.Paths.Absolute("Assets/Bricks" + TextureGraphDocument.Extension)
        );

        built.Show(document, TexturePreviewBlocker.NoDevice);

        Settle(fixture);

        return (built, built.Canvas, document);
    }

    static void Settle(TexturingFixture fixture) {
        fixture.Shell.Document.Update();
        fixture.Shell.Document.Draw();
    }
}
