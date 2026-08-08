// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
///         ⚠ <b><c>rotate</c> and <c>scale</c> are refused, and there are no tests here for them
///         because there is nothing to test.</b> A <c>DrawCommand</c> is an axis-aligned rectangle and
///         the clip stack intersects rectangles, so a rotated box cannot be represented and a rotated
///         clip is not a rectangle at all — the per-axis <c>overflow</c> trick of pushing one pair of
///         edges to <c>DrawListBuilder.UnboundedClip</c> works precisely because what comes out is
///         still axis-aligned. Scaling is the box in four multiplications and the picture in none:
///         glyph advances are shaped at <c>run.Size</c> during layout, so a scaled subtree needs
///         re-shaping, which would make a transform affect layout — the one thing CSS Transforms 1 §3
///         says it must never do. Both are recorded in <c>InertProperties.txt</c> under the properties
///         CSS actually has, so the day a compositor arrives the gate's expiry check is what says so.
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
}
