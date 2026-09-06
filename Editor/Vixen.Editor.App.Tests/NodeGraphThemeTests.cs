// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.AssetEditors.Shading;
using Vixen.Editor.Testing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The graph canvas, measured in the editor somebody actually runs.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>NodeGraphTheme.Install</c> had no editor caller at all</b>
///         (<a href="https://github.com/Rikarin/Vixen/issues/917">#917</a>). One test fixture called
///         it, so every rule in the sheet was exercised by the assembly's own suite and by nothing
///         that ships — the shape this workstream keeps finding, a finished thing nothing calls, and
///         the reason the finding was a *layout* one rather than a colour one: three of the four
///         graph panels were borrowing <c>flex-grow</c> from an
///         <c>&lt;x&gt;-editor &gt; node-graph</c> rule in <c>AssetEditorTheme.vcss</c>, one rule
///         each, and the plugin's texture graph had none and measured zero pixels high.
///     </para>
///     <para>
///         ⚠ <b>Measured through <see cref="EditorSession" />, which builds a real
///         <c>EditorApplication</c>.</b> A test over a bare <c>EditorShell</c> — or over the
///         assembly's own <c>ViewFixture</c>, which installs the sheet itself — would pass in an
///         editor where the install was never made, which is precisely the state this closes. What is
///         asserted is the consequence rather than the call: a canvas with a height, and a preview
///         layer that is out of flow and takes no clicks.
///     </para>
///     <para>
///         ⚠ <b>"Tests pass" is not evidence for a visual defect, so the assertions are the
///         numbers.</b> The canvas is a box whose height is the room the column leaves it; the
///         preview layer's <c>position</c> and <c>pointer-events</c> are what decide whether a click
///         reaches a node. Each is false without the sheet and each says which rule is missing.
///     </para>
/// </remarks>
public class NodeGraphThemeTests {
    /// <summary>⚠ A graph panel in the shipping editor is styled by the sheet its assembly ships.</summary>
    /// <remarks>
    ///     <para>
    ///         The shader graph rather than the texture graph, because it is in the editor's own
    ///         assemblies and needs no plugin to be loaded — and because it is one of the three that
    ///         was surviving on a borrowed rule, so it is the panel where the sheet changes the least
    ///         and a false green is hardest to get.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>"The canvas has a height" is not one of the things the sheet buys here, and
    ///         measuring that first is how this test was nearly a tautology.</b> Without the sheet
    ///         <c>node-graph</c> takes CSS's initial <c>flex-direction: row</c>, and a row's child
    ///         stretches on the cross axis — so the canvas filled the panel's height by accident, and
    ///         a test asserting only the height passes with the install deleted. What the sheet
    ///         actually decides for this panel is the direction, the clipping and the containing
    ///         block; the height is asserted after them, as the consequence rather than the evidence.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_sheets_rules_reach_a_graph_panel_in_a_real_editor() {
        using var fixture = EditorSession.Start();

        fixture.Run("assets.create-shader-graph").Settle();

        // The panel is open because creating opens it — see `ShaderGraphSurfaceTests`.
        Assert.NotEmpty(fixture.Project.Documents.OfType<ShaderGraphDocument>());

        var graph = fixture.Ui.Get("node-graph");

        Assert.Equal(1, graph.Count);

        var canvas = graph.Find("node-canvas");

        Assert.Equal(1, canvas.Count);

        // ⚠ The three declarations no `<x>-editor` rule carries, read as computed style: the view
        // stacks rather than lays its children out in a row, it clips, and it is the containing
        // block its absolutely positioned children are pinned to.
        Assert.Equal("column", fixture.Ui.StyleOf(graph.Element, "flex-direction"));
        Assert.Equal("hidden", fixture.Ui.StyleOf(graph.Element, "overflow"));
        Assert.Equal("relative", fixture.Ui.StyleOf(graph.Element, "position"));

        // And the canvas is the whole of the view's height, which is `node-graph > node-canvas
        // { flex-grow: 1 }` doing its job in that column.
        Assert.True(
            canvas.Element.Bounds.Height > 100f,
            $"the canvas is {canvas.Element.Bounds.Width}x{canvas.Element.Bounds.Height}, so there is "
            + "nothing on it to click."
        );

        Assert.Equal(graph.Element.Bounds.Height, canvas.Element.Bounds.Height);
    }

    /// <summary>⚠ And it has a width, which the height above says nothing about.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The canvas was 0×796 in the shipping editor and the suite above was green</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/939">#939</a>. Height and width fail
    ///         for opposite reasons here: the height is what a column hands its growing child, and
    ///         the width is what is left of the panel after a fixed side strip, so a test that
    ///         measured one of them measured the half that could not go wrong.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What was wrong was where the panel docked, not the stylesheet.</b>
    ///         <c>DockingHost.Rekey</c> puts a panel the arrangement does not name into the
    ///         <em>first</em> group, which in every <c>LayoutPresets.Standard</c> preset is the left
    ///         browser at <c>0.2</c> of the width — 320 px, less than <c>shadergraph-side</c>'s own
    ///         300 px column, so the graph was the child that shrank and <c>min-width: 0</c> let it
    ///         shrink to nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The threshold is the side strip's width and not a round number.</b> A canvas
    ///         narrower than the fixed column beside it is a panel docked somewhere a document does
    ///         not belong; anything wider is a real graph however the window is sized. Asserting a
    ///         specific 609 would be asserting the preset's ratios.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_graph_canvas_is_wider_than_the_fixed_column_beside_it() {
        using var fixture = EditorSession.Start();

        fixture.Run("assets.create-shader-graph").Settle();

        var canvas = fixture.Ui.Get("node-canvas").Element;
        var side = fixture.Ui.Get("shadergraph-side").Element;

        Assert.True(side.Bounds.Width > 0f, "the side column is not laid out, so there is nothing to compare against.");

        Assert.True(
            canvas.Bounds.Width > side.Bounds.Width,
            $"the graph canvas is {canvas.Bounds.Width}x{canvas.Bounds.Height} beside a "
            + $"{side.Bounds.Width}px side column, so the panel is a strip of fields with no graph in it."
        );
    }

    /// <summary>⚠ And the preview layer is out of flow and takes no clicks.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The half a height cannot cover.</b> <c>node-previews</c> covers the whole canvas,
    ///         and its own comment says a full-canvas element that swallowed presses would make every
    ///         node unreachable — so in the editor that ran without this sheet, it was an in-flow
    ///         element sitting above the nodes and eating the pointer. The <c>flex-grow</c> the three
    ///         panels borrowed from <c>AssetEditorTheme</c> did nothing about it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Read as computed style rather than as the sheet's text</b>, because the question
    ///         is what the cascade decided for this element in this document and not what some sheet
    ///         says. A rule that lost to another one would read as the loser's value here.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_preview_layer_is_out_of_flow_and_takes_no_clicks() {
        using var fixture = EditorSession.Start();

        fixture.Run("assets.create-shader-graph").Settle();

        var previews = fixture.Ui.Get("node-previews");

        Assert.Equal(1, previews.Count);
        Assert.Equal("absolute", fixture.Ui.StyleOf(previews.Element, "position"));
        Assert.Equal("none", fixture.Ui.StyleOf(previews.Element, "pointer-events"));

        // And the element it is pinned to is the one it is inside, which is what `position: relative`
        // on `node-graph` buys and nothing else in the editor declares.
        Assert.Equal("relative", fixture.Ui.StyleOf(fixture.Ui.Get("node-graph").Element, "position"));
    }
}
