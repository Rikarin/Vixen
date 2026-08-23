// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Rendering;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>The seven colour functions of <c>filter</c>, from the stylesheet to the pixels.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>These are the tests <c>UiCompositingTests</c> cannot be, and the argument is
///         <c>FilterBlurTests</c>' one word for word.</b> That file's job is that the device and the
///         software renderer draw the same frame, and both of them take the matrix from the
///         <i>same</i> builder — so a <c>sepia</c> that came out as a <c>grayscale</c> would be
///         identically wrong on both paths and the comparison would pass. What a colour is has to be
///         asserted here, against arithmetic, and only the agreement belongs over there.
///     </para>
///     <para>
///         ⚠ <b>And these are the tests the consumption gate cannot be.</b> That gate's verdict is
///         "the draw list changed", and any <c>filter</c> changes it by opening a group — the
///         bracket appears whatever the matrix says. It would pass on a matrix no executor reads, on a
///         <c>grayscale</c> that came out the identity, and on a scene with no colour in it. Its scene
///         list is not the thing to fix either, because the draw list is where the gate stops.
///     </para>
///     <para>
///         ⚠ <b>Half of these read pixels and half read the matrix, and the split is on purpose.</b>
///         A matrix is exactly checkable — <c>invert(1)</c> takes white to black and there is nothing
///         to argue about — so where the claim is arithmetic it is made against the matrix, and where
///         the claim is that the arithmetic <i>reached the frame</i> it is made against the picture
///         <see cref="UiTest.Capture" /> renders. A file that only did the first would pass with the
///         matrix parked on a command nothing executes, which is this repository's commonest defect.
///     </para>
///     <para>
///         ⚠ <b>Linear, not sRGB, and the numbers here bake that in.</b> Filter Effects 1 § 8.5 runs
///         the shorthand functions with <c>color-interpolation-filters: sRGB</c> and Vixen is linear
///         from the parser down — see <see cref="UiColorMatrix" />, which argues the trade. So a
///         <c>grayscale-100</c> here is a slightly different grey from a browser's, and the assertions
///         below are written as relations that hold in either space (channels equal, ordering
///         preserved, an inversion reaching zero) rather than as levels anyone would have to
///         recompute.
///     </para>
/// </remarks>
public class FilterColourTests {
    /// <summary>A coloured square on a black field, and nothing else.</summary>
    /// <remarks>
    ///     ⚠ <b>A saturated colour rather than the white square <c>FilterBlurTests</c> uses, and the
    ///     difference is the whole point.</b> A blur is visible on anything with an edge; a
    ///     <c>grayscale</c> over white is the identity, so a fixture borrowed from next door would
    ///     measure every one of these filters as working while doing nothing at all. The channels are
    ///     deliberately far apart and deliberately not in a symmetric arrangement — a
    ///     <c>hue-rotate</c> over a grey, or over an equal-parts colour, is also nearly nothing.
    /// </remarks>
    static UiTest Square(string filter, string colour = "#3366cc") {
        var ui = UiTest.Create(40f, 40f);
        ui.Document.Compositing = true;

        ui.Load(
            $$"""
            root { width: 40px; height: 40px; background-color: #000000; }
            .box { position: absolute; left: 10px; top: 10px; width: 20px; height: 20px;
                   background-color: {{colour}}; {{filter}} }
            """
        );

        ui.Create("div", null, "box", "box");
        ui.Frame();

        return ui;
    }

    /// <summary>The middle of the square, as three channels.</summary>
    /// <remarks>
    ///     ⚠ Read from the centre and not from an edge. Every one of these squares has an antialiased
    ///     border, and a partly covered pixel carries a fraction of the colour — which is a fine thing
    ///     to test and a terrible thing to accidentally test, because the fraction moves every
    ///     assertion by an amount that depends on the rasteriser rather than on the filter.
    /// </remarks>
    static (int R, int G, int B) Centre(UiTest ui) {
        var bitmap = ui.Capture();
        var offset = bitmap.Offset(20, 20);

        return (bitmap.Pixels[offset], bitmap.Pixels[offset + 1], bitmap.Pixels[offset + 2]);
    }

    /// <summary>A colour filter opens a group where an opacity of one would not.</summary>
    /// <remarks>
    ///     ⚠ <b>The same step <c>FilterBlurTests</c> checks for the blur, and it is not optional for
    ///     the same reason.</b> CSS transforms the group's <i>rendered result</i>, so a filter pushed
    ///     down onto each command's colour would be right on a bare panel and wrong the moment two of
    ///     the group's children overlap with partial coverage. The group has to be opened by the
    ///     filter alone, on an element that is fully opaque and would never otherwise have had one.
    /// </remarks>
    [Fact]
    public void A_colour_filter_opens_a_group_on_an_element_that_is_fully_opaque() {
        using var ui = Square("filter: grayscale(1);");

        var layer = Assert.Single(ui.Geometry.Layers);

        Assert.Equal(0f, layer.Blur);
        Assert.Equal(1f, layer.Alpha, 3);
        Assert.NotNull(layer.Filter);
    }

