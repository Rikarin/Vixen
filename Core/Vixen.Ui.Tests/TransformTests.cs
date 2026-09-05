// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Rendering;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>CSS Transforms 2's <c>translate</c>, which is the whole of the engine's transform stage.</summary>
/// <remarks>
///     <para>
///         <b>Doc 43 § A7's first third.</b> <c>translate-x-*</c> and <c>translate-y-*</c> used to emit
///         <c>--translate-x</c> and <c>--translate-y</c> — names no engine anywhere reads, so the
///         classes resolved, computed a value, and moved nothing. They are composed now, into the one
///         <c>translate</c> property <see cref="TranslationReader" /> resolves and
///         <c>UiDocument.Accumulate</c> applies.
///     </para>
///     <para>
///         ⚠ <b>The first test is the one that matters and the rest are its corners.</b> A transform
///         that draws in the new place and is clicked in the old one is the classic way this feature
///         is got wrong — two consumers, two copies of the arithmetic, one of them updated. It is not
///         merely absent here: it is unstateable, because the translation lands in
///         <c>AbsoluteLeft</c>/<c>AbsoluteTop</c> and both consumers read that rather than the
///         property. <see cref="A_translation_moves_the_box_it_draws_and_the_box_it_is_clicked_on" />
///         is what holds the design to that, and it was sabotage-tested against a hit test that read
///         an untranslated box: it fails, on the assertion it is supposed to fail on.
///     </para>
///     <para>
///         ⚠ <b><c>rotate</c> and <c>scale</c> were refused here, and the refusal has been retired
///         because the thing it was waiting for arrived.</b> What it said was true of the renderer it
///         was written against: a <c>DrawCommand</c> is an axis-aligned rectangle, the clip stack
///         intersects rectangles, and glyph advances are shaped at <c>run.Size</c> during layout, so
///         there was no per-command form of a rotation and no honest way to scale a picture. Its last
///         sentence named the way out — "both need the offscreen compositor <c>DrawListBuilder</c>'s
///         opacity remark already owes" — and that compositor now exists, with five things opening
///         groups through it.
///     </para>
///     <para>
///         ⚠ <b>Every clause of the refusal survives; none of them blocks any more, because the group
///         moved where they apply.</b> The subtree still rasterises into its surface axis-aligned,
///         every command in it still a rectangle and every clip in it still a rectangle — a
///         transformed element's own <c>overflow: hidden</c> cuts in its local space, which is
///         precisely what CSS Transforms 1 §3 asks for. Glyphs are still shaped once at their layout
///         size, and the <i>surface</i> is scaled rather than the text re-shaped, which is what keeps
///         the transform out of layout. Only the composite quad's four vertices move, and an affine
///         map is exactly the class for which both executors' linear interpolation of a texture
///         coordinate is exact — so the feature cost no shader and no vertex format. See
///         <c>UiTransform</c> and docs/guide/ui/compositing.md.
///     </para>
///     <para>
///         ⚠ <b>The tests for the two live in two files, and the split is not arbitrary.</b> What a
///         rotation <i>is</i> can only be asserted against pixels, because any transform opens a group
///         and so changes the draw list identically whatever the matrix says — that is
///         <c>Vixen.Ui.Controls.Tests.TransformPaintTests</c>, whose probes are chosen to fail for the
///         neighbouring transform. What is asserted <i>here</i> is the property the picture cannot
///         show: that the pointer agrees with it.
///     </para>
/// </remarks>
public class TransformTests {
    const float Tolerance = 0.001f;

    static UiDocument Drawn(string css, Action<UiDocument> build) {
        var document = new UiDocument(400f, 300f);
        document.Load(css);
        build(document);
        document.Update();
        document.Draw();
        return document;
    }

    static DrawCommand Rectangle(UiDocument document) =>
        Assert.Single(document.Drawing.Commands, command => command.Kind == DrawCommandKind.Rectangle);

