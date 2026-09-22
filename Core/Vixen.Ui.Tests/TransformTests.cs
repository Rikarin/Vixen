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
    ///         ⚠ <b>The three-dimensional functions were why "dropped whole" is the rule, and they
    ///         are read now (#550)</b> — so what is pinned here is the rest of it. <c>rotateX</c>,
    ///         <c>translate3d</c> and <c>perspective</c> used to be legal CSS with no third axis to
    ///         express them, and reading the functions that happened to be flat and skipping the rest
    ///         turned a card flip into a card that never moves. The rule outlives that reason,
    ///         because a list is still all-or-nothing: an unreadable <i>argument</i> — a
    ///         <c>calc()</c>, a percentage where z takes none, a degenerate <c>rotate3d</c> axis —
    ///         drops the whole declaration exactly as CSS does.
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
    [InlineData("rotate(45)")]
    [InlineData("translate(10)")]
    [InlineData("matrix(1, 0, 0, 1, 0)")]
    [InlineData("matrix(1, 0, 0, 1, 0, 0, 0)")]
    [InlineData("scale(calc(1 + 1))")]
    [InlineData("nonsense")]

    // ⚠ <b>A percentage along z is invalid rather than zero</b>, per Transforms 2 § 12 — there is no
    // box dimension for it to resolve against, and the two-dimensional reader beside it would
    // cheerfully resolve one against the element's height and produce a plausible wrong number.
    [InlineData("translateZ(50%)")]
    [InlineData("translate3d(10px, 10px, 50%)")]

    // ⚠ A zero-length axis is an invalid function and not a no-op, which is the same rule one level
    // down: read as the identity, `rotate3d(0, 0, 0, 45deg)` would silently do nothing.
    [InlineData("rotate3d(0, 0, 0, 45deg)")]
    [InlineData("rotate3d(1, 0, 45deg)")]

    // A perspective distance must be positive: zero puts every point of the plane on the eye plane
    // at once, and there is no picture on the far side of that.
    [InlineData("perspective(0)")]
    [InlineData("perspective(-100px)")]
    [InlineData("perspective(50%)")]
    [InlineData("matrix3d(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0)")]
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

    /// <summary>
    ///     A <c>perspective()</c> and a <c>rotateX()</c> in one list compose in four dimensions and
    ///     reduce once, which is a different picture from reducing each and folding the results.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the whole of why #550 is a shape change and not nine more names in a
    ///         switch.</b> Reducing a 4×4 to a <c>UiTransform</c> keeps rows x, y, w against columns
    ///         x, y, 1 and throws the z row and the z column away, so <c>R(A·B) = R(A)·R(B)</c> holds
    ///         only where <c>A</c> has no z column or <c>B</c> no z row — and a <c>perspective()</c>
    ///         is nothing <i>but</i> a z column while a <c>rotateX()</c> is nothing but a z row,
    ///         which is the one pair every card flip is written from. A <c>Function</c> returning a
    ///         <c>UiTransform</c> each makes every <c>perspective()</c> silently the identity,
    ///         because every point of an element sits at z = 0 until something has moved it.
    ///     </para>
    ///     <para>
    ///         <b>The oracle is closed-form and the two answers are far apart.</b> The box is 200
    ///         square about (200, 150), so its bottom edge is 100 below the origin.
    ///         <c>rotateX(60deg)</c> sends that point to <c>y = 50</c>, <c>z = 86.6</c>, and
    ///         <c>perspective(200px)</c> then divides by <c>w = 1 − 86.6/200 = 0.567</c> — so it
    ///         lands at <b>88.19</b> below the origin, and the top edge at <b>34.9</b> above it.
    ///         Reduced per function it lands at 50 and −50, which is <c>rotateX</c> alone. The two
    ///         probes are inside one reading and outside the other, in both directions.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The element grows downwards and shrinks upwards, which no affine can do</b> —
    ///         that asymmetry is the assertion, and it is why both probes are on the vertical centre
    ///         line where the horizontal scaling cannot reach them.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_perspective_and_a_rotation_compose_in_four_dimensions_and_reduce_once() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .card { position: absolute; left: 100px; top: 50px; width: 200px; height: 200px;
                    background-color: #111; transform: perspective(200px) rotateX(60deg); }
            """,
            document => document.Root.Add("div", classNames: "card")
        );

        var card = document.Root.Children[0];

        // The near edge reaches 88.19 below the origin. Reduced per function it would stop at 50, so
        // this point is outside the element on the plausible wrong reading.
        Assert.Same(card, document.HitTest(200f, 220f));
        Assert.Same(document.Root, document.HitTest(200f, 240f));

        // And the far edge stops 34.9 above it, where the wrong reading reaches 50 — so this point is
        // INSIDE the element on that reading and outside on this one. Without it, a transform that
        // merely scaled the whole card up would pass the pair above.
        Assert.Same(card, document.HitTest(200f, 120f));
        Assert.Same(document.Root, document.HitTest(200f, 110f));
    }

    /// <summary>
    ///     <c>perspective</c> the property is established by the PARENT, and <c>perspective()</c> the
    ///     function by the element itself.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Getting these the same way round is the classic mistake and it produces a
    ///         plausible picture</b> — a weaker projection rather than an obviously wrong one — so
    ///         the two halves are asserted against each other rather than separately. Transforms 2
    ///         § 6: an element's <c>perspective</c> applies to its children.
    ///     </para>
    ///     <para>
    ///         Both elements carry the same <c>rotateX(60deg)</c> and the same 200-point distance,
    ///         written once on the parent and once on the element itself. The first is projected —
    ///         its near edge reaches 88.19 below its origin — and the second is not, because a
    ///         <c>perspective</c> on an element says nothing about that element. A reader that took
    ///         the property off the element would make the second one project and the first one flat,
    ///         which is exactly what the two probes separate.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_perspective_property_is_the_parents_and_a_perspective_function_is_the_elements() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .stage { position: absolute; left: 20px; top: 100px; width: 100px; height: 100px;
                     perspective: 200px; }
            .card { position: absolute; left: 0px; top: 0px; width: 100px; height: 100px;
                    background-color: #111; transform: rotateX(60deg); }
            .alone { position: absolute; left: 250px; top: 100px; width: 100px; height: 100px;
                     background-color: #222; perspective: 200px; transform: rotateX(60deg); }
            """,
            document => {
                var stage = document.Root.Add("div", classNames: "stage");
                stage.Add("div", classNames: "card");
                document.Root.Add("div", classNames: "alone");
            }
        );

        var card = document.Root.Children[0].Children[0];
        var alone = document.Root.Children[1];

        // ⚠ The parent's perspective reaches the child, and both halves say so. Its near edge lands
        // 31.9 below the origin where an unprojected `rotateX(60deg)` stops at 25, and its far edge
        // is pulled in to 20.6 where that reading reaches 25 — a box that grows downwards and shrinks
        // upwards, which no affine can do and no scaling of the whole card could fake.
        Assert.Same(card, document.HitTest(70f, 178f));
        Assert.NotSame(card, document.HitTest(70f, 127f));

        // ⚠ And the element's own `perspective` does not reach itself. `alone` is the same rotation
        // at the same size with the property written one level down, and it stops at 25 in both
        // directions. A reader that took the property off the element would make this one project
        // and the card above it flat, which is the plausible mistake this pair separates.
        Assert.Same(alone, document.HitTest(300f, 172f));
        Assert.NotSame(alone, document.HitTest(300f, 178f));
    }

    /// <summary>
    ///     The parent's <c>perspective</c> is outermost — outside the element's own <c>scale</c> and
    ///     <c>rotate</c>, not merely outside its <c>transform</c> list.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Two of the element's three transform properties at once, and an origin that is
    ///         not the vanishing point, because neither alone can see this.</b> Transforms 2 § 3
    ///         composes <c>transform</c>, then <c>scale</c>, then <c>rotate</c> into the element's own
    ///         matrix, and § 6 projects <i>that</i> through the parent's vanishing point. Folding the
    ///         perspective in beside the list instead scales and rotates a picture that has already
    ///         been projected. The two agree exactly whenever <c>transform-origin</c> and
    ///         <c>perspective-origin</c> coincide — a 2D scale or rotation about the projection's own
    ///         centre genuinely does commute with it — so a fixture with one centred child, which is
    ///         how one is written without thinking about it, proves nothing here.
    ///     </para>
    ///     <para>
    ///         <b>Closed form, derived rather than recorded.</b> The stage is 300 square at the
    ///         origin, so its vanishing point is (150, 150); each card is 100 square at (200, 60), so
    ///         its <c>transform-origin</c> is (250, 110) and both coordinates of the two centres
    ///         differ. A card point at <c>u</c> below the origin leaves <c>rotateX(60deg)</c> at
    ///         <c>(0, u/2, u·sin60)</c>, and the projection's <c>w</c> is <c>1 − u·sin60 / 200</c> —
    ///         0.78349365 for the bottom edge at <c>u = 50</c>. Carrying that through the spec's order
    ///         puts the bottom-centre of the scaled card at <c>100/w + 150</c> across and
    ///         <c>−2.5/w + 150</c> down; carrying it through the other order gives <c>200/w + 50</c>
    ///         and <c>−22.5/w + 170</c>, which are the second pair of numbers below. Both are written
    ///         out, so a failure says which composition the reader performed rather than only that it
    ///         missed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The scale is non-uniform and the rotation is a half turn for the same reason.</b>
    ///         A uniform scale about a point whose y already matches the vanishing point's moves only
    ///         x, and half of the evidence disappears.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_parents_perspective_is_applied_after_the_elements_own_scale_and_rotate() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .stage { position: absolute; left: 0px; top: 0px; width: 300px; height: 300px;
                     perspective: 200px; }
            .scaled { position: absolute; left: 200px; top: 60px; width: 100px; height: 100px;
                      background-color: #111; transform: rotateX(60deg); scale: 2 1.5; }
            .turned { position: absolute; left: 200px; top: 60px; width: 100px; height: 100px;
                      background-color: #222; transform: rotateX(60deg); rotate: 180deg; }
            """,
            document => {
                var stage = document.Root.Add("div", classNames: "stage");
                stage.Add("div", classNames: "scaled");
                stage.Add("div", classNames: "turned");
            }
        );

        var scaled = Assert.IsType<UiTransform>(document.Root.Children[0].Children[0].Transform);
        var turned = Assert.IsType<UiTransform>(document.Root.Children[0].Children[1].Transform);

        // The instrument first: both really are projective, so a pair of numbers that happened to
        // match could not be two affines that never met a perspective at all.
        Assert.False(scaled.IsAffine);
        Assert.False(turned.IsAffine);

        var bottom = new Vector2(250f, 160f);

        var scaledAt = scaled.Apply(bottom);
        Assert.Equal(277.6335f, scaledAt.X, 0.01f);
        Assert.Equal(146.8092f, scaledAt.Y, 0.01f);

        // ⚠ And not where a perspective folded in beside the list puts it, which is 27.6 points
        // across and 5.5 down from the right answer — a difference no test on a centred child can
        // produce and one a card under a `perspective-normal` stage produces immediately.
        Assert.NotEqual(305.2670f, scaledAt.X, 0.01f);
        Assert.NotEqual(141.2825f, scaledAt.Y, 0.01f);

        var turnedAt = turned.Apply(bottom);
        Assert.Equal(277.6335f, turnedAt.X, 0.01f);
        Assert.Equal(67.0382f, turnedAt.Y, 0.01f);
        Assert.NotEqual(222.3665f, turnedAt.X, 0.01f);
        Assert.NotEqual(89.1450f, turnedAt.Y, 0.01f);
    }

    /// <summary>
    ///     A <c>perspective</c> written in <c>em</c> is measured in the font of the element that
    ///     declared it, which is the parent.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The only two properties this reader takes off another element are the only two
    ///         whose <c>em</c> belongs to another element.</b> A stage at <c>font-size: 32px</c>
    ///         declaring <c>perspective: 10em</c> means 320 points; measuring it in the caller's
    ///         context makes it 160, which is a projection twice as strong as authored and nothing
    ///         says so.
    ///     </para>
    ///     <para>
    ///         The card's bottom-centre is 50 below its origin, so <c>w = 1 − 50·sin60 / d</c> and the
    ///         point lands at <c>−50/w + 120</c> across. At the parent's 320 that is 62.18; at the
    ///         160 a caller-context reading gives, 51.45. The card's own <c>font-size: 16px</c> is
    ///         written explicitly so the two fonts cannot be confused by inheritance.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_perspective_in_em_is_measured_in_the_parents_font() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .stage { position: absolute; left: 20px; top: 20px; width: 200px; height: 200px;
                     font-size: 32px; perspective: 10em; }
            .card { position: absolute; left: 0px; top: 0px; width: 100px; height: 100px;
                    font-size: 16px; background-color: #111; transform: rotateX(60deg); }
            """,
            document => document.Root.Add("div", classNames: "stage").Add("div", classNames: "card")
        );

        var card = Assert.IsType<UiTransform>(document.Root.Children[0].Children[0].Transform);
        Assert.False(card.IsAffine);

        var at = card.Apply(new Vector2(70f, 120f));

        Assert.Equal(62.1756f, at.X, 0.01f);
        Assert.Equal(91.0878f, at.Y, 0.01f);

        // ⚠ Not the half-distance a reading in the caller's context gives. Both are plausible
        // pictures of a card tipped away, which is why the number is named rather than bounded.
        Assert.NotEqual(51.4473f, at.X, 0.01f);
        Assert.NotEqual(85.7237f, at.Y, 0.01f);
    }

    /// <summary>
    ///     <c>rotate3d</c> about x and <c>matrix3d</c> spelling a perspective are the same matrices
    ///     the named functions are.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A differential rather than two numbers, because the failure worth catching is a
    ///     transposed matrix and a transpose is invisible on a symmetric case.</b> Each row here is
    ///     written two ways — the named function and its general form — and the assertion is that
    ///     the two produce the same matrix to the last few bits. <c>matrix3d</c> takes CSS's
    ///     column-major listing, which IS this engine's row-major order, so a reader who transposes
    ///     "to be safe" fails the second row and no other.
    /// </remarks>
    /// <param name="named">The list written with the named functions.</param>
    /// <param name="general">The same list written with the general ones.</param>
    [Theory]
    [InlineData("perspective(200px) rotateX(60deg)", "perspective(200px) rotate3d(1, 0, 0, 60deg)")]
    [InlineData(
        "perspective(200px) rotateX(60deg)",
        "matrix3d(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, -0.005, 0, 0, 0, 1) rotateX(60deg)"
    )]
    [InlineData("perspective(400px) rotateY(25deg)", "perspective(400px) rotate3d(0, 1, 0, 25deg)")]
    [InlineData("perspective(300px) translateZ(40px)", "perspective(300px) translate3d(0, 0, 40px)")]
    [InlineData("perspective(300px) scaleZ(2) rotateX(40deg)", "perspective(300px) scale3d(1, 1, 2) rotateX(40deg)")]
    public void The_general_functions_spell_the_named_ones(string named, string general) {
        using var document = Drawn(
            $$"""
              root { width: 400px; height: 300px; }
              .one { position: absolute; left: 100px; top: 50px; width: 200px; height: 200px;
                     background-color: #111; transform: {{named}}; }
              .two { position: absolute; left: 100px; top: 50px; width: 200px; height: 200px;
                     background-color: #222; transform: {{general}}; }
              """,
            document => {
                document.Root.Add("div", classNames: "one");
                document.Root.Add("div", classNames: "two");
            }
        );

        var one = Assert.IsType<UiTransform>(document.Root.Children[0].Transform);
        var two = Assert.IsType<UiTransform>(document.Root.Children[1].Transform);

        // ⚠ The instrument: a pair that both came out the identity would agree perfectly and mean
        // nothing, and `perspective()` on its own IS the identity on a plane at z = 0.
        Assert.False(one.IsIdentity);
        Assert.False(one.IsAffine);

        Assert.Equal(one.M11, two.M11, 1e-4f);
        Assert.Equal(one.M12, two.M12, 1e-4f);
        Assert.Equal(one.M21, two.M21, 1e-4f);
        Assert.Equal(one.M22, two.M22, 1e-4f);
        Assert.Equal(one.Dx, two.Dx, 1e-3f);
        Assert.Equal(one.Dy, two.Dy, 1e-3f);
        Assert.Equal(one.M13, two.M13, 1e-7f);
        Assert.Equal(one.M23, two.M23, 1e-7f);
        Assert.Equal(one.M33, two.M33, 1e-5f);
    }

    /// <summary>
    ///     A list with nothing to move a point off the plane reduces to the same affine it always
    ///     did, and <c>perspective()</c> alone is the identity.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Both halves of the four-dimensional path's cost.</b> The first is that a
    ///         <c>perspective()</c> with no neighbour to move a point off z = 0 is <i>exactly</i>
    ///         nothing — <c>w = 1 − z/d</c> and every point of an element is at z = 0 — so the
    ///         element gets no transform at all rather than a group and a viewport-sized surface.
    ///         That is CSS's answer as well as this engine's.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The second is that a flat list still takes the closed-form fold, to the bit.</b>
    ///         Folding the origin in four dimensions is two matrix products where
    ///         <c>UiTransform.About</c> is a closed form, and the two differ in the last bit — on a
    ///         picture every committed screenshot in <c>Vixen.Ui.Controls.Tests</c> was rendered
    ///         against. So a list of flat functions is asserted to come out <i>exactly</i> the matrix
    ///         the properties beside it would compose to, and not merely near it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_flat_list_is_unchanged_and_a_lone_perspective_is_nothing() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .flat { position: absolute; left: 100px; top: 100px; width: 40px; height: 40px;
                    background-color: #111; transform: rotate(30deg) translate(12px, -7px) scale(1.4, 0.8); }
            .deep { position: absolute; left: 200px; top: 100px; width: 40px; height: 40px;
                    background-color: #222; transform: perspective(200px); }
            """,
            document => {
                document.Root.Add("div", classNames: "flat");
                document.Root.Add("div", classNames: "deep");
            }
        );

        var flat = Assert.IsType<UiTransform>(document.Root.Children[0].Transform);

        // The same three functions, composed by hand about the same origin: right to left, folded
        // once. Exact equality, because the flat branch has to be the arithmetic it always was.
        var about = new Vector2(120f, 120f);

        // ⚠ Right to left: the LAST function is applied to a point first, so the scale runs before
        // the translation and the translation before the rotation. Written the way it reads, this
        // expectation is the transpose of the right answer — which is right for a uniform scale and
        // wrong for this one, and is the mistake the non-uniform 1.4/0.8 is here to catch.
        var expected = new UiTransform(1.4f, 0f, 0f, 0.8f, 0f, 0f)
            .Then(new UiTransform(1f, 0f, 0f, 1f, 12f, -7f))
            .Then(UiTransform.Rotation(30f, Vector2.Zero))
            .About(about);

        Assert.Equal(expected, flat);
        Assert.True(flat.IsAffine);

        // And a perspective with nothing to project is no transform at all.
        Assert.Null(document.Root.Children[1].Transform);
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
