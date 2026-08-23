// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Rendering;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary><c>mask-image</c>, from the stylesheet to the pixels.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>These are the tests <c>UiCompositingTests</c> cannot be, and the argument is
///         <c>FilterColourTests</c>' one word for word.</b> That file's job is that the device and the
///         software renderer draw the same frame, and both of them take the mask from the <i>same</i>
///         builder — so a <c>to right</c> that came out as a <c>to left</c> would be identically wrong
///         on both paths and the comparison would pass. What a mask <i>is</i> has to be asserted here,
///         against arithmetic, and only the agreement belongs over there.
///     </para>
///     <para>
///         ⚠ <b>And these are the tests the consumption gate cannot be.</b> That gate's verdict is
///         "the draw list changed", and any <c>mask-image</c> changes it by opening a group — the
///         bracket appears whatever the ramp says. It would pass on a mask no executor reads, on a
///         gradient that came out opaque, and on a mask pointing the wrong way. Its scene list is not
///         the thing to fix either, because the draw list is where the gate stops.
///     </para>
///     <para>
///         ⚠ <b>The relations are chosen to fail for the <i>neighbouring</i> case and not only for no
///         mask at all.</b> A test that asserted "the pixel changed" would pass with every direction
///         wired to every keyword. So <c>to right</c> must keep the left and lose the right,
///         <c>to left</c> must do the opposite on the same fixture, <c>to bottom</c> must move the ramp
///         onto the other axis, a stop must move the edge to where it was written, a hard pair of
///         stops must produce a step where a soft pair produces a slope, and a round mask must fade
///         outwards where a linear one fades sideways.
///     </para>
///     <para>
///         ⚠ <b>Every square here is drawn on black, which is what makes the premultiply assertion
///         possible at all.</b> A layer surface holds premultiplied colour, so a mask has to scale all
///         four channels. An implementation that scaled the alpha alone — the straight-alpha
///         convention, and the one mistake this feature was most likely to make — composites
///         `rgb + dst·(1−a·m)` over black and lands on the *unmasked* colour at full strength, at
///         every coverage. Against a black field that is not a subtle error: the square simply does
///         not fade. See <see cref="A_mask_dims_the_colour_and_not_only_the_coverage" />, which is the
///         assertion that would catch it, and note that a fixture on a white field would not.
///     </para>
/// </remarks>
public class MaskGradientTests {
    /// <summary>A blue square on a black field, masked by whatever is asked for.</summary>
    /// <remarks>
    ///     ⚠ The square is the whole of the element and runs from 10 to 30 on both axes, so the
    ///     coordinates below are the mask's own box and not the viewport's. A mask resolves against
    ///     the border box; a fixture where the two coincided would measure a mask that used the wrong
    ///     one as working.
    /// </remarks>
    static UiTest Square(string mask, string extra = "") {
        var ui = UiTest.Create(40f, 40f);
        ui.Document.Compositing = true;

        ui.Load(
            $$"""
            root { width: 40px; height: 40px; background-color: #000000; }
            .box { position: absolute; left: 10px; top: 10px; width: 20px; height: 20px;
                   background-color: #3366cc; {{mask}} {{extra}} }
            """
        );

        ui.Create("div", null, "box", "box");
        ui.Frame();

        return ui;
    }

    /// <summary>One pixel of the rendered frame, as three channels.</summary>
    static (int R, int G, int B) At(UiTest ui, int x, int y) {
        var bitmap = ui.Capture();
        var offset = bitmap.Offset(x, y);

        return (bitmap.Pixels[offset], bitmap.Pixels[offset + 1], bitmap.Pixels[offset + 2]);
    }

    /// <summary>The blue channel, which is the square's dominant one and so its best coverage proxy.</summary>
    static int Blue(UiTest ui, int x, int y) => At(ui, x, y).B;

