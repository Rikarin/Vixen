// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Layout;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>CSS Box Alignment §4.1's <c>[ safe | unsafe ]? &lt;position&gt;</c>, written in CSS.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The layout could already do this and no stylesheet could ask for it, which is a state
///         every measured column in the parity ledger reads as correct.</b> <c>OverflowAlignment</c>,
///         the six <c>*Overflow</c> fields and <c>LayoutTree.SafeFallback</c>'s six call sites landed
///         with 76 conformance fixtures behind them — and those fixtures reach the store through
///         <c>TaffyStyleMap</c>, which calls the setters directly and never parses a declaration. So
///         every one of them would still pass with the CSS side deleted entirely. What is checked
///         here is the half the corpus is blind to.
///     </para>
///     <para>
///         ⚠ <b>Six properties and six tests, because a single-site sabotage proves nothing about the
///         other five.</b> <c>LayoutStyleBuilder.TryAlignment</c> is one method and
///         <c>ApplyKeywords</c> calls it six times, each writing a different pair of fields —
///         forgetting one <c>result.XOverflow =</c> line is the whole defect, and it is invisible from
///         the other five.
///     </para>
///     <para>
///         <b>Every assertion is a box, and every one has both halves.</b> A <c>safe</c> alignment
///         with room to spare is indistinguishable from an <c>unsafe</c> one — that is the definition,
///         not an accident — so a test that only measured the overflowing case would pass against a
///         reader that answered <c>FlexStart</c> unconditionally. Each test therefore pins the
///         overflowing box <i>and</i> the fitting one, and the unsafe twin's negative coordinate says
///         the free space really was negative.
///     </para>
/// </remarks>
public class SafeAlignmentFromCssTests {
    const float Tolerance = 0.001f;

    static UiDocument Laid(string css, Action<UiDocument> build) {
        var document = new UiDocument(400f, 300f);
        document.Load(css);
        build(document);
        document.Update();

        return document;
    }

    /// <summary>A child's offset inside its own container, the two boxes being siblings.</summary>
    /// <remarks>
    ///     ⚠ Relative and not absolute: the root is a flex row, so the second container of each pair
    ///     starts where the first one ends and an absolute coordinate would fold that in. The first
    ///     draft of this file asserted absolutes and read 200 for an item at its container's start.
    /// </remarks>
    static float OffsetLeft(UiElement host) => host.ChildList[0].AbsoluteLeft - host.AbsoluteLeft;

    /// <inheritdoc cref="OffsetLeft" />
    static float OffsetTop(UiElement host) => host.ChildList[0].AbsoluteTop - host.AbsoluteTop;

    /// <summary>One overflowing child in a 100-point cross axis, and one that fits.</summary>
    static (float Overflowing, float Fitting) CrossAxisTops(string declaration) {
        using var document = Laid(
            $$"""
              root { width: 400px; height: 300px; }
              .box { display: flex; width: 200px; height: 100px; {{declaration}} }
              .tall { width: 20px; height: 150px; flex-shrink: 0; }
              .short { width: 20px; height: 40px; flex-shrink: 0; }
              """,
            document => {
                document.Root.Add("div", null, "box").Add("div", null, "tall");
                document.Root.Add("div", null, "box").Add("div", null, "short");
            }
        );

        return (OffsetTop(document.Root.ChildList[0]), OffsetTop(document.Root.ChildList[1]));
    }

    [Fact]
    public void Align_items_end_overflows_upwards_and_the_safe_form_does_not() {
        // 100 − 150 = −50 of free space, spent above the line by `end` and at the end by `safe end`.
        Assert.Equal(-50f, CrossAxisTops("align-items: end").Overflowing, Tolerance);
        Assert.Equal(-50f, CrossAxisTops("align-items: unsafe end").Overflowing, Tolerance);
        Assert.Equal(0f, CrossAxisTops("align-items: safe end").Overflowing, Tolerance);

        // ⚠ And the half that stops this passing against a reader that always answers start: with
        // 60 points to spare, `safe end` is `end` — 100 − 40 — and the keyword is unobservable.
        Assert.Equal(60f, CrossAxisTops("align-items: end").Fitting, Tolerance);
        Assert.Equal(60f, CrossAxisTops("align-items: safe end").Fitting, Tolerance);
    }

