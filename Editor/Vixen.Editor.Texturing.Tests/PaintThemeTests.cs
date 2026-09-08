// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Texturing.Painting;
using Vixen.Ui;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>The two paint panes and the brush column get their layout from the stylesheet.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/1106">#1106</a>.</b>
///         <c>PaintMeshView</c>, <c>PaintUvView</c> and <c>PaintBrushInspector</c> each styled their
///         own host with three <c>SetStyle</c> calls, because <c>TexturingTheme.vcss</c> named none
///         of their tags. ⚠ <b>The issue says the 2D pane's status line was already themed and it
///         was not</b> — no stylesheet in this repository named a <c>paint-</c> anything — so it was
///         one gap across three files rather than the two the issue describes.
///     </para>
///     <para>
///         ⚠ <b>Read as geometry after a layout pass, which is <c>LayerStackThemeTests</c>' whole
///         argument and is what makes these assertions able to fail.</b> An element no sheet
///         mentions takes CSS's initial <c>flex-direction: row</c>, so a test asserting
///         <c>paint-mesh { flex-direction: row }</c> would pass with the sheet deleted. What the
///         sheet decides here is the <em>column</em> — the title above the picture and the sentence
///         under it — and the height the viewer inside gets from having a parent that grew.
///     </para>
/// </remarks>
public class PaintThemeTests {
    /// <summary>⚠ The 3D pane stacks its title, picture and sentence, and the picture has a height.</summary>
    [Fact]
    public void The_sheet_reaches_the_3d_pane() {
        using var fixture = new TexturingFixture(graphics: true);

        Open(fixture);

        var panel = fixture.Shell.Workspace.Open(TexturingModule.MeshPanel);

        Assert.NotNull(panel);

        Lay(fixture);

        var title = Only(panel, "world-title");
        var status = Only(panel, "paint-mesh-status");
        var bar = Only(panel, "paint-mesh-bar");
        var image = Assert.IsType<ImageView>(Only(panel, "image-view"));

        // ⚠ The declaration with no initial value behind it. CSS's initial direction is `row`, which
        // would put the title, the picker strip, the picture and the sentence side by side at the
        // same top — so this is false in exactly the way a missing sheet makes it false rather than
        // being true of any laid-out pane.
        Assert.True(
            title.Top <= bar.Top && bar.Top < status.Top,
            $"the title is at y={title.Top}, the picker strip at y={bar.Top} and the sentence at "
            + $"y={status.Top}: the pane is laying its children out in a row, which is CSS's initial "
            + "direction and what `paint-mesh { flex-direction: column }` in TexturingTheme.vcss is for."
        );

        // ⚠ And they start at the same left edge, which is the other thing a row could not do: four
        // children laid out in a row are at four different x and one y, and this is the reverse.
        Assert.Equal(title.Left, status.Left);
        Assert.Equal(bar.Left, status.Left);

        // ⚠ And the other half of #886's shape: `image-view { flex-grow: 1 }` in AdvancedTheme.vcss
        // can only grow inside a parent that has room, so a root with no `flex-grow` of its own
        // collapses to its content and the viewer gets no height at all. A pane that is a strip is
        // the symptom, and it is not an error anywhere.
        Assert.True(image.Height > 100f, $"the viewer is {image.Height} tall, so the pane collapsed to a strip.");
    }

    /// <summary>⚠ The 2D pane is in the same shape, which is the half the issue said was already done.</summary>
    [Fact]
    public void The_sheet_reaches_the_2d_pane() {
        using var fixture = new TexturingFixture(graphics: true);

        Open(fixture);

        var panel = fixture.Shell.Workspace.Open(TexturingModule.PaintPanel);

        Assert.NotNull(panel);

        Lay(fixture);

        var title = Only(panel, "world-title");
        var status = Only(panel, "paint-uv-status");
        var image = Assert.IsType<ImageView>(Only(panel, "image-view"));

        Assert.True(
            title.Top < status.Top,
            $"the title is at y={title.Top} and the sentence at y={status.Top}: `paint-uv "
            + "{ flex-direction: column }` did not reach this pane."
        );

        Assert.Equal(title.Left, status.Left);
        Assert.True(image.Height > 100f, $"the viewer is {image.Height} tall, so the pane collapsed to a strip.");
    }