    /// <summary>The square's blue channel with no mask on it, which every reading below is a fraction of.</summary>
    /// <remarks>
    ///     ⚠ <b>154 and not 204, because the renderer is linear all the way to the buffer.</b>
    ///     <c>#3366cc</c>'s blue is <c>0xcc</c> — 204 as sRGB — and the draw list carries it as linear
    ///     0.6038, which <c>SoftwareUiRasterizer</c> stores without an sRGB encode because the target
    ///     format is <c>Rgba8UNorm</c>. Writing 204 into the assertions below would have made every one
    ///     of them fail against a correct mask, which is how this constant came to be spelled out
    ///     rather than assumed. Measured rather than hard-coded so that a change to the fixture's
    ///     colour moves the expectations with it.
    /// </remarks>
    static int Unmasked() {
        using var bare = Square("");

        return Blue(bare, 20, 20);
    }

    /// <summary>The columns of the square, and the two facts about them that the readings depend on.</summary>
    /// <remarks>
    ///     ⚠ <b>The square spans <i>x</i> 10 to 30, so its pixels are columns 10 to 29 and its centre
    ///     is 20.0 — which means column 11 mirrors column 28 and <i>not</i> column 29.</b> A pair
    ///     picked as "one in from each end" is off by half a pixel each way, and on a ramp across
    ///     twenty pixels that is a five percent difference: enough to fail an exact-mirror assertion
    ///     while the mask is perfectly correct. This tripped the first draft of this file.
    /// </remarks>
    const int Near = 11;

    /// <inheritdoc cref="Near" />
    const int Far = 28;

    /// <summary>A mask opens a group where an opacity of one would not.</summary>
    /// <remarks>
    ///     ⚠ <b>The same step <c>FilterColourTests</c> checks for the matrix, and it is not optional
    ///     for the same reason.</b> CSS masks the group's <i>rendered result</i>, so a mask pushed down
    ///     onto each command's alpha would be right on a bare panel and wrong the moment two of the
    ///     group's children overlap with partial coverage. The group has to be opened by the mask
    ///     alone, on an element that is fully opaque and would never otherwise have had one.
    /// </remarks>
    [Fact]
    public void A_mask_opens_a_group_on_an_element_that_is_fully_opaque() {
        using var ui = Square("mask-image: linear-gradient(to right, #000000, transparent);");

        var layer = Assert.Single(ui.Geometry.Layers);

        Assert.Equal(0f, layer.Blur);
        Assert.Equal(1f, layer.Alpha, 3);
        Assert.NotNull(layer.Mask);
        Assert.Null(layer.Filter);
    }

    /// <summary>A mask does not outset the group the way a blur does.</summary>
    /// <remarks>
    ///     A mask only ever removes coverage, so the ink it can reach is the ink that was already
    ///     there. Growing the bounds "to be safe" would spend surface on pixels that are provably
    ///     transparent, and shrinking them would be deciding the group's extent from a coverage the
    ///     composite has not applied yet.
    /// </remarks>
    [Fact]
    public void A_mask_does_not_move_the_bounds_the_way_a_blur_does() {
        using var masked = Square("mask-image: linear-gradient(to right, #000000, transparent);");
        using var blurred = Square("mask-image: linear-gradient(to right, #000000, transparent);", "filter: blur(4px);");

        var plain = Assert.Single(masked.Geometry.Layers);
        var gaussian = Assert.Single(blurred.Geometry.Layers);

        Assert.Equal(new Rectangle(10f, 10f, 20f, 20f), plain.Bounds);
        Assert.True(
            gaussian.Bounds.Width > plain.Bounds.Width,
            $"a blur beside the mask must still outset: {gaussian.Bounds} against {plain.Bounds}"
        );
    }

