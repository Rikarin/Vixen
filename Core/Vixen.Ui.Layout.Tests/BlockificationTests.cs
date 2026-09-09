// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     CSS Display §2.7's <i>automatic box type transformations</i> — blockification — and the
///     measurement that says this store does not need them.
/// </summary>
/// <remarks>
///     <para>
///         §2.7 rewrites a box's computed <c>display</c> when it is a <b>flex item</b>, a
///         <b>grid item</b>, a <b>float</b>, <b>absolutely positioned</b> or the <b>root</b>:
///         <c>inline</c> → <c>block</c>, <c>inline-block</c> → <c>block</c>,
///         <c>inline-flex</c> → <c>flex</c>. Those five are between them every way of holding a box
///         out of an inline formatting context, which is exactly why the transformation exists — the
///         inline-level half of a <c>display</c> value is a statement about a line, and none of these
///         five boxes is on one.
///     </para>
///     <para>
///         ⚠ <b>This store does not implement it, and the refusal is measured rather than argued:
///         the transformation moves nothing.</b>
///         <see cref="Blockifying_a_box_in_any_of_the_five_contexts_moves_no_geometry" /> lays every
///         shape below out twice — once with the specified <c>display</c> and once with
///         §2.7's blockified one — and compares every rectangle the public API can report. All 468
///         comparisons agree to the bit. So blockification here would be a computed-value step whose
///         whole effect is to produce the answer already being produced, and the honest form of
///         "we do not do this" is the pair of tests in this file rather than a paragraph.
///     </para>
///     <para>
///         ⚠ <b>The reason it is a no-op is one line of the dispatch, and it is worth knowing before
///         anyone tries to make blockification observable.</b>
///         <c>CalculateLayoutImpl</c> sends <see cref="Display.Block" />,
///         <see cref="Display.InlineBlock" />, <see cref="Display.Inline" /> and
///         <see cref="Display.FlowRoot" /> down the SAME branch — CSS Display §2.1's <i>inner</i>
///         display decides the algorithm and the outer half decides only how the box relates to its
///         siblings — and a box in any of these five contexts has no inline-level siblings to relate
///         to. Everything that reads the outer half is unreachable from here: <c>IsNonAtomicInline</c>
///         is consulted only from a parent already walking lines,
///         <c>EstablishesInlineFormattingContext</c> asks about a container's CHILDREN, and
///         <c>BlockMarginsCollapsibleWithParent</c> wants a <see cref="Display.Block" /> parent, which
///         a flex container, a grid container and the absent parent of the root are not.
///     </para>
///     <para>
///         ⚠ <b>So the issue's premise is refuted in its measured half (#1149).</b> It reads a
///         <c>display: inline</c> flex item honouring <c>width: 90</c> and wrapping its third child
///         at 85 as a divergence from Chrome. It is not one: Chrome blockifies that span to
///         <c>block</c>, a block container whose children are all inline-level runs an inline
///         formatting context, and the numbers that come out are the same 90 and the same wrap.
///         What this store really has and CSS does not is the <i>atomic</i> non-replaced
///         <c>inline</c> box — recorded in <c>InlineKnownGaps.txt</c> and pinned by
///         <see cref="InlineFragmentationTests" /> — and that is a different divergence, visible only
///         where the box is NOT in one of these five contexts.
///     </para>
///     <para>
///         ⚠ <b>The second test is why the first one is worth anything.</b> A file full of
///         <c>Assert.Equal</c> between two layouts is satisfied by a store that ignores
///         <c>display</c> altogether, so
///         <see cref="The_same_comparison_separates_two_displays_that_really_do_differ" />
///         runs the identical shapes through a pair the store must tell apart and requires every one
///         of them to disagree. Both tests also assert how many shapes they saw: a loop that asserts
///         inside itself is green on an empty enumeration.
///     </para>
///     <para>
///         The day this file goes red is the day blockification stops being a no-op — a table
///         formatting context (#258), a writing mode (#952) or any use of the outer display half that
///         a box outside a line can reach. Then §2.7 has to be written, and the seam is a
///         computed-value step: it needs the PARENT's display, so it cannot be a per-node style
///         resolution in <c>LayoutStyleBuilder</c>.
///     </para>
/// </remarks>
public class BlockificationTests {
    /// <summary>The five contexts CSS Display §2.7 blockifies in.</summary>
    enum Context {
        /// <summary>A child of a flex container.</summary>
        FlexItem,

        /// <summary>A child of a grid container.</summary>
        GridItem,

        /// <summary>A floated box, which §9.7 blockifies for the same reason.</summary>
        Float,

        /// <summary>An absolutely positioned box.</summary>
        Absolute,

        /// <summary>The node <c>CalculateLayout</c> was called on.</summary>
        Root
    }

    /// <summary>What the box holds, which decides which algorithm runs inside it.</summary>
    enum Content {
        /// <summary>Three atomic inline-level boxes, so the box runs a line walk.</summary>
        InlineLevel,

