// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>CSS Transforms 2's <c>rotate</c> and <c>scale</c>, from the stylesheet to the pixels.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every assertion here is a pixel, and every one of them is chosen to fail for the
///         <i>neighbouring</i> transform.</b> This is the lesson the colour filters cost: any
///         transform changes the draw list by opening a group, so a test that looked at the draw list
///         — or a consumption gate that looks at anything the draw list can tell it — would pass a
///         <c>rotate</c> that came out a <c>scale</c>, a <c>scale</c> that came out the identity, and
///         a matrix parked on a command no executor reads. The bracket appears either way.
///     </para>
///     <para>
///         So the shape of every test below is: probe a point the <i>right</i> transform inks and the
///         plausible wrong one does not, and a second point the other way round. A single positive
///         probe is worth very little here — a transform that did too much still inks the middle of
///         the shape, and so does one that did nothing.
///     </para>
///     <para>
///         ⚠ <b>The software rasteriser, not the device, and <c>UiCompositingTests</c> is what stops
///         that being a hole.</b> What a rotation <i>is</i> has to be asserted against arithmetic and
///         a picture, which needs no GPU; that the two executors draw the same picture is a different
///         claim and lives over there. The same split <c>FilterColourTests</c> makes, for the same
///         reason.
///     </para>
/// </remarks>
public class TransformPaintTests {
    const int Side = 60;
    const int Middle = 30;

    /// <summary>A white bar on a black field, under whatever transform the caller names.</summary>
    /// <remarks>
    ///     ⚠ <b>A bar and not a square, and that is the fixture doing most of the work.</b> A square
    ///     is symmetric under every quarter turn, so a <c>rotate-90</c> over one is pixel-identical to
    ///     doing nothing at all — a fixture that would measure this whole feature as working while it
    ///     was refused. The bar is 24×8 and centred, so its long axis is an observable that a rotation
    ///     moves and nothing else does.
    /// </remarks>
    static UiTest Bar(string transform) {
        var ui = UiTest.Create(Side, Side);
        ui.Document.Compositing = true;

        ui.Load(
            $$"""
            root { width: {{Side}}px; height: {{Side}}px; background-color: #000000; }
            .bar { position: absolute; left: 18px; top: 26px; width: 24px; height: 8px;
                   background-color: #ffffff; {{transform}} }
            """
        );

        ui.Create("div", null, "bar", "bar");
        ui.Frame();

        return ui;
    }

    /// <summary>A white square on a black field, for the tests a bar's asymmetry would confuse.</summary>
    static UiTest Square(string transform) {
        var ui = UiTest.Create(Side, Side);
        ui.Document.Compositing = true;

        ui.Load(
            $$"""
            root { width: {{Side}}px; height: {{Side}}px; background-color: #000000; }
            .box { position: absolute; left: 20px; top: 20px; width: 20px; height: 20px;
                   background-color: #ffffff; {{transform}} }
            """
        );

        ui.Create("div", null, "box", "box");
        ui.Frame();

        return ui;
    }

    /// <summary>Whether a pixel is more white than black.</summary>
    /// <remarks>
    ///     ⚠ A threshold rather than an equality, because a transformed edge is antialiased at an
    ///     angle no pixel grid agrees with — a rotated bar's boundary runs through partial coverage
    ///     for its whole length. Every probe below is placed well inside or well outside the shape, so
    ///     the answer is never near the threshold and the number is not load-bearing.
    /// </remarks>
    static bool Inked(UiTest ui, int x, int y) {
        var bitmap = ui.Capture();

        return bitmap.Pixels[bitmap.Offset(x, y)] > 128;
    }

