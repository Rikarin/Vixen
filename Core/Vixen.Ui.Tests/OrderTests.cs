// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>
///     What <c>order</c> reaches, and — mostly — what it does not.
/// </summary>
/// <remarks>
///     <para>
///         CSS Flexbox §5.4 gives <c>order</c> a deliberately narrow blast radius: it changes the
///         order items are <b>laid out</b> in and the order they are <b>painted</b> in, and it
///         explicitly changes neither <b>selector matching</b> nor <b>sequential focus
///         navigation</b>. The specification says so in as many words, because reordering that
///         dragged the tab order along with it would let a stylesheet decide what a keyboard does.
///     </para>
///     <para>
///         ⚠ <b>Two of the three are immune structurally rather than carefully</b>, and that is
///         worth writing down because it is the reason not to go looking for a fix. Selector
///         matching runs over <c>StyleTree</c>, which keeps its own <c>IndexInParent</c>; focus
///         traversal walks <c>UiElement.Children</c>. Neither reads the flexbox store, and the sort
///         this feature added lives entirely inside that store's child arena — so there is no path
///         by which either could have started following visual order. These tests are therefore
///         guards rather than proofs of a fix: they fail only against a future change that wires
///         one of those two to the layout tree, which is exactly the change that should have to
///         argue with a red test.
///     </para>
///     <para>
///         The third — painting — is the one that needed doing, and it is held here and in
///         <c>UtilityFamilySupportTests</c>. The numeric layout side is
///         <c>Vixen.Ui.Layout.Tests.OrderTests</c>, against <c>web-platform-tests</c>.
///     </para>
/// </remarks>
public class OrderTests {
    const string Css = """
        root { width: 400px; height: 200px; }
        div { width: 50px; height: 50px; }
        .moved { order: -1; }
        .even { width: 60px; }
        """;

    /// <summary>
    ///     ⚠ <b>Trap one: <c>:nth-child</c> counts document position, not visual position.</b>
    /// </summary>
    /// <remarks>
    ///     This is the shape <c>KeyValueList</c> stripes with — <c>key-value-row:nth-child(even)</c>
    ///     — so an implementation that let <c>order</c> reach the matcher would stripe a reordered
    ///     list by where the rows ended up, and the alternation would break wherever a row was
    ///     moved. The assertion is deliberately on the <i>third</i> element: it is the one that both
    ///     moves and stays odd, so a matcher following visual order would call it even and be caught.
    /// </remarks>
    [Fact]
    public void Order_does_not_change_which_elements_nth_child_matches() {
        using var document = new UiDocument(400f, 200f);
        document.Load(Css);
        document.Load("div:nth-child(even) { height: 70px; }");

        var first = document.Root.Add("div");
        var second = document.Root.Add("div");
        var third = document.Root.Add("div");
        var fourth = document.Root.Add("div");

        // The third child is laid out first and is still the third child.
        third.AddClass("moved");

        document.Update();

        Assert.Equal(0f, third.AbsoluteLeft, 0.001f);

        // Even by document position: the second and fourth, whatever the order moved.
        Assert.Equal(70f, second.Bounds.Height, 0.001f);
        Assert.Equal(70f, fourth.Bounds.Height, 0.001f);

        // Odd by document position, and the one an order-following matcher would have struck.
        Assert.Equal(50f, first.Bounds.Height, 0.001f);
        Assert.Equal(50f, third.Bounds.Height, 0.001f);
    }

    /// <summary>
    ///     ⚠ <b>Trap two: tab order follows the document, not the ordinal groups.</b>
    /// </summary>
    /// <remarks>
    ///     The item moved to the front of the row is still the last thing tabbed to. CSS is explicit
    ///     that <c>order</c> does not affect sequential navigation, and the reason is an
    ///     accessibility one rather than an implementation one: a keyboard user's path through a
    ///     form should not be re-routed by a visual tweak.
    /// </remarks>
    [Fact]
    public void Order_does_not_change_the_tab_order() {
        using var document = new UiDocument(400f, 200f);
        document.Load(Css);

        var first = Stop(document.Root);
        var second = Stop(document.Root);
        var third = Stop(document.Root);

        third.AddClass("moved");
        document.Update();

        // Visually first...
        Assert.Equal(0f, third.AbsoluteLeft, 0.001f);
        Assert.True(third.AbsoluteLeft < first.AbsoluteLeft);

        // ...and last in the tab order, which is the document's.
        document.MoveFocus(FocusDirection.Next);
        Assert.Same(first, document.Focused);

        document.MoveFocus(FocusDirection.Next);
        Assert.Same(second, document.Focused);

        document.MoveFocus(FocusDirection.Next);
        Assert.Same(third, document.Focused);
    }