    [Fact]
    public void Align_items_center_halves_a_negative_free_space_unless_it_is_safe() {
        Assert.Equal(-25f, CrossAxisTops("align-items: center").Overflowing, Tolerance);
        Assert.Equal(0f, CrossAxisTops("align-items: safe center").Overflowing, Tolerance);

        Assert.Equal(30f, CrossAxisTops("align-items: center").Fitting, Tolerance);
        Assert.Equal(30f, CrossAxisTops("align-items: safe center").Fitting, Tolerance);
    }

    [Fact]
    public void Align_self_carries_its_own_prefix_and_the_container_s_does_not_reach_it() {
        using var document = Laid(
            """
            root { width: 400px; height: 300px; }
            .box { display: flex; width: 200px; height: 100px; align-items: end; }
            .tall { width: 20px; height: 150px; flex-shrink: 0; }
            .safe { align-self: safe end; }
            """,
            document => {
                var host = document.Root.Add("div", null, "box");
                host.Add("div", null, "tall");
                host.Add("div", null, "tall", "safe");
            }
        );

        var host = document.Root.ChildList[0];

        Assert.Equal(-50f, host.ChildList[0].AbsoluteTop - host.AbsoluteTop, Tolerance);
        Assert.Equal(0f, host.ChildList[1].AbsoluteTop - host.AbsoluteTop, Tolerance);
    }

    [Fact]
    public void An_align_self_of_auto_inherits_the_container_s_prefix_with_its_position() {
        // ⚠ The half `LayoutTree.ResolveChildAlignmentOverflow` exists for, and the only path on
        // which the two fields of one declaration could come from two different elements. The child
        // says nothing at all; both the position and the `safe` have to arrive from the container.
        using var document = Laid(
            """
            root { width: 400px; height: 300px; }
            .box { display: flex; width: 200px; height: 100px; }
            .safe { align-items: safe end; }
            .unsafe { align-items: end; }
            .tall { width: 20px; height: 150px; flex-shrink: 0; }
            """,
            document => {
                document.Root.Add("div", null, "box", "unsafe").Add("div", null, "tall");
                document.Root.Add("div", null, "box", "safe").Add("div", null, "tall");
            }
        );

        Assert.Equal(-50f, OffsetTop(document.Root.ChildList[0]), Tolerance);
        Assert.Equal(0f, OffsetTop(document.Root.ChildList[1]), Tolerance);
    }

    [Fact]
    public void Justify_content_spends_a_negative_main_axis_free_space_at_the_end_when_safe() {
        using var document = Laid(
            """
            root { width: 400px; height: 300px; }
            .box { display: flex; width: 200px; height: 40px; }
            .safe { justify-content: safe end; }
            .unsafe { justify-content: end; }
            .wide { width: 150px; height: 20px; flex-shrink: 0; }
            """,
            document => {
                var unsafeHost = document.Root.Add("div", null, "box", "unsafe");
                unsafeHost.Add("div", null, "wide");
                unsafeHost.Add("div", null, "wide");

                var safeHost = document.Root.Add("div", null, "box", "safe");
                safeHost.Add("div", null, "wide");
                safeHost.Add("div", null, "wide");
            }
        );

        // Two 150-point items in 200 points is −100 of free space.
        Assert.Equal(-100f, OffsetLeft(document.Root.ChildList[0]), Tolerance);
        Assert.Equal(0f, OffsetLeft(document.Root.ChildList[1]), Tolerance);
    }

    [Fact]
    public void Align_content_moves_the_lines_and_gives_up_on_them_when_safe() {
        using var document = Laid(
            """
            root { width: 400px; height: 300px; }
            .box { display: flex; flex-wrap: wrap; width: 100px; height: 100px; }
            .safe { align-content: safe end; }
            .unsafe { align-content: end; }
            .cell { width: 60px; height: 60px; flex-shrink: 0; }
            """,
            document => {
                var unsafeHost = document.Root.Add("div", null, "box", "unsafe");
                unsafeHost.Add("div", null, "cell");
                unsafeHost.Add("div", null, "cell");

                var safeHost = document.Root.Add("div", null, "box", "safe");
                safeHost.Add("div", null, "cell");
                safeHost.Add("div", null, "cell");
            }
        );

        // Two 60-point lines in 100 points is −20 of free space across the lines.
        Assert.Equal(-20f, OffsetTop(document.Root.ChildList[0]), Tolerance);
        Assert.Equal(0f, OffsetTop(document.Root.ChildList[1]), Tolerance);
    }

