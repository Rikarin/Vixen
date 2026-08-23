// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Rendering;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary><c>filter: blur()</c>, from the stylesheet to the pixels.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>These are the tests <c>UiCompositingTests</c> cannot be.</b> That file's job is that
///         the device and the software renderer draw the same frame, and both of them take the group's
///         bounds from the <i>same</i> builder — so a bounds outset that was wrong, or missing
///         entirely, would clip the halo identically on both paths and the comparison would pass. It
///         was checked by breaking it: with the outset deleted the two renderers still agreed to the
///         pixel. Everything about the shape of a blur therefore has to be asserted here, against
///         arithmetic, and only the <i>agreement</i> belongs over there.
///     </para>
///     <para>
///         ⚠ <b>Three sigma, and it comes from <see cref="UiLayer.KernelRadius" /> rather than from a
///         number written here.</b> The point of the constant is that the builder, the shader and the
///         software rasteriser all take the kernel from one place; a test that wrote <c>12</c> where
///         the code says <c>KernelRadius(4, 1)</c> would keep passing after somebody changed the rule
///         in one of the three.
///     </para>
/// </remarks>
public class FilterBlurTests {
    static UiTest Compositing(float width, float height) {
        var ui = UiTest.Create(width, height);
        ui.Document.Compositing = true;

        return ui;
    }

    /// <summary>A blurred square on a black field, and nothing else.</summary>
    /// <remarks>
    ///     ⚠ Forty pixels across rather than twenty, so that the centre is five sigma from every edge
    ///     at the radius these tests use. At twenty it is two and a half, the kernel reaches the far
    ///     side, and "the middle is untouched" stops being true — the measurement comes out at 249
    ///     rather than 255, which is the Gaussian being right and the assertion being wrong.
    /// </remarks>
    static UiTest Square(string filter, float side = 80f) {
        var ui = Compositing(side, side);

        ui.Load(
            $$"""
            root { width: {{side}}px; height: {{side}}px; background-color: #000000; }
            .box { position: absolute; left: 20px; top: 20px; width: 40px; height: 40px;
                   background-color: #ffffff; {{filter}} }
            """
        );

        ui.Create("div", null, "box", "box");
        ui.Frame();

        return ui;
    }

    /// <summary>A blur opens a group where an opacity of one would not.</summary>
    /// <remarks>
    ///     ⚠ <b>The whole of what makes the property reachable, and the one step that is not
    ///     optional.</b> An opacity can be approximated by fading each element and a blur cannot: with
    ///     no surface there is nothing to convolve. So the group has to be opened by the filter alone,
    ///     on an element that is fully opaque and would otherwise never have had one.
    /// </remarks>
    [Fact]
    public void A_blur_opens_a_group_on_an_element_that_is_fully_opaque() {
        using var ui = Square("filter: blur(4px);");

        var layer = Assert.Single(ui.Geometry.Layers);

        Assert.Equal(4f, layer.Blur, 3);
        Assert.Equal(1f, layer.Alpha, 3);
    }

    /// <summary>A blurred group is not collapsed away, however few commands it drew.</summary>
    /// <remarks>
    ///     ⚠ <b>The trap this property walks straight into.</b> <c>DrawList.Collapse</c> throws the
    ///     bracket away and multiplies the one command's alpha instead, which is an exact identity for
    ///     opacity and nonsense for a filter — a blurred rectangle is not a fainter rectangle. And a
    ///     <c>blur-*</c> on a bare panel is precisely one background rectangle, so this is the common
    ///     case rather than a corner of one:
    ///     <c>GroupOpacityTests.A_single_command_group_is_collapsed_rather_than_composited</c> is the
    ///     same fixture with the filter taken off, and it asserts the opposite.
    /// </remarks>
    [Fact]
    public void A_blurred_group_survives_the_single_command_collapse() {
        using var ui = Square("filter: blur(3px);");

        Assert.Contains(ui.Document.Drawing.Commands, command => command.Kind == DrawCommandKind.LayerPush);
        Assert.NotEmpty(ui.Geometry.Layers);
    }

    /// <summary>The group's bounds are grown by the kernel, so the halo has somewhere to land.</summary>
    /// <remarks>
    ///     ⚠ <b>Asserted on the geometry rather than on pixels, because the failure is invisible in a
    ///     comparison.</b> Both executors composite through <see cref="UiLayer.Bounds" />, so a bound
    ///     that is too small cuts the halo off on both of them in the same place — the picture is a
    ///     soft edge with a hard line across it, and every renderer agrees about it.
    /// </remarks>
    [Fact]
    public void The_groups_bounds_are_outset_by_the_kernel() {
        using var wide = Square("filter: blur(4px);");
        using var narrow = Square("filter: blur(1px);");

        // ⚠ Two blurs differenced rather than a blur against an unblurred group, because there is no
        // unblurred group to compare with: the same fixture with only an `opacity` on it is one
        // command and gets collapsed away — which is what the test next door asserts.
        var difference = UiLayer.KernelRadius(4f, 1f) - UiLayer.KernelRadius(1f, 1f);
        var grown = Assert.Single(wide.Geometry.Layers).Bounds;
        var plain = Assert.Single(narrow.Geometry.Layers).Bounds;

        Assert.Equal(plain.X - difference, grown.X, 3);
        Assert.Equal(plain.Y - difference, grown.Y, 3);
        Assert.Equal(plain.Width + (2 * difference), grown.Width, 3);
        Assert.Equal(plain.Height + (2 * difference), grown.Height, 3);
    }

