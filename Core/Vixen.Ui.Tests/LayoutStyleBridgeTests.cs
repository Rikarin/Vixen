// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Layout;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>The step between the cascade and layout.</summary>
public class LayoutStyleBridgeTests {
    const float Tolerance = 0.001f;

    [Fact]
    public void An_element_with_no_declarations_gets_CSS_initial_values_and_not_Yoga_s() {
        var style = new BridgeFixture().Build("color: red");

        // ⚠ All five of these differ between the two specifications, and Vixen.Ui.Layout is right to
        // start from Yoga's — it is judged by Yoga's conformance suite. The bridge is where a VCSS
        // author's expectations take over.
        Assert.Equal(FlexDirection.Row, style.FlexDirection);
        Assert.Equal(Align.Stretch, style.AlignContent);
        Assert.Equal(PositionType.Static, style.PositionType);
        Assert.Equal(BoxSizing.ContentBox, style.BoxSizing);

        // ⚠ The fifth arrived last and was the one that changed what documents looked like rather
        // than only what an author had to type. `StyleResolution.ResolveFlexShrink` reads an unset
        // shrink as Yoga's 0, so this has to be a written 1 rather than a NaN. See #628, and
        // `FlexShrinkFromCssTests` for the geometry it buys.
        Assert.Equal(1f, style.FlexShrink);
        Assert.True(float.IsNaN(LayoutStyle.Default.FlexShrink));

        Assert.NotEqual(LayoutStyle.Default.FlexDirection, style.FlexDirection);
        Assert.NotEqual(LayoutStyle.Default.PositionType, style.PositionType);
    }

    [Theory]
    [InlineData("width: 42px", 42f)]
    [InlineData("width: 2em", 32f)]
    [InlineData("width: 2rem", 32f)]
    [InlineData("width: 10vw", 100f)]
    [InlineData("width: 10vh", 50f)]
    [InlineData("width: 10vmin", 50f)]
    [InlineData("width: 10vmax", 100f)]
    [InlineData("width: 0", 0f)]
    public void A_relative_length_is_resolved_against_its_context(string css, float expected) {
        // A 1000x500 viewport and a 16px font, so every unit lands on a different number and a
        // wrong one cannot pass by coincidence.
        var style = new BridgeFixture().Build(css);

        Assert.Equal(LayoutUnit.Point, style.Dimensions[(int) Dimension.Width].Unit);
        Assert.Equal(expected, style.Dimensions[(int) Dimension.Width].Value, Tolerance);
    }

    [Fact]
    public void A_percentage_is_handed_on_unresolved() {
        var style = new BridgeFixture().Build("width: 50%");

        // The one place where doing less is correct: only layout knows the containing block, so
        // resolving this here would be resolving it against the wrong thing.
        Assert.Equal(LayoutUnit.Percent, style.Dimensions[(int) Dimension.Width].Unit);
        Assert.Equal(50f, style.Dimensions[(int) Dimension.Width].Value, Tolerance);
    }

    [Fact]
    public void An_em_on_font_size_is_the_parent_s_and_everywhere_else_is_the_element_s() {
        var fixture = new BridgeFixture();

        // 1.5em of a 20px parent is 30px.
        Assert.Equal(30f, fixture.FontSize("font-size: 1.5em", 20f), Tolerance);

        // And 50% means the same thing on this property alone.
        Assert.Equal(10f, new BridgeFixture().FontSize("font-size: 50%", 20f), Tolerance);

        // Whereas `width: 1.5em` measures against the element's own size, which the caller has
        // already resolved and put in the context. Conflating the two compounds down the tree.
        var style = new BridgeFixture().Build(
            "width: 1.5em",
            LengthContext.ForViewport(1000f, 500f).WithFontSize(20f)
        );

        Assert.Equal(30f, style.Dimensions[(int) Dimension.Width].Value, Tolerance);
    }

