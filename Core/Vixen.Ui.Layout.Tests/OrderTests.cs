// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     CSS Flexbox §5.4 — <c>order</c>, which assigns items to ordinal groups and makes the
///     container lay them out lowest group first.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The ported suite says nothing about any of this, because Yoga has no
///         <c>order</c>.</b> Its style surface has no such property, so
///         <c>Tools/Vixen.YogaTestGen</c> emits no fixture that sets one and all 534 of them stay
///         green against a tree that ignores the field entirely. This is the same shape of blind
///         spot <see cref="AutomaticMinimumSizeTests" /> was written for, and it is worse in one
///         respect: §4.5 was a rule the fixtures merely failed to exercise, whereas here the oracle
///         does not implement the feature at all and never could have.
///     </para>
///     <para>
///         <b>So the oracle is <c>web-platform-tests</c>, <c>css/css-flexbox/</c>, which is a
///         browser conformance suite rather than one engine's regression suite.</b> Every case below
///         names the file it comes from. They are re-expressed rather than translated — WPT's
///         <c>order</c> tests are mostly reftests and <c>offsetLeft</c> comparisons over
///         auto-sized text, and this store has neither a renderer nor a default font — so what
///         carries across is the <i>relation</i> each test asserts, with the geometry restated in
///         fixed sizes. Where a test asserts an ordering, the assertion here is that ordering; where
///         it asserts a number, the number is derived from the fixed sizes and shown in the comment.
///     </para>
///     <para>
///         ⚠ <b>What this file still does not cover</b>, and what a future clone of WPT would:
///         <c>order</c> against a real font's baseline alignment (<c>flex-order-last-baseline</c>),
///         hit testing through overlapped ordinal groups (<c>hittest-overlapping-order</c>), and the
///         interaction with <c>writing-mode</c>. The first two are about layers above this store,
///         and the paint half of them is held by
///         <c>UtilityFamilySupportTests.An_ordered_item_is_laid_out_and_painted_in_its_ordinal_group</c>.
///     </para>
/// </remarks>
public class OrderTests {
    const float Tolerance = 0.0001f;

    /// <summary>
    ///     <c>css/css-flexbox/order_value.html</c> — <c>order: -1</c> on the middle of three items
    ///     moves it in front of the first, and leaves all three on one line.
    /// </summary>
    /// <remarks>
    ///     WPT asserts <c>test01.offsetTop == test02.offsetTop</c> and that <c>test02</c>'s
    ///     <c>offsetLeft</c> is <i>not</i> greater than or equal to <c>test01</c>'s. With three
    ///     50-point items in a 200-point row that pins the numbers: the ordered item takes 0, and
    ///     the two defaulted ones follow at 50 and 100 in their own document order.
    /// </remarks>
    [Fact]
    public void A_negative_order_moves_an_item_in_front_of_a_defaulted_one() {
        using var tree = new LayoutTree();
        var root = Row(tree, 200f);

        var first = Item(tree, root, 50f);
        var second = Item(tree, root, 50f);
        var third = Item(tree, root, 50f);

        tree.SetOrder(second, -1);
        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(0f, tree.GetLeft(second), Tolerance);
        Assert.Equal(50f, tree.GetLeft(first), Tolerance);
        Assert.Equal(100f, tree.GetLeft(third), Tolerance);

        // "Rectangle 1 and 2 have the same offsetTop value" — reordering is along the main axis
        // only, and must not push anything onto a second line.
        Assert.Equal(tree.GetTop(first), tree.GetTop(second), Tolerance);
    }

    /// <summary>
    ///     <c>css/css-flexbox/flexbox-order-from-lowest.html</c> — a container lays its content out
    ///     starting with the lowest ordinal group and going up.
    /// </summary>
    /// <remarks>
    ///     The WPT document declares its three paragraphs in exactly reverse order — <c>order: 1</c>
    ///     first, then <c>0</c>, then <c>-1</c> — and passes if they read "First,Second,Third". That
    ///     makes it the case that fails for an implementation which sorts but sorts the wrong way,
    ///     which a test using only <c>0</c> and one other group cannot distinguish.
    /// </remarks>
    [Fact]
    public void A_container_lays_out_the_lowest_ordinal_group_first() {
        using var tree = new LayoutTree();
        var root = Row(tree, 300f);

        var rightmost = Item(tree, root, 100f);
        var middle = Item(tree, root, 100f);
        var leftmost = Item(tree, root, 100f);

        tree.SetOrder(rightmost, 1);
        tree.SetOrder(middle, 0);
        tree.SetOrder(leftmost, -1);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(0f, tree.GetLeft(leftmost), Tolerance);
        Assert.Equal(100f, tree.GetLeft(middle), Tolerance);
        Assert.Equal(200f, tree.GetLeft(rightmost), Tolerance);
    }

