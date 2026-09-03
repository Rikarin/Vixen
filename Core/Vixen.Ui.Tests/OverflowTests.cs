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
    public void A_scroll_container_reserves_its_scrollbar_out_of_the_room_its_children_get() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .port { display: flex; flex-direction: column; width: 70px; height: 50px;
                    overflow: scroll; scrollbar-width: 10px; }
            .fill { height: 8px; }
            """,
            document => document.Root.Add("div", classNames: "port").Add("div", classNames: "fill")
        );

        var stretched = document.Root.Children[0].Children[0];

        // The bar is 10 points down the right-hand side, so a stretched child gets 60, not 70.
        Assert.Equal(60f, stretched.Width, Tolerance);
    }

    [Fact]
    public void The_gutter_is_inert_where_there_is_no_scrollbar_to_reserve_it_for() {
        // ⚠ <b>The half of this property that is not a gap.</b> `hidden` clips without drawing a
        // bar, so `scrollbar-width` beside it cannot move a box — which is why 156 of the Taffy
        // corpus's 336 declarations are correctly ignored rather than refused, and why a utility
        // layer can set `scrollbar-auto` broadly without moving anything that does not scroll.
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .port { display: flex; flex-direction: column; width: 70px; height: 50px;
                    overflow: hidden; scrollbar-width: 10px; }
            .fill { height: 8px; }
            """,
            document => document.Root.Add("div", classNames: "port").Add("div", classNames: "fill")
        );

        Assert.Equal(70f, document.Root.Children[0].Children[0].Width, Tolerance);
    }

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

    [Fact]
    public void Clip_reaches_the_layout_and_reaches_it_as_hidden() {
        // ⚠ `clip` was the fifth keyword and it fell out of the bridge's table exactly as `auto` had,
        // with exactly the same consequence — see the class remark. It reads as `Hidden` rather than
        // as a fourth member because CSS separates the two by a scroll container and by programmatic
        // scrolling, and this engine grants `hidden` neither: `ScrollView` reads no `overflow` at all.
        var shorthand = new BridgeFixture().Build("overflow: clip");

        Assert.Equal(Overflow.Hidden, shorthand.OverflowX);
        Assert.Equal(Overflow.Hidden, shorthand.OverflowY);

        // ⚠ A fixture each, because `BridgeFixture.Build` *adds* a stylesheet rather than replacing
        // one — two calls on one fixture leave the first rule still cascading, and the axis this line
        // is about is exactly the one the previous declaration would have set.
        var axis = new BridgeFixture().Build("overflow-x: clip");

        Assert.Equal(Overflow.Hidden, axis.OverflowX);
        Assert.Equal(Overflow.Visible, axis.OverflowY);
    }

    [Fact]
    public void Clip_is_not_a_scroll_container() {
        // The half `clip` shares with `hidden` and not with `auto`: no gutter is reserved, so a
        // `scrollbar-width` beside it moves nothing. A fourth enum member would have had to reproduce
        // this, and every consumer would have written `is Hidden or Clip` to get it.
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .port { display: flex; flex-direction: column; width: 70px; height: 50px;
                    overflow: clip; scrollbar-width: 10px; }
            .fill { height: 8px; }
            """,
            document => document.Root.Add("div", classNames: "port").Add("div", classNames: "fill")
        );

        Assert.Equal(70f, document.Root.Children[0].Children[0].Width, Tolerance);
    }

    [Fact]
    public void Clip_lets_a_flex_item_shrink_past_the_size_its_content_needs() {
        // ⚠ **The half that was actually broken, and it is not the clip.** `overflow: clip` already
        // clipped the draw list before this keyword existed anywhere — `OverflowReader` tests
        // anything that is not `visible` — so the picture looked right and the box laid out as though
        // it had said nothing, keeping the §4.5 content floor that the `hidden` beside it drops. Two
        // boxes styled to do the same thing, laying out differently, with a correct picture of each.
        Assert.True(LabelWidth("") > 100f, "without an opt-out the item stops at its content");
        Assert.Equal(100f, LabelWidth("overflow: clip;"), Tolerance);
        Assert.Equal(LabelWidth("overflow: hidden;"), LabelWidth("overflow: clip;"), Tolerance);
    }

    [Fact]
    public void Clip_and_hidden_push_the_same_rectangle() {
        using var clipped = Drawn(
            """
            root { width: 400px; height: 300px; }
            .clip { width: 80px; height: 60px; overflow: clip; background-color: #111; }
            """,
            document => document.Root.Add("div", classNames: "clip")
        );

        using var hidden = Drawn(
            """
            root { width: 400px; height: 300px; }
            .clip { width: 80px; height: 60px; overflow: hidden; background-color: #111; }
            """,
            document => document.Root.Add("div", classNames: "clip")
        );

        var a = Push(clipped);
        var b = Push(hidden);

        Assert.Equal(b.X, a.X, Tolerance);
        Assert.Equal(b.Y, a.Y, Tolerance);
        Assert.Equal(b.Width, a.Width, Tolerance);
        Assert.Equal(b.Height, a.Height, Tolerance);
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
