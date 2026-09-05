// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     CSS Containment 2's <c>contain</c>, on the two halves of it that live in the layout store.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every fixture here is an AUTO-SIZED box whose children overflow it, and that is the
///         instrument rather than a detail of it.</b> A contained box and an uncontained one draw the
///         same picture wherever the children happen to fit, so a fixture with an explicit
///         <c>width</c> and <c>height</c> would pass character for character against an engine that
///         had never heard of the property. Only a box the contents were deciding can be seen to stop
///         deciding it.
///     </para>
///     <para>
///         ⚠ <b>And the children are asserted to be where they were, in the same test.</b> Size
///         containment is not "skip the subtree": § 3.2 collapses the box and goes on laying its
///         contents out, painting them and hit-testing them. An implementation that skipped them
///         would pass every assertion about the box alone, which is half of what the property says.
///     </para>
///     <para>
///         Hand-written for <c>ContentSizingTests</c>' reason: the ported corpora have never heard of
///         <c>contain</c>, so there is no fixture in <c>Generated/</c> that could move.
///     </para>
/// </remarks>
public class ContainmentTests {
    /// <summary>An auto-sized box holding one 60×40 child, inside a root that gives it room.</summary>
    /// <remarks>
    ///     The child's size is written rather than measured, so the numbers below are closed form: an
    ///     uncontained box is exactly its child, and a contained one is exactly its own padding and
    ///     border. Nothing between the two is a passing answer.
    /// </remarks>
    static (LayoutTree Tree, LayoutNodeId Root, LayoutNodeId Box, LayoutNodeId Child) Fixture(Display display) {
        var tree = new LayoutTree();

        var root = tree.CreateNode();
        tree.SetDisplay(root, Display.Block);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(300f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(300f));

        var box = tree.CreateNode();
        tree.SetDisplay(box, display);
        tree.SetAlignItems(box, Align.FlexStart);

        // ⚠ A block box stretches to its parent across the inline axis whatever its contents are, so
        // the WIDTH half of this test needs a box that shrink-wraps. An absolutely positioned box
        // with neither inset pair given is sized by its contents on both axes and positioned where it
        // statically would have been — which is CSS's own shrink-to-fit and is the only spelling this
        // store has for one on the block path.
        tree.SetPositionType(box, PositionType.Absolute);
        tree.AddChild(root, box);

        var child = tree.CreateNode();
        tree.SetDisplay(child, Display.Block);
        tree.SetDimension(child, Dimension.Width, StyleLength.Points(60f));
        tree.SetDimension(child, Dimension.Height, StyleLength.Points(40f));
        tree.AddChild(box, child);

        return (tree, root, box, child);
    }

    /// <summary>Without containment, the box is its contents — the half that makes the rest able to fail.</summary>
    [Theory]
    [InlineData(Display.Flex)]
    [InlineData(Display.Block)]
    public void An_auto_sized_box_is_the_size_of_what_is_in_it(Display display) {
        var (tree, root, box, _) = Fixture(display);
        tree.CalculateLayout(root, 300f, 300f, Direction.Ltr);

        Assert.Equal(60f, tree.GetWidth(box));
        Assert.Equal(40f, tree.GetHeight(box));
    }

    /// <summary>Size containment collapses the box to its own box decoration, on both axes.</summary>
    /// <remarks>
    ///     ⚠ The padding is on purpose and is not decoration of the test. "Collapses to zero" would be
    ///     met by a box that was never laid out at all; § 3.2 says the box sizes as if it had no
    ///     <i>contents</i>, and its own padding and border are not contents. Five each way makes the
    ///     expected answer 10 rather than 0, which no failure mode of "did nothing" produces.
    /// </remarks>
    [Theory]
    [InlineData(Display.Flex)]
    [InlineData(Display.Block)]
    public void Size_containment_sizes_the_box_as_if_it_were_empty(Display display) {
        var (tree, root, box, child) = Fixture(display);

        foreach (var edge in (Edge[])[Edge.Left, Edge.Right, Edge.Top, Edge.Bottom]) {
            tree.SetPadding(box, edge, StyleLength.Points(5f));
        }

        tree.SetContainment(box, Containment.Size);
        tree.CalculateLayout(root, 300f, 300f, Direction.Ltr);

        Assert.Equal(10f, tree.GetWidth(box));
        Assert.Equal(10f, tree.GetHeight(box));

        // ⚠ The other half of § 3.2, and the half a "skip the children" implementation fails. The
        // child is still laid out, at its own size, inside the padding of a box far too small to
        // hold it — which is exactly the overflow containment produces and does not suppress.
        Assert.Equal(60f, tree.GetWidth(child));
        Assert.Equal(40f, tree.GetHeight(child));
        Assert.Equal(5f, tree.GetLeft(child));
        Assert.Equal(5f, tree.GetTop(child));
    }

