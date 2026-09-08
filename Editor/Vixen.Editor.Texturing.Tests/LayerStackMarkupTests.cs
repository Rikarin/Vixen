// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Editor.Texturing.Layers;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>The panel's tree is <c>LayerStackChrome.vxml</c>, and its one binding follows the model.</summary>
/// <remarks>
///     <para>
///         <b>The second half of <a href="https://github.com/Rikarin/Vixen/issues/881">#881</a>, as
///         far as it went.</b> <c>TexturingTheme.vcss</c> took the panel's flex boxes in batch 18;
///         this is the markup, for the part of the panel that is a fixed tree. The rows are still
///         written from C# and the issue's own comments say why — a <c>@for</c> over the layers is a
///         model change first, because <c>LayerStackView.Shape</c> rebuilds a row only when its shape
///         signature changes and the key rule then wants a <c>Signal&lt;LayerAsset&gt;</c> per row.
///     </para>
///     <para>
///         ⚠ <b>What is asserted here is the binding rather than the file.</b> A test that found a
///         <c>layer-stack-messages</c> element would pass against the C# that built one for sixteen
///         batches; what only the markup can do is grow and shrink the block from an assignment, and
///         show and hide it from a class the stylesheet has both states of. Both directions are
///         asserted, because a region that never rebuilt would be right the first time.
///     </para>
/// </remarks>
public class LayerStackMarkupTests {
    /// <summary>Where this file puts a view of its own, beside the module's.</summary>
    const string ViewPanel = "texturing.tests.layer-stack-markup";

    /// <summary>⚠ The block grows, is laid out, shrinks again and goes back to occupying nothing.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Four states in one panel, because each of the two halves is satisfied by half a
    ///         port.</b> A <c>@for</c> whose region was built once and never re-run draws the first
    ///         list for ever, and is green against any single reading; a class binding that was
    ///         written once is green against the same. So the document is put in, taken out and put
    ///         back, and the assertions alternate.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Geometry rather than the class, for <c>LayerStackThemeTests</c>' reason.</b>
    ///         Asserting <c>HasClass("shown")</c> would assert that markup wrote a class, which is
    ///         true whether or not <c>TexturingTheme.vcss</c> has a rule for it — and the rule is the
    ///         half that replaced the <c>SetStyle</c>. A <c>display: none</c> block occupies no box
    ///         at all; a shown one is stretched to its column. Deleting
    ///         <c>layer-stack-messages.shown</c> from the sheet leaves the second assertion red,
    ///         which is how it was confirmed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The width and emphatically not the height, and the reason is a property of the
    ///         test host rather than of the panel.</b> Nothing in this fixture measures text: the
    ///         legend — one element with its <c>Text</c> assigned in C#, untouched by this port —
    ///         reports a height of zero beside a message block reporting the same, so a height
    ///         assertion here cannot tell a hidden block from a shown one and would be green in both
    ///         directions. A hidden element is not stretched by its column and a shown one is, so
    ///         the width is the measurement that differs.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_message_block_follows_the_document_in_both_directions() {
        using var fixture = new TexturingFixture();

        // ⚠ A file the parser refuses, because a load diagnostic is the one message that needs
        // neither a device nor a compile — `LayerStackDocument` answers the empty file with the
        // starter stack before the parser is reached at all, so every other input here says nothing.
        var document = Open(
            fixture,
            "Future",
            "version: 1\nname: Future\nsets:\n  - name: S\n    layers:\n      - id: l\n        kind: Fill\n"
            + "        blend: Hologram\n"
        );

        // The instrument first: a fixture whose YAML happened to parse would leave every assertion
        // below about an empty list, which is the state the panel starts in anyway.
        Assert.Single(document.LoadDiagnostics);

        var view = Viewed(fixture);
        var block = Only(view.Root, "layer-stack-messages");

        view.Show(null);
        Settle(fixture);

        Assert.Empty(block.Children);
        Assert.Equal(0f, block.Width);

        view.Show(document);
        Settle(fixture);

        var said = Assert.Single(block.Children);

        Assert.Contains(
            TexturingDiagnostics.StackFileDoesNotParse,
            LayerStackPanelTests.Said(said),
            StringComparison.Ordinal
        );

        Assert.True(
            block.Width > 0f,
            "the message block occupies no box with a message in it, so `layer-stack-messages.shown` "
            + "in TexturingTheme.vcss did not reach it — the block is still `display: none` and the "
            + "diagnostic is on the tree and off the screen, which is exactly what #830 closed."
        );