        /// <summary>Three block-level boxes with vertical margins, so the box stacks them.</summary>
        BlockLevel,

        /// <summary>Three floated boxes, so §9.5 places them and no line walk runs.</summary>
        Floats
    }

    /// <summary>CSS Display §2.7's three rewrites of a flow box's computed <c>display</c>.</summary>
    static readonly (Display Specified, Display Blockified)[] Transformations = [
        (Display.Inline, Display.Block),
        (Display.InlineBlock, Display.Block),
        (Display.InlineFlex, Display.Flex)
    ];

    /// <summary>
    ///     §2.7's rewrite is not observable through any rectangle this store reports.
    /// </summary>
    /// <remarks>
    ///     Three transformations over 156 shapes. The count is asserted because the enumeration is
    ///     generated rather than written out, and an enumeration that silently yields nothing would
    ///     otherwise make this test green for the wrong reason.
    /// </remarks>
    [Fact]
    public void Blockifying_a_box_in_any_of_the_five_contexts_moves_no_geometry() {
        var compared = 0;

        foreach (var shape in Shapes()) {
            foreach (var (specified, blockified) in Transformations) {
                compared++;

                Assert.Equal(Describe(shape, blockified), Describe(shape, specified));
            }
        }

        Assert.Equal(468, compared);
    }

    /// <summary>
    ///     The same comparison, run over a pair of displays the store genuinely lays out differently,
    ///     disagrees on every one of the same shapes.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the instrument check and not a property of the engine.</b>
    ///     <see cref="Blockifying_a_box_in_any_of_the_five_contexts_moves_no_geometry" /> asserts that
    ///     two layouts agree, and the cheapest way to satisfy such an assertion is for the comparison
    ///     to be blind — a <see cref="Describe" /> that reported a constant, a shape enumeration whose
    ///     boxes are all empty, a <c>SetDisplay</c> that did nothing. <see cref="Display.Block" />
    ///     against <see cref="Display.Grid" /> is the same box holding the same children under the
    ///     same parent, differing only in the algorithm inside it, and it must disagree 156 times out
    ///     of 156.
    /// </remarks>
    [Fact]
    public void The_same_comparison_separates_two_displays_that_really_do_differ() {
        var disagreed = 0;
        var compared = 0;

        foreach (var shape in Shapes()) {
            compared++;

            if (Describe(shape, Display.Block) != Describe(shape, Display.Grid)) {
                disagreed++;
            }
        }

        Assert.Equal(156, compared);
        Assert.Equal(156, disagreed);
    }

    /// <summary>
    ///     One shape written out in full, so the file is not only differential.
    /// </summary>
    /// <remarks>
    ///     The flex item of #1149's own reading: a 90-wide box with 5 of padding holding three 40s.
    ///     The content box is 85, so two fit on the first line and the third opens a second one —
    ///     which is what the issue measured and read as a divergence. The arithmetic is forced and
    ///     Chrome agrees with it, because Chrome blockifies the span to <c>block</c> and a block
    ///     container holding only inline-level children lays them out on lines exactly here.
    /// </remarks>
    [Fact]
    public void The_reading_that_1149_took_for_a_divergence_is_what_a_blockified_box_gives() {
        foreach (var display in new[] { Display.Inline, Display.Block }) {
            using var tree = new LayoutTree();

            var root = tree.CreateNode();
            tree.SetDisplay(root, Display.Flex);
            tree.SetDimension(root, Dimension.Width, StyleLength.Points(200f));

            var box = tree.CreateNode();
            tree.SetDisplay(box, display);
            tree.SetDimension(box, Dimension.Width, StyleLength.Points(90f));
            tree.SetPadding(box, Edge.Left, StyleLength.Points(5f));
            tree.SetPadding(box, Edge.Top, StyleLength.Points(5f));
            tree.AddChild(root, box);

            for (var i = 0; i < 3; i++) {
                var kid = tree.CreateNode();
                tree.SetDisplay(kid, Display.InlineBlock);
                tree.SetDimension(kid, Dimension.Width, StyleLength.Points(40f));
                tree.SetDimension(kid, Dimension.Height, StyleLength.Points(20f));
                tree.AddChild(box, kid);
            }

            Assert.Equal(3, tree.GetChildCount(box));

            tree.CalculateLayout(root, 200f, float.NaN, Direction.Ltr);

            // One box, the declared width, and 5 of padding over two 20-point lines.
            Assert.Equal(1, tree.GetFragmentCount(box));
            Assert.Equal(90f, tree.GetWidth(box), Tolerance);
            Assert.Equal(45f, tree.GetHeight(box), Tolerance);
        }
    }

