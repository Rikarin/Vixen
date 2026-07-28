// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>
///     What every virtualiser in this assembly used to need a caller for: noticing that its own box
///     changed size.
/// </summary>
/// <remarks>
///     ⚠ <b>Not one of these tests calls <c>Refresh</c>.</b> That is the whole assertion. Each of
///     them resizes the document, runs a pass, and checks that the control realised against the new
///     size — which is what <c>Control.WhenResized</c> over
///     <see cref="UiDocument.LayoutFinished" /> is for, and what the README used to record as owed.
/// </remarks>
public class ResizeTests {
    /// <summary>A document whose root fills whatever the viewport is, so a resize reaches the control.</summary>
    /// <remarks>
    ///     The stock fixture pins the root with a rule in pixels, which survives
    ///     <see cref="UiDocument.Resize" /> and would make every test here resize nothing.
    /// </remarks>
    static AdvancedFixture Fluid(float width, float height) =>
        new(width, height, "root { width: 100%; height: 100%; }");

    [Fact]
    public void A_tree_realises_more_rows_when_it_is_made_taller() {
        using var fixture = Fluid(400f, 120f);

        var tree = fixture.Add<TreeView>();

        for (var index = 0; index < 200; index++) {
            tree.Root.Add("node" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        tree.Refresh();
        fixture.Update();

        var cramped = tree.Rows.Count;

        fixture.Document.Resize(400f, 600f);
        fixture.Update();

        Assert.True(tree.Rows.Count > cramped, $"realised {tree.Rows.Count} rows, was {cramped}");
    }

    [Fact]
    public void A_canvas_realises_the_nodes_a_bigger_viewport_reaches() {
        using var fixture = Fluid(300f, 300f);

        var canvas = fixture.Add<NodeCanvas>();
        var graph = new NodeGraph();

        // A column of nodes far taller than the small viewport, so growing it reaches more of them.
        for (var index = 0; index < 40; index++) {
            graph.AddNode("n" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), new Vector2(0f, index * 120f));
        }

        canvas.Graph = graph;
        canvas.Refresh();
        fixture.Update();

        var cramped = canvas.Visible.Count;

        fixture.Document.Resize(300f, 1200f);
        fixture.Update();

        Assert.True(canvas.Visible.Count > cramped, $"realised {canvas.Visible.Count} nodes, was {cramped}");
    }

    [Fact]
    public void A_viewport_asks_for_a_new_render_target_when_its_box_changes() {
        using var fixture = Fluid(400f, 300f);

        var viewport = fixture.Add<Viewport>();

        fixture.Update();

        var resizes = 0;
        viewport.Resized += _ => resizes++;

        fixture.Document.Resize(800f, 600f);
        fixture.Update();

        // ⚠ Without anybody calling Refresh. A host that had to remember was a host that forgot on
        // the frame a splitter settled, and the failure is a viewport rendering at the old size.
        Assert.Equal(1, resizes);
        Assert.Equal(800, viewport.RenderWidth);
    }

    [Fact]
    public void A_frame_in_which_nothing_resized_costs_nothing() {
        using var fixture = Fluid(400f, 300f);

        var editor = fixture.Add<CodeEditor>();

        editor.Buffer.Text = "one\ntwo\nthree";
        fixture.Update();

        var realised = editor.Rows.Count;

        // Several passes with no resize in them. The gate is two float comparisons, which matters
        // because CodeEditor.Refresh walks every line in the buffer.
        for (var frame = 0; frame < 5; frame++) {
            fixture.Document.Invalidate();
            fixture.Update();
        }

        Assert.Equal(realised, editor.Rows.Count);
        Assert.True(fixture.Document.Settled);
        Assert.Equal(0, fixture.Document.SettlingPasses);
    }

    [Fact]
    public void A_resize_settles_within_the_budget() {
        using var fixture = Fluid(400f, 200f);

        var tree = fixture.Add<TreeView>();

        for (var index = 0; index < 500; index++) {
            tree.Root.Add("node" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        tree.Refresh();
        fixture.Update();

        fixture.Document.Resize(400f, 900f);
        fixture.Update();

        // ⚠ Settled, not merely finished. A virtualiser that asked for another pass every time it
        // was called would exhaust the budget and report false — a frame drawn one pass stale, for
        // ever, and the only sign of it is this flag.
        Assert.True(fixture.Document.Settled);
    }

    [Fact]
    public void A_removed_control_stops_being_asked() {
        using var fixture = Fluid(400f, 300f);

        var canvas = fixture.Add<NodeCanvas>();

        canvas.Graph = new NodeGraph();
        fixture.Update();

        fixture.Document.Remove(canvas);
        fixture.Update();

        // A handler left subscribed holds the control alive and realises into a removed tree. The
        // resize is what would have run it.
        fixture.Document.Resize(900f, 700f);
        fixture.Update();

        Assert.True(canvas.IsRemoved);
    }
}