    /// <summary>
    ///     <c>css/css-flexbox/order/order-with-row-reverse.html</c> — the ordinal groups are laid
    ///     out lowest first <i>along the main axis</i>, so <c>row-reverse</c> reverses the result.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the case that catches a sort applied in the wrong place.</b> Reordering is
    ///     defined on the item sequence, and the direction of the main axis is applied to that
    ///     sequence afterwards — so under <c>row-reverse</c> the <i>lowest</i> group ends up on the
    ///     right. An implementation that reordered the final positions rather than the item list
    ///     would put it on the left and pass every non-reversed test in this file.
    /// </remarks>
    [Fact]
    public void Row_reverse_reverses_the_ordinal_groups_with_everything_else() {
        using var tree = new LayoutTree();
        var root = Row(tree, 300f);
        tree.SetFlexDirection(root, FlexDirection.RowReverse);

        var first = Item(tree, root, 100f);
        var second = Item(tree, root, 100f);
        var third = Item(tree, root, 100f);

        tree.SetOrder(first, 1);
        tree.SetOrder(second, 0);
        tree.SetOrder(third, -1);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        // Sequence is (third, second, first) by group; row-reverse then fills from the right.
        Assert.Equal(200f, tree.GetLeft(third), Tolerance);
        Assert.Equal(100f, tree.GetLeft(second), Tolerance);
        Assert.Equal(0f, tree.GetLeft(first), Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>Ties break by document order, and the sort has to be stable to keep them there.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         §5.4: items in the same ordinal group are laid out "in document order". WPT has no
    ///         dedicated test for this because a browser's sort is stable and the property is
    ///         defined in terms of a sequence rather than a sort — it is an implementation hazard
    ///         rather than a specification subtlety, and it is the classic bug in this property:
    ///         <c>Span.Sort</c> is an introsort, introsort is not stable, and equal keys come out in
    ///         whatever arrangement the partitioning left them in.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Thirty-four items, and the count is load-bearing.</b> This was written with
    ///         eight and it passed against a deliberately unstable sort, which is the trap inside
    ///         the trap: .NET's introsort delegates any span of sixteen or fewer to an
    ///         <i>insertion</i> sort, and insertion sort is stable. A small test therefore certifies
    ///         stability the implementation does not have, and only a list long enough to reach the
    ///         quicksort partitioning can tell the difference. The two items carrying a non-zero
    ///         order are what make the sort run at all.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Items_in_the_same_ordinal_group_keep_document_order() {
        using var tree = new LayoutTree();
        var root = Row(tree, 3400f);

        var items = new LayoutNodeId[34];
        for (var i = 0; i < items.Length; i++) {
            items[i] = Item(tree, root, 100f);
        }

        // Two groups: the last two items lead, and the other thirty-two tie with each other.
        tree.SetOrder(items[32], -1);
        tree.SetOrder(items[33], -1);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(0f, tree.GetLeft(items[32]), Tolerance);
        Assert.Equal(100f, tree.GetLeft(items[33]), Tolerance);

        // The thirty-two defaulted items follow, still in the sequence they were declared in.
        for (var i = 0; i < 32; i++) {
            Assert.Equal(200f + (i * 100f), tree.GetLeft(items[i]), Tolerance);
        }
    }

    /// <summary>
    ///     <c>css/css-flexbox/flexbox_order-abspos-space-around.html</c> — an absolutely positioned
    ///     child is not a flex item, so its <c>order</c> changes nothing about the line.
    /// </summary>
    /// <remarks>
    ///     The in-flow items are what get distributed; an absolute child is placed against the
    ///     padding box regardless of where it sits in the sequence. This is worth a case because the
    ///     sort runs over the whole child list — absolute children included — and only the in-flow
    ///     filter downstream keeps that from mattering.
    /// </remarks>
    [Fact]
    public void An_absolutely_positioned_child_is_not_reordered_into_the_line() {
        using var tree = new LayoutTree();
        var root = Row(tree, 200f);

        var floating = Item(tree, root, 50f);
        tree.SetPositionType(floating, PositionType.Absolute);
        tree.SetOrder(floating, -1);

        var first = Item(tree, root, 50f);
        var second = Item(tree, root, 50f);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        // The two in-flow items start at the container's edge: the absolute one consumed no space
        // and its `order: -1` did not push them along.
        Assert.Equal(0f, tree.GetLeft(first), Tolerance);
        Assert.Equal(50f, tree.GetLeft(second), Tolerance);
    }

    /// <summary>
    ///     ⚠ <b><c>order</c> also decides which item a line breaks after.</b>
    /// </summary>
    /// <remarks>
    ///     Line breaking walks the item sequence and stops where the line is full, so it has to walk
    ///     the <i>reordered</i> sequence — CSS Flexbox §9.3 collects items into lines "in the order
    ///     they appear", which §5.4 has already redefined. Three 100-point items in a 200-point
    ///     wrapping row put two on the first line and one on the second, and which item is alone on
    ///     the second line is decided entirely by the ordering.
    /// </remarks>
    [Fact]
    public void Wrapping_breaks_the_line_by_ordinal_group_rather_than_by_document_order() {
        using var tree = new LayoutTree();
        var root = Row(tree, 200f);
        tree.SetFlexWrap(root, Wrap.Wrap);

        var first = Item(tree, root, 100f);
        var second = Item(tree, root, 100f);
        var third = Item(tree, root, 100f);

        // Sequence becomes (third, first, second), so `second` is the one pushed onto line two.
        tree.SetOrder(third, -1);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(0f, tree.GetTop(third), Tolerance);
        Assert.Equal(0f, tree.GetTop(first), Tolerance);
        Assert.Equal(50f, tree.GetTop(second), Tolerance);
        Assert.Equal(0f, tree.GetLeft(second), Tolerance);
    }

    /// <summary>Changing an <c>order</c> relays the container out without anything else changing.</summary>
    /// <remarks>
    ///     ⚠ <b>The property is stored on the item and read by its container</b>, so the invalidation
    ///     has to travel upwards — and the sorted child list is a cache, which is the kind of thing
    ///     that is right on the first pass and stale on the second. Two passes over one tree is what
    ///     catches that; a test that built a fresh tree per arrangement never would.
    /// </remarks>
    [Fact]
    public void Changing_an_order_reorders_an_already_laid_out_container() {
        using var tree = new LayoutTree();
        var root = Row(tree, 200f);

        var first = Item(tree, root, 100f);
        var second = Item(tree, root, 100f);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);
        Assert.Equal(0f, tree.GetLeft(first), Tolerance);

        tree.SetOrder(first, 1);
        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(100f, tree.GetLeft(first), Tolerance);
        Assert.Equal(0f, tree.GetLeft(second), Tolerance);

        // And back, which is the case that leaves a stale sorted block behind if going to zero is
        // treated as "nothing to do".
        tree.SetOrder(first, 0);
        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(0f, tree.GetLeft(first), Tolerance);
        Assert.Equal(100f, tree.GetLeft(second), Tolerance);
    }