    /// <summary>Every shape the two comparisons run over.</summary>
    /// <remarks>
    ///     The <see cref="Context.Root" /> rows carry no sibling, because the box is the tree's root
    ///     and has none — which is why the total is 156 rather than 180.
    /// </remarks>
    static IEnumerable<Shape> Shapes() {
        foreach (var context in new[] {
                     Context.FlexItem, Context.GridItem, Context.Float, Context.Absolute, Context.Root
                 }) {
            foreach (var content in new[] { Content.InlineLevel, Content.BlockLevel, Content.Floats }) {
                foreach (var padded in new[] { false, true }) {
                    foreach (var autoWidth in new[] { false, true }) {
                        foreach (var sibling in new[] { Display.None, Display.InlineBlock, Display.Block }) {
                            if (context == Context.Root && sibling != Display.None) {
                                continue;
                            }

                            yield return new(context, content, padded, autoWidth, sibling);
                        }
                    }
                }
            }
        }
    }

    /// <summary>Lays one shape out with the given <c>display</c> on the box, and reports every rectangle.</summary>
    static string Describe(Shape shape, Display display) {
        using var tree = new LayoutTree();
        var children = new List<LayoutNodeId>();

        var root = tree.CreateNode();

        tree.SetDisplay(
            root,
            shape.Context switch {
                Context.FlexItem => Display.Flex,
                Context.GridItem => Display.Grid,
                _ => Display.Block
            }
        );

        // The root's own width is what makes a `width: auto` box's answer definite, so the ROOT case
        // — where the box is the root — is the one that has to give it up to vary the same axis.
        if (shape.Context != Context.Root || !shape.AutoWidth) {
            tree.SetDimension(root, Dimension.Width, StyleLength.Points(200f));
        }

        tree.SetPadding(root, Edge.Left, StyleLength.Points(10f));
        tree.SetPadding(root, Edge.Top, StyleLength.Points(10f));

        LayoutNodeId box;
        LayoutNodeId? sibling = null;

        if (shape.Context == Context.Root) {
            box = root;
            tree.SetDisplay(box, display);
        } else {
            box = tree.CreateNode();
            tree.SetDisplay(box, display);

            if (!shape.AutoWidth) {
                tree.SetDimension(box, Dimension.Width, StyleLength.Points(90f));
            }

            if (shape.Padded) {
                tree.SetPadding(box, Edge.Left, StyleLength.Points(5f));
                tree.SetPadding(box, Edge.Top, StyleLength.Points(5f));
            }

            // A margin on the box, so a version that started collapsing it with a child's would show.
            tree.SetMargin(box, Edge.Top, StyleLength.Points(3f));
            tree.AddChild(root, box);

            if (shape.Context == Context.Float) {
                tree.SetFloat(box, FloatSide.Left);
            }

            if (shape.Context == Context.Absolute) {
                tree.SetPositionType(box, PositionType.Absolute);
            }

            if (shape.Sibling != Display.None) {
                var node = tree.CreateNode();
                tree.SetDisplay(node, shape.Sibling);
                tree.SetDimension(node, Dimension.Width, StyleLength.Points(30f));
                tree.SetDimension(node, Dimension.Height, StyleLength.Points(15f));
                tree.AddChild(root, node);
                sibling = node;
            }
        }

        for (var i = 0; i < 3; i++) {
            var kid = tree.CreateNode();
            tree.SetDisplay(kid, shape.Content == Content.BlockLevel ? Display.Block : Display.InlineBlock);
            tree.SetDimension(kid, Dimension.Width, StyleLength.Points(40f));
            tree.SetDimension(kid, Dimension.Height, StyleLength.Points(20f));

            // Vertical margins, because §2.7's rewrite of the box's display is the only thing standing
            // between its children and CSS 2.1 §8.3.1's collapse into it.
            tree.SetMargin(kid, Edge.Top, StyleLength.Points(7f));
            tree.SetMargin(kid, Edge.Bottom, StyleLength.Points(11f));

            if (shape.Content == Content.Floats) {
                tree.SetFloat(kid, FloatSide.Left);
            }

            tree.AddChild(box, kid);
            children.Add(kid);
        }

        tree.CalculateLayout(root, 200f, float.NaN, Direction.Ltr);

        var text = new StringBuilder();
        Append(text, tree, "root", root);
        Append(text, tree, "box", box);

        foreach (var kid in children) {
            Append(text, tree, "kid", kid);
        }

        if (sibling is { } sibs) {
            Append(text, tree, "sibling", sibs);
        }

        return text.ToString();
    }

    static void Append(StringBuilder text, LayoutTree tree, string name, LayoutNodeId node) =>
        text.Append(CultureInfo.InvariantCulture, $"{name} ")
            .Append(CultureInfo.InvariantCulture, $"{tree.GetLeft(node):0.####},{tree.GetTop(node):0.####} ")
            .Append(CultureInfo.InvariantCulture, $"{tree.GetWidth(node):0.####}x{tree.GetHeight(node):0.####} ")
            .Append(CultureInfo.InvariantCulture, $"f{tree.GetFragmentCount(node)} | ");

    const float Tolerance = 0.0001f;

    /// <summary>One tree to lay out, with everything but the box's own <c>display</c> fixed.</summary>
    readonly record struct Shape(Context Context, Content Content, bool Padded, bool AutoWidth, Display Sibling);
}