    /// <summary>
    ///     ⚠ <b>Trap three, the one that is not a trap: <c>order</c> does change painting.</b>
    /// </summary>
    /// <remarks>
    ///     Modelled on <c>web-platform-tests</c> <c>css/css-flexbox/order-painting.html</c>, which
    ///     overlaps a green <c>order: 2</c> item onto a red <c>order: 1</c> one with a negative
    ///     margin and passes if no red shows. The green box is declared <i>first</i> and must paint
    ///     <i>last</i>. Here the same arrangement is read off the draw list rather than off a
    ///     rendering, so the assertion is on the sequence of fills.
    /// </remarks>
    [Fact]
    public void Order_does_change_the_order_things_are_painted_in() {
        using var document = new UiDocument(400f, 200f);
        document.Load(
            """
            root { width: 400px; height: 200px; }
            .over { order: 2; background-color: #00ff00; width: 100px; height: 100px; }
            .under { order: 1; background-color: #ff0000; width: 50px; height: 100px; }
            """
        );

        var over = document.Root.Add("div");
        over.AddClass("over");

        var under = document.Root.Add("div");
        under.AddClass("under");

        document.Update();
        document.Draw();

        var fills = document.Drawing.Commands
            .Where(command => command.Kind == DrawCommandKind.Rectangle)
            .Select(command => command.Color)
            .ToList();

        var red = fills.FindIndex(color => color.R > 0.9f && color.G < 0.1f);
        var green = fills.FindIndex(color => color.G > 0.9f && color.R < 0.1f);

        Assert.True(red >= 0 && green >= 0, "both boxes were filled");

        // Declared first, painted last: `order: 2` puts it in the later ordinal group, and painting
        // follows order-modified document order.
        Assert.True(green > red, "the higher ordinal group paints on top");
    }

    /// <summary>
    ///     <c>z-index</c> outranks <c>order</c>, because <c>order</c> modifies document order rather
    ///     than replacing the stacking one.
    /// </summary>
    /// <remarks>
    ///     ⚠ The case that catches the two sort keys being swapped. With <c>order</c> as the primary
    ///     key a low-<c>z</c> item in a later ordinal group would paint above a lifted sibling,
    ///     which no browser does — <c>z-index</c> is the outer sort and <c>order</c> breaks its ties.
    /// </remarks>
    [Fact]
    public void A_lifted_sibling_still_paints_above_a_later_ordinal_group() {
        using var document = new UiDocument(400f, 200f);
        document.Load(
            """
            root { width: 400px; height: 200px; }
            .late { order: 9; background-color: #00ff00; width: 50px; height: 50px; }
            .lifted { z-index: 5; background-color: #ff0000; width: 50px; height: 50px; }
            """
        );

        var late = document.Root.Add("div");
        late.AddClass("late");

        var lifted = document.Root.Add("div");
        lifted.AddClass("lifted");

        document.Update();
        document.Draw();

        var fills = document.Drawing.Commands
            .Where(command => command.Kind == DrawCommandKind.Rectangle)
            .Select(command => command.Color)
            .ToList();

        var green = fills.FindIndex(color => color.G > 0.9f && color.R < 0.1f);
        var red = fills.FindIndex(color => color.R > 0.9f && color.G < 0.1f);

        Assert.True(green >= 0 && red >= 0, "both boxes were filled");
        Assert.True(red > green, "z-index is the outer sort");
    }

    /// <summary>
    ///     <c>css/css-flexbox/flexbox_order-noninteger-invalid.html</c> — <c>order</c> takes
    ///     <c>&lt;integer&gt;</c>, so a fractional value is an invalid declaration.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The case the oracle caught.</b> This bridge was first written to round, on the
    ///     reasoning that a fractional order should not silently truncate — which put the item in
    ///     ordinal group 2 where WPT (and `parsing/order-invalid.html`, which lists <c>123.45</c>
    ///     alongside <c>auto</c>) says it belongs in group 0. An invalid declaration leaves the
    ///     initial value; it does not get repaired into a nearby valid one.
    /// </remarks>
    [Fact]
    public void A_fractional_order_is_an_invalid_declaration_and_not_a_rounded_one() {
        using var document = new UiDocument(400f, 200f);
        document.Load(
            """
            root { width: 400px; height: 200px; flex-direction: row; }
            div { width: 50px; height: 50px; }
            .fractional { order: 1.5; }
            """
        );

        var fractional = document.Root.Add("div");
        fractional.AddClass("fractional");

        var plain = document.Root.Add("div");

        document.Update();

        // Rounding to 2 would have put it second. Dropped, it keeps ordinal group 0 and document
        // order decides — so it stays first.
        Assert.Equal(0f, fractional.AbsoluteLeft, 0.001f);
        Assert.Equal(50f, plain.AbsoluteLeft, 0.001f);
    }

    static UiElement Stop(UiElement parent) {
        var element = parent.Add("div");
        element.Focusable = true;
        return element;
    }
}