    /// <summary>
    ///     ⚠ <b>Both halves in one test, deliberately.</b> Split in two they are two tests that can
    ///     pass separately while the interface is broken — the whole failure mode is that the picture
    ///     and the pointer disagree, and only an assertion that names both can see a disagreement.
    /// </summary>
    /// <remarks>
    ///     The negative half of the hit test is the load-bearing one. Asserting only that the point
    ///     under the *new* box hits the element passes an implementation that moved nothing and left
    ///     the box overlapping both points, which for a translation smaller than the box is every
    ///     real case. The point under the old box has to have stopped hitting it.
    /// </remarks>
    [Fact]
    public void A_translation_moves_the_box_it_draws_and_the_box_it_is_clicked_on() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .moved { width: 80px; height: 80px; background-color: #111; translate: 100px 40px; }
            """,
            document => document.Root.Add("div", classNames: "moved")
        );

        var moved = document.Root.Children[0];

        Assert.Equal(100f, moved.AbsoluteLeft, Tolerance);
        Assert.Equal(40f, moved.AbsoluteTop, Tolerance);

        // Drawn there.
        var box = Rectangle(document);
        Assert.Equal(100f, box.X, Tolerance);
        Assert.Equal(40f, box.Y, Tolerance);

        // And clicked there. `Root` is what the vacated corner falls through to.
        Assert.Same(moved, document.HitTest(140f, 80f));
        Assert.Same(document.Root, document.HitTest(40f, 20f));
    }

    /// <summary>
    ///     ⚠ <b>A transform is not layout, and the sibling is what proves it.</b> CSS Transforms 1 §3
    ///     applies a transform after layout: the box keeps the space it was given and is painted
    ///     somewhere else. An implementation that reached the layout style instead — the obvious place
    ///     to put it, next to <c>left</c> — would push the neighbour along, and every assertion above
    ///     would still pass.
    /// </summary>
    [Fact]
    public void A_translation_leaves_the_layout_it_came_out_of_alone() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; display: flex; flex-direction: row; }
            .a { width: 40px; height: 40px; translate: 100px 0px; }
            .b { width: 40px; height: 40px; }
            """,
            document => {
                document.Root.Add("div", classNames: "a");
                document.Root.Add("div", classNames: "b");
            }
        );

        var first = document.Root.Children[0];
        var second = document.Root.Children[1];

        Assert.Equal(100f, first.AbsoluteLeft, Tolerance);

        // Still at 40, where the untranslated first box left it — not at 140.
        Assert.Equal(40f, second.AbsoluteLeft, Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>The subtree moves with it</b>, per §3 — a translated panel takes its contents along
    ///     rather than sliding out from under them. Free here, because the accumulation already
    ///     descends from the parent's resolved position; asserted because "free" is a property of this
    ///     design and not of the feature, and the next person to move the resolution somewhere else
    ///     needs to be told.
    /// </summary>
    [Fact]
    public void A_translation_takes_its_subtree_with_it() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .outer { width: 80px; height: 80px; translate: 50px 30px; }
            .inner { width: 20px; height: 20px; }
            """,
            document => document.Root.Add("div", classNames: "outer").Add("div", classNames: "inner")
        );

        var inner = document.Root.Children[0].Children[0];

        Assert.Equal(50f, inner.AbsoluteLeft, Tolerance);
        Assert.Equal(30f, inner.AbsoluteTop, Tolerance);
        Assert.Same(inner, document.HitTest(55f, 35f));
    }

    /// <summary>
    ///     ⚠ <b>A percentage is of the element's own border box and not of its container</b>, which is
    ///     the opposite of every other percentage in the box model — CSS Transforms 1 §8. It is what
    ///     makes <c>-translate-x-full</c> the idiom for sliding a drawer exactly its own width off the
    ///     edge, and the container here is deliberately a different size from the box so that
    ///     resolving against the wrong one gives a different number rather than the same one.
    /// </summary>
    [Fact]
    public void A_percentage_translation_is_of_the_elements_own_box() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .half { width: 60px; height: 20px; translate: 50% 100%; }
            """,
            document => document.Root.Add("div", classNames: "half")
        );

        var half = document.Root.Children[0];

        // Half of sixty and all of twenty. Against the 400×300 root it would be 200 and 300.
        Assert.Equal(30f, half.AbsoluteLeft, Tolerance);
        Assert.Equal(20f, half.AbsoluteTop, Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>A translated element's clip moves with it, and it is still a rectangle.</b> That is
    ///     the whole reason <c>translate</c> is the one transform this engine can have: the clip stack
    ///     pushes rectangles and intersects them, a translated rectangle is a rectangle, and a rotated
    ///     one is not. Nothing in <c>DrawListBuilder</c> was taught about transforms to get this —
    ///     the push already used <c>AbsoluteLeft</c>.
    /// </summary>
    [Fact]
    public void A_translated_clip_is_pushed_where_the_box_ended_up() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .clip { width: 80px; height: 60px; overflow: hidden; translate: 25px 15px; }
            """,
            document => document.Root.Add("div", classNames: "clip")
        );

        var push = Assert.Single(
            document.Drawing.Commands,
            command => command.Kind == DrawCommandKind.ClipPush
        );

        Assert.Equal(25f, push.X, Tolerance);
        Assert.Equal(15f, push.Y, Tolerance);
        Assert.Equal(80f, push.Width, Tolerance);
        Assert.Equal(60f, push.Height, Tolerance);
    }

    /// <summary>One component is an x, and the y is zero rather than a repeat of it — §3.</summary>
    [Fact]
    public void One_component_moves_along_x_only() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .one { width: 40px; height: 40px; translate: 30px; }
            """,
            document => document.Root.Add("div", classNames: "one")
        );

        var one = document.Root.Children[0];