    [Fact]
    public void A_font_size_chain_compounds() {
        var fixture = new BridgeFixture();
        var size = LengthContext.InitialFontSize;

        // Three nested 1.2em are 1.728x, not 1.2x. Resolving `em` against the element's own size
        // instead of its parent's gives the second answer, and the error grows with depth — which
        // is what makes it look like a rendering quirk rather than an arithmetic one.
        for (var depth = 0; depth < 3; depth++) {
            size = fixture.FontSize("font-size: 1.2em", size);
        }

        Assert.Equal(LengthContext.InitialFontSize * 1.728f, size, Tolerance);
    }

    [Fact]
    public void The_viewport_units_the_sizing_utilities_emit_reach_the_dimensions() {
        // ⚠ This is the far end of `w-dvw` and `h-svh`, and it is asserted here rather than left to
        // the utility test because the two halves fail independently: the family table can emit a
        // perfectly good `100vw` into a sheet whose units nothing resolves, which is a class that
        // generates, cascades and moves nothing. `docs/plan/43` counts the six viewport keywords as
        // closed on the strength of this path existing.
        //
        // ⚠ The same file used to record, one column over, that the *content* keywords on those
        // roots had no such far end and were dropped by `LayoutStyleBuilder.ToEdgeLength`. They have
        // one now — see the two tests below — and it is a longer one, which is why theirs is
        // asserted on a resolved BOX rather than on a `StyleLength`. A viewport unit is a number by
        // the time it leaves this file; `min-content` is still a keyword, and a test that stopped
        // here would have passed over a declaration that nothing measured.
        var style = new BridgeFixture().Build(
            "width: 100vw; height: 100vh; max-width: 50vw; min-height: 20vh",
            LengthContext.ForViewport(1000f, 500f)
        );

        Assert.Equal(1000f, style.Dimensions[(int) Dimension.Width].Value, Tolerance);
        Assert.Equal(500f, style.Dimensions[(int) Dimension.Height].Value, Tolerance);
        Assert.Equal(500f, style.MaxDimensions[(int) Dimension.Width].Value, Tolerance);
        Assert.Equal(100f, style.MinDimensions[(int) Dimension.Height].Value, Tolerance);
    }

    /// <summary>Three ten-point words: 30 across on one line, 10 across on three.</summary>
    static LayoutSize MeasureThreeWords(in MeasureRequest request) {
        const float word = 10f;
        const int words = 3;

        var width = request.WidthMode switch {
            MeasureMode.Exactly => request.AvailableWidth,
            MeasureMode.AtMost => MathF.Max(word, MathF.Min(request.AvailableWidth, words * word)),
            _ => words * word
        };

        var perLine = MathF.Max(1f, MathF.Floor(width / word));

        return new LayoutSize(width, request.HeightMode == MeasureMode.Exactly ? request.AvailableHeight : MathF.Ceiling(words / perLine) * 10f);
    }

    /// <summary>Lays a styled box with three words in it out inside a 500-point block container.</summary>
    static (float Width, float Height) BoxFor(string declarations) {
        var style = new BridgeFixture().Build(declarations, LengthContext.ForViewport(1000f, 500f));

        using var tree = new LayoutTree();

        var root = tree.CreateNode();
        tree.SetDisplay(root, Display.Block);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(500f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(500f));

        var box = tree.CreateNode();
        tree.SetStyle(box, in style);
        tree.AddChild(root, box);

        var text = tree.CreateNode();
        tree.SetMeasureFunction(text, MeasureThreeWords);
        tree.AddChild(box, text);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        return (tree.GetWidth(box), tree.GetHeight(box));
    }