    [Fact]
    public void Justify_items_places_a_grid_item_in_its_area_and_the_safe_form_keeps_it_inside() {
        using var document = Laid(
            """
            root { width: 400px; height: 300px; }
            .grid { display: grid; grid-template-columns: 100px; width: 100px; height: 60px; }
            .safe { justify-items: safe end; }
            .unsafe { justify-items: end; }
            .wide { width: 150px; height: 20px; }
            """,
            document => {
                document.Root.Add("div", null, "grid", "unsafe").Add("div", null, "wide");
                document.Root.Add("div", null, "grid", "safe").Add("div", null, "wide");
            }
        );

        Assert.Equal(-50f, OffsetLeft(document.Root.ChildList[0]), Tolerance);
        Assert.Equal(0f, OffsetLeft(document.Root.ChildList[1]), Tolerance);
    }

    [Fact]
    public void Justify_self_overrides_the_container_s_prefix_as_well_as_its_position() {
        using var document = Laid(
            """
            root { width: 400px; height: 300px; }
            .grid { display: grid; grid-template-columns: 100px; width: 100px; height: 60px;
                    justify-items: end; }
            .wide { width: 150px; height: 20px; }
            .safe { justify-self: safe end; }
            """,
            document => {
                document.Root.Add("div", null, "grid").Add("div", null, "wide");
                document.Root.Add("div", null, "grid").Add("div", null, "wide", "safe");
            }
        );

        Assert.Equal(-50f, OffsetLeft(document.Root.ChildList[0]), Tolerance);
        Assert.Equal(0f, OffsetLeft(document.Root.ChildList[1]), Tolerance);
    }

    // ── What the bridge refuses, and why each refusal is not an omission ────────────────────────

    /// <summary>The prefix is valid on a position and on nothing else.</summary>
    /// <remarks>
    ///     ⚠ <b>A refusal here is the whole declaration and not just the prefix</b>, which is what CSS
    ///     does with a value it cannot parse and is the difference that matters: dropping only the
    ///     `safe` would leave `align-items: safe stretch` stretching, so a browser and this engine
    ///     would disagree about a declaration both had read.
    /// </remarks>
    /// <remarks>
    ///     ⚠ <b><c>align-items: safe</c> and <c>align-items: safe center extra</c> are absent from
    ///     this list and it is not because they work.</b> Neither reaches the bridge at all: ExCSS's
    ///     `ConditionalStartsWithValueConverter` matches the prefix, fails to match a position after
    ///     it, and dereferences a null — so `StyleEngine.Load` throws and the whole sheet is lost.
    ///     Four properties do it (`align-items`, `align-self`, `align-content`, `justify-content`)
    ///     and the three ExCSS has no converter for do not. Filed as `Rikarin/Vixen#530`; the rows
    ///     belong here the day the loader drops the declaration instead of crashing.
    /// </remarks>
    [Theory]
    [InlineData("align-items: safe stretch", Align.Stretch)]
    [InlineData("align-items: safe baseline", Align.Stretch)]
    [InlineData("align-items: last baseline", Align.Stretch)]
    [InlineData("align-items: sideways center", Align.Stretch)]
    public void An_overflow_position_on_anything_but_a_position_drops_the_declaration(
        string declaration,
        Align expected
    ) {
        var style = new BridgeFixture().Build(declaration);

        Assert.Equal(expected, style.AlignItems);
        Assert.Equal(OverflowAlignment.Unsafe, style.AlignItemsOverflow);
    }

    [Fact]
    public void Safe_space_between_is_refused_on_the_content_distributions_too() {
        var style = new BridgeFixture().Build("justify-content: safe space-between");

        Assert.Equal(Justify.FlexStart, style.JustifyContent);
        Assert.Equal(OverflowAlignment.Unsafe, style.JustifyContentOverflow);
    }

    /// <summary>A later bare keyword replaces both halves, because they are one property.</summary>
    [Fact]
    public void An_unprefixed_keyword_clears_a_prefix_an_earlier_rule_set() {
        var style = new BridgeFixture().Build("align-items: safe end; align-items: end");

        Assert.Equal(Align.FlexEnd, style.AlignItems);
        Assert.Equal(OverflowAlignment.Unsafe, style.AlignItemsOverflow);
    }

    /// <summary><c>unsafe</c> is spellable and means the default, which is not the same as unspelt.</summary>
    [Theory]
    [InlineData("align-items: safe center", OverflowAlignment.Safe)]
    [InlineData("align-items: unsafe center", OverflowAlignment.Unsafe)]
    [InlineData("align-items: center", OverflowAlignment.Unsafe)]
    public void The_prefix_is_read_off_the_declaration_and_defaults_to_unsafe(
        string declaration,
        OverflowAlignment expected
    ) {
        var style = new BridgeFixture().Build(declaration);

        Assert.Equal(Align.Center, style.AlignItems);
        Assert.Equal(expected, style.AlignItemsOverflow);
    }

