// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Layout;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>Per-axis <c>overflow</c>, and what <c>auto</c> means to a layout that cannot scroll.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>All of this was a silent no-op.</b> <c>overflow-x</c> and <c>overflow-y</c> were
///         emitted by the utility generator and interned by nobody, so <c>overflow-y-auto</c>
///         resolved cleanly and did nothing; <c>overflow: auto</c> clipped in the draw list — which
///         tests anything that is not <c>visible</c> — and stayed <c>Visible</c> to the layout, whose
///         keyword table listed only the other three. A style that resolves and then does nothing is
///         worse than one that fails, because there is nothing to notice.
///     </para>
///     <para>
///         The two halves are tested separately because they fail separately: the clip is a draw-list
///         and hit-test question, the scroll container is a flexbox one.
///     </para>
/// </remarks>
public class OverflowTests {
    const float Tolerance = 0.001f;
    static readonly FontFace Font = LoadFont();

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Tests.Fonts.TestShapeLana.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "TestShapeLana");
    }

    static UiDocument Drawn(string css, Action<UiDocument>? build = null) {
        var document = new UiDocument(400f, 300f);
        document.Load(css);
        build?.Invoke(document);
        document.Update();
        document.Draw();
        return document;
    }

    static DrawCommand Push(UiDocument document) =>
        Assert.Single(document.Drawing.Commands, command => command.Kind == DrawCommandKind.ClipPush);

    [Fact]
    public void Overflow_y_hidden_clips_the_vertical_axis_and_leaves_the_horizontal_one_alone() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .clip { width: 80px; height: 80px; overflow-y: hidden; background-color: #111; }
            """,
            document => document.Root.Add("div", classNames: "clip")
        );

        var push = Push(document);

        // ⚠ The unclipped axis is a pair of edges at infinity, written as a number large enough that
        // the intersection with the viewport is what bounds it. See `DrawListBuilder.UnboundedClip`.
        Assert.Equal(80f, push.Height, Tolerance);
        Assert.Equal(0f, push.Y, Tolerance);
        Assert.Equal(-DrawListBuilder.UnboundedClip, push.X, Tolerance);
        Assert.Equal(2f * DrawListBuilder.UnboundedClip, push.Width, Tolerance);
    }

    [Fact]
    public void Overflow_x_hidden_clips_the_horizontal_axis_and_leaves_the_vertical_one_alone() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .clip { width: 80px; height: 80px; overflow-x: hidden; background-color: #111; }
            """,
            document => document.Root.Add("div", classNames: "clip")
        );

        var push = Push(document);

        Assert.Equal(80f, push.Width, Tolerance);
        Assert.Equal(0f, push.X, Tolerance);
        Assert.Equal(-DrawListBuilder.UnboundedClip, push.Y, Tolerance);
        Assert.Equal(2f * DrawListBuilder.UnboundedClip, push.Height, Tolerance);
    }

    [Fact]
    public void Both_axes_clipped_separately_is_the_same_rectangle_as_the_shorthand() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .clip { width: 80px; height: 60px; overflow-x: hidden; overflow-y: scroll; background-color: #111; }
            """,
            document => document.Root.Add("div", classNames: "clip")
        );

        var push = Push(document);

        Assert.Equal(0f, push.X, Tolerance);
        Assert.Equal(0f, push.Y, Tolerance);
        Assert.Equal(80f, push.Width, Tolerance);
        Assert.Equal(60f, push.Height, Tolerance);
    }

    [Fact]
    public void A_longhand_beats_the_shorthand_on_its_own_axis_and_nowhere_else() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .clip { width: 80px; height: 60px; overflow: hidden; overflow-y: visible; background-color: #111; }
            """,
            document => document.Root.Add("div", classNames: "clip")
        );

        var push = Push(document);

        Assert.Equal(80f, push.Width, Tolerance);
        Assert.Equal(2f * DrawListBuilder.UnboundedClip, push.Height, Tolerance);
    }

    [Fact]
    public void Both_axes_visible_pushes_no_clip_at_all() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .clip { width: 80px; height: 60px; overflow-x: visible; overflow-y: visible; background-color: #111; }
            """,
            document => document.Root.Add("div", classNames: "clip")
        );

        Assert.DoesNotContain(document.Drawing.Commands, command => command.Kind == DrawCommandKind.ClipPush);
    }

    [Fact]
    public void A_one_axis_clip_only_takes_the_clicks_on_the_axis_it_clips() {
        var document = new UiDocument(400f, 300f);
        document.Load("""
            root { width: 400px; height: 300px; }
            .clip { width: 20px; height: 20px; overflow-y: hidden; }
            .right { position: absolute; left: 100px; top: 0px; width: 40px; height: 20px; }
            .below { position: absolute; left: 0px; top: 100px; width: 20px; height: 40px; }
        """);

        var parent = document.Root.Add("div", classNames: "clip");
        var right = parent.Add("div", classNames: "right");
        parent.Add("div", classNames: "below");

        document.Update();

        // ⚠ Hit testing and the clip stack have to agree, and a clip that cut one axis in the picture
        // while cutting both for the pointer would be a control you can see and cannot press.
        Assert.Same(right, document.HitTest(110f, 10f));
        Assert.Same(document.Root, document.HitTest(10f, 110f));

        document.Dispose();
    }

    [Fact]
    public void Auto_is_a_scroll_container_to_the_layout() {
        var fixture = new BridgeFixture();

        // ⚠ `auto` and `scroll` differ in CSS only by whether the scrollbar gutter is always there,
        // and nothing here draws a scrollbar of its own — so they are one layout mode, not two.
        Assert.Equal(Overflow.Scroll, fixture.Build("overflow: auto").OverflowX);
        Assert.Equal(Overflow.Scroll, fixture.Build("overflow: auto").OverflowY);
    }

    [Fact]
    public void A_per_axis_keyword_reaches_the_layout_on_that_axis_only() {
        var fixture = new BridgeFixture();
        var style = fixture.Build("overflow-y: auto");

        Assert.Equal(Overflow.Visible, style.OverflowX);
        Assert.Equal(Overflow.Scroll, style.OverflowY);
    }

    [Fact]
    public void The_shorthand_still_sets_both_axes() {
        var style = new BridgeFixture().Build("overflow: hidden");

        Assert.Equal(Overflow.Hidden, style.OverflowX);
        Assert.Equal(Overflow.Hidden, style.OverflowY);
    }

    [Fact]
    public void Auto_lets_a_flex_item_shrink_past_the_size_its_content_needs() {
        // ⚠ This is the half of `overflow` that is not a clip. CSS Flexbox §4.5 gives every flex item
        // a content-sized floor it cannot shrink below — the rule that stops a row of text from being
        // squeezed to one character — and an item that handles its own overflow opts out of it. While
        // `auto` was a keyword the bridge did not know, every `overflow: auto` panel in the editor
        // kept the floor: it refused to shrink, pushed its neighbours out of the pane, and the draw
        // list clipped the result at the *window* rather than at the panel.
        Assert.True(LabelWidth("") > 100f, "without an opt-out the item stops at its content");
        Assert.Equal(100f, LabelWidth("overflow: auto;"), Tolerance);
    }

    [Fact]
    public void A_vertical_keyword_does_not_opt_a_row_item_out_of_its_width_floor() {
        // §4.5 is about the *main* axis, and the container here is a row. `overflow-y` says nothing
        // about what happens across the item, so the floor stands — where a browser would have
        // computed the `overflow-x: visible` beside it to `auto` and dropped the floor anyway.
        Assert.True(LabelWidth("overflow-y: auto;") > 100f, "the vertical axis is not the row's main axis");
        Assert.Equal(100f, LabelWidth("overflow-x: auto;"), Tolerance);
    }

    /// <summary>A shrinking label in a hundred-point row, whose text is wider than that.</summary>
    static float LabelWidth(string declarations) {
        var document = new UiDocument(400f, 300f);
        document.Fonts.Register("Test", Font);
        document.Load($$"""
            root { width: 100px; height: 50px; flex-direction: row; align-items: flex-start; }
            label { flex-shrink: 1; white-space: nowrap; {{declarations}} }
        """);

        var label = document.Root.Add("label");
        label.Text = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        document.Update();

        var width = label.Width;
        document.Dispose();

        return width;
    }
}