    /// <summary>Inserting and removing children keeps the sorted list in step with the real one.</summary>
    /// <remarks>
    ///     ⚠ The sorted block is a <i>copy</i> of the child list, so every mutation of the original
    ///     has to invalidate it. A copy that outlived a removed child would name a node that is no
    ///     longer there.
    /// </remarks>
    [Fact]
    public void The_sorted_list_survives_children_being_added_and_removed() {
        using var tree = new LayoutTree();
        var root = Row(tree, 300f);

        var first = Item(tree, root, 100f);
        var second = Item(tree, root, 100f);
        tree.SetOrder(first, 1);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);
        Assert.Equal(100f, tree.GetLeft(first), Tolerance);

        var third = Item(tree, root, 100f);
        tree.SetOrder(third, -1);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        // (third, second, first).
        Assert.Equal(0f, tree.GetLeft(third), Tolerance);
        Assert.Equal(100f, tree.GetLeft(second), Tolerance);
        Assert.Equal(200f, tree.GetLeft(first), Tolerance);

        tree.RemoveChild(root, second);
        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(0f, tree.GetLeft(third), Tolerance);
        Assert.Equal(100f, tree.GetLeft(first), Tolerance);
    }

    /// <summary>
    ///     ⚠ <b><c>order</c> changes layout order and nothing about the child list.</b>
    /// </summary>
    /// <remarks>
    ///     <c>GetChild</c> is the document's index, and the whole of CSS's position-dependent
    ///     machinery — <c>:nth-child</c> above this, and this store's own insert-at-index — is
    ///     defined on it. A reordering that reached the arena itself would be invisible here in the
    ///     geometry and wrong everywhere that counts siblings.
    /// </remarks>
    [Fact]
    public void The_document_child_list_is_not_the_thing_that_gets_sorted() {
        using var tree = new LayoutTree();
        var root = Row(tree, 300f);

        var first = Item(tree, root, 100f);
        var second = Item(tree, root, 100f);
        var third = Item(tree, root, 100f);

        tree.SetOrder(first, 5);
        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        // Laid out last...
        Assert.Equal(200f, tree.GetLeft(first), Tolerance);

        // ...and still the zeroth child.
        Assert.Equal(3, tree.GetChildCount(root));
        Assert.Equal(first, tree.GetChild(root, 0));
        Assert.Equal(second, tree.GetChild(root, 1));
        Assert.Equal(third, tree.GetChild(root, 2));
    }

    static LayoutNodeId Row(LayoutTree tree, float width) {
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(width));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(100f));
        return root;
    }

    static LayoutNodeId Item(LayoutTree tree, LayoutNodeId parent, float width) {
        var child = tree.CreateNode();
        tree.SetDimension(child, Dimension.Width, StyleLength.Points(width));
        tree.SetDimension(child, Dimension.Height, StyleLength.Points(50f));
        tree.AddChild(parent, child);
        return child;
    }
}