    [Theory]
    [InlineData("width: min-content", 10f)]
    [InlineData("width: max-content", 30f)]
    [InlineData("width: fit-content", 30f)]
    [InlineData("max-width: min-content", 10f)]
    [InlineData("min-width: max-content; width: 5px", 30f)]
    public void The_content_keywords_the_sizing_utilities_emit_move_the_box(string declarations, float expected) {
        // ⚠ THE ASSERTION IS ON A BOX AND NOT ON A `StyleLength`, and that is the whole point of the
        // test. `width: min-content` arrived in `LayoutStyle.Dimensions` as a well-formed keyword
        // long before it did anything: `Resolve` answers NaN for one, so `SetLength` left the
        // dimension alone and every reader downstream took the declaration for its own absence.
        // Thirteen Tailwind sizing roots resolved, cascaded and moved nothing, and both the utility
        // gate and the cascade tests were green over all of them. Nothing short of laying the box
        // out can tell the two states apart.
        //
        // The container is 500 wide and the box is in normal flow, so CSS 2.1 §10.3.3 would give an
        // `auto` width the whole of it. Every number below is therefore one the old behaviour could
        // not have produced.
        Assert.Equal(expected, BoxFor(declarations).Width, Tolerance);
    }

    [Fact]
    public void A_content_keyword_on_the_block_axis_is_measured_at_the_width_the_inline_axis_settled() {
        // The two halves of one declaration, in the order CSS Sizing § 4.1 resolves them: ten across
        // puts one word on each of three lines, so the content-based height is thirty rather than
        // the one line a height measured against the container's 500 would have found.
        var box = BoxFor("width: min-content; height: max-content");

        Assert.Equal(10f, box.Width, Tolerance);
        Assert.Equal(30f, box.Height, Tolerance);
    }

    [Fact]
    public void Rem_ignores_the_element_s_own_font_size() {
        var deep = LengthContext.ForViewport(1000f, 500f, rootFontSize: 16f).WithFontSize(40f);
        var style = new BridgeFixture().Build("width: 2rem; height: 2em", deep);

        Assert.Equal(32f, style.Dimensions[(int) Dimension.Width].Value, Tolerance);
        Assert.Equal(80f, style.Dimensions[(int) Dimension.Height].Value, Tolerance);
    }

    [Theory]
    [InlineData("flex-direction: column", "FlexDirection", "Column")]
    [InlineData("justify-content: space-between", "JustifyContent", "SpaceBetween")]
    [InlineData("align-items: center", "AlignItems", "Center")]
    [InlineData("align-self: flex-end", "AlignSelf", "FlexEnd")]
    [InlineData("position: absolute", "PositionType", "Absolute")]
    [InlineData("flex-wrap: wrap-reverse", "FlexWrap", "WrapReverse")]
    [InlineData("overflow: hidden", "OverflowX", "Hidden")]
    [InlineData("overflow: hidden", "OverflowY", "Hidden")]
    [InlineData("overflow-x: scroll", "OverflowX", "Scroll")]
    [InlineData("overflow-y: auto", "OverflowY", "Scroll")]
    [InlineData("display: none", "Display", "None")]
    [InlineData("display: block", "Display", "Block")]

    // ⚠ Not an alias for `block`: a flow root establishes a block formatting context whatever its
    // `overflow` says, which is the whole content of the keyword. See `Display.FlowRoot`.
    [InlineData("display: flow-root", "Display", "FlowRoot")]
    [InlineData("float: left", "Float", "Left")]
    [InlineData("float: right", "Float", "Right")]
    [InlineData("float: none", "Float", "None")]
    [InlineData("clear: left", "Clear", "Left")]
    [InlineData("clear: both", "Clear", "Both")]

    // ⚠ <b>The logical keywords ARE mapped now, and these two cases used to assert the opposite.</b>
    // The comment here said `float: inline-start` resolving to `Left` would be right in an LTR
    // container and wrong in an RTL one inside the same declaration — which is true of an ALIAS and
    // was read as if it were true of the keyword. `FloatSide` gained a flow-relative pair beside its
    // physical one, and the resolution happens in the layout where the direction is known, so the
    // bridge's job is only to carry the keyword across intact. The physical pair still does not flip,
    // which is why there are four values and not two.
    [InlineData("float: inline-start", "Float", "InlineStart")]
    [InlineData("float: inline-end", "Float", "InlineEnd")]
    [InlineData("clear: inline-start", "Clear", "InlineStart")]
    [InlineData("clear: inline-end", "Clear", "InlineEnd")]
    [InlineData("box-sizing: border-box", "BoxSizing", "BorderBox")]
    [InlineData("direction: rtl", "Direction", "Rtl")]
    public void A_keyword_becomes_its_enum(string css, string field, string expected) {
        var style = new BridgeFixture().Build(css);
        var value = typeof(LayoutStyle).GetField(field)!.GetValue(style)!;

        Assert.Equal(expected, value.ToString());
    }

