// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     CSS 2.1 §9.5 for a leaf that breaks its own lines: <see cref="LayoutTree.ContentBands" />.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The staircase, and the reason it could not be done inside the text layer.</b> A
///         paragraph beside a float needs a width PER LINE — short while it crosses the float, full
///         below it — and a measure function is asked one question with one width. What was missing
///         was never the answer's shape: §9.5 shortens the LINE BOXES and leaves the block box full
///         width, so the leaf's measured size is one rectangle either way. What was missing was the
///         QUESTION, and this is it.
///     </para>
///     <para>
///         <b>The geometry these tests pin was read out of Chrome 148.0.7778.280</b>, over
///         <c>http://localhost</c>, in a 300-wide <c>display: flow-root</c> holding a 100×60 left
///         float and a paragraph at <c>line-height: 20px</c>: the paragraph's own rectangle is
///         <c>[0, 0, 300 × 80]</c> — <i>not</i> narrowed — its first three line rectangles start at
///         <c>x = 100</c>, and the fourth, whose top is 60, starts at <c>x = 0</c>. A right float of
///         the same size leaves every line at <c>x = 0</c> and takes the same 100 off the far end.
///     </para>
///     <para>
///         ⚠ <b>The measure function here counts characters and the numbers are closed-form.</b>
///         Chrome's own break positions depend on a font and on shaping, which is a three-way
///         agreement this repository does not have with any browser yet — <c>#257</c> is where the
///         browser-recorded break positions are, and what has to agree before one of them is
///         usable. So what is taken from Chrome is the band geometry, and what makes each expected
///         number checkable by hand is a leaf whose glyphs are ten points wide by construction.
///     </para>
/// </remarks>
public class FloatBandQueryTests {
    const float Tolerance = 0.0001f;