    /// <summary><c>inline-size</c> takes the width and leaves the height to the contents.</summary>
    /// <remarks>
    ///     ⚠ <b>The one axis is the whole keyword</b>, so both assertions are load-bearing: an
    ///     implementation that treated it as <c>size</c> fails the second, and one that ignored it
    ///     fails the first. The height is the child's height and not a number of its own, because the
    ///     child is laid out at the width containment just fixed.
    /// </remarks>
    [Fact]
    public void Inline_size_containment_takes_the_inline_axis_alone() {
        var (tree, root, box, _) = Fixture(Display.Block);

        tree.SetContainment(box, Containment.InlineSize);
        tree.CalculateLayout(root, 300f, 300f, Direction.Ltr);

        Assert.Equal(0f, tree.GetWidth(box));
        Assert.Equal(40f, tree.GetHeight(box));
    }

    /// <summary>A content keyword on a contained box measures the empty box, not the contents.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that would otherwise let the contents back in through the front door.</b>
    ///     <c>width: max-content</c> is resolved by a pre-pass that lays the node out with an
    ///     unbounded offer and substitutes the answer, so a containment branch that lived only in the
    ///     flex and block algorithms would collapse the box and then have a keyword hand it its
    ///     contents' width straight back.
    /// </remarks>
    [Theory]
    [InlineData(LayoutUnit.MaxContent)]
    [InlineData(LayoutUnit.MinContent)]
    [InlineData(LayoutUnit.FitContent)]
    public void A_content_keyword_on_a_contained_box_resolves_to_no_content(LayoutUnit keyword) {
        var (tree, root, box, _) = Fixture(Display.Block);
        tree.SetDimension(box, Dimension.Width, StyleLength.Keyword(keyword));

        tree.SetContainment(box, Containment.Size);
        tree.CalculateLayout(root, 300f, 300f, Direction.Ltr);

        Assert.Equal(0f, tree.GetWidth(box));
    }

    /// <summary>Layout containment makes the box the containing block of an absolute descendant.</summary>
    /// <remarks>
    ///     ⚠ <b>Neither the contained box nor anything between it and the absolute child is
    ///     positioned, which is the whole point of the fixture.</b> § 3.1's observable half here is
    ///     the containing block — the thing <c>position: relative</c> otherwise provides — so a test
    ///     that let either box be relative would pass without the property existing. The root is 300
    ///     wide and the contained box is 100, and <c>right: 0</c> tells the two apart by 200 points.
    /// </remarks>
    [Theory]
    [InlineData(Containment.Layout)]
    [InlineData(Containment.Paint)]
    public void Layout_containment_is_a_containing_block_for_an_absolute_descendant(Containment containment) {
        var tree = new LayoutTree();

        var root = tree.CreateNode();
        tree.SetDisplay(root, Display.Block);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(300f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(300f));

        var box = tree.CreateNode();
        tree.SetDisplay(box, Display.Block);

        // ⚠ Written out, and it is the line without which this test cannot fail. `LayoutStyle.Default`
        // is YOGA's initial state and Yoga's `position` is `relative`, so every node in a bare
        // `LayoutTree` is already a containing block — the control arm would have measured 80 with the
        // property absent and agreed with the contained arm for the wrong reason. `static` is CSS's
        // initial value and is what `LayoutStyleBuilder.CssInitial` gives a `.vcss` author.
        tree.SetPositionType(box, PositionType.Static);
        tree.SetDimension(box, Dimension.Width, StyleLength.Points(100f));
        tree.SetDimension(box, Dimension.Height, StyleLength.Points(100f));
        tree.AddChild(root, box);

        var floater = tree.CreateNode();
        tree.SetDisplay(floater, Display.Block);
        tree.SetPositionType(floater, PositionType.Absolute);
        tree.SetPosition(floater, Edge.Right, StyleLength.Points(0f));
        tree.SetDimension(floater, Dimension.Width, StyleLength.Points(20f));
        tree.SetDimension(floater, Dimension.Height, StyleLength.Points(20f));
        tree.AddChild(box, floater);

        tree.CalculateLayout(root, 300f, 300f, Direction.Ltr);
        Assert.Equal(280f, tree.GetLeft(floater));

        tree.SetContainment(box, containment);
        tree.CalculateLayout(root, 300f, 300f, Direction.Ltr);
        Assert.Equal(80f, tree.GetLeft(floater));
    }
}