    [Theory]
    [InlineData("scrollbar-width: 15px", 15f)]
    [InlineData("scrollbar-width: 0", 0f)]
    [InlineData("scrollbar-width: none", 0f)]
    [InlineData("scrollbar-width: 1rem", 16f)]
    [InlineData("color: red", 0f)]
    public void The_scrollbar_gutter_crosses_the_bridge_as_a_length(string css, float expected) {
        // ⚠ A length where the web has `auto | thin | none`, because nothing here owns the widget
        // the web keyword is a preference about — see `LayoutStyleBuilder.ApplyScrollbar`. `none` is
        // kept because a stylesheet turning a gutter off should not have to spell it `0`.
        Assert.Equal(expected, new BridgeFixture().Build(css).ScrollbarWidth, Tolerance);
    }

    [Fact]
    public void A_keyword_the_bridge_does_not_know_leaves_the_initial_value_alone() {
        var style = new BridgeFixture().Build("justify-content: legacy-nonsense; align-items: center");

        // CSS says an invalid declaration is dropped, not that it means the first thing in an enum.
        Assert.Equal(LayoutStyleBuilder.CssInitial.JustifyContent, style.JustifyContent);
        Assert.Equal(Align.Center, style.AlignItems);
    }

    [Fact]
    public void An_unparseable_length_leaves_the_initial_value_alone() {
        // ⚠ Written against inline declarations, not a stylesheet, and that is the whole point.
        // ExCSS drops what it cannot parse, so `width: 4furlongs` in a stylesheet never reaches the
        // cascade — a test written that way passes whatever the bridge does with a bad value,
        // including overwriting a good one. Sabotage caught exactly that: removing the guard broke
        // nothing until this test went through a path ExCSS does not vet.
        // And the value has to be one that *parses* but is not a length, because TryValue already
        // rejects anything the parser cannot read at all. A bare `5` and a duration are both
        // perfectly good CSS values that mean nothing as a width.
        var style = new BridgeFixture().BuildInline(("width", "5"), ("min-width", "200ms"), ("height", "8px"));

        // Zero is a valid answer that happens to be invisible, so using it for "I did not
        // understand this" turns one typo into a missing element with nothing said about it.
        Assert.Equal(LayoutUnit.Auto, style.Dimensions[(int) Dimension.Width].Unit);
        Assert.False(style.MinDimensions[(int) Dimension.Width].IsDefined);
        Assert.Equal(8f, style.Dimensions[(int) Dimension.Height].Value, Tolerance);
    }

    [Fact]
    public void A_box_shorthand_arrives_already_expanded_into_longhands() {
        // ⚠ ExCSS expands `margin`, `padding`, `border-width`, `gap` and `flex` while parsing,
        // exactly as a browser does, so the cascade never sees those words. The bridge was first
        // written to expand them itself and its tests said every one of those paths was dead.
        var four = new BridgeFixture().Build("margin: 1px 2px 3px 4px");

        Assert.Equal(1f, four.Margin[(int) Edge.Top].Value, Tolerance);
        Assert.Equal(2f, four.Margin[(int) Edge.Right].Value, Tolerance);
        Assert.Equal(3f, four.Margin[(int) Edge.Bottom].Value, Tolerance);
        Assert.Equal(4f, four.Margin[(int) Edge.Left].Value, Tolerance);
        Assert.False(four.Margin[(int) Edge.All].IsDefined);
    }