    /// <summary>The mask box is the element's border box and does not move when a blur is added.</summary>
    /// <remarks>
    ///     ⚠ <b>The trap this exists for is reading <see cref="UiLayer.Bounds" /> for the mask's
    ///     box.</b> Those bounds are the ink and a blur has already outset them, so a mask resolved
    ///     against them would run its ramp across a wider rectangle the moment a blur appeared beside
    ///     it — the gradient would slide sideways, which reads as the blur being mis-centred rather
    ///     than as the mask being wrong.
    /// </remarks>
    [Fact]
    public void The_mask_box_is_the_border_box_and_a_blur_beside_it_does_not_move_it() {
        using var plain = Square("mask-image: linear-gradient(to right, #000000, transparent);");
        using var blurred = Square("mask-image: linear-gradient(to right, #000000, transparent);", "filter: blur(4px);");

        var one = Assert.Single(plain.Geometry.Layers).Mask;
        var two = Assert.Single(blurred.Geometry.Layers).Mask;

        Assert.NotNull(one);
        Assert.NotNull(two);
        Assert.Equal(one!.Value.Centre, two!.Value.Centre);
        Assert.Equal(one.Value.Half, two.Value.Half);
    }

    /// <summary>A mask that is opaque everywhere opens no group.</summary>
    /// <remarks>
    ///     The identity has to be dropped where the group is decided, not in an executor: the group is
    ///     the expensive half — a viewport-sized surface, a pass and a composite — and spending it on
    ///     a mask that changes no pixel is the cost this check exists to refuse.
    /// </remarks>
    [Theory]
    [InlineData("linear-gradient(to right, #000000, #000000)")]
    [InlineData("linear-gradient(to bottom, #ff0000, #00ff00)")]
    public void A_mask_that_is_opaque_everywhere_opens_no_group(string image) {
        using var ui = Square($"mask-image: {image};");

        Assert.Empty(ui.Geometry.Layers);
    }

    /// <summary>A mask the engine cannot paint masks nothing rather than everything.</summary>
    /// <remarks>
    ///     ⚠ <b>Failing open, and it is the opposite of what <c>background-image</c> does with the
    ///     same refusal.</b> An unpaintable background is left out and the element keeps its colour; an
    ///     unresolvable mask left to fail closed would erase the element, and a blank rectangle is
    ///     indistinguishable from a layout collapse. Masking 1 § 4.1 agrees.
    /// </remarks>
    [Theory]
    [InlineData("none")]
    [InlineData("url(nothing.png)")]
    [InlineData("repeating-linear-gradient(to right, #000000, transparent)")]
    public void A_mask_the_engine_cannot_paint_leaves_the_element_alone(string image) {
        using var refused = Square($"mask-image: {image};");
        using var bare = Square("");

        Assert.Empty(refused.Geometry.Layers);
        Assert.Equal(Blue(bare, 20, 20), Blue(refused, 20, 20));
    }

    /// <summary>A rightward ramp keeps the left of the square and loses the right.</summary>
    /// <remarks>
    ///     ⚠ <b>Both ends asserted and the middle between them, because each catches a different
    ///     wrong answer.</b> Only checking that the right end faded would pass for a mask that faded
    ///     everything; only checking the left would pass for no mask at all; and a monotone middle is
    ///     what separates a ramp from a step. The columns are 11 and 29 rather than 10 and 30 — one
    ///     pixel inside the square, so the square's own antialiased border is not what is being
    ///     measured.
    /// </remarks>
    [Fact]
    public void A_rightward_ramp_keeps_the_left_edge_and_loses_the_right() {
        using var ui = Square("mask-image: linear-gradient(to right, #000000, transparent);");

        var full = Unmasked();
        var left = Blue(ui, 11, 20);
        var middle = Blue(ui, 20, 20);
        var right = Blue(ui, 29, 20);

        // Column 11 is 7.5% along a ramp that spans the box, so the near end keeps 92.5% of it and
        // column 29 keeps 2.5%. The bounds are loose around those two numbers rather than tight
        // around zero and one, because a mask that ran edge to edge of the *viewport* would also
        // give a high reading here and a low one there — the middle is what rules that out.
        Assert.True(left > full * 0.85, $"the near end keeps its coverage: {left} of {full}");
        Assert.True(right < full * 0.10, $"the far end loses it: {right} of {full}");
        Assert.True(left > middle && middle > right, $"and the ramp is monotone across: {left}, {middle}, {right}");
    }