        Assert.Equal(30f, one.AbsoluteLeft, Tolerance);
        Assert.Equal(0f, one.AbsoluteTop, Tolerance);
    }

    /// <summary>
    ///     <c>none</c> is the initial value and moves nothing, and so does a value with no reading.
    /// </summary>
    /// <remarks>
    ///     ⚠ The second case is the one worth an assertion. <c>2fr</c> is a length in nobody's
    ///     grammar for this property, and the tempting implementation reads
    ///     <c>StyleValue.Number</c> off it and moves the box two points — a distance invented out of a
    ///     unit the author did not write, which is a great deal harder to notice than not moving.
    /// </remarks>
    [Theory]
    [InlineData("none")]
    [InlineData("2fr")]
    [InlineData("nonsense")]
    public void A_translation_with_no_reading_moves_nothing(string value) {
        using var document = Drawn(
            $$"""
              root { width: 400px; height: 300px; }
              .still { width: 40px; height: 40px; translate: {{value}}; }
              """,
            document => document.Root.Add("div", classNames: "still")
        );

        var still = document.Root.Children[0];

        Assert.Equal(0f, still.AbsoluteLeft, Tolerance);
        Assert.Equal(0f, still.AbsoluteTop, Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>A translation and an <see cref="UiElement.OffsetX" /> add rather than one replacing the
    ///     other.</b> They have different owners — the offset is what <c>ScrollView</c> and
    ///     <c>DockingHost</c> slide content with, the translation is whatever the cascade last computed
    ///     — so folding either into the other would make a stylesheet silently erase a scroll position.
    ///     That reads as the panel jumping home on an unrelated theme change, which is a bug nobody
    ///     traces back to a transform.
    /// </summary>
    [Fact]
    public void A_translation_and_an_imperative_offset_compose() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .both { width: 40px; height: 40px; translate: 10px 5px; }
            """,
            document => {
                var element = document.Root.Add("div", classNames: "both");
                element.OffsetX = 7f;
                element.OffsetY = 3f;
            }
        );

        var both = document.Root.Children[0];

        Assert.Equal(17f, both.AbsoluteLeft, Tolerance);
        Assert.Equal(8f, both.AbsoluteTop, Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>A translation transitions rather than jumps, and it cost nothing to get — which is
    ///     the half of doc 43 § A7 that asked for "animatable".</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>Animator</c> keeps no allow-list of animatable properties; it interpolates whatever
    ///         <c>StyleValue.CanInterpolate</c> accepts, and that has understood a list since it was
    ///         written. So a two-component <c>translate</c> was interpolable the moment something read
    ///         it. Asserted rather than assumed, because "it should just work" is the claim this whole
    ///         programme exists to stop believing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The mid-flight value is the assertion and the endpoints are worthless</b>, for the
    ///         reason <c>TransitionTests</c> exists to record: a jump and a transition agree about
    ///         where a value starts and finishes and disagree only in between. A declined
    ///         interpolation reads 100 here, not 0 — the animator applies the target and stops — so
    ///         asserting the destination passes against no animation at all.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_translation_interpolates_across_ticks_rather_than_jumping() {
        using var document = new UiDocument(400f, 300f);

        document.Load(
            """
            root { width: 400px; height: 300px; }
            #slide { width: 20px; height: 20px; translate: 0px 0px;
                     transition-property: translate; transition-duration: 200ms;
                     transition-timing-function: linear; }
            #slide.out { translate: 100px 0px; }
            """
        );

        var slide = document.Create("div", document.Root, "slide");

        document.Tick(TimeSpan.Zero);
        document.Update();

        Assert.Equal(0f, slide.AbsoluteLeft, Tolerance);

        // The pass that sees the class change is what starts the transition, so it is still at the
        // old value; the clock only begins to matter from the frame after.
        slide.AddClass("out");
        document.Tick(TimeSpan.Zero);
        document.Update();

        document.Tick(TimeSpan.FromMilliseconds(100));
        document.Update();

        // Half way through a linear 200 ms run from nought to a hundred. The bounds are loose — the
        // curve itself is pinned in `Vixen.Ui.Styling.Tests` — and they exclude both endpoints, which
        // is the entire assertion: a jump reads 100 here, and a declined interpolation reads 0.
        Assert.InRange(slide.AbsoluteLeft, 20f, 80f);

        document.Tick(TimeSpan.FromMilliseconds(400));
        document.Update();

        Assert.Equal(100f, slide.AbsoluteLeft, Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>A rotation is clicked where it is painted, and this is the test the whole feature is
    ///     judged by.</b> The translation's own version of this is at the top of the file and its
    ///     remark applies word for word: painted in the new place and clickable in the old one moves
    ///     every observable a draw list has, so it passes a consumption gate and it is a broken
    ///     interface. A rotation cannot borrow the trick that makes it unstateable for
    ///     <c>translate</c> — there is no accumulated rectangle a rotated box could be folded into —
    ///     so the two consumers hold one matrix instead, and this is what checks they hold the same
    ///     one.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The negative probe is the load-bearing half, exactly as it is for the translation.</b>
    ///     A long bar rotated a quarter turn overlaps its own untransformed box across the middle, so
    ///     a point near the centre hits under either reading. The two probes here are at the ends: one
    ///     that only the turned bar covers, and one that only the upright bar covered.
    /// </remarks>
    [Fact]
    public void A_rotation_moves_the_box_it_draws_and_the_box_it_is_clicked_on() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .bar { position: absolute; left: 100px; top: 140px; width: 80px; height: 20px;
                   background-color: #111; rotate: 90deg; }
            """,
            document => document.Root.Add("div", classNames: "bar")
        );

        var bar = document.Root.Children[0];

        // Layout is untouched: the box is still where it was put, and `Bounds` still reports it.
        Assert.Equal(100f, bar.AbsoluteLeft, Tolerance);
        Assert.Equal(140f, bar.AbsoluteTop, Tolerance);

        // The bar is 80x20 about its centre (140, 150). Turned, it occupies x in [130,150] and
        // y in [110,190]. A point near the top of the turned bar is inside it, and thirty points above
        // the untransformed box, which never reached y = 120.
        Assert.Same(bar, document.HitTest(140f, 120f));

        // And a point at the untransformed bar's left end, which the turned one has vacated. `Root` is
        // what it falls through to — the same shape the translation's test uses.
        Assert.Same(document.Root, document.HitTest(105f, 150f));
    }

    /// <summary>A scale is clicked at its painted size, on both sides of the box it grew out of.</summary>
    /// <remarks>
    ///     ⚠ <b>Two probes again, and the second is not symmetric with the first.</b> A grown element
    ///     covers everything it used to, so every point that hit before still hits — asserting only
    ///     that would pass an implementation that ignored <c>scale</c> entirely. The point outside the
    ///     original box is the whole assertion, and the shrunk case is what proves the arithmetic runs
    ///     in both directions rather than just growing a bound.
    /// </remarks>
    [Fact]
    public void A_scale_is_clicked_at_the_size_it_is_drawn() {
        using var grown = Drawn(
            """
            root { width: 400px; height: 300px; }
            .big { position: absolute; left: 100px; top: 100px; width: 40px; height: 40px;
                   background-color: #111; scale: 200%; }
            """,
            document => document.Root.Add("div", classNames: "big")
        );

        var big = grown.Root.Children[0];

        // 40x40 about (120, 120), so it paints x in [80,160]. A point at 150 is outside the authored
        // box and inside the painted one.
        Assert.Same(big, grown.HitTest(150f, 120f));
        Assert.Same(grown.Root, grown.HitTest(170f, 120f));

        using var shrunk = Drawn(
            """
            root { width: 400px; height: 300px; }
            .small { position: absolute; left: 100px; top: 100px; width: 40px; height: 40px;
                     background-color: #111; scale: 50%; }
            """,
            document => document.Root.Add("div", classNames: "small")
        );

        var small = shrunk.Root.Children[0];

        // Painted x in [110,130]. The authored box reached 140 and the painted one does not.
        Assert.Same(small, shrunk.HitTest(120f, 120f));
        Assert.Same(shrunk.Root, shrunk.HitTest(135f, 120f));
    }

    /// <summary>A transformed parent's children are clicked where the parent put them.</summary>
    /// <remarks>
    ///     ⚠ <b>The child carries no transform of its own, which is what makes this a test of the walk
    ///     rather than of the reader.</b> <c>Accumulate</c> deliberately does not push the matrix down
    ///     — the child's <c>AbsoluteLeft</c> is untransformed — so the only thing that can put the
    ///     pointer in the right place is the recursion having mapped it on the way through the parent.
    ///     An implementation that applied the inverse inside <c>Contains</c> instead of at the top of
    ///     the walk would pass every single-element test above and fail this one.
    /// </remarks>
    [Fact]
    public void A_transformed_parent_moves_where_its_children_are_clicked() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .outer { position: absolute; left: 100px; top: 100px; width: 40px; height: 40px;
                     background-color: #111; scale: 200%; }
            .inner { position: absolute; left: 0px; top: 0px; width: 10px; height: 10px;
                     background-color: #222; }
            """,
            document => document.Root.Add("div", classNames: "outer").Add("div", classNames: "inner")
        );

        var outer = document.Root.Children[0];
        var inner = outer.Children[0];

        // The child is at (100,100)-(110,110) untransformed, and the parent scales about (120,120), so
        // it paints (80,80)-(100,100).
        Assert.Equal(100f, inner.AbsoluteLeft, Tolerance);
        Assert.Same(inner, document.HitTest(90f, 90f));

        // Its authored corner now belongs to the parent, not to it.
        Assert.Same(outer, document.HitTest(105f, 105f));
    }

    /// <summary>Nested transforms compose, and the pointer composes with them.</summary>
    /// <remarks>
    ///     ⚠ <b>Non-uniform on the outside and a rotation on the inside, on purpose.</b> A uniform
    ///     scale commutes with a rotation, so a fixture built from two of those would pass an
    ///     implementation that composed them in the wrong order — which is the mistake a nested walk
    ///     invites, because the inverses have to be applied outermost first. Here the two do not
    ///     commute and the wrong order lands somewhere else.
    /// </remarks>
    [Fact]
    public void Nested_transforms_compose_for_the_pointer() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .outer { position: absolute; left: 100px; top: 100px; width: 40px; height: 40px;
                     background-color: #111; scale: 2 1; }
            .inner { position: absolute; left: 100px; top: 100px; width: 20px; height: 4px;
                     background-color: #222; rotate: 90deg; }
            """,
            document => document.Root.Add("div", classNames: "outer").Add("div", classNames: "inner")
        );

        var outer = document.Root.Children[0];
        var inner = outer.Children[0];

        // An absolutely positioned child is placed from its containing block's origin, so the inner
        // bar lands at (200,200) and is 20x4 about its centre (210,202).
        Assert.Equal(200f, inner.AbsoluteLeft, Tolerance);

        // Its own quarter turn makes it x in [208,212], y in [192,212]. The outer scale is 2x in x
        // about (120,120) and 1x in y, which sends that to x in [296,304], y unchanged.
        //
        // ⚠ Both probes are chosen against the *rotation being dropped*, which is what a composition
        // applied in the wrong order most often degenerates to. Without it the bar would be the wide
        // one, x in [280,320] and y in [200,204]: this point is inside the composed answer and above
        // that one.
        Assert.Same(inner, document.HitTest(300f, 195f));

        // ...and this one is inside the un-rotated reading and outside the composed one. It reaches
        // neither the bar nor the parent, whose own painted box is x in [80,160].
        Assert.Same(document.Root, document.HitTest(285f, 202f));
    }

    /// <summary>An element scaled to nothing is neither drawn nor clickable.</summary>
    /// <remarks>
    ///     ⚠ <b>Both halves, because either alone is a bug of its own.</b> A <c>scale-0</c> that still
    ///     took the pointer would be an invisible control swallowing clicks over its old box — the
    ///     worst version of the disagreement this whole file is about, since nothing on screen explains
    ///     it. One that vanished from the draw list but stayed in the hit test is exactly that; one
    ///     that stayed in both is <c>scale-0</c> not working at all.
    /// </remarks>
    [Fact]
    public void A_zero_scale_is_neither_painted_nor_clicked() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .gone { position: absolute; left: 100px; top: 100px; width: 40px; height: 40px;
                    background-color: #111; scale: 0; }
            """,
            document => document.Root.Add("div", classNames: "gone")
        );

        Assert.DoesNotContain(document.Drawing.Commands, command => command.Kind == DrawCommandKind.Rectangle);
        Assert.Same(document.Root, document.HitTest(120f, 120f));
    }

    /// <summary>
    ///     ⚠ <b>An unreadable <c>rotate</c> or <c>scale</c> leaves the element alone, and the
    ///     three-axis form of <c>rotate</c> is refused whole rather than half-read.</b> CSS Transforms
    ///     2 §3 also spells a rotation as an axis and an angle — <c>rotate: x 45deg</c> — which is out
    ///     of the plane this engine has depth for. Picking the angle out of it and applying it about z
    ///     would turn every one of those into a forty-five degree spin, which is not a degraded picture
    ///     but a different one, and is the sort of thing that looks like the feature working.
    /// </summary>
    [Theory]
    [InlineData("rotate: none")]
    [InlineData("rotate: 45")]
    [InlineData("rotate: x 45deg")]
    [InlineData("rotate: nonsense")]
    [InlineData("scale: none")]
    [InlineData("scale: nonsense")]
    public void A_transform_with_no_reading_leaves_the_element_alone(string declaration) {
        using var document = Drawn(
            $$"""
              root { width: 400px; height: 300px; }
              .still { position: absolute; left: 100px; top: 100px; width: 40px; height: 40px;
                       background-color: #111; {{declaration}}; }
              """,
            document => document.Root.Add("div", classNames: "still")
        );

        var still = document.Root.Children[0];

        Assert.Null(still.Transform);
        Assert.Same(still, document.HitTest(120f, 120f));
        Assert.Same(document.Root, document.HitTest(150f, 120f));
    }

    /// <summary>The three angle units CSS has besides degrees all arrive as degrees.</summary>
    /// <remarks>
    ///     ⚠ <b>Asserted through the same quarter turn rather than against a matrix, because what
    ///     matters is that the four spellings are one value.</b> Values 1 § 6.1 fixes the ratios
    ///     between them, so there is nothing to resolve later and no context to resolve it in — which
    ///     is why the conversion is in the parser rather than here. Before it was, <c>0.25turn</c>
    ///     parsed as <c>Unknown</c> and the element simply did not turn.
    /// </remarks>
    [Theory]
    [InlineData("90deg")]
    [InlineData("100grad")]
    [InlineData("0.25turn")]
    [InlineData("1.5707963rad")]
    public void Every_angle_unit_reaches_the_same_quarter_turn(string angle) {
        using var document = Drawn(
            $$"""
              root { width: 400px; height: 300px; }
              .bar { position: absolute; left: 100px; top: 140px; width: 80px; height: 20px;
                     background-color: #111; rotate: {{angle}}; }
              """,
            document => document.Root.Add("div", classNames: "bar")
        );

        var bar = document.Root.Children[0];

        Assert.Same(bar, document.HitTest(140f, 120f));
        Assert.Same(document.Root, document.HitTest(105f, 150f));
    }

    // ── `transform`, the property, and its function list ────────────────────────────────────────

    /// <summary>
    ///     <c>transform: rotate(90deg)</c> reaches the same matrix <c>rotate: 90deg</c> does, and is
    ///     asserted with the same two probes.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Deliberately the same fixture as
    ///     <see cref="A_rotation_moves_the_box_it_draws_and_the_box_it_is_clicked_on" />, because the
    ///     one thing worth knowing first about a new property is whether it lands where the old one
    ///     does.</b> Transforms 2 §3 says the two spellings are the same rotation about the same
    ///     origin, and a list that composed about the box's corner instead — the natural mistake,
    ///     since a function list has no origin written in it — passes an assertion about the turned
    ///     bar's own extent and fails these.
    /// </remarks>
    [Fact]
    public void A_function_list_reaches_the_same_place_the_independent_property_does() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .bar { position: absolute; left: 100px; top: 140px; width: 80px; height: 20px;
                   background-color: #111; transform: rotate(90deg); }
            """,
            document => document.Root.Add("div", classNames: "bar")
        );

        var bar = document.Root.Children[0];

        Assert.Equal(100f, bar.AbsoluteLeft, Tolerance);
        Assert.Same(bar, document.HitTest(140f, 120f));
        Assert.Same(document.Root, document.HitTest(105f, 150f));
    }

    /// <summary>The last function in a list is applied to a point first.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Two boxes that do not overlap at all, which is the only way to state this.</b>
    ///         <c>transform: A B</c> is the matrix product <c>A · B</c>, so <c>B</c> maps the point
    ///         first — <c>rotate(90deg) translate(40px)</c> moves the element forty points along its
    ///         own <i>turned</i> axis, and <c>translate(40px) rotate(90deg)</c> moves it forty points
    ///         across the screen. A 40×40 box at (100, 100) about its centre (120, 120) lands at
    ///         x ∈ [100, 140], y ∈ [140, 180] under the first and at x ∈ [140, 180], y ∈ [100, 140]
    ///         under the second: disjoint, so each probe rejects the other reading rather than merely
    ///         preferring one.
    ///     </para>
    ///     <para>
    ///         Composing left to right is the mistake this exists for, and it is invisible on every
    ///         single-function declaration — which is most of them.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_last_function_written_is_the_first_one_applied() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            div { position: absolute; left: 100px; top: 100px; width: 40px; height: 40px;
                  background-color: #111; }
            .turned-then-moved { transform: rotate(90deg) translate(40px); }
            .moved-then-turned { transform: translate(40px) rotate(90deg); }
            """,
            document => {
                document.Root.Add("div", classNames: "turned-then-moved");
                document.Root.Add("div", classNames: "moved-then-turned");
            }
        );

        var first = document.Root.Children[0];
        var second = document.Root.Children[1];

        Assert.Same(first, document.HitTest(120f, 160f));
        Assert.Same(second, document.HitTest(160f, 120f));
    }

    /// <summary><c>matrix()</c>'s six numbers are the six cells, in CSS's order.</summary>
    /// <remarks>
    ///     ⚠ <b>A non-uniform, non-symmetric matrix, because half the orderings agree on anything
    ///     else.</b> <c>matrix(2, 0, 0, 1, 10, 0)</c> doubles x, leaves y, and shifts by ten — so a
    ///     40×40 box about (120, 120) paints x ∈ [90, 170] and keeps y ∈ [100, 140]. A reading that
    ///     transposed the two middle cells, or that took <c>e</c>/<c>f</c> as a scale, moves the box
    ///     somewhere else entirely and one of these three probes says so.
    /// </remarks>
    [Fact]
    public void A_matrix_is_read_cell_for_cell() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .cell { position: absolute; left: 100px; top: 100px; width: 40px; height: 40px;
                    background-color: #111; transform: matrix(2, 0, 0, 1, 10, 0); }
            """,
            document => document.Root.Add("div", classNames: "cell")
        );

        var cell = document.Root.Children[0];

        Assert.Same(cell, document.HitTest(160f, 120f));
        Assert.Same(document.Root, document.HitTest(180f, 120f));
        Assert.Same(document.Root, document.HitTest(85f, 120f));
    }

    /// <summary><c>skewX</c> shifts a point's x by its y, which is the other row.</summary>
    /// <remarks>
    ///     ⚠ <b>Probed off the axis on purpose.</b> A skew leaves the line through the origin alone,
    ///     so every probe on the element's own centre row hits under either reading and under none at
    ///     all. The point below is above the centre — 15 points up — where a 45° <c>skewX</c> has
    ///     moved the covered range 15 points to the left. Written into the wrong cell the box slants
    ///     the other way, along y, and the same point misses.
    /// </remarks>
    [Fact]
    public void A_skew_shifts_the_axis_it_names_by_the_other_one() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .slanted { position: absolute; left: 100px; top: 100px; width: 40px; height: 40px;
                       background-color: #111; transform: skewX(45deg); }
            """,
            document => document.Root.Add("div", classNames: "slanted")
        );

        var slanted = document.Root.Children[0];

        // Fifteen points above the centre, the covered range is x ∈ [85, 125].
        Assert.Same(slanted, document.HitTest(90f, 105f));
        Assert.Same(document.Root, document.HitTest(130f, 105f));

        // And fifteen below it is the mirror, x ∈ [115, 155].
        Assert.Same(slanted, document.HitTest(150f, 135f));
        Assert.Same(document.Root, document.HitTest(110f, 135f));
    }

    /// <summary>
    ///     The list is the innermost factor, so <c>scale</c> the property applies <i>after</i> it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Transforms 2 §3 orders the four as translate, rotate, scale, then <c>transform</c> —
    ///     as matrix multiplications, which reverses them for a point.</b> A 40×40 box at (100, 100)
    ///     with <c>transform: translate(40px)</c> and <c>scale: 2</c> is translated first and then
    ///     doubled about its own centre, landing at x ∈ [160, 240]. Scaled first and translated
    ///     after, it lands at x ∈ [120, 200] — overlapping, which is why both probes are needed and
    ///     why a test written with a uniform scale and no translation could not tell the two apart.
    /// </remarks>
    [Fact]
    public void The_list_is_applied_before_the_independent_properties() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .both { position: absolute; left: 100px; top: 100px; width: 40px; height: 40px;
                    background-color: #111; transform: translate(40px); scale: 2; }
            """,
            document => document.Root.Add("div", classNames: "both")
        );

        var both = document.Root.Children[0];

        Assert.Same(both, document.HitTest(220f, 120f));
        Assert.Same(document.Root, document.HitTest(130f, 120f));
    }

    /// <summary>A percentage inside <c>translate()</c> is of the element's own border box.</summary>
    /// <remarks>
    ///     Transforms 1 §8, the same rule the <c>translate</c> property follows and the opposite of
    ///     every percentage in the box model. Fifty per cent of an 80-point box is 40 points, and the
    ///     containing block is 400 wide — so a reading against the parent would put it at 200 and both
    ///     probes would miss.
    /// </remarks>
    [Fact]
    public void A_percentage_inside_a_function_is_of_the_elements_own_box() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .moved { position: absolute; left: 0px; top: 100px; width: 80px; height: 80px;
                     background-color: #111; transform: translate(50%); }
            """,
            document => document.Root.Add("div", classNames: "moved")
        );

        var moved = document.Root.Children[0];

        Assert.Same(moved, document.HitTest(100f, 140f));
        Assert.Same(document.Root, document.HitTest(20f, 140f));
    }

    /// <summary>
    ///     A list this cannot read is dropped whole, and the properties beside it still apply.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The three-dimensional functions are why "dropped whole" is the rule.</b>
    ///         <c>rotateX</c>, <c>translate3d</c> and <c>perspective</c> are legal CSS and there is no
    ///         third axis here; reading the functions that happen to be flat and skipping the rest
    ///         turns a card flip into a card that never moves, which is a different picture rather
    ///         than a degraded one. The same judgement <c>rotate: x 45deg</c> already gets.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it is the <i>list</i> that is dropped, not the element's transform.</b> CSS
    ///         drops an invalid declaration and leaves its neighbours alone, so the <c>scale: 2</c>
    ///         beside it still doubles the box — which is what the second probe is for. Returning "no
    ///         transform at all" would let one pasted <c>perspective()</c> cancel a scale two lines
    ///         above it.
    ///     </para>
    /// </remarks>
    /// <param name="value">The <c>transform</c> value.</param>
    [Theory]
    [InlineData("rotateX(45deg)")]
    [InlineData("translate3d(10px, 10px, 10px)")]
    [InlineData("perspective(400px)")]
    [InlineData("rotate(45)")]
    [InlineData("translate(10)")]
    [InlineData("matrix(1, 0, 0, 1, 0)")]
    [InlineData("matrix(1, 0, 0, 1, 0, 0, 0)")]
    [InlineData("scale(calc(1 + 1))")]
    [InlineData("rotate(45deg) rotateY(20deg)")]
    [InlineData("nonsense")]
    public void A_function_list_with_no_reading_is_dropped_whole(string value) {
        using var document = Drawn(
            $$"""
              root { width: 400px; height: 300px; }
              .still { position: absolute; left: 100px; top: 100px; width: 40px; height: 40px;
                       background-color: #111; transform: {{value}}; scale: 2; }
              """,
            document => document.Root.Add("div", classNames: "still")
        );

        var still = document.Root.Children[0];

        // The scale survives: 40x40 about (120, 120) doubled paints x in [80, 160].
        Assert.Same(still, document.HitTest(150f, 120f));
        Assert.Same(document.Root, document.HitTest(170f, 120f));
    }

    /// <summary><c>transform: none</c> is the initial value written out, and is not a refusal.</summary>
    [Fact]
    public void None_leaves_the_element_untransformed() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .plain { position: absolute; left: 100px; top: 100px; width: 40px; height: 40px;
                     background-color: #111; transform: none; }
            """,
            document => document.Root.Add("div", classNames: "plain")
        );

        Assert.Null(document.Root.Children[0].Transform);
    }

    /// <summary>
    ///     A card rotated past ninety degrees under a perspective is not clickable where its far half
    ///     is reflected to, although the inverse answers there perfectly happily.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The interesting failure of a projective inverse is not that it blows up.</b> An
    ///         affine inverse has one failure mode — a vanishing determinant — and the hit test reads
    ///         it as "nothing here", which is right: a <c>scale-0</c> element paints nothing. A
    ///         homography has a second, and it does not announce itself. Part of the element's plane
    ///         lies behind the eye once the flip passes ninety degrees; those points have a
    ///         non-positive <c>w</c>, they invert to <i>finite</i> coordinates inside the border box,
    ///         and the pointer lands on an element in a band of the screen where nothing is drawn.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So the assertion that carries this test is the middle one, not the last.</b>
    ///         <c>Assert.Same(Root, …)</c> alone would pass against a hit test that had simply stopped
    ///         working — and against one that never mapped the point at all, since the reflected point
    ///         is well outside the untransformed box. The probe first shows that the naive inverse
    ///         <i>does</i> answer there, with a point this element contains: that is the defect stated
    ///         as a fact about the screen rather than as a claim about the code.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the front half has to go on hitting</b>, or "reject a non-positive <c>w</c>"
    ///         and "reject everything" are the same test. The two probes are the same element, the
    ///         same matrix, and opposite signs of one number.
    ///     </para>
    ///     <para>
    ///         The matrix is written here rather than parsed because <c>TransformReader</c> reads no
    ///         <c>perspective()</c> yet — #550 — and this property is the hit test's rather than the
    ///         parser's. <c>rotateX(120deg)</c> under <c>perspective(100px)</c> about the card's centre
    ///         folds the plane at <c>y = 315.5</c>, which is 34.5 points above the card's bottom edge.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_point_behind_the_eye_is_not_a_hit_although_the_inverse_answers_there() {
        using var document = new UiDocument(800f, 600f);

        document.Load(
            """
            root { width: 800px; height: 600px; }
            .card { position: absolute; left: 200px; top: 50px; width: 400px; height: 300px;
                    background-color: #111; }
            """
        );

        document.Root.Add("div", classNames: "card");
        document.Update();
        document.Draw();

        var card = document.Root.Children[0];
        var centre = new Vector2(card.AbsoluteLeft + (card.Width / 2f), card.AbsoluteTop + (card.Height / 2f));

        Assert.Equal(400f, centre.X, Tolerance);
        Assert.Equal(200f, centre.Y, Tolerance);

        // `perspective(100px) rotateX(120deg)` reduced to the element's plane and re-centred. The
        // rotation puts `y · sin` on the z axis and the perspective turns that into `w`, so the whole
        // of the third column is one cell — which is what makes a card flip a homography.
        var radians = 120f * (MathF.PI / 180f);

        var flip = new UiTransform(1f, 0f, 0f, MathF.Cos(radians), 0f, 0f) {
            M13 = 0f,
            M23 = -MathF.Sin(radians) / 100f,
            M33 = 1f
        }.About(centre);

        card.Transform = flip;

        // The instrument first: the fixture has to straddle the eye plane, or every assertion below is
        // about the front half and passes against the defect it is written for.
        var behind = new Vector2(400f, 345f);
        var front = new Vector2(400f, 100f);

        Assert.True(flip.Project(behind).Z < 0f, "the far probe is meant to be behind the eye");
        Assert.True(flip.Project(front).Z > 0f, "the near probe is meant to be in front of it");

        var reflected = flip.Apply(behind);
        var shown = flip.Apply(front);

        Assert.Equal(483.49f, reflected.Y, 0.01f);
        Assert.Equal(226.79f, shown.Y, 0.01f);

        // ⚠ The trap, stated: the inverse hands back a point this element contains, so a hit test that
        // divided and asked no question returns the card for a pixel 133 points below the card.
        var undo = flip.Invert();

        Assert.NotNull(undo);

        var naive = undo.Value.Apply(reflected);

        Assert.Equal(behind.X, naive.X, 0.01f);
        Assert.Equal(behind.Y, naive.Y, 0.01f);
        Assert.InRange(naive.X, card.AbsoluteLeft, card.AbsoluteLeft + card.Width);
        Assert.InRange(naive.Y, card.AbsoluteTop, card.AbsoluteTop + card.Height);

        // And the answer: nothing is drawn there, so nothing is clicked there.
        Assert.Same(document.Root, document.HitTest(reflected.X, reflected.Y));

        // The front half is untouched, which is what keeps this from being satisfied by a refusal.
        Assert.Same(card, document.HitTest(shown.X, shown.Y));
    }
}