    [Fact]
    public void A_shorthand_written_after_a_longhand_wins_because_it_expanded_first() {
        // The question that decides whether expansion-on-parse is enough. A browser gives 8px here;
        // by the time the cascade runs this is two `margin-left` declarations and the later one
        // wins, so document order does the work and the bridge needs no notion of it.
        var later = new BridgeFixture().Build("margin-left: 0px; margin: 8px");
        Assert.Equal(8f, later.Margin[(int) Edge.Left].Value, Tolerance);

        var earlier = new BridgeFixture().Build("margin: 8px; margin-left: 0px");
        Assert.Equal(0f, earlier.Margin[(int) Edge.Left].Value, Tolerance);
    }

    [Fact]
    public void Auto_survives_as_auto_rather_than_becoming_a_number() {
        var style = new BridgeFixture().Build("margin-left: auto; width: auto; flex-basis: auto");

        Assert.Equal(LayoutUnit.Auto, style.Margin[(int) Edge.Left].Unit);
        Assert.Equal(LayoutUnit.Auto, style.Dimensions[(int) Dimension.Width].Unit);
        Assert.Equal(LayoutUnit.Auto, style.FlexBasis.Unit);
    }

    [Fact]
    public void Border_width_reaches_the_border_edges_and_not_the_padding_ones() {
        var style = new BridgeFixture().Build("border-width: 2px; border-left-width: 5px");

        Assert.Equal(2f, style.Border[(int) Edge.Top].Value, Tolerance);
        Assert.Equal(5f, style.Border[(int) Edge.Left].Value, Tolerance);
        Assert.False(style.Padding[(int) Edge.Top].IsDefined);
    }

    [Fact]
    public void Inset_and_its_longhands_reach_the_position_edges() {
        var style = new BridgeFixture().Build("inset: 3px; top: 7px");

        Assert.Equal(3f, style.Position[(int) Edge.All].Value, Tolerance);
        Assert.Equal(7f, style.Position[(int) Edge.Top].Value, Tolerance);
    }

    [Fact]
    public void Gap_puts_the_row_first_because_that_is_what_CSS_says() {
        // `gap: <row> <column>` — the opposite order to the enum, and exactly the sort of thing
        // that reads correct and renders transposed. ExCSS expands it and gets the order right.
        var pair = new BridgeFixture().Build("gap: 4px 12px");
        Assert.Equal(4f, pair.Gap[(int) Gutter.Row].Value, Tolerance);
        Assert.Equal(12f, pair.Gap[(int) Gutter.Column].Value, Tolerance);

        var longhand = new BridgeFixture().Build("row-gap: 1px; column-gap: 2px");
        Assert.Equal(1f, longhand.Gap[(int) Gutter.Row].Value, Tolerance);
        Assert.Equal(2f, longhand.Gap[(int) Gutter.Column].Value, Tolerance);
    }

    [Fact]
    public void An_aspect_ratio_can_be_written_either_way() {
        Assert.Equal(16f / 9f, new BridgeFixture().Build("aspect-ratio: 16 / 9").AspectRatio, Tolerance);
        Assert.Equal(1.5f, new BridgeFixture().Build("aspect-ratio: 1.5").AspectRatio, Tolerance);
    }

    [Fact]
    public void The_flex_numbers_are_numbers_and_not_lengths() {
        var style = new BridgeFixture().Build("flex-grow: 2; flex-shrink: 0; flex-basis: 30%");

        Assert.Equal(2f, style.FlexGrow, Tolerance);
        Assert.Equal(0f, style.FlexShrink, Tolerance);
        Assert.Equal(LayoutUnit.Percent, style.FlexBasis.Unit);
        Assert.Equal(30f, style.FlexBasis.Value, Tolerance);
    }

