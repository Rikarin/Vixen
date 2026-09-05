// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     The float rules no fixture in any of the 5 524 corpora asks about.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>Taffy/FloatKnownGaps.txt</c>'s OWED section, turned into assertions.</b> That file
///         exists because a reader who sees 84 of 84 and an empty failure list concludes floats are
///         finished, and its own framing is the thing to keep: all 84 fixtures were an <i>engine</i>
///         gap. The store had no <c>float</c> field, so nothing there had ever been run, and the
///         algorithm was written against those exact numbers. A corpus that had never run and went
///         green is evidence that somebody read the expectations. What it is <i>not</i> evidence about
///         is anything the corpus does not contain — and it turns out to contain rather less than the
///         count suggests.
///     </para>
///     <para>
///         ⚠ <b>Two of the four rows this file was written to pin were already right, one was wrong
///         for a different reason than the row said, and the fourth turned out to be a special case of
///         a defect nobody had named.</b> Recorded per test below, because a refuted row is worth as
///         much as a fix and this file is the only place either lands.
///     </para>
///     <para>
///         The numbers are CSS 2.1 §9.5 read as written rather than a browser reading. Each is a
///         closed-form consequence of a rule the 84 fixtures already pin somewhere else — clearance,
///         the margin box, §10.6.3's containment — varied in the one dimension the corpus holds
///         constant. Where a test asserts a <i>cousin's</i> position rather than the float's, that is
///         deliberate: the float's own rectangle and the exclusion list's copy of it disagreed in one
///         of these cases for as long as floats have existed, and only the cousin can see it.
///     </para>
/// </remarks>
public sealed class FloatBlindSpotTests {
    const float Tolerance = 0.0001f;

    // ── float-in-auto-centred-block, and the wider defect it turned out to name ──────────────────

    /// <summary>
    ///     A float inside a block centred by <c>margin: 0 auto</c> is excluded where it is drawn.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The gap file's own diagnosis was half right and its stated consequence was wrong.</b>
    ///     It said the float "lands at the uncentred edge". It did not: the float's own rectangle came
    ///     out at the centred block's content edge and looked perfect. What landed at the uncentred
    ///     edge was the <i>exclusion</i>, because the float origin read an <c>auto</c> margin as its
    ///     stated zero while the position was written from the resolved one. So the float was drawn at
    ///     150 and excluded at 0..50, and the only witness is a box that has to avoid it — here a
    ///     <c>overflow: hidden</c> root, which slid to x = 50 instead of x = 150.
    ///     <para>
    ///         The reason given for not resolving it was also refuted: "resolving it needs the used
    ///         width of the very layout the origin is about to start". The used width is
    ///         <c>childWidth</c>, which is in hand — it is the number the <c>StretchFit</c> call is
    ///         handed one line later, so the box is that wide by construction.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_float_in_an_auto_centred_block_is_excluded_where_it_is_drawn() {
        using var tree = new LayoutTree();
        var root = Block(tree, LayoutNodeId.Invalid, 400f);

        var centred = Block(tree, root, 200f);
        tree.SetMargin(centred, Edge.Left, StyleLength.Auto);
        tree.SetMargin(centred, Edge.Right, StyleLength.Auto);

        var f = Float(tree, centred, FloatSide.Left, 50f, 50f);
        var cousin = FormattingContextRoot(tree, root, 30f, 20f);

        tree.CalculateLayout(root, 400f, float.NaN, Direction.Ltr);