    /// <summary>All six properties carry the prefix, none of them onto a neighbour's field.</summary>
    /// <remarks>
    ///     The cheap half of the six tests above: it cannot see a box move, but it does catch the
    ///     paste error the box tests are individually blind to — a seventh call site writing
    ///     <c>AlignItemsOverflow</c> because that is what the line above it said.
    /// </remarks>
    [Fact]
    public void Each_of_the_six_writes_its_own_overflow_field_and_only_its_own() {
        var style = new BridgeFixture().Build(
            """
            align-items: safe center; align-self: end; align-content: end;
            justify-content: end; justify-items: end; justify-self: end;
            """
        );

        Assert.Equal(OverflowAlignment.Safe, style.AlignItemsOverflow);
        Assert.Equal(OverflowAlignment.Unsafe, style.AlignSelfOverflow);
        Assert.Equal(OverflowAlignment.Unsafe, style.AlignContentOverflow);
        Assert.Equal(OverflowAlignment.Unsafe, style.JustifyContentOverflow);
        Assert.Equal(OverflowAlignment.Unsafe, style.JustifyItemsOverflow);
        Assert.Equal(OverflowAlignment.Unsafe, style.JustifySelfOverflow);

        var all = new BridgeFixture().Build(
            """
            align-items: safe center; align-self: safe end; align-content: safe end;
            justify-content: safe end; justify-items: safe end; justify-self: safe end;
            """
        );

        Assert.Equal(OverflowAlignment.Safe, all.AlignItemsOverflow);
        Assert.Equal(OverflowAlignment.Safe, all.AlignSelfOverflow);
        Assert.Equal(OverflowAlignment.Safe, all.AlignContentOverflow);
        Assert.Equal(OverflowAlignment.Safe, all.JustifyContentOverflow);
        Assert.Equal(OverflowAlignment.Safe, all.JustifyItemsOverflow);
        Assert.Equal(OverflowAlignment.Safe, all.JustifySelfOverflow);
    }

    /// <summary><c>normal</c> is the initial value spelt out, on both alignment tables.</summary>
    [Fact]
    public void Normal_reaches_the_bridge_and_undoes_a_position_an_earlier_rule_set() {
        var justify = new BridgeFixture().Build("justify-content: center; justify-content: normal");
        Assert.Equal(Justify.FlexStart, justify.JustifyContent);

        var content = new BridgeFixture().Build("align-content: center; align-content: normal");
        Assert.Equal(Align.Stretch, content.AlignContent);

        var items = new BridgeFixture().Build("justify-items: center; justify-items: normal");
        Assert.Equal(Align.Stretch, items.JustifyItems);
    }

    // ── The compound keyword that was never readable ────────────────────────────────────────────

    /// <summary>CSS Grid §8.5's <c>[ row | column ] || dense</c>, all seven spellings.</summary>
    /// <remarks>
    ///     ⚠ <b>Four of these were dead and the family measured green over them</b>, because
    ///     <c>grid-auto-flow</c> is read by the three one-word spellings and the consumption gate
    ///     scores a family, not a value. See `Rikarin/Vixen#528`.
    /// </remarks>
    [Theory]
    [InlineData("row", GridAutoFlow.Row)]
    [InlineData("column", GridAutoFlow.Column)]
    [InlineData("dense", GridAutoFlow.RowDense)]
    [InlineData("row dense", GridAutoFlow.RowDense)]
    [InlineData("dense row", GridAutoFlow.RowDense)]
    [InlineData("column dense", GridAutoFlow.ColumnDense)]
    [InlineData("dense column", GridAutoFlow.ColumnDense)]
    public void Every_spelling_of_grid_auto_flow_reaches_the_layout(string value, GridAutoFlow expected) =>
        Assert.Equal(expected, new BridgeFixture().Build($"grid-auto-flow: {value}").GridAutoFlow);

    [Theory]
    [InlineData("dense dense")]
    [InlineData("row column")]
    [InlineData("row dense column")]
    public void A_grid_auto_flow_the_grammar_does_not_allow_leaves_the_initial_value(string value) =>
        Assert.Equal(GridAutoFlow.Row, new BridgeFixture().Build($"grid-auto-flow: {value}").GridAutoFlow);
}