    /// <summary>And it costs no surface a blur would not, which is the design in one assertion.</summary>
    /// <remarks>
    ///     ⚠ <b>A group's bounds are <i>not</i> outset for a colour matrix, and a reader who assumed
    ///     the two filters were the same kind of thing would outset for both.</b> A blur moves coverage
    ///     to texels no vertex touched, so <c>UiGeometryBuilder.Layer</c> grows the ink by the kernel;
    ///     a matrix moves none. Growing it anyway would cost surface on pixels that are provably
    ///     transparent, and would do it silently. The two layers here are the same element with the
    ///     same box, so the bounds must be identical.
    /// </remarks>
    [Fact]
    public void A_colour_filter_does_not_outset_the_group_the_way_a_blur_does() {
        using var plain = Square("filter: none;");
        using var filtered = Square("filter: invert(1);");
        using var blurred = Square("filter: blur(2px);");

        // `filter: none` opens no group at all, so the reference is the element's own box.
        Assert.Empty(plain.Geometry.Layers);

        var matrix = Assert.Single(filtered.Geometry.Layers);
        var gaussian = Assert.Single(blurred.Geometry.Layers);

        Assert.Equal(new Rectangle(10f, 10f, 20f, 20f), matrix.Bounds);
        Assert.True(
            gaussian.Bounds.Width > matrix.Bounds.Width,
            $"the blur's bounds must be the wider pair: {gaussian.Bounds} against {matrix.Bounds}"
        );
    }

    /// <summary>A filtered group is not collapsed away, however few commands it drew.</summary>
    /// <remarks>
    ///     ⚠ <b>The same trap the blur walks into, and it catches a colour matrix harder.</b>
    ///     <c>DrawList.Collapse</c> throws the bracket away and multiplies the one command's alpha
    ///     instead — an exact identity for opacity and nonsense for a filter. And a <c>grayscale</c> on
    ///     a bare panel is precisely one background rectangle, so this is the common case rather than
    ///     a corner of one: it is the shape of every <c>grayscale</c> anybody writes on a disabled
    ///     thumbnail.
    /// </remarks>
    [Fact]
    public void A_filtered_group_survives_the_single_command_collapse() {
        using var ui = Square("filter: sepia(1);");

        Assert.Single(ui.Geometry.Layers);
        Assert.Contains(ui.Document.Drawing.Commands, command => command.Kind == DrawCommandKind.LayerPush);
    }

    /// <summary>Each of the seven reaches the frame and does what it says.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Read off the picture rather than off the matrix, because a matrix on a command
    ///         nothing executes is this repository's most-repeated defect</b> — a value that resolves,
    ///         cascades, and is never looked at. <see cref="UiTest.Capture" /> runs
    ///         <c>SoftwareUiRasterizer</c> over the geometry, which is one of the two executors, and
    ///         <c>UiCompositingTests</c> is what says the other one agrees with it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The relations are chosen to fail for the <i>neighbouring</i> function and not only
    ///         for no function at all.</b> A test that asserted "the pixel changed" would pass for all
    ///         seven with any one of them wired to all seven names. So <c>grayscale</c> must flatten,
    ///         <c>saturate</c> must widen, <c>brightness</c> must lift without flattening,
    ///         <c>invert</c> must cross over, <c>sepia</c> must end up red-dominant where the source is
    ///         blue-dominant, and <c>hue-rotate(180)</c> must move blue off the top slot without
    ///         flattening it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Grayscale_flattens_the_channels_and_saturate_pulls_them_apart() {
        using var plain = Square("filter: none;");
        using var grey = Square("filter: grayscale(1);");
        using var vivid = Square("filter: saturate(2);");

        var (r, g, b) = Centre(plain);
        Assert.True(b > r, $"the fixture must start blue-dominant: {r},{g},{b}");

        var flat = Centre(grey);
        Assert.InRange(Math.Abs(flat.R - flat.G), 0, 1);
        Assert.InRange(Math.Abs(flat.G - flat.B), 0, 1);