    /// <summary>A leftward ramp is the rightward one reflected, on the same fixture.</summary>
    /// <remarks>
    ///     ⚠ <b>The assertion that a sign error cannot survive.</b> An axis that points at the near
    ///     stop instead of the far one paints every gradient in the interface backwards — a mistake
    ///     that looks like a design choice and survives review — and every one-ended assertion above
    ///     would pass with it in place. Two directions on one fixture cannot both be satisfied by one
    ///     sign.
    /// </remarks>
    [Fact]
    public void A_leftward_ramp_is_the_rightward_one_reflected() {
        using var rightward = Square("mask-image: linear-gradient(to right, #000000, transparent);");
        using var leftward = Square("mask-image: linear-gradient(to left, #000000, transparent);");

        Assert.True(
            Blue(rightward, Near, 20) > Blue(rightward, Far, 20),
            "to right keeps the left"
        );

        Assert.True(
            Blue(leftward, Far, 20) > Blue(leftward, Near, 20),
            "to left keeps the right"
        );

        // The two are the same ramp mirrored, so the pair of readings swap rather than merely differ.
        // This is what needs `Near` and `Far` to be a true mirror pair about the box centre.
        Assert.InRange(Blue(leftward, Far, 20) - Blue(rightward, Near, 20), -2, 2);
        Assert.InRange(Blue(leftward, Near, 20) - Blue(rightward, Far, 20), -2, 2);
    }

    /// <summary>A downward ramp runs down the square and not across it.</summary>
    /// <remarks>
    ///     ⚠ <b>The assertion an axis that ignores the angle cannot pass.</b> A mask hard-wired to one
    ///     direction satisfies every test above; what it cannot do is leave a row flat while a column
    ///     falls.
    /// </remarks>
    [Fact]
    public void A_downward_ramp_runs_down_the_square_and_not_across_it() {
        using var ui = Square("mask-image: linear-gradient(to bottom, #000000, transparent);");

        var full = Unmasked();
        var top = Blue(ui, 20, 11);
        var bottom = Blue(ui, 20, 29);

        Assert.True(top > full * 0.85, $"the top keeps its coverage: {top} of {full}");
        Assert.True(bottom < full * 0.10, $"the bottom loses it: {bottom} of {full}");

        // And the row through the middle is flat, which is what "not across it" means.
        Assert.InRange(Blue(ui, Near, 20) - Blue(ui, Far, 20), -2, 2);
    }

    /// <summary>A stop written at a position puts the edge at that position.</summary>
    /// <remarks>
    ///     ⚠ <b>A pair of stops at the same place is a step, and a step is what pins the position.</b>
    ///     A soft ramp can be off by a third of the box and still look like a gradient; a hard edge
    ///     cannot be off by a pixel without the assertion seeing it. So this measures where the edge
    ///     landed rather than what shape the slope had — which is the one thing
    ///     <see cref="A_rightward_ramp_keeps_the_left_edge_and_loses_the_right" /> does not establish.
    /// </remarks>
    [Fact]
    public void A_pair_of_stops_at_one_position_is_a_step_where_it_was_written() {
        using var ui = Square("mask-image: linear-gradient(to right, #000000 50%, transparent 50%);");

        // The square spans 10..30, so half way is column 20. Either side of it the step is total,
        // which is the point: a soft ramp reading 55% and 45% here would pass a "left is brighter"
        // assertion and fail these two.
        var full = Unmasked();

        Assert.True(Blue(ui, 18, 20) > full - 2, $"opaque up to the stop: {Blue(ui, 18, 20)} of {full}");
        Assert.True(Blue(ui, 22, 20) < 2, $"and gone past it: {Blue(ui, 22, 20)}");
    }

    /// <summary>Moving a stop moves the ramp, and the direction it moves is the one written.</summary>
    [Fact]
    public void Moving_a_stop_moves_the_ramp() {
        using var early = Square("mask-image: linear-gradient(to right, #000000 0%, transparent 25%);");
        using var late = Square("mask-image: linear-gradient(to right, #000000 0%, transparent 100%);");

        var column = 20;

        Assert.True(
            Blue(late, column, 20) > Blue(early, column, 20),
            $"a ramp that finishes later is still going at the middle: {Blue(late, column, 20)} against {Blue(early, column, 20)}"
        );

        Assert.True(Blue(early, column, 20) < 20, $"the early ramp is over by the middle: {Blue(early, column, 20)}");
    }

