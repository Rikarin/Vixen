// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>What an over-full row does to items that never mentioned <c>flex-shrink</c>.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The corpus cannot see this and neither can Yoga's suite.</b> Taffy's fixtures reach
///         the store through <c>TaffyStyleMap</c>, which writes <c>flex-shrink: 1</c> onto every
///         non-root node before a fixture's own attributes are read — its own remark says skipping
///         that "does not produce a few wrong fixtures, it produces thousands". Yoga's 534 go the
///         other way and are judged against Yoga's initial value, which is <c>0</c>. So the only
///         thing that can catch the engine's own bridge starting at the wrong one is a test that
///         goes in through a stylesheet, which is what these do.
///     </para>
///     <para>
///         Every number below is closed-form rather than recorded: equal bases with equal shrink
///         factors take equal shares of the deficit, so <i>n</i> such items in an over-full row are
///         each exactly the container over <i>n</i>, and their sum is exactly the container. A
///         browser is not needed to know that, and neither is a previous run of this engine.
///     </para>
/// </remarks>
public class FlexShrinkFromCssTests {
    const float Tolerance = 0.001f;

    static UiDocument Laid(string css, int items, string itemClass = "item") {
        var document = new UiDocument(400f, 300f);
        document.Load(css);

        for (var i = 0; i < items; i++) {
            document.Root.Add("div", classNames: itemClass);
        }

        document.Update();

        return document;
    }

    /// <summary>Three 300-wide items in a 300-wide row are 100 each, because nothing said otherwise.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This was the whole of <c>Rikarin/Vixen#628</c>.</b> <c>LayoutStyle.Default</c>
    ///         leaves <c>FlexShrink</c> unset and <c>StyleResolution.ResolveFlexShrink</c> reads
    ///         unset as Yoga's <c>0</c>, so before the bridge wrote CSS's <c>1</c> these three
    ///         stayed 300 wide and hung 600 points out of their container — in every <c>.vcss</c>
    ///         and <c>.vxml</c> in the tree.
    ///     </para>
    ///     <para>
    ///         The container is 300 rather than a rounder 400 so that a third of it is a whole
    ///         number of pixels. Layout rounds to the device grid, and 133⅓ against a tolerance of
    ///         a thousandth is a test about rounding wearing a test about shrinking.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_item_that_never_mentioned_flex_shrink_shrinks() {
        using var document = Laid(
            """
            root { width: 300px; height: 300px; }
            .item { width: 300px; height: 10px; }
            """,
            items: 3
        );

        var items = document.Root.ChildList;

        // The container over three. Not a recorded number: the three bases are equal and so are the
        // three shrink factors, so the deficit divides three ways whatever it is.
        foreach (var item in items) {
            Assert.Equal(100f, item.Width, Tolerance);
        }

        // …and the same law said as a sum: nothing overflows and nothing is left over. A shrink
        // factor that were merely *nonzero but wrong* would still pass the line above on the first
        // item and fail here.
        Assert.Equal(300f, items.Sum(static item => item.Width), Tolerance);
        Assert.Equal(200f, items[2].AbsoluteLeft, Tolerance);
    }

    /// <summary>Two 400-wide items in a 400-wide row halve, which is the same law with a rounder answer.</summary>
    [Fact]
    public void Equal_items_in_an_over_full_row_halve() {
        using var document = Laid(
            """
            root { width: 400px; height: 300px; }
            .item { width: 400px; height: 10px; }
            """,
            items: 2
        );

        var items = document.Root.ChildList;

        Assert.Equal(200f, items[0].Width, Tolerance);
        Assert.Equal(200f, items[1].Width, Tolerance);
        Assert.Equal(200f, items[1].AbsoluteLeft, Tolerance);
    }

    /// <summary>
    ///     <c>flex-shrink: 0</c> still refuses, so what changed is an initial value and not a
    ///     behaviour.
    /// </summary>
    /// <remarks>
    ///     The other half of the predicate. Without it "items shrink" could be satisfied by an
    ///     engine that had stopped reading the declaration at all, which is the failure the initial
    ///     value being wrong looked like from the outside.
    /// </remarks>
    [Fact]
    public void A_declared_zero_shrink_still_overflows() {
        using var document = Laid(
            """
            root { width: 400px; height: 300px; }
            .item { width: 300px; height: 10px; flex-shrink: 0; }
            """,
            items: 3
        );

        var items = document.Root.ChildList;

        Assert.Equal(300f, items[0].Width, Tolerance);
        Assert.Equal(900f, items.Sum(static item => item.Width), Tolerance);
    }

    /// <summary>
    ///     <c>flex: 1</c> is <c>1 1 0%</c>, and it always was — by the longhands, not by this
    ///     initial value.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Written as a refutation and kept as one.</b> #628 reads as though the shorthand were
    ///     broken by the same defect, on the argument that the only path from
    ///     <c>LayoutStyle.Flex</c> to a shrink factor is Yoga's convention that a <i>negative</i>
    ///     flex is a shrink, which no stylesheet can write. Measured, that is not what happens: ExCSS
    ///     expands the shorthand into <c>flex-grow</c>, <c>flex-shrink</c> and <c>flex-basis</c>
    ///     before the cascade stores it, so <c>LayoutStyle.Flex</c> is NaN for every stylesheet
    ///     anywhere in this repository and the shrink of 1 comes off the longhand. This test is
    ///     green with the initial value reverted, which is exactly why it says so rather than
    ///     claiming a second fix.
    /// </remarks>
    [Fact]
    public void The_flex_shorthand_carries_its_own_shrink_and_never_reaches_the_initial_value() {
        var style = new BridgeFixture().Build("flex: 1");

        // The expansion, in the two fields that prove which route the value took.
        Assert.True(float.IsNaN(style.Flex));
        Assert.Equal(1f, style.FlexGrow);
        Assert.Equal(1f, style.FlexShrink);

        // …and the geometry it buys: two items told to be 500 wide in a 400-wide row come back at
        // half the container each, which only a shrink factor can do.
        using var document = Laid(
            """
            root { width: 400px; height: 300px; }
            .item { flex: 1; flex-basis: 500px; height: 10px; }
            """,
            items: 2
        );

        var items = document.Root.ChildList;

        Assert.Equal(200f, items[0].Width, Tolerance);
        Assert.Equal(200f, items[1].Width, Tolerance);
    }
}