    /// <summary>⚠ The brush column is 220 wide, beside the layers rather than over them.</summary>
    /// <remarks>
    ///     A width, which is the one kind of declaration that has no plausible initial value behind
    ///     it — <c>layer-stack-preview</c>'s 280 is asserted next door for the same reason. Without
    ///     the rule this column is as wide as its widest control.
    /// </remarks>
    [Fact]
    public void The_sheet_reaches_the_brush_column() {
        using var fixture = new TexturingFixture(graphics: true);

        Open(fixture);

        var panel = fixture.Shell.Workspace.Open(TexturingModule.StackPanel);

        Assert.NotNull(panel);

        Lay(fixture);

        var brush = Only(panel, "paint-brush");
        var summary = Only(panel, "paint-brush-summary");

        Assert.Equal(220f, brush.Width);

        // The instrument: an empty column is 220 wide too, and would say nothing about the rule
        // having reached anything an artist can see.
        Assert.True(summary.Width > 0f, "the brush column drew nothing, so the width above is vacuous.");
    }

    /// <summary>⚠ A pane built on its own gets the sheet, with no layers panel in the document.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The finding that moving the layout into the sheet turned up, and it was a real
    ///         defect rather than a test artefact.</b> <c>TexturingTheme.Install</c> had exactly one
    ///         production caller — <c>LayerStackView</c>'s constructor — which was invisible for as
    ///         long as the paint panes styled their own hosts inline, and became "the pane is a
    ///         strip" the moment they stopped. A docked workspace restoring the paint pane without
    ///         the layers panel is a state an artist reaches by closing one panel and quitting.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Built directly rather than through the module, which is the only way to say
    ///         it.</b> Opening the pane through <c>TexturingModule</c> opens the layers panel too, so
    ///         a case that went that way would install the sheet by the side door and pass against a
    ///         view that asked for nothing.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_pane_built_without_the_layers_panel_still_gets_the_sheet() {
        using var fixture = new TexturingFixture();

        var host = fixture.Shell.Document.Root.Add<UiElement>();

        _ = new PaintUvView(host, new PaintTool());

        Lay(fixture);

        var title = Only(host, "world-title");
        var status = Only(host, "paint-uv-status");

        Assert.True(
            title.Top < status.Top,
            $"the title is at y={title.Top} and the sentence at y={status.Top}: this pane took CSS's "
            + "initial `flex-direction: row`, so TexturingTheme.vcss is not in this document at all."
        );
    }

    /// <summary>Activates the module on a stack with the paint verb on.</summary>
    /// <param name="fixture">The host.</param>
    static void Open(TexturingFixture fixture) {
        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(LayerStackPanelTests.AddStack(fixture, "Hull"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));
        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.PaintCommand));
    }

    /// <summary>Lays the workspace out and draws it, so geometry can be read off it.</summary>
    /// <param name="fixture">The host.</param>
    static void Lay(TexturingFixture fixture) {
        fixture.Shell.Document.Update();
        fixture.Shell.Document.Draw();
    }

    /// <summary>The only element under that name — its tag, or its class.</summary>
    /// <param name="root">Where to look.</param>
    /// <param name="tag">What to look for.</param>
    /// <returns>The element.</returns>
    static UiElement Only(UiElement root, string tag) {
        List<UiElement> found = [];

        Walk(root);

        return Assert.Single(found);

        void Walk(UiElement element) {
            if (string.Equals(element.Tag, tag, StringComparison.Ordinal) || element.HasClass(tag)) {
                found.Add(element);
            }

            foreach (var child in element.Children) {
                Walk(child);
            }
        }
    }
}