    [Fact]
    public void Min_and_max_dimensions_are_kept_apart_from_the_requested_one() {
        var style = new BridgeFixture().Build("width: 10px; min-width: 20px; max-height: 30px");

        Assert.Equal(10f, style.Dimensions[(int) Dimension.Width].Value, Tolerance);
        Assert.Equal(20f, style.MinDimensions[(int) Dimension.Width].Value, Tolerance);
        Assert.Equal(30f, style.MaxDimensions[(int) Dimension.Height].Value, Tolerance);
        Assert.False(style.MinDimensions[(int) Dimension.Height].IsDefined);
    }

    [Fact]
    public void Inset_is_the_one_shorthand_the_bridge_expands_itself() {
        // ExCSS does not know `inset`, so it passes the text through whole and the four-value form
        // has to be read here. The longhands it does know still land on their own slots.
        var four = new BridgeFixture().Build("inset: 1px 2px 3px 4px");

        Assert.Equal(1f, four.Position[(int) Edge.Top].Value, Tolerance);
        Assert.Equal(2f, four.Position[(int) Edge.Right].Value, Tolerance);
        Assert.Equal(3f, four.Position[(int) Edge.Bottom].Value, Tolerance);
        Assert.Equal(4f, four.Position[(int) Edge.Left].Value, Tolerance);

        var two = new BridgeFixture().Build("inset: 5px 6px");
        Assert.Equal(5f, two.Position[(int) Edge.Vertical].Value, Tolerance);
        Assert.Equal(6f, two.Position[(int) Edge.Horizontal].Value, Tolerance);
    }

    /// <summary>A <c>calc()</c> folds on the way through, so layout is handed a number.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The folding is asserted in <c>CalcTests</c> against the parser; this asserts the
    ///         wire, which is the half that could have been missing.</b> Doc 43 § D1 and #240 ask for
    ///         <c>calc()</c> "resolved in the cascade", and a parser that folds it is not by itself
    ///         that: the bridge reads declarations through its own
    ///         <c>StyleValueParser</c> instance, and a second reader that did not — an edge shorthand
    ///         split by hand, a size read as text — would leave the class computing correctly and the
    ///         box the wrong size. Nothing else in this file goes through <c>calc()</c>.
    ///     </para>
    ///     <para>
    ///         The relative cases are here rather than in <c>CalcTests</c> for the reason the whole
    ///         file exists: <c>1rem + 4px</c> cannot fold in the parser, which has no idea what a
    ///         <c>rem</c> is worth, so <c>calc(1rem + 4px)</c> is a mixed-unit sum and correctly
    ///         refused — while <c>calc(1rem * 2)</c> folds to <c>2rem</c> and is only then measured.
    ///         Both are asserted so that a widened evaluator, which would answer the first with a
    ///         plausible wrong pixel count, goes red here.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_calc_is_folded_before_layout_sees_it() {
        // 2 + 2 in pixels, and the sum is not either operand — a bridge that took the first number it
        // found would answer 2.
        Assert.Equal(4f, Width("width: calc(2px + 2px)"), Tolerance);

        // Folded to `2rem` in the parser and measured against the 16px root here, which is the two
        // stages in one declaration.
        Assert.Equal(32f, Width("width: calc(1rem * 2)"), Tolerance);

        // ⚠ And a mixed-unit sum is refused rather than guessed at, so the width keeps CSS's initial
        // `auto` instead of arriving as a plausible number. `calc(100% - 10px)` is the same refusal
        // and the one a stylesheet author is likeliest to write.
        //
        // ⚠ `auto` and not undefined, which is worth being exact about: `Set` writes nothing when a
        // declaration is not understood, so what survives is the initial value the bridge already
        // put there — asserting `IsDefined` is false here would be asserting that the bridge has no
        // initial values, and it does.
        Assert.Equal(
            LayoutUnit.Auto,
            new BridgeFixture().Build("width: calc(1rem + 4px)").Dimensions[(int) Dimension.Width].Unit
        );

        static float Width(string css) =>
            new BridgeFixture().Build(css).Dimensions[(int) Dimension.Width].Value;
    }
}