        // ⚠ Against the *plain* spread rather than against a number, so the assertion says what
        // saturation is — the distance from grey — rather than restating an arithmetic result.
        var wide = Centre(vivid);
        Assert.True(wide.B - wide.R > b - r, $"saturate must widen the spread: {wide} against {r},{g},{b}");
    }

    /// <summary>Brightness lifts every channel without touching their ratios.</summary>
    [Fact]
    public void Brightness_lifts_the_channels_and_leaves_the_hue_alone() {
        using var plain = Square("filter: none;");
        using var bright = Square("filter: brightness(1.5);");

        var (r, g, b) = Centre(plain);
        var lit = Centre(bright);

        Assert.True(lit.R > r && lit.G > g && lit.B > b, $"every channel lifts: {lit} against {r},{g},{b}");

        // Still blue-dominant, and still in the same order — a brightness that flattened would be a
        // grayscale wired to the wrong name.
        Assert.True(lit.B > lit.G && lit.G > lit.R, $"the ordering survives: {lit}");
    }

    /// <summary>A full inversion crosses every channel over the midpoint.</summary>
    /// <remarks>
    ///     ⚠ <b>The one function with a non-zero offset, and the offset is what a premultiplied
    ///     implementation gets wrong.</b> <c>c' = M·c + o·a</c> — an implementation that dropped the
    ///     <c>·a</c> would be exactly right here, on an opaque square, and wrong on every partly
    ///     covered pixel. That half is asserted where partial coverage exists: the glyph edges and
    ///     rounded corners of <c>UiCompositingTests</c>' fixture, which carries an <c>invert(0.25)</c>
    ///     for precisely this reason.
    /// </remarks>
    [Fact]
    public void A_full_inversion_takes_the_bright_channel_to_the_dark_one() {
        using var plain = Square("filter: none;");
        using var flipped = Square("filter: invert(1);");

        var (r, g, b) = Centre(plain);
        var inverted = Centre(flipped);

        Assert.InRange(inverted.R, 254 - r, 256 - r);
        Assert.InRange(inverted.G, 254 - g, 256 - g);
        Assert.InRange(inverted.B, 254 - b, 256 - b);
    }

    /// <summary>Sepia turns a blue square warm, and a hue rotation turns it somewhere else.</summary>
    [Fact]
    public void Sepia_warms_the_square_and_a_half_turn_moves_the_hue() {
        using var aged = Square("filter: sepia(1);");
        using var turned = Square("filter: hue-rotate(180deg);");

        var warm = Centre(aged);
        Assert.True(warm.R > warm.B, $"sepia is red-dominant whatever it was handed: {warm}");

        // ⚠ Not merely "different": a half turn has to take the *dominant* channel off the top, which
        // is what distinguishes a hue rotation from a saturation change that also moved the numbers.
        var rotated = Centre(turned);
        Assert.True(rotated.R > rotated.B, $"a half turn takes blue off the top slot: {rotated}");
        Assert.True(
            Math.Abs(rotated.R - rotated.G) > 1,
            $"and it is not a flattening in disguise: {rotated}"
        );
    }

    /// <summary>Contrast pushes away from the pivot in both directions at once.</summary>
    /// <remarks>
    ///     ⚠ <b>Two squares, one either side of the pivot, because a contrast that only pushed one way
    ///     is a brightness.</b> The pivot is a literal <c>0.5</c> in the working space — Filter Effects
    ///     1 § 8.5's <c>feComponentTransfer</c> intercept — so a dark square must get darker and a
    ///     bright one brighter under the same declaration, and that pair is the only assertion that
    ///     tells the two functions apart.
    /// </remarks>
    [Fact]
    public void Contrast_darkens_below_the_pivot_and_brightens_above_it() {
        using var darkPlain = Square("filter: none;", "#202020");
        using var darkPushed = Square("filter: contrast(2);", "#202020");
        using var lightPlain = Square("filter: none;", "#e0e0e0");
        using var lightPushed = Square("filter: contrast(2);", "#e0e0e0");

        Assert.True(
            Centre(darkPushed).R < Centre(darkPlain).R,
            $"below the pivot it darkens: {Centre(darkPushed)} against {Centre(darkPlain)}"
        );

        Assert.True(
            Centre(lightPushed).R > Centre(lightPlain).R,
            $"and above it, it brightens: {Centre(lightPushed)} against {Centre(lightPlain)}"
        );
    }

    /// <summary>Two functions in one list are applied in the order they were written.</summary>
    /// <remarks>
    ///     ⚠ <b>The pair that is not commutative, chosen for that.</b> <c>invert(1) brightness(0.5)</c>
    ///     inverts and then halves; <c>brightness(0.5) invert(1)</c> halves and then inverts, which is
    ///     a different picture — and an implementation that composed the matrices the other way round,
    ///     or that added the offsets instead of transforming the first by the second, would produce the
    ///     two answers swapped. <see cref="UiColorMatrix.Then" /> is named after the reading order for
    ///     exactly this reason.
    /// </remarks>
    [Fact]
    public void Two_functions_in_one_list_run_left_to_right() {
        using var first = Square("filter: invert(1) brightness(0.5);", "#ffffff");
        using var second = Square("filter: brightness(0.5) invert(1);", "#ffffff");

        // White inverted is black, and half of black is black.
        Assert.Equal(0, Centre(first).R);

        // Half of white is mid grey, and mid grey inverted is mid grey.
        Assert.InRange(Centre(second).R, 100, 155);
    }

    /// <summary>A blur and a colour matrix in one list are both honoured, in either order.</summary>
    /// <remarks>
    ///     ⚠ <b>Both orders give the same picture, and that is a fact about the arithmetic rather than
    ///     a shortcut.</b> A Gaussian is a weighted sum whose weights total one and a colour matrix on
    ///     premultiplied colour is affine, so <c>M(Σ wᵢ sᵢ) = Σ wᵢ M(sᵢ)</c> — the two commute exactly.
    ///     That is what lets <c>UiRenderer</c> blur in a pass and transform in the composite's fragment
    ///     stage while <c>SoftwareUiRasterizer</c> does both at the seam, and it is worth an assertion
    ///     because the day it stops being true the two executors part company and nothing else says
    ///     which one moved.
    /// </remarks>
    [Fact]
    public void A_blur_and_a_matrix_in_one_list_commute() {
        using var before = Square("filter: grayscale(1) blur(2px);");
        using var after = Square("filter: blur(2px) grayscale(1);");

        var one = Assert.Single(before.Geometry.Layers);
        var two = Assert.Single(after.Geometry.Layers);

        Assert.Equal(2f, one.Blur, 3);
        Assert.Equal(2f, two.Blur, 3);
        Assert.Equal(one.Filter, two.Filter);
        Assert.Equal(Centre(before), Centre(after));
    }

    /// <summary>A percentage and the number it stands for are the same filter.</summary>
    [Theory]
    [InlineData("filter: grayscale(50%);", "filter: grayscale(0.5);")]
    [InlineData("filter: brightness(150%);", "filter: brightness(1.5);")]
    public void A_percentage_is_the_number_over_a_hundred(string percentage, string number) {
        using var one = Square(percentage);
        using var two = Square(number);

        Assert.Equal(
            Assert.Single(one.Geometry.Layers).Filter,
            Assert.Single(two.Geometry.Layers).Filter
        );
    }

    /// <summary>A filter that composes to the identity opens no group and costs no surface.</summary>
    /// <remarks>
    ///     ⚠ <b>A deliberate departure from CSS, and the cost is what justifies it.</b> Filter Effects
    ///     1 § 5 makes any <c>filter</c> other than <c>none</c> a stacking context, so a browser
    ///     isolates for <c>brightness(1)</c>. A group here is a viewport-sized render target and a
    ///     pass, and the utility layer assembles all eight functions into every <c>filter</c> it emits
    ///     — so <c>blur-0</c> alone would otherwise buy a surface to convolve nothing and multiply by
    ///     one. Nothing else in the engine depends on the isolation.
    ///     <para>
    ///         ⚠ <c>invert(1) invert(1)</c> is the interesting row: it is the identity only after both
    ///         functions have been composed, so a reader that discarded an identity as it went would
    ///         throw the first one away and keep the second. See <c>DrawListBuilder.Settle</c>.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("filter: brightness(1);")]
    [InlineData("filter: grayscale(0) sepia(0);")]
    [InlineData("filter: invert(1) invert(1);")]
    public void A_filter_that_composes_to_the_identity_opens_no_group(string filter) {
        using var ui = Square(filter);

        Assert.Empty(ui.Geometry.Layers);
        Assert.DoesNotContain(ui.Document.Drawing.Commands, command => command.Kind == DrawCommandKind.LayerPush);
    }

    /// <summary>The four amounts CSS clamps are clamped, and the two it does not are not.</summary>
    /// <remarks>
    ///     ⚠ <c>grayscale(2)</c> is <c>grayscale(1)</c> per the spec, and <c>saturate(2)</c> is a real
    ///     over-saturation. Following the spec each way round rather than picking one rule for all six
    ///     is what stops <c>saturate-200</c> — a class Tailwind ships — from quietly meaning
    ///     <c>saturate-100</c>.
    /// </remarks>
    [Fact]
    public void The_amounts_CSS_clamps_are_clamped_and_the_ones_it_does_not_are_not() {
        using var over = Square("filter: grayscale(2);");
        using var full = Square("filter: grayscale(1);");
        using var vivid = Square("filter: saturate(2);");
        using var plain = Square("filter: saturate(1.0001);");

        Assert.Equal(
            Assert.Single(full.Geometry.Layers).Filter,
            Assert.Single(over.Geometry.Layers).Filter
        );

        Assert.NotEqual(
            Assert.Single(plain.Geometry.Layers).Filter,
            Assert.Single(vivid.Geometry.Layers).Filter
        );
    }
}