        // (400 − 200) / 2 = 100, so the float's margin box is 100..150 in the root's coordinates.
        AssertAt(tree, centred, 100f, 0f);
        AssertAt(tree, f, 0f, 0f);
        AssertAt(tree, cousin, 150f, 0f);
    }

    /// <summary>
    ///     A float starts at its own containing block's content edge, not at the formatting context
    ///     root's.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the defect the auto-margin row was a special case of, and no row named it.</b>
    ///     §9.5.1's rules 1 and 7 are stated against the float's <i>containing block</i> — its parent
    ///     box. The exclusion list is stated against the formatting context <i>root</i>, and
    ///     <c>PlaceFloatChild</c> took the band's edge as the float's edge without narrowing it to the
    ///     container. With a <b>stated</b> 100-point left margin on the parent, the float was placed at
    ///     the root's content edge and reported at <c>x = −100</c> from its own parent — a negative
    ///     offset, which is the shape of the bug rather than a rounding of it.
    ///     <para>
    ///         ⚠ Invisible to all 84 fixtures for a reason worth stating exactly:
    ///         <c>float_bfc_avoids_float_from_sibling_subtree</c> is the only one that nests a float at
    ///         all, and the box it nests it in has no margin, no padding and no width of its own. Every
    ///         other float in the corpus is a direct child of the context root, where the containing
    ///         block's content edge and the root's are the same number.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_float_starts_at_its_own_containing_blocks_content_edge() {
        using var tree = new LayoutTree();
        var root = Block(tree, LayoutNodeId.Invalid, 400f);

        var offset = Block(tree, root, 200f);
        tree.SetMargin(offset, Edge.Left, StyleLength.Points(100f));

        var f = Float(tree, offset, FloatSide.Left, 50f, 50f);
        var cousin = FormattingContextRoot(tree, root, 30f, 20f);

        tree.CalculateLayout(root, 400f, float.NaN, Direction.Ltr);

        AssertAt(tree, f, 0f, 0f);
        AssertAt(tree, cousin, 150f, 0f);
    }

    /// <summary>A right float stops at its own containing block's right content edge.</summary>
    /// <remarks>
    ///     The other half of the same clamp, and the half that fails silently in the other direction:
    ///     without it a right float in a 200-point box inside a 400-point root is placed against the
    ///     ROOT's right edge, which is 200 points outside the box that owns it.
    /// </remarks>
    [Fact]
    public void A_right_float_stops_at_its_own_containing_blocks_right_edge() {
        using var tree = new LayoutTree();
        var root = Block(tree, LayoutNodeId.Invalid, 400f);

        var offset = Block(tree, root, 200f);
        tree.SetMargin(offset, Edge.Left, StyleLength.Points(100f));

        var f = Float(tree, offset, FloatSide.Right, 50f, 50f);

        tree.CalculateLayout(root, 400f, float.NaN, Direction.Ltr);

        // 200 wide, so the right float's left edge is at 150 in its parent's coordinates.
        AssertAt(tree, f, 150f, 0f);
    }

    // ── float-margin-collapse-through ───────────────────────────────────────────────────────────

    /// <summary>
    ///     A float's own top margin is part of its margin box, and collapses with nothing.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Confirmed rather than refuted, and the row's own framing was the honest one: the
    ///     arithmetic was reasoned and is now measured.</b>
    ///     <c>grep 'float=' Corpus/float.xml</c> finds no floated box with a <c>margin-top</c>, so
    ///     nothing pinned either half. Both are asserted here at once, because they fail differently:
    ///     the float's border box at 20 says the margin was spent, and the cleared box at 70 says the
    ///     exclusion's bottom edge included it. A float is a formatting context root (§9.4.1), so its
    ///     margin cannot collapse with the container's top edge either — which is the third number,
    ///     the container's own height of 80.
    /// </remarks>
    [Fact]
    public void A_floats_own_top_margin_is_part_of_its_margin_box() {
        using var tree = new LayoutTree();
        var root = Block(tree, LayoutNodeId.Invalid, 200f);

        var f = Float(tree, root, FloatSide.Left, 50f, 50f);
        tree.SetMargin(f, Edge.Top, StyleLength.Points(20f));

        var cleared = Block(tree, root, float.NaN);
        tree.SetClear(cleared, Clear.Left);
        tree.SetDimension(cleared, Dimension.Height, StyleLength.Points(10f));

        tree.CalculateLayout(root, 200f, float.NaN, Direction.Ltr);

        AssertAt(tree, f, 0f, 20f);
        AssertAt(tree, cleared, 0f, 70f);
        Assert.Equal(80f, tree.GetHeight(root), Tolerance);
    }

    // ── float-percentage-height ─────────────────────────────────────────────────────────────────

    /// <summary>A float's percentage height resolves against its containing block, like any block.</summary>
    /// <remarks>
    ///     ⚠ <b>Confirmed, and worth an assertion precisely because the correct answer is the boring
    ///     one.</b> A float is out of flow and a formatting context root, which are both places this
    ///     engine has had a definite-size rule quietly diverge; the honest statement is that a float's
    ///     percentage height is not special-cased, and this is what stops that from becoming true by
    ///     accident. The exclusion's bottom edge is asserted through a cleared sibling rather than
    ///     read off the float, so a height that resolved in the geometry but not in the exclusion list
    ///     would still be caught.
    /// </remarks>
    [Fact]
    public void A_float_with_a_percentage_height_resolves_against_its_containing_block() {
        using var tree = new LayoutTree();
        var root = Block(tree, LayoutNodeId.Invalid, 200f);
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(100f));

        var f = tree.CreateNode();
        tree.SetDisplay(f, Display.Block);
        tree.SetFloat(f, FloatSide.Left);
        tree.SetDimension(f, Dimension.Width, StyleLength.Points(50f));
        tree.SetDimension(f, Dimension.Height, StyleLength.Percent(50f));
        tree.AddChild(root, f);

        var cleared = Block(tree, root, float.NaN);
        tree.SetClear(cleared, Clear.Left);
        tree.SetDimension(cleared, Dimension.Height, StyleLength.Points(10f));

        tree.CalculateLayout(root, 200f, 100f, Direction.Ltr);

        Assert.Equal(50f, tree.GetHeight(f), Tolerance);
        AssertAt(tree, cleared, 0f, 50f);
    }

    // ── right-float-narrows-bfc-root ────────────────────────────────────────────────────────────

    /// <summary>An automatic-width formatting context root narrows beside a RIGHT float.</summary>
    /// <remarks>
    ///     ⚠ <b>Confirmed: <c>AvoidFloats</c> really is symmetric, and the row was right that nothing
    ///     proved it.</b> All ten <c>float_bfc_*</c> families float LEFT, in their RTL variant as much
    ///     as their LTR one — <c>float="right"</c> appears twice in the whole corpus and neither
    ///     fixture puts a formatting context root beside one. So the left half of that branch is
    ///     measured forty times and the right half was measured zero times. This is the mirror of
    ///     <c>float_bfc_narrows_beside_float</c>.
    /// </remarks>
    [Fact]
    public void An_auto_width_formatting_context_root_narrows_beside_a_right_float() {
        using var tree = new LayoutTree();
        var root = Block(tree, LayoutNodeId.Invalid, 200f);

        var f = Float(tree, root, FloatSide.Right, 50f, 50f);

        var bfc = tree.CreateNode();
        tree.SetDisplay(bfc, Display.Block);
        tree.SetOverflow(bfc, Overflow.Hidden);
        tree.SetDimension(bfc, Dimension.Height, StyleLength.Points(20f));
        tree.AddChild(root, bfc);

        tree.CalculateLayout(root, 200f, float.NaN, Direction.Ltr);

        AssertAt(tree, f, 150f, 0f);
        AssertAt(tree, bfc, 0f, 0f);
        Assert.Equal(150f, tree.GetWidth(bfc), Tolerance);
    }

    /// <summary>A stated-width formatting context root moves below a RIGHT float it cannot fit beside.</summary>
    /// <remarks>
    ///     The mirror of <c>float_bfc_moves_below_float</c>, and the pair matters: an automatic width
    ///     narrows and a stated one drops, which is the distinction two of the ten left-floating
    ///     families exist to draw. A branch that handled the right side by narrowing only would pass
    ///     the test above and fail this one.
    /// </remarks>
    [Fact]
    public void A_stated_width_formatting_context_root_moves_below_a_right_float() {
        using var tree = new LayoutTree();
        var root = Block(tree, LayoutNodeId.Invalid, 200f);

        var f = Float(tree, root, FloatSide.Right, 50f, 50f);

        var bfc = tree.CreateNode();
        tree.SetDisplay(bfc, Display.Block);
        tree.SetOverflow(bfc, Overflow.Hidden);
        tree.SetDimension(bfc, Dimension.Width, StyleLength.Points(200f));
        tree.SetDimension(bfc, Dimension.Height, StyleLength.Points(20f));
        tree.AddChild(root, bfc);

        tree.CalculateLayout(root, 200f, float.NaN, Direction.Ltr);

        AssertAt(tree, f, 150f, 0f);
        AssertAt(tree, bfc, 0f, 50f);
        Assert.Equal(200f, tree.GetWidth(bfc), Tolerance);
    }

    // ── The flow-relative keywords, which are a `direction` question and not a writing-mode one ──
    //
    // ⚠ CSS Logical Properties adds `inline-start` and `inline-end` to `float` and `clear`, and doc
    // 43's ledger, `UtilityFamilies` and `LayoutStyleBuilder` all recorded them as waiting on a
    // writing mode this store decided (in #282) never to gain. They resolve against the writing mode
    // AND the direction, and with no vertical writing mode the inline axis is horizontal in every
    // configuration this engine can be in — so the whole of the resolution is `direction`.
    //
    // ⚠ The three tests below have to be read together. Two of them would pass against an
    // implementation that simply aliased `inline-start` onto `Left`, which is the shortcut the
    // refusal was written to avoid; the RTL one is the only one that can tell the two apart, and the
    // fourth pins that the PHYSICAL keywords still do not flip beside them.

    /// <summary><c>float: inline-start</c> is the left in LTR.</summary>
    [Fact]
    public void An_inline_start_float_is_the_left_in_ltr() {
        using var tree = new LayoutTree();
        var root = Block(tree, LayoutNodeId.Invalid, 200f);
        var f = Float(tree, root, FloatSide.InlineStart, 50f, 50f);
        var bfc = FormattingContextRoot(tree, root, 30f, 20f);

        tree.CalculateLayout(root, 200f, float.NaN, Direction.Ltr);

        AssertAt(tree, f, 0f, 0f);
        AssertAt(tree, bfc, 50f, 0f);
    }

    /// <summary><c>float: inline-start</c> is the right in RTL, and the exclusion goes with it.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the test the whole refusal turns on.</b> An implementation that aliased
    ///     <c>inline-start</c> onto <see cref="FloatSide.Left" /> — "right in LTR and wrong in RTL
    ///     inside the same declaration", which is how the refusal described the shortcut — passes the
    ///     LTR test above and fails this one, on both assertions. The avoiding box is asserted as well
    ///     as the float, because the side is written into the exclusion list as well as into the
    ///     rectangle and a resolution applied to only one of them is a float that clears from the
    ///     wrong edge.
    /// </remarks>
    [Fact]
    public void An_inline_start_float_is_the_right_in_rtl() {
        using var tree = new LayoutTree();
        var root = Block(tree, LayoutNodeId.Invalid, 200f);
        tree.SetDirection(root, Direction.Rtl);

        var f = Float(tree, root, FloatSide.InlineStart, 50f, 50f);
        var bfc = FormattingContextRoot(tree, root, 30f, 20f);

        tree.CalculateLayout(root, 200f, float.NaN, Direction.Rtl);

        AssertAt(tree, f, 150f, 0f);
        AssertAt(tree, bfc, 120f, 0f);
    }

    /// <summary><c>clear: inline-end</c> clears the right in LTR and the left in RTL.</summary>
    /// <remarks>
    ///     The clear side is resolved where the clearance point is asked for, which is two call sites
    ///     — the block walk's and <c>PlaceFloatChild</c>'s own — so this asserts the first and
    ///     <see cref="An_inline_start_float_is_the_right_in_rtl" />'s shape covers the second.
    /// </remarks>
    [Theory]
    [InlineData(Direction.Ltr, 50f)]
    [InlineData(Direction.Rtl, 0f)]
    public void An_inline_end_clear_names_the_far_side_of_the_inline_axis(Direction direction, float expectedTop) {
        using var tree = new LayoutTree();
        var root = Block(tree, LayoutNodeId.Invalid, 200f);
        tree.SetDirection(root, direction);

        // A physical RIGHT float. `clear: inline-end` clears it in LTR and does not in RTL, where
        // `inline-end` names the left.
        Float(tree, root, FloatSide.Right, 50f, 50f);

        var cleared = Block(tree, root, float.NaN);
        tree.SetClear(cleared, Clear.InlineEnd);
        tree.SetDimension(cleared, Dimension.Height, StyleLength.Points(10f));

        tree.CalculateLayout(root, 200f, float.NaN, direction);

        Assert.Equal(expectedTop, tree.GetTop(cleared), Tolerance);
    }

    /// <summary>The physical keywords still do not flip, which is why the logical pair is separate.</summary>
    /// <remarks>
    ///     ⚠ <b>The observation the refusal rested on is true and was about the other keywords.</b>
    ///     The ten <c>float_bfc_*</c> families ship RTL variants whose expectations are identical to
    ///     their LTR twins, which proves <c>float: left</c> does not flip — and that is precisely the
    ///     reason <see cref="FloatSide.InlineStart" /> has to be a fourth value rather than a
    ///     rereading of <see cref="FloatSide.Left" />. Asserted here so that a later simplification
    ///     that "unifies" the two pairs goes red.
    /// </remarks>
    [Fact]
    public void A_physical_left_float_is_still_the_left_in_rtl() {
        using var tree = new LayoutTree();
        var root = Block(tree, LayoutNodeId.Invalid, 200f);
        tree.SetDirection(root, Direction.Rtl);

        var f = Float(tree, root, FloatSide.Left, 50f, 50f);
        var bfc = FormattingContextRoot(tree, root, 30f, 20f);

        tree.CalculateLayout(root, 200f, float.NaN, Direction.Rtl);

        // The float stays on the left. The avoiding box goes to 170 rather than to 50, and that is
        // §10.3.3 rather than avoidance: a stated width is anchored at the inline START, which in RTL
        // is the right edge, and 170..200 is already clear of a float occupying 0..50.
        AssertAt(tree, f, 0f, 0f);
        AssertAt(tree, bfc, 170f, 0f);
    }

    // ── avoid-floats-band-is-the-containers ─────────────────────────────────────────────────────

    /// <summary>An inset container holding one float-avoiding box, with a float outside it.</summary>
    /// <remarks>
    ///     ⚠ <b>The inset is the whole fixture.</b> The float lives in the ROOT and the avoiding box
    ///     lives 100 points inside it, so the exclusion list's coordinates and the container's differ
    ///     by exactly that — which is the one thing no fixture in <c>Corpus/float.xml</c> arranges.
    ///     The container is 200 wide inside a root of 400, so a band read in the wrong coordinates is
    ///     wrong by 100 and a width taken from the wrong box is wrong by 200: no rounding produces
    ///     either.
    /// </remarks>
    static (LayoutNodeId Root, LayoutNodeId Avoiding) InsetAvoidingFixture(
        LayoutTree tree,
        float floatWidth,
        float statedWidth,
        Direction direction
    ) {
        var root = Block(tree, LayoutNodeId.Invalid, 400f);
        tree.SetDirection(root, direction);

        Float(tree, root, FloatSide.Left, floatWidth, 50f);

        var inset = Block(tree, root, 200f);
        tree.SetMargin(inset, direction == Direction.Ltr ? Edge.Left : Edge.Right, StyleLength.Points(100f));

        var avoiding = tree.CreateNode();
        tree.SetDisplay(avoiding, Display.Block);
        tree.SetOverflow(avoiding, Overflow.Hidden);
        tree.SetDimension(avoiding, Dimension.Height, StyleLength.Points(20f));

        if (!float.IsNaN(statedWidth)) {
            tree.SetDimension(avoiding, Dimension.Width, StyleLength.Points(statedWidth));
        }

        tree.AddChild(inset, avoiding);

        return (root, avoiding);
    }

    /// <summary>A box is not slid aside from a float its own container never reaches.</summary>
    /// <remarks>
    ///     ⚠ <b>The defect <c>Rikarin/Vixen#781</c> names, and both numbers are wrong at once.</b> The
    ///     float's margin box ends at context x = 50 and the container's content begins at context
    ///     x = 100, so the box is already clear: §9.5 has nothing to say about it and §10.3.3 gives it
    ///     the container's whole width. Reading the root's band as the container's answered x = 50 and
    ///     a width of 350 — moved aside from a float it was never beside, and 150 points wider than the
    ///     box that contains it.
    ///     <para>
    ///         The width is asserted as well as the position because either alone passes for the other
    ///         half being broken, and the two are checked against the CONTAINER rather than against a
    ///         constant: a box clear of every float fills its container exactly.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_float_avoiding_box_ignores_a_float_its_own_container_never_reaches() {
        using var tree = new LayoutTree();
        var (root, avoiding) = InsetAvoidingFixture(tree, floatWidth: 50f, statedWidth: float.NaN, Direction.Ltr);

        tree.CalculateLayout(root, 400f, float.NaN, Direction.Ltr);

        AssertAt(tree, avoiding, 0f, 0f);
        Assert.Equal(200f, tree.GetWidth(avoiding), Tolerance);
    }

    /// <summary>Where the float does reach in, the box narrows to what is left of its own container.</summary>
    /// <remarks>
    ///     ⚠ <b>The other half, and without it the test above is satisfied by an engine that stopped
    ///     avoiding floats altogether.</b> A 150-point float ends 50 points inside a container whose
    ///     content starts at 100, so §9.5 really does apply — and the answer is the container's own
    ///     remainder. The closed form is that the box still ends where its container does:
    ///     <c>left + width</c> is the container's content width whichever side of the clamp the band
    ///     came from.
    /// </remarks>
    [Fact]
    public void A_float_avoiding_box_narrows_to_what_is_left_of_its_own_container() {
        using var tree = new LayoutTree();
        var (root, avoiding) = InsetAvoidingFixture(tree, floatWidth: 150f, statedWidth: float.NaN, Direction.Ltr);

        tree.CalculateLayout(root, 400f, float.NaN, Direction.Ltr);

        AssertAt(tree, avoiding, 50f, 0f);
        Assert.Equal(150f, tree.GetWidth(avoiding), Tolerance);
        Assert.Equal(200f, tree.GetLeft(avoiding) + tree.GetWidth(avoiding), Tolerance);
    }

    /// <summary>A stated width is offered the container's band, not the root's.</summary>
    /// <remarks>
    ///     The stated-width path asks whether the box FITS before it decides where to put it, and it
    ///     asked against the root's remainder — 250 points where the container has 150. A box of 180
    ///     fits one and not the other, so this is the fixture where the two readings disagree about
    ///     which slice the box lands in rather than only about where in the slice it goes: it belongs
    ///     below the float, at the container's own content edge and its own full width.
    /// </remarks>
    [Fact]
    public void A_stated_width_float_avoiding_box_is_asked_to_fit_its_own_container() {
        using var tree = new LayoutTree();
        var (root, avoiding) = InsetAvoidingFixture(tree, floatWidth: 150f, statedWidth: 180f, Direction.Ltr);

        tree.CalculateLayout(root, 400f, float.NaN, Direction.Ltr);

        AssertAt(tree, avoiding, 0f, 50f);
        Assert.Equal(180f, tree.GetWidth(avoiding), Tolerance);
    }

    /// <summary>In RTL the anchor is the container's own inline start, not the context root's.</summary>
    /// <remarks>
    ///     ⚠ <b>The RTL branch anchors a stated width against the far edge, and that edge was the
    ///     ROOT's.</b> With the float outside the container's band the box is an ordinary §10.3.3 one
    ///     again and RTL puts the leftover space on its left: 200 − 80 = 120 inside the container.
    ///     Anchored against the root instead it came out past the container's right edge entirely.
    /// </remarks>
    [Fact]
    public void In_rtl_a_stated_width_avoiding_box_is_anchored_to_its_own_container() {
        using var tree = new LayoutTree();
        var (root, avoiding) = InsetAvoidingFixture(tree, floatWidth: 50f, statedWidth: 80f, Direction.Rtl);

        tree.CalculateLayout(root, 400f, float.NaN, Direction.Rtl);

        AssertAt(tree, avoiding, 120f, 0f);
        Assert.Equal(80f, tree.GetWidth(avoiding), Tolerance);
    }

    // ── Fixture helpers ─────────────────────────────────────────────────────────────────────────

    static LayoutNodeId Block(LayoutTree tree, LayoutNodeId parent, float width) {
        var node = tree.CreateNode();
        tree.SetDisplay(node, Display.Block);

        if (!float.IsNaN(width)) {
            tree.SetDimension(node, Dimension.Width, StyleLength.Points(width));
        }

        if (parent != LayoutNodeId.Invalid) {
            tree.AddChild(parent, node);
        }

        return node;
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

    /// <summary>An <c>overflow: hidden</c> box, which §9.5 moves aside whole rather than overlapping.</summary>
    static LayoutNodeId FormattingContextRoot(LayoutTree tree, LayoutNodeId parent, float width, float height) {
        var node = tree.CreateNode();
        tree.SetDisplay(node, Display.Block);
        tree.SetOverflow(node, Overflow.Hidden);
        tree.SetDimension(node, Dimension.Width, StyleLength.Points(width));
        tree.SetDimension(node, Dimension.Height, StyleLength.Points(height));
        tree.AddChild(parent, node);

        return node;
    }

    static void AssertAt(LayoutTree tree, LayoutNodeId node, float left, float top) {
        Assert.Equal(left, tree.GetLeft(node), Tolerance);
        Assert.Equal(top, tree.GetTop(node), Tolerance);
    }
}