        view.Show(null);
        Settle(fixture);

        Assert.Empty(block.Children);
        Assert.Equal(0f, block.Width);

        // ⚠ **The state neither assertion above reaches, and it is the only one that tests the
        // class.** `Show(null)` puts `display: none` on the panel's whole *root*, so a block whose
        // own `shown` class was stuck on measures zero anyway — both hidden assertions above are
        // entailed by the root and say nothing about `layer-stack-messages`. A document that loads
        // cleanly is the arrangement where the root is visible and the block must not be.
        var clean = new LayerStackDocument(
            fixture.Project,
            LayerStackPanelTests.AddStack(fixture, "Plain"),
            Path.Combine(fixture.Paths.Assets, "Plain" + LayerStackDocument.Extension)
        );

        Assert.Empty(clean.LoadDiagnostics);

        view.Show(clean);
        Settle(fixture);

        Assert.True(view.Root.Width > 0f, "the panel is hidden, so the block below proves nothing.");
        Assert.Empty(block.Children);

        Assert.Equal(0f, block.Width);
    }

    /// <summary>⚠ The markup's element <em>is</em> the panel's, rather than a box inside one.</summary>
    /// <remarks>
    ///     <b>What <c>@inherits Vixen.Ui.UiElement</c> buys, and the thing a port to a
    ///     <c>Component</c> would have quietly changed.</b> A component mounts inside a host element
    ///     of its own, so every rule in <c>TexturingTheme.vcss</c> and every geometry assertion in
    ///     <c>LayerStackThemeTests</c> would have been one level out. Asserting the type of the
    ///     element the sheet selects is what says the tree did not gain a level: it is the markup's
    ///     class, it answers to <c>layer-stack</c>, and it is the panel's own child.
    /// </remarks>
    [Fact]
    public void The_panels_layer_stack_element_is_the_markup_component() {
        using var fixture = new TexturingFixture();

        var view = Viewed(fixture);
        var element = Only(view.Root, "layer-stack");

        Assert.IsType<LayerStackChrome>(element);
        Assert.Same(view.Root, element);
        Assert.NotSame(view.Root, view.Empty);
        Assert.Same(view.Root.Parent, view.Empty.Parent);
    }

    /// <summary>Opens a stack whose bytes are given, through the module's own verb.</summary>
    static LayerStackDocument Open(TexturingFixture fixture, string name, string contents) {
        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(LayerStackPanelTests.AddStack(fixture, name, contents));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        return Assert.IsType<LayerStackDocument>(fixture.Project.Documents.Single());
    }

    /// <summary>A view of this file's own, in a panel of its own.</summary>
    /// <remarks>
    ///     ⚠ <b>Its own rather than the module's, because these assertions drive <c>Show</c>
    ///     directly.</b> The module refreshes its view from its own document and its own evaluator;
    ///     what is being asserted is that one assignment reaches the tree, which needs the two calls
    ///     to be this test's.
    /// </remarks>
    static LayerStackView Viewed(TexturingFixture fixture) {
        LayerStackView? built = null;

        fixture.Shell.RegisterPanel(
            ViewPanel,
            new StringId("editor.panel." + ViewPanel, "Layers"),
            panel => built = new LayerStackView(panel)
        );

        fixture.Shell.Workspace.Open(ViewPanel);

        Assert.NotNull(built);

        return built;
    }

    /// <summary>Runs the frame, which is what drains the effects a markup binding queues.</summary>
    /// <remarks>
    ///     ⚠ <b>A real pass rather than <c>Effects.Flush</c>, because half of what is asserted is
    ///     geometry.</b> <c>UiDocument.Update</c> drains the queue before its first pass — which is
    ///     the ordering that makes a bound panel correct in the frame the assignment happened in —
    ///     and the layout that follows is what turns <c>display: none</c> into a height of zero.
    /// </remarks>
    static void Settle(TexturingFixture fixture) {
        fixture.Shell.Document.Update();
        fixture.Shell.Document.Draw();
    }

    /// <summary>The only element under that name — its tag, or its class.</summary>
    static UiElement Only(UiElement root, string name) {
        List<UiElement> found = [];

        Walk(root);

        return Assert.Single(found);

        void Walk(UiElement element) {
            if (string.Equals(element.Tag, name, StringComparison.Ordinal) || element.HasClass(name)) {
                found.Add(element);
            }

            foreach (var child in element.Children) {
                Walk(child);
            }
        }
    }
}