    /// <summary>The bands a paragraph beside a left float gets, one per line it crosses.</summary>
    /// <remarks>
    ///     Three slots because the float is 60 tall and the lines are 20 — and the list stops there,
    ///     because every line below the float has the width its caller already knows about.
    /// </remarks>
    [Fact]
    public void A_leaf_beside_a_left_float_is_handed_one_band_per_line_it_crosses() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f);
        Float(tree, root, FloatSide.Left, 100f, 60f);

        var bands = new List<(float Start, float Available)>();
        var leaf = Leaf(tree, root, characters: 0, lineHeight: 20f, bands);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(3, bands.Count);
        Assert.All(bands, band => Assert.Equal(100f, band.Start, Tolerance));
        Assert.All(bands, band => Assert.Equal(200f, band.Available, Tolerance));

        // Chrome's paragraph rectangle is the container's full width, at its origin: §9.5 shortens
        // line boxes and moves no block box.
        Assert.Equal(0f, tree.GetLeft(leaf), Tolerance);
        Assert.Equal(300f, tree.GetWidth(leaf), Tolerance);
    }

    /// <summary>The lines that cross the float are short and the ones below it are not.</summary>
    /// <remarks>
    ///     ⚠ <b>The step-out, which is the half a single narrowed width cannot express.</b> Eighty
    ///     ten-point characters: twenty fit on each of the three lines beside the float and thirty
    ///     on the one below it, so the paragraph is four lines and 80 tall. Wrapped to one width it
    ///     is three lines of thirty and 60 tall — and wrapped to the band's 200 throughout it is four
    ///     lines with the last one short, which is the answer that looks right and puts a line's
    ///     worth of text under a float that is no longer there.
    /// </remarks>
    [Fact]
    public void The_lines_below_the_float_step_back_out_to_the_full_width() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f);
        Float(tree, root, FloatSide.Left, 100f, 60f);

        var bands = new List<(float Start, float Available)>();
        var leaf = Leaf(tree, root, characters: 80, lineHeight: 20f, bands);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(80f, tree.GetHeight(leaf), Tolerance);
        Assert.Equal(80f, tree.GetHeight(root), Tolerance);
    }

    /// <summary>With no float in the tree the leaf is told nothing and wraps to its own width.</summary>
    /// <remarks>
    ///     ⚠ <b>The instrument's own control, and it is the assertion that matters most here.</b>
    ///     A query that answered a band for every node would be satisfied by exactly the arrangement
    ///     it exists to detect. On the day nothing narrows this paragraph the answer is <i>zero</i>
    ///     slots, the caller keeps the width it was offered, and eighty characters are three lines.
    /// </remarks>
    [Fact]
    public void A_leaf_with_no_float_beside_it_is_handed_no_bands_at_all() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f);

        var bands = new List<(float Start, float Available)>();
        var leaf = Leaf(tree, root, characters: 80, lineHeight: 20f, bands);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Empty(bands);
        Assert.Equal(60f, tree.GetHeight(leaf), Tolerance);
    }

    /// <summary>A float that ends above the paragraph takes nothing from it.</summary>
    /// <remarks>
    ///     ⚠ <b>The other half of the control, and it is the one the origin guard is for.</b> The
    ///     float is 100×20 and a 40-tall block sits before the paragraph, so the float's bottom is
    ///     above the paragraph's own top — it is still in the exclusion list, still reachable, and
    ///     takes nothing from any line here. An implementation that measured the band from the
    ///     container's origin rather than from the paragraph's own would shorten the first two lines.
    /// </remarks>
    [Fact]
    public void A_float_that_ends_above_the_leaf_narrows_none_of_its_lines() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f);
        Float(tree, root, FloatSide.Left, 100f, 20f);
        Block(tree, root, 300f, 40f);

        var bands = new List<(float Start, float Available)>();
        var leaf = Leaf(tree, root, characters: 80, lineHeight: 20f, bands);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Empty(bands);
        Assert.Equal(60f, tree.GetHeight(leaf), Tolerance);
        Assert.Equal(40f, tree.GetTop(leaf), Tolerance);
    }

    /// <summary>A right float leaves the lines where they start and takes room off their far end.</summary>
    /// <remarks>
    ///     Chrome, same fixture mirrored: every line rectangle stays at <c>x = 0</c> and the three
    ///     that cross the float have 200 to run in rather than 300.
    /// </remarks>
    [Fact]
    public void A_right_float_narrows_the_band_without_moving_where_it_starts() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f);
        Float(tree, root, FloatSide.Right, 100f, 60f);

        var bands = new List<(float Start, float Available)>();
        var leaf = Leaf(tree, root, characters: 80, lineHeight: 20f, bands);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(3, bands.Count);
        Assert.All(bands, band => Assert.Equal(0f, band.Start, Tolerance));
        Assert.All(bands, band => Assert.Equal(200f, band.Available, Tolerance));
        Assert.Equal(80f, tree.GetHeight(leaf), Tolerance);
    }

    /// <summary>The paragraph's own padding is inside the band, on both axes.</summary>
    /// <remarks>
    ///     ⚠ <b>Where a wrong answer is a plausible one.</b> The band is asked for in the leaf's
    ///     CONTENT coordinates, because that is what its measure function is laying text out in: 20
    ///     of left padding puts its content box at x = 20, so the 100-wide float leaves 80 between
    ///     the two — and 20 of top padding pushes the content down into a float that ends 20 lower
    ///     than it otherwise would, which is one slot fewer.
    /// </remarks>
    [Fact]
    public void The_band_is_answered_in_the_leafs_own_content_coordinates() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f);
        Float(tree, root, FloatSide.Left, 100f, 60f);

        var bands = new List<(float Start, float Available)>();
        var leaf = Leaf(tree, root, characters: 0, lineHeight: 20f, bands);
        tree.SetPadding(leaf, Edge.Left, StyleLength.Points(20f));
        tree.SetPadding(leaf, Edge.Top, StyleLength.Points(20f));

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        // 60 of float minus the 20 the content box starts down at, over 20-point lines.
        Assert.Equal(2, bands.Count);
        Assert.All(bands, band => Assert.Equal(80f, band.Start, Tolerance));

        // 300 of container, less 20 of padding, less the 80 the float leaves between the two.
        Assert.All(bands, band => Assert.Equal(200f, band.Available, Tolerance));
    }

    static LayoutNodeId Root(LayoutTree tree, float width) {
        var root = tree.CreateNode();
        tree.SetDisplay(root, Display.Block);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(width));

        return root;
    }

    static LayoutNodeId Float(LayoutTree tree, LayoutNodeId parent, FloatSide side, float width, float height) {
        var node = tree.CreateNode();
        tree.SetDisplay(node, Display.Block);
        tree.SetFloat(node, side);
        tree.SetDimension(node, Dimension.Width, StyleLength.Points(width));
        tree.SetDimension(node, Dimension.Height, StyleLength.Points(height));
        tree.AddChild(parent, node);

        return node;
    }

    static LayoutNodeId Block(LayoutTree tree, LayoutNodeId parent, float width, float height) {
        var node = tree.CreateNode();
        tree.SetDisplay(node, Display.Block);
        tree.SetDimension(node, Dimension.Width, StyleLength.Points(width));
        tree.SetDimension(node, Dimension.Height, StyleLength.Points(height));
        tree.AddChild(parent, node);

        return node;
    }

    /// <summary>
    ///     A block-level leaf that wraps a row of ten-point characters, asking for its bands the way
    ///     a text leaf does.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The band list is refilled on every call and the last call is the one that counts.</b>
    ///     A tree with floats in it measures each child twice — once at a probe origin and once for
    ///     real — so a test that captured the first answer would be pinning a guess. The list handed
    ///     in is the one the assertions read, which makes them read the real pass.
    /// </remarks>
    static LayoutNodeId Leaf(
        LayoutTree tree,
        LayoutNodeId parent,
        int characters,
        float lineHeight,
        List<(float Start, float Available)> bands
    ) {
        var node = tree.CreateNode();
        tree.SetDisplay(node, Display.Block);
        tree.AddChild(parent, node);

        tree.SetMeasureFunction(
            node,
            (in MeasureRequest request) => {
                request.Tree.ContentBands(request.Node, lineHeight, request.AvailableWidth, bands);

                var remaining = characters;
                var lines = 0;

                while (remaining > 0) {
                    var room = lines < bands.Count ? bands[lines].Available : request.AvailableWidth;
                    var fits = Math.Max(1, (int)MathF.Floor(room / 10f));

                    remaining -= fits;
                    lines++;
                }

                return new LayoutSize(request.AvailableWidth, lines * lineHeight);
            }
        );

        return node;
    }
}