    /// <summary>A quarter turn swaps the bar's axes, which no scale can do.</summary>
    /// <remarks>
    ///     ⚠ <b>Four probes, and the two negatives are the load-bearing ones.</b> Asserting only that
    ///     the bar now inks above and below its centre would pass a <c>scale</c> that grew it in both
    ///     directions, and asserting only that it stopped inking to the left would pass a transform
    ///     that shrank it to nothing. The pair together say the extent <i>moved</i> from one axis to
    ///     the other, which is a rotation and is not any scale, any skew, or any translation.
    /// </remarks>
    [Fact]
    public void A_quarter_turn_moves_the_bars_length_onto_the_other_axis() {
        using var upright = Bar("");

        Assert.True(Inked(upright, Middle - 10, Middle));
        Assert.False(Inked(upright, Middle, Middle - 10));

        using var turned = Bar("rotate: 90deg;");

        // The length is now vertical...
        Assert.True(Inked(turned, Middle, Middle - 10));

        // ...and is no longer horizontal. A scale that doubled both axes would ink both.
        Assert.False(Inked(turned, Middle - 10, Middle));
    }

    /// <summary>And it turns clockwise, which is the half of a rotation a symmetric test cannot see.</summary>
    /// <remarks>
    ///     ⚠ <b>A quarter turn of a bar is symmetric under a sign error, so the direction is asserted
    ///     on an L instead.</b> `rotate: 90deg` and `rotate: -90deg` produce the identical picture for
    ///     every fixture above — the bar is its own 180° rotation — so a transform built with the
    ///     y-down convention got backwards would pass all of them. Here a small square sits at the
    ///     bar's left end; CSS Transforms 1 § 11 makes a positive angle clockwise on screen, and
    ///     clockwise sends a point on the −x axis to the −y axis, so the mark must finish <i>above</i>
    ///     the centre.
    /// </remarks>
    [Fact]
    public void A_positive_angle_turns_clockwise() {
        var ui = UiTest.Create(Side, Side);
        ui.Document.Compositing = true;

        ui.Load(
            $$"""
            root { width: {{Side}}px; height: {{Side}}px; background-color: #000000; }
            .arm { position: absolute; left: 14px; top: 26px; width: 32px; height: 8px;
                   background-color: #202020; rotate: 90deg; transform-origin: center; }
            .tip { position: absolute; left: 0px; top: 0px; width: 8px; height: 8px;
                   background-color: #ffffff; }
            """
        );

        var arm = ui.Create("div", null, "arm", "arm");
        ui.Create("div", arm, "tip", "tip");
        ui.Frame();

        using var _ = ui;

        // The tip started at the arm's left end, sixteen points left of the centre. A clockwise
        // quarter turn puts it sixteen points *above* it.
        Assert.True(Inked(ui, Middle, Middle - 12));
        Assert.False(Inked(ui, Middle, Middle + 12));
    }

    /// <summary>A scale grows the picture past where any rotation of it could reach.</summary>
    /// <remarks>
    ///     ⚠ <b>The probe is at 1.7 half-widths, and that number is the discriminator.</b> A square
    ///     rotated by any angle still fits inside a circle of √2 half-widths, so no rotation whatever
    ///     can ink a point at 1.7 of them on the axis. A <c>scale: 2</c> reaches 2.0 and does. Picking
    ///     a probe at 1.2 instead would have been satisfied by <c>rotate-45</c>, which is exactly the
    ///     neighbouring transform this has to exclude.
    /// </remarks>
    [Fact]
    public void A_scale_reaches_further_than_any_rotation_of_the_same_box() {
        using var plain = Square("");
        using var turned = Square("rotate: 45deg;");
        using var grown = Square("scale: 200%;");

        // Ten points of half-width; the probe is seventeen out.
        Assert.False(Inked(plain, Middle + 17, Middle));
        Assert.False(Inked(turned, Middle + 17, Middle));
        Assert.True(Inked(grown, Middle + 17, Middle));
    }

    /// <summary>A rotation reaches the diagonal a scale of the same extent does not.</summary>
    /// <remarks>
    ///     The converse probe, and the reason both exist: the test above alone would pass a
    ///     <c>rotate</c> that had been implemented as a scale by √2. Here the corner of a
    ///     <c>rotate-45</c> square sits on the axis where the unrotated one has nothing.
    /// </remarks>
    [Fact]
    public void A_rotation_puts_a_corner_where_the_upright_box_had_none() {
        using var plain = Square("");
        using var turned = Square("rotate: 45deg;");

        // Thirteen out: past the upright box's ten, inside the rotated one's fourteen.
        Assert.False(Inked(plain, Middle + 13, Middle));
        Assert.True(Inked(turned, Middle + 13, Middle));

        // And the diagonal is the other way round, which is what makes this a rotation rather than
        // a uniform growth: the upright box's own corner is now outside the rotated one.
        Assert.True(Inked(plain, Middle + 8, Middle + 8));
        Assert.False(Inked(turned, Middle + 8, Middle + 8));
    }