    /// <summary>Ink lands outside the square the element drew, and fades with distance.</summary>
    /// <remarks>
    ///     ⚠ <b>The measurement that says a blur happened rather than that a surface was
    ///     allocated.</b> A group that was composited and never convolved passes every assertion above
    ///     it. What only a blur produces is coverage where the element painted none, monotonically
    ///     decreasing away from the edge — so this reads three pixels marching out of the box and
    ///     requires each to be dimmer than the last and all of them to be lit.
    /// </remarks>
    [Fact]
    public void A_blur_puts_light_outside_the_box_and_fades_it_with_distance() {
        using var ui = Square("filter: blur(4px);");

        var bitmap = ui.Capture();

        // The box is 20..60 on both axes; these march left out of its edge along its middle row.
        int just = bitmap.Pixels[bitmap.Offset(18, 40)];
        int further = bitmap.Pixels[bitmap.Offset(14, 40)];
        int furthest = bitmap.Pixels[bitmap.Offset(10, 40)];

        Assert.True(just > 0, "nothing was drawn outside the box, so the blur did not happen");
        Assert.True(just > further, $"the halo did not fall off: {just} then {further}");
        Assert.True(further > furthest, $"the halo did not fall off: {further} then {furthest}");
    }

    /// <summary>The edge that was hard becomes a ramp, and the middle stays lit.</summary>
    /// <remarks>
    ///     ⚠ A blur that only spread outwards would pass the test above and be wrong in the other
    ///     direction — the inside of the edge has to give up light in the same amount. The centre of a
    ///     twenty-pixel box is more than three sigma from every edge, so it must stay at full
    ///     strength: a kernel that was not normalised would dim it, which is the failure mode that
    ///     reads as an opacity bug rather than as a blur one. Five sigma, not three, because the
    ///     margin between "untouched" and "very nearly untouched" is one level of 255 and this has to
    ///     be able to tell them apart.
    /// </remarks>
    [Fact]
    public void The_edge_becomes_a_ramp_while_the_centre_stays_at_full_strength() {
        using var ui = Square("filter: blur(4px);");

        var bitmap = ui.Capture();

        int inside = bitmap.Pixels[bitmap.Offset(22, 40)];
        int centre = bitmap.Pixels[bitmap.Offset(40, 40)];

        Assert.InRange(inside, 1, 250);
        Assert.InRange(centre, 254, 255);
    }

    /// <summary>A <c>filter</c> carrying a function nothing implements is refused whole.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Refused whole rather than applied in part, which is <c>EmitShadow</c>'s rule.</b>
    ///         A <c>filter</c> carrying a function this engine does not implement would, if the blur in
    ///         it were honoured alone, draw a blurred element that is missing something — and look
    ///         like a bug in the blur. Drawing it unfiltered is the honest answer.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Four of these rows have moved out over two changes, and the theory staying put
    ///         while its rows empty is the point of it.</b> <c>brightness(0.5)</c> and
    ///         <c>blur(4px) brightness(0.5)</c> left when the seven colour functions landed — see
    ///         <c>FilterColourTests</c> — and the two <c>drop-shadow</c> rows left when the shadow did,
    ///         to <c>FilterDropShadowTests</c>. What it asserts is the <i>rule</i>, and the rule
    ///         outlives any particular function being absent. What is left of the set that genuinely
    ///         is not read is <c>opacity()</c>, which is a filter-list spelling of a thing the group
    ///         already carries, and <c>url()</c>, which would want an SVG filter graph.
    ///     </para>
    ///     <para>
    ///         ⚠ The last two rows are arguments rather than functions, and they are here because a
    ///         reader that clamped instead of refusing would pass every other row. <c>brightness(-1)</c>
    ///         is invalid CSS and <c>hue-rotate(90)</c> is a bare number where an angle is required;
    ///         both are the kind of thing a stylesheet gets wrong once, and both must take the whole
    ///         declaration with them rather than quietly becoming zero.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("filter: none;")]
    [InlineData("filter: blur(0px);")]
    [InlineData("filter: opacity(0.5);")]
    [InlineData("filter: blur(4px) opacity(0.5);")]
    [InlineData("filter: brightness(0.5) url(#thing);")]
    [InlineData("filter: blur(nonsense);")]
    [InlineData("filter: brightness(-1);")]
    [InlineData("filter: hue-rotate(90);")]
    public void Anything_carrying_a_function_the_engine_cannot_run_leaves_the_element_unfiltered(string filter) {
        using var ui = Square(filter);

        Assert.Empty(ui.Geometry.Layers);
        Assert.DoesNotContain(ui.Document.Drawing.Commands, command => command.Kind == DrawCommandKind.LayerPush);
    }

    /// <summary>Relative units resolve against the element's own font size, as every other length does.</summary>
    [Fact]
    public void The_radius_is_a_length_and_not_a_number() {
        using var ui = Compositing(60f, 60f);

        ui.Load(
            """
            root { width: 60px; height: 60px; background-color: #000000; }
            .box { position: absolute; left: 20px; top: 20px; width: 20px; height: 20px;
                   font-size: 8px; background-color: #ffffff; filter: blur(0.5em); }
            """
        );

        ui.Create("div", null, "box", "box");
        ui.Frame();

        Assert.Equal(4f, Assert.Single(ui.Geometry.Layers).Blur, 3);
    }
}