    /// <summary>A mask dims the colour and not only the coverage.</summary>
    /// <remarks>
    ///     ⚠ <b>The premultiply assertion, and the reason every fixture in this file is on black.</b>
    ///     A layer surface holds premultiplied colour, so masking is a scale on all four channels. An
    ///     implementation that scaled the alpha alone — <c>(rgb, a·m)</c>, which is what an ordinary
    ///     straight-alpha image wants and what <c>ui-image.frag</c>'s <c>varying_shape.x</c> exists to
    ///     distinguish — leaves <c>rgb</c> at full strength, and the premultiplied blend
    ///     <c>dst = src + dst·(1−a)</c> over a black field then lands on the unmasked colour at every
    ///     coverage. So the square would not fade at all, and it would be the *left* end of the ramp
    ///     that agreed with the truth. Half-way along the ramp the two answers are as far apart as
    ///     they get.
    /// </remarks>
    [Fact]
    public void A_mask_dims_the_colour_and_not_only_the_coverage() {
        using var unmasked = Square("");
        using var masked = Square("mask-image: linear-gradient(to right, #000000, transparent);");

        var full = Blue(unmasked, 20, 20);
        var half = Blue(masked, 20, 20);

        Assert.True(full > 100, $"the fixture must start well above the noise floor: {full}");

        // Roughly half of it, and emphatically not all of it. The bound that matters is the upper
        // one: an alpha-only mask returns `full` exactly.
        Assert.True(half < full - 40, $"a mask at half coverage must dim the colour: {half} against {full}");
        Assert.InRange(half, (full / 2) - 20, (full / 2) + 20);
    }

    /// <summary>A round mask fades outwards where a linear one fades sideways.</summary>
    /// <remarks>
    ///     ⚠ <b>Four points at one radius, which is the shape no linear ramp can produce.</b> A radial
    ///     mask mistakenly evaluated as a linear one would still fade, and would still differ from no
    ///     mask; what it cannot do is treat left and right alike while treating the centre
    ///     differently from both.
    /// </remarks>
    [Fact]
    public void A_round_mask_fades_outwards_rather_than_sideways() {
        using var ui = Square("mask-image: radial-gradient(#000000, transparent);");

        // 12 and 27 are a mirror pair about the box centre at 20.0, for the reason `Near` gives.
        var centre = Blue(ui, 20, 20);
        var left = Blue(ui, 12, 20);
        var right = Blue(ui, 27, 20);
        var up = Blue(ui, 20, 12);

        Assert.True(centre > left, $"the centre keeps more than the edge: {centre} against {left}");
        Assert.InRange(left - right, -2, 2);
        Assert.InRange(left - up, -3, 3);
    }

    /// <summary>A conic mask sweeps around the centre.</summary>
    /// <remarks>
    ///     ⚠ <b>Two points at the same radius and different angles, which is what separates a sweep
    ///     from a ring.</b> A conic mask evaluated as a radial one would give these two the same
    ///     answer — and the radial test one method up would still pass, so the pair is what pins
    ///     which is which.
    /// </remarks>
    [Fact]
    public void A_conic_mask_sweeps_around_the_centre() {
        using var ui = Square("mask-image: conic-gradient(#000000, transparent);");

        // Twelve o'clock is the start of the sweep and nine o'clock is three quarters round it, so
        // the second has lost far more coverage than the first.
        var top = Blue(ui, 20, 12);
        var left = Blue(ui, 12, 20);

        Assert.True(top > left, $"the sweep starts at the top and has run down by nine o'clock: {top} against {left}");
    }