    /// <summary>Two components scale the two axes separately.</summary>
    /// <remarks>
    ///     ⚠ <b>And one component scales both, which is the trap this pair exists for.</b> A reader
    ///     written from <c>translate</c>'s shape — where a missing second component is zero — would
    ///     make <c>scale: 2</c> a horizontal stretch, and every uniform-scale assertion above would
    ///     still pass on a square. The second half of this test is the one that catches it.
    /// </remarks>
    [Fact]
    public void A_one_component_scale_is_uniform_and_a_two_component_one_is_not() {
        using var wide = Square("scale: 2 1;");

        Assert.True(Inked(wide, Middle + 17, Middle));
        Assert.False(Inked(wide, Middle, Middle + 17));

        using var both = Square("scale: 2;");

        Assert.True(Inked(both, Middle + 17, Middle));
        Assert.True(Inked(both, Middle, Middle + 17));
    }

    /// <summary>The origin is the box's centre, and naming a corner moves it.</summary>
    /// <remarks>
    ///     ⚠ <b>The default is asserted first and it is not the obvious one.</b> Transforms 1 § 6
    ///     makes <c>transform-origin</c> <c>50% 50%</c>, so a scale grows a box about its middle and
    ///     leaves the middle where it was. An implementation that defaulted to the box's top left —
    ///     the natural thing to write, since that is where the rectangle's coordinates are — would
    ///     grow it down and to the right instead, and every symmetric probe in this file would still
    ///     pass.
    /// </remarks>
    [Fact]
    public void A_scale_grows_about_the_centre_unless_told_otherwise() {
        using var centred = Square("scale: 200%;");

        // Symmetric about the middle: both sides gained the same.
        Assert.True(Inked(centred, Middle - 17, Middle));
        Assert.True(Inked(centred, Middle + 17, Middle));

        using var cornered = Square("scale: 200%; transform-origin: top left;");

        // Pinned at the box's top left, which is ten points up and left of the middle. It now grows
        // only down and right.
        Assert.False(Inked(cornered, Middle - 17, Middle));
        Assert.True(Inked(cornered, Middle + 17, Middle));
    }

    /// <summary>Two origin keywords mean the same point in either order.</summary>
    /// <remarks>
    ///     ⚠ <b>The one place this grammar is not positional, and getting it wrong is silent.</b>
    ///     Transforms 1 §6 lets <c>top right</c> and <c>right top</c> name the same corner, because
    ///     <c>top</c> can only be a y and <c>right</c> can only be an x. A reader that took them
    ///     positionally would ask <c>top</c> for an x, get nothing, fall back to the centre on both
    ///     axes — and five of Tailwind's nine <c>origin-*</c> classes would quietly be
    ///     <c>origin-center</c>. Every probe in the rest of this file would still pass.
    /// </remarks>
    [Theory]
    [InlineData("top right")]
    [InlineData("right top")]
    public void Two_origin_keywords_name_the_same_corner_in_either_order(string origin) {
        using var pinned = Square($"scale: 200%; transform-origin: {origin};");

        // The box is (20,20)-(40,40); pinned at its top right corner, 2x grows it left and down only.
        Assert.True(Inked(pinned, Middle - 17, Middle));
        Assert.False(Inked(pinned, Middle + 17, Middle));
        Assert.True(Inked(pinned, Middle, Middle + 17));
        Assert.False(Inked(pinned, Middle, Middle - 17));
    }

    /// <summary>A transformed parent carries its children, and does not move its siblings.</summary>
    /// <remarks>
    ///     ⚠ <b>Both halves, because they are the two ways this can leak.</b> A subtree left behind is
    ///     a panel that slides out from under its contents; a sibling that moved is a transform that
    ///     reached layout, which CSS Transforms 1 § 3 forbids outright and which would make every
    ///     <c>scale-*</c> in an interface reflow the row it is in.
    /// </remarks>
    [Fact]
    public void A_transformed_parent_carries_its_subtree_and_leaves_its_siblings() {
        // A wider field than the rest of the file, because the sibling has to have somewhere to be
        // wrong: the whole point of the last two probes is that there is room at x = 60 for a scale
        // that had leaked into layout to have pushed it to.
        var ui = UiTest.Create(80f, 80f);
        ui.Document.Compositing = true;

        ui.Load(
            """
            root { width: 80px; height: 80px; background-color: #000000; padding: 20px;
                   display: flex; flex-direction: row; align-items: flex-start; }
            .grown { width: 20px; height: 20px; background-color: #303030; scale: 200%; }
            .mark { width: 6px; height: 6px; background-color: #ffffff; }
            .after { width: 10px; height: 10px; background-color: #ffffff; }
            """
        );

        var grown = ui.Create("div", null, "grown", "grown");
        ui.Create("div", grown, "mark", "mark");
        ui.Create("div", null, "after", "after");
        ui.Frame();

        using var _ = ui;

        // Layout puts the parent at (20,20)–(40,40), so it scales about (30,30). The 6×6 mark in its
        // top-left corner spans (20,20)–(26,26) untransformed and (10,10)–(22,22) at 2×.
        //
        // ⚠ The two probes are a pair, and the second is the one with teeth. (12,12) is inside the
        // scaled mark and outside the unscaled one, so it fails if the subtree was left behind;
        // (24,24) is inside the *unscaled* mark and outside the scaled one, so it fails if the parent
        // grew and the child did not come along. Either alone would pass an implementation that
        // scaled the mark independently of its parent.
        Assert.True(Inked(ui, 12, 12));
        Assert.False(Inked(ui, 24, 24));

        // The sibling is still at x = 40, where layout put it beside a 20-point-wide box — not at 60,
        // where a scale that had reached layout would have pushed it.
        Assert.True(Inked(ui, 44, 24));
        Assert.False(Inked(ui, 64, 24));
    }

    /// <summary>An identity transform opens no group, and a real one does.</summary>
    /// <remarks>
    ///     ⚠ <b>The negative is what this is for.</b> <c>rotate: 0deg</c> and <c>scale: 1</c> are the
    ///     initial values and are written constantly — every <c>rotate-0</c>, every animation back at
    ///     rest. Each one that opened a group would spend a viewport-sized surface and a render pass
    ///     to draw the identical picture, which is a cost nothing in the frame would explain. This is
    ///     the one claim in the file a screenshot cannot make, so it is made against the geometry.
    /// </remarks>
    [Fact]
    public void Only_a_transform_that_moves_something_opens_a_group() {
        using var still = Square("rotate: 0deg; scale: 100%;");
        Assert.Empty(still.Geometry.Layers);

        using var turned = Square("rotate: 30deg;");
        Assert.Single(turned.Geometry.Layers);

        // ⚠ And the group survives the single-command collapse, which is the peephole that folds a
        // group carrying nothing but a fade back into its one draw. A plain background rectangle
        // under a `rotate-*` is exactly the shape that peephole catches, so a transform that forgot
        // to guard it would paint the element square and unrotated.
        Assert.NotNull(turned.Geometry.Layers[0].Transform);
    }

    /// <summary>A degenerate scale paints nothing rather than a point.</summary>
    /// <remarks>
    ///     <c>scale-0</c> is a real class and a common way to hide something. The group has no
    ///     inverse, so there is no surface worth allocating; asserting the layer list is empty is what
    ///     says the group was dropped rather than composited into a zero-area quad.
    /// </remarks>
    [Fact]
    public void A_zero_scale_draws_nothing() {
        using var gone = Square("scale: 0;");

        Assert.Empty(gone.Geometry.Layers);
        Assert.False(Inked(gone, Middle, Middle));
    }
}