    /// <summary>A mask and a colour filter on one element apply both.</summary>
    /// <remarks>
    ///     ⚠ <b>The combination is where a two-pipeline design fails silently.</b> The device picks
    ///     one pipeline per composite draw, so a mask and a matrix on one group must be served by a
    ///     shader that does both — and the failure mode of getting that wrong is not a crash but one
    ///     of the two being dropped. The assertion is therefore that <i>each</i> is still visible in
    ///     the presence of the other: the channels flatten as <c>grayscale</c> demands, and the ramp
    ///     still falls from left to right.
    /// </remarks>
    [Fact]
    public void A_mask_and_a_colour_filter_on_one_element_both_apply() {
        using var ui = Square(
            "mask-image: linear-gradient(to right, #000000, transparent);",
            "filter: grayscale(1);"
        );

        var layer = Assert.Single(ui.Geometry.Layers);

        Assert.NotNull(layer.Mask);
        Assert.NotNull(layer.Filter);

        var near = At(ui, 11, 20);
        var far = At(ui, 29, 20);

        Assert.InRange(Math.Abs(near.R - near.G), 0, 2);
        Assert.InRange(Math.Abs(near.G - near.B), 0, 2);
        Assert.True(near.B > far.B, $"and the ramp survives the matrix: {near.B} against {far.B}");
    }

    /// <summary>The declaration the <c>mask-*</c> utilities generate reaches the pixels.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The assembled text, <c>var()</c> fallbacks and all, rather than the tidy CSS the
    ///         rest of this file writes — and it is a different claim from either of the two tests
    ///         that surround it.</b> <c>UtilityFamilyTests</c> proves <c>mask-linear-from-50%</c>
    ///         emits this string; the consumption gate proves the string moves the draw list. Neither
    ///         proves it produces the <i>right</i> ramp, because a mask whose stops came out swapped
    ///         opens exactly the same group and emits exactly the same text.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The fallbacks are the part that has to work.</b> Only one fragment is set here —
    ///         <c>--tw-mask-from-position</c>, as one class would set — so the colours and the angle
    ///         all come from the <c>var()</c> defaults. That is the arrangement that makes a single
    ///         utility work on its own, and it is also the one where a wrong initial value is
    ///         invisible: <c>--tw-mask-from</c> defaulting to <c>transparent</c> the way the
    ///         *gradient* fragments do would erase the element, which is what the reversed default in
    ///         <c>UtilityComposition</c> exists to prevent and what this notices.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_declaration_the_utilities_generate_fades_the_element_downwards() {
        const string Generated =
            "--tw-mask-from-position: 50%; "
            + "mask-image: linear-gradient(var(--tw-mask-linear-angle, 180deg), "
            + "var(--tw-mask-from, black) var(--tw-mask-from-position, 0%), "
            + "var(--tw-mask-to, transparent) var(--tw-mask-to-position, 100%));";

        using var ui = Square(Generated);
        var full = Unmasked();

        Assert.NotNull(Assert.Single(ui.Geometry.Layers).Mask);

        // 180deg is `to bottom`, and the first stop sits at half way — so the top half is untouched
        // and the bottom fades to nothing.
        Assert.True(Blue(ui, 20, 13) > full - 2, $"opaque above the first stop: {Blue(ui, 20, 13)} of {full}");
        Assert.True(Blue(ui, 20, 29) < full * 0.10, $"and gone by the bottom: {Blue(ui, 20, 29)} of {full}");
        Assert.True(Blue(ui, 20, 22) > Blue(ui, 20, 26), "with a ramp between them");
    }

    /// <summary>A masked group survives the single-command collapse.</summary>
    /// <remarks>
    ///     The peephole throws the bracket away and multiplies the one command's alpha instead —
    ///     right when the surface's only job was a fade, and wrong here for the filter's reason: a
    ///     masked rectangle is not a fainter rectangle. A bare panel with one background rectangle
    ///     under a mask is exactly the shape the collapse catches.
    /// </remarks>
    [Fact]
    public void A_masked_group_survives_the_single_command_collapse() {
        using var ui = Square("mask-image: linear-gradient(to right, #000000, transparent);");

        Assert.Single(ui.Geometry.Layers);
        Assert.Contains(ui.Document.Drawing.Commands, command => command.Kind == DrawCommandKind.LayerPush);
    }
}
