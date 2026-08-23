// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Rendering;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary><c>filter: drop-shadow()</c>, from the stylesheet to the pixels.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>These are the tests <c>UiCompositingTests</c> cannot be, and the argument is
///         <c>FilterBlurTests</c>' and <c>FilterColourTests</c>' word for word.</b> Both executors
///         take the shadow's offset, its kernel and its bounds from the <i>same</i> builder, so a
///         displacement with the wrong sign, a silhouette taken from the wrong surface or a bound
///         that clipped the halo would be identically wrong on both paths and the comparison over
///         there would pass. What a drop shadow <i>is</i> has to be asserted here, against pixels;
///         only the agreement belongs next door.
///     </para>
///     <para>
///         ⚠ <b>And these are the tests a "did the draw list change" gate cannot be, twice over.</b>
///         Any <c>filter</c> opens a group, so the <c>LayerPush</c> bracket appears whatever the
///         declaration said — that is the hole the colour functions' gate fell into. A drop shadow
///         adds a second: it emits a <i>second quad</i>, so even "the geometry grew a draw" is
///         satisfied by a shadow drawn in the wrong place, in the wrong colour, over the element
///         instead of under it, or as an unfiltered copy of the element itself. Every assertion below
///         is chosen to fail for one of those four.
///     </para>
///     <para>
///         ⚠ <b>The fixture's element is red and its shadow is black, deliberately.</b> The cheapest
///         wrong implementation — and the one the machinery makes easy, because a shadow surface is a
///         blurred copy of the group — is compositing that copy untinted. A white-on-black fixture
///         cannot tell a black shadow from a missing one, and a shadow the same colour as the element
///         cannot tell a tinted silhouette from an untinted copy. Two different hues is what makes
///         both of those a failure.
///     </para>
/// </remarks>
public class FilterDropShadowTests {
    const string Element = "#ff0000";

    /// <summary>A red square on a white field, with whatever filter is asked for.</summary>
    /// <remarks>
    ///     ⚠ <b>A white field rather than the black one next door uses, because black is the shadow's
    ///     own colour.</b> On black, a shadow that landed in the right place and one that was never
    ///     drawn are the same picture — which is the fixture mistake this whole file is written
    ///     against. Eighty pixels across so that a shadow displaced by eight and blurred by three
    ///     still lands inside the viewport with room to fall off.
    /// </remarks>
    static UiTest Square(string filter, float side = 80f) {
        var ui = UiTest.Create(side, side);
        ui.Document.Compositing = true;

        ui.Load(
            $$"""
            root { width: {{side}}px; height: {{side}}px; background-color: #ffffff; }
            .box { position: absolute; left: 20px; top: 20px; width: 40px; height: 40px;
                   background-color: {{Element}}; {{filter}} }
            """
        );

        ui.Create("div", null, "box", "box");
        ui.Frame();

        return ui;
    }

    /// <summary>One pixel, as three channels.</summary>
    static (int R, int G, int B) At(UiTest ui, int x, int y) {
        var bitmap = ui.Capture();
        var offset = bitmap.Offset(x, y);

        return (bitmap.Pixels[offset], bitmap.Pixels[offset + 1], bitmap.Pixels[offset + 2]);
    }

    /// <summary>A drop shadow opens a group on an element that is fully opaque.</summary>
    /// <remarks>
    ///     ⚠ <b>The step that makes the property reachable at all, and it is not optional for the
    ///     blur's reason rather than the matrix's.</b> A shadow is a function of the rasterised
    ///     subtree's <i>alpha</i>, so with no surface there is nothing to take a silhouette of. The
    ///     group has to be opened by the shadow alone, on an element whose own opacity is one and
    ///     which would otherwise never have had one.
    /// </remarks>
    [Fact]
    public void A_drop_shadow_opens_a_group_on_an_element_that_is_fully_opaque() {
        using var ui = Square("filter: drop-shadow(6px 6px 3px #000000);");

        var layer = Assert.Single(ui.Geometry.Layers);
        var shadow = Assert.NotNull(layer.Shadow);

        Assert.Equal(1f, layer.Alpha, 3);
        Assert.Equal(0f, layer.Blur, 3);
        Assert.Equal(6f, shadow.Offset.X, 3);
        Assert.Equal(6f, shadow.Offset.Y, 3);
        Assert.Equal(3f, shadow.Blur, 3);

        // ⚠ A number of its own and not `Image`, and the assertion is that they differ. A shadow that
        // named the group's surface would composite the element twice, sharp and displaced, and every
        // pixel assertion below would still find something dark where it looked.
        Assert.NotEqual(layer.Image, layer.ShadowImage);
    }

    /// <summary>The shadow's quad is drawn before the group's, which is the whole of "under".</summary>
    /// <remarks>
    ///     ⚠ <b>Asserted on the order of the draws and not only on a pixel, because the pixel test
    ///     that would catch it needs the shadow to be opaque where the element is.</b> Paint order is
    ///     the only thing putting one surface behind the other — neither executor knows a shadow from
    ///     a nested group's composite — so a builder that appended the quad instead of inserting it
    ///     would paint the silhouette <i>over</i> the element. That is a solid block of the shadow's
    ///     colour where the element was, and the test that reads the element's centre below is the
    ///     one that would catch it; this is the one that says why.
    /// </remarks>
    [Fact]
    public void The_shadows_quad_precedes_the_groups_composite() {
        using var ui = Square("filter: drop-shadow(6px 6px 3px #000000);");

        var layer = Assert.Single(ui.Geometry.Layers);
        var draws = ui.Geometry.Draws;

        Assert.Equal(layer.ShadowImage, draws[layer.First + layer.Count].Image);
        Assert.Equal(layer.Image, draws[layer.Composite].Image);
        Assert.Equal(layer.First + layer.Count + 1, layer.Composite);
    }

    /// <summary>The element keeps its own colour and the shadow lands beside it, not on it.</summary>
    /// <remarks>
    ///     ⚠ <b>Two pixels, and each one fails for a different wrong implementation.</b> The centre
    ///     is the element: a shadow composited <i>over</i> the group turns it black, and one that
    ///     replaced the group's own composite removes the red entirely. The corner is past the
    ///     element's bottom-right, inside the displacement: it is white when no shadow was drawn, and
    ///     red when the shadow surface was composited untinted — which is what a missing
    ///     <c>UiDropShadow.Tint</c> gives, and is the failure the machinery makes easiest.
    /// </remarks>
    [Fact]
    public void The_shadow_is_a_dark_silhouette_beside_the_element_and_not_over_it() {
        using var ui = Square("filter: drop-shadow(10px 10px 2px #000000);");

        var centre = At(ui, 40, 40);
        var beyond = At(ui, 64, 64);

        Assert.True(
            centre.R > 200 && centre.G < 60 && centre.B < 60,
            $"the element is no longer its own colour at the centre: {centre}. Black means the shadow "
            + "was composited over the group instead of under it."
        );

        Assert.True(
            beyond.R < 200 && beyond.G < 200 && beyond.B < 200,
            $"nothing darkened the field past the element's corner: {beyond}. The shadow was not drawn."
        );

        // ⚠ <b>Neutral, and this is the assertion an untinted copy fails.</b> The element is
        // saturated red; a shadow that is the group's surface blurred and displaced but never put
        // through the tint keeps that hue, so the red channel would stand well clear of the other
        // two. A silhouette painted in black has no hue at all.
        Assert.True(
            Math.Abs(beyond.R - beyond.G) < 12 && Math.Abs(beyond.G - beyond.B) < 12,
            $"the shadow carries the element's hue: {beyond}. It is a copy of the element rather than "
            + "a silhouette — `UiDropShadow.Tint` did not reach the composite."
        );
    }

    /// <summary>It is displaced in the direction it was asked for, and not in the other one.</summary>
    /// <remarks>
    ///     ⚠ <b>A sign test, and it is worth its own fact because y is the axis this engine gets
    ///     wrong.</b> Positive y is down in CSS and in the layout, and up in clip space — see
    ///     <c>Conventions.md</c> — so a shadow assembled with the projection's sense rather than the
    ///     document's is a shadow above the element. Reading only one side would pass for a shadow
    ///     with no offset at all, so both are read and they are required to <i>differ</i>.
    /// </remarks>
    [Fact]
    public void The_shadow_falls_on_the_side_the_offset_names() {
        using var ui = Square("filter: drop-shadow(0 12px 2px #000000);");

        // The box is 20..60 on both axes. Twelve pixels below its bottom edge is inside the shadow;
        // twelve above its top edge is not.
        var below = At(ui, 40, 66);
        var above = At(ui, 40, 14);

        Assert.True(below.R < 160, $"nothing below the element: {below}. The shadow was not displaced down.");
        Assert.True(above.R > 240, $"something above the element: {above}. The offset's sign is inverted.");
    }

    /// <summary>The silhouette follows the ink and not the box, which is the whole difference from
    /// <c>box-shadow</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>The one assertion that separates this feature from the one it is named after.</b>
    ///     <c>DrawListBuilder.EmitShadow</c> draws a rounded rectangle the shape of the border box; a
    ///     <c>drop-shadow</c> blurs whatever coverage the subtree actually rasterised. On a filled
    ///     panel the two are the same picture, which is why the fixture here is <i>not</i> one: the
    ///     element has no background of its own and one small opaque child in its top-left corner. A
    ///     box shadow of that element is a shadow of the whole forty-pixel box; a drop shadow is a
    ///     shadow of the child, and the element's empty bottom-right corner casts nothing.
    /// </remarks>
    [Fact]
    public void The_shadow_is_cast_by_the_ink_and_not_by_the_border_box() {
        using var ui = UiTest.Create(80f, 80f);
        ui.Document.Compositing = true;

        ui.Load(
            """
            root { width: 80px; height: 80px; background-color: #ffffff; }
            .host { position: absolute; left: 20px; top: 20px; width: 40px; height: 40px;
                    filter: drop-shadow(8px 8px 2px #000000); }
            .ink { position: absolute; left: 0px; top: 0px; width: 12px; height: 12px;
                   background-color: #ff0000; }
            """
        );

        var host = ui.Create("div", null, "host", "host");
        ui.Create("div", host, "ink", "ink");
        ui.Frame();

        Assert.Single(ui.Geometry.Layers);

        // The child inks 20..32; its shadow is 28..40. This is inside that and outside the child.
        var underInk = At(ui, 36, 36);

        // The host's own bottom-right corner is 48..60 and inks nothing, so its shadow band at
        // 56..68 must be untouched. A box shadow of the host would darken it.
        var underNothing = At(ui, 64, 64);

        Assert.True(underInk.R < 200, $"no shadow under the child's ink: {underInk}.");

        Assert.True(
            underNothing.R > 240 && underNothing.G > 240 && underNothing.B > 240,
            $"the element's empty corner cast a shadow: {underNothing}. That is the border box's "
            + "silhouette and not the alpha channel's — see `EmitShadow`, which is the other feature."
        );
    }

    /// <summary>A wider blur spreads the silhouette further and softens it.</summary>
    /// <remarks>
    ///     ⚠ <b>The measurement that says a Gaussian ran rather than that a quad was drawn.</b> A
    ///     shadow composited with no convolution is a hard-edged displaced copy, which satisfies every
    ///     assertion above. What only a blur produces is coverage marching past the silhouette's edge
    ///     and fading — so this reads outward along the shadow's own edge and requires each step to be
    ///     lighter than the last, on a fixture whose offset is zero so that the element is not in the
    ///     way.
    /// </remarks>
    [Fact]
    public void A_blurred_shadow_fades_outwards_and_a_wider_one_reaches_further() {
        using var soft = Square("filter: drop-shadow(0 0 6px #000000);");
        using var tight = Square("filter: drop-shadow(0 0 2px #000000);");

        // The box is 20..60; these march left out of its edge along its middle row.
        var just = At(soft, 18, 40).R;
        var further = At(soft, 14, 40).R;
        var furthest = At(soft, 10, 40).R;

        Assert.True(just < 250, "nothing outside the box, so the shadow's blur did not happen");
        Assert.True(further > just, $"the shadow does not fade outwards: {just} then {further}");
        Assert.True(furthest > further, $"the shadow does not fade outwards: {further} then {furthest}");

        // ⚠ The same pixel under the two widths, which is what makes this a statement about the
        // sigma rather than about there being a shadow at all. A renderer that ignored the length and
        // ran one fixed kernel passes every assertion above and fails this one.
        Assert.True(
            At(soft, 14, 40).R < At(tight, 14, 40).R,
            "a six-pixel sigma reaches no further than a two-pixel one"
        );
    }

    /// <summary>The group's bounds are grown by the shadow's kernel and <i>not</i> by its offset.</summary>
    /// <remarks>
    ///     ⚠ <b>Two claims in one fact, and the second is the one a reader would get wrong.</b> The
    ///     kernel has to be in the bounds because the shadow's taps read the group's own surface —
    ///     texels this pass has to have left defined. The offset must <i>not</i> be, because the
    ///     group's surface has not moved: only the shadow's quad has, and it carries the displacement
    ///     in its vertices. Outsetting by the offset as well would spend surface on a rectangle that
    ///     is provably transparent, and would do it silently.
    /// </remarks>
    [Fact]
    public void The_bounds_carry_the_shadows_kernel_and_not_its_offset() {
        using var near = Square("filter: drop-shadow(0 0 1px #000000);");
        using var far = Square("filter: drop-shadow(0 0 5px #000000);");
        using var moved = Square("filter: drop-shadow(20px 20px 1px #000000);");

        var difference = UiLayer.KernelRadius(5f, 1f) - UiLayer.KernelRadius(1f, 1f);
        var tight = Assert.Single(near.Geometry.Layers).Bounds;
        var wide = Assert.Single(far.Geometry.Layers).Bounds;

        Assert.Equal(tight.X - difference, wide.X, 3);
        Assert.Equal(tight.Width + (2 * difference), wide.Width, 3);

        // The displacement changes the bounds by nothing at all: same kernel, same rectangle.
        Assert.Equal(tight, Assert.Single(moved.Geometry.Layers).Bounds);
    }

    /// <summary>A blur and a drop shadow on one element do not exchange their kernels.</summary>
    /// <remarks>
    ///     ⚠ <b>The regression this pair is most likely to have, because the two features share the
    ///     kernel, the scratch and the quad.</b> The bounds are outset by the <i>wider</i> of the two
    ///     and not by their sum — the shadow reads the surface, not the shadow, so the reaches do not
    ///     compose — and the composite quad is no longer the draw immediately after the group's
    ///     range. A renderer that convolved through the shadow's quad instead of the group's would
    ///     produce a picture shifted by the offset, which only a fixture carrying both can show.
    /// </remarks>
    [Fact]
    public void A_blur_and_a_shadow_share_the_wider_kernel_and_not_their_sum() {
        using var ui = Square("filter: blur(4px) drop-shadow(0 0 2px #000000);");
        using var alone = Square("filter: blur(4px);");

        var layer = Assert.Single(ui.Geometry.Layers);

        Assert.Equal(4f, layer.Blur, 3);
        Assert.Equal(2f, Assert.NotNull(layer.Shadow).Blur, 3);

        // Four sigma is twelve pixels of reach and two is six, so the wider one already covers it and
        // the bounds are the blur's alone.
        Assert.Equal(Assert.Single(alone.Geometry.Layers).Bounds, layer.Bounds);
    }

    /// <summary>The default colour is the element's own, which is what CSS says.</summary>
    /// <remarks>
    ///     ⚠ <b>Not black, and the difference shows on exactly the themes nobody tests on.</b> Filter
    ///     Effects 1 § 8.4 defaults the colour to <c>currentcolor</c>; a reader that defaulted to
    ///     black would be right on every light theme and would put a black halo round white text on
    ///     every dark one. The fixture sets <c>color</c> to a saturated blue so that the shadow's hue
    ///     is unmistakable — black, red and blue are three different answers here.
    /// </remarks>
    [Fact]
    public void An_omitted_colour_is_currentcolor() {
        using var ui = UiTest.Create(80f, 80f);
        ui.Document.Compositing = true;

        ui.Load(
            """
            root { width: 80px; height: 80px; background-color: #ffffff; }
            .box { position: absolute; left: 20px; top: 20px; width: 40px; height: 40px;
                   color: #0000ff; background-color: #ff0000;
                   filter: drop-shadow(12px 12px 1px); }
            """
        );

        ui.Create("div", null, "box", "box");
        ui.Frame();

        var shadow = Assert.NotNull(Assert.Single(ui.Geometry.Layers).Shadow);

        Assert.Equal(0f, shadow.Colour.R, 3);
        Assert.Equal(0f, shadow.Colour.G, 3);
        Assert.Equal(1f, shadow.Colour.B, 3);
        Assert.Equal(1f, shadow.Colour.A, 3);
    }

    /// <summary>The colour's alpha fades the shadow and does not darken it.</summary>
    /// <remarks>
    ///     ⚠ <b>Where the alpha goes is the one part of the arithmetic that cannot be read off the
    ///     matrix, and putting it in the wrong half is a shadow sixteen times too faint.</b>
    ///     <see cref="UiDropShadow.Tint" /> has three rows and cannot touch alpha, so a translucent
    ///     shadow colour has to ride the quad — see <c>UiGeometryBuilder.Layer</c>. An implementation
    ///     that premultiplied the colour into the tint <i>and</i> put the alpha on the quad would
    ///     square it. Half opacity over white is mid grey; a quarter of a quarter is nearly white.
    /// </remarks>
    [Fact]
    public void A_translucent_shadow_colour_fades_the_silhouette_once() {
        using var ui = Square("filter: drop-shadow(20px 20px 0 rgba(0, 0, 0, 0.5));");

        // The box is 20..60, so its shadow is 40..80 and this is well inside it and outside the box.
        var inside = At(ui, 70, 70);

        Assert.InRange(inside.R, 100, 160);
        Assert.InRange(inside.G, 100, 160);
        Assert.InRange(inside.B, 100, 160);
    }

    /// <summary>A shadow that would paint nothing opens no group.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the identity <c>UtilityComposition.Filter</c> puts on every element carrying
    ///     any filter at all, so it is not a corner case — it is the common one.</b> Every other
    ///     function has a number that means "unchanged"; <c>drop-shadow</c>'s identity is a
    ///     transparent colour. Counted, it would buy every <c>blur-2</c> in the engine a second
    ///     viewport-sized surface and two more render passes to composite nothing.
    /// </remarks>
    [Theory]
    [InlineData("filter: drop-shadow(0 0 0 transparent);")]
    [InlineData("filter: drop-shadow(8px 8px 4px rgba(0, 0, 0, 0));")]
    public void A_transparent_shadow_opens_no_group(string filter) {
        using var ui = Square(filter);

        Assert.Empty(ui.Geometry.Layers);
        Assert.DoesNotContain(ui.Document.Drawing.Commands, command => command.Kind == DrawCommandKind.LayerPush);
    }

    /// <summary>Shapes this refuses take the whole declaration with them.</summary>
    /// <remarks>
    ///     ⚠ <b>The rule the rest of <c>DrawListBuilder.Filter</c> already keeps, and it matters more
    ///     here than anywhere because this function is the one that can arrive half-written.</b> A
    ///     second <c>drop-shadow</c> is legal CSS and is a second surface and two more passes, so it
    ///     is refused rather than half-applied — drawing the first and dropping the second is the
    ///     silent middle state. A negative blur is invalid CSS. A percentage is not a length. Each
    ///     takes the <c>blur(2px)</c> beside it with it, which is what "refused" means and what makes
    ///     the failure visible.
    /// </remarks>
    [Theory]
    [InlineData("filter: blur(2px) drop-shadow(2px 2px #000) drop-shadow(4px 4px #000);")]
    [InlineData("filter: blur(2px) drop-shadow(2px 2px -3px #000000);")]
    [InlineData("filter: blur(2px) drop-shadow(50% 2px #000000);")]
    [InlineData("filter: blur(2px) drop-shadow(2px);")]
    [InlineData("filter: blur(2px) drop-shadow(1px 2px 3px 4px #000000);")]
    public void A_shape_this_refuses_refuses_the_whole_list(string filter) {
        using var ui = Square(filter);

        Assert.Empty(ui.Geometry.Layers);
    }

    /// <summary>Two lengths and no blur is a hard-edged offset silhouette.</summary>
    /// <remarks>
    ///     ⚠ <b>The case the device path has to special-case and the software path does not, which is
    ///     exactly the shape of divergence worth a test of its own.</b> <c>KernelRadius</c> answers
    ///     zero, so <c>UiRenderer.ShadowSurface</c> runs one pass instead of two and hands the shader
    ///     a sigma it does not use — see its remarks, and the NaN that a real zero would produce.
    ///     Asserted here on the model and on the software picture; the agreement of the two executors
    ///     is <c>UiCompositingTests</c>' half.
    /// </remarks>
    [Fact]
    public void A_shadow_with_no_blur_is_the_silhouette_moved() {
        using var ui = Square("filter: drop-shadow(16px 16px #000000);");

        var shadow = Assert.NotNull(Assert.Single(ui.Geometry.Layers).Shadow);

        Assert.Equal(0f, shadow.Blur, 3);
        Assert.Equal(new Vector2(16f, 16f), shadow.Offset);

        // The box is 20..60 and the shadow 36..76. Two pixels inside its far corner is solid black;
        // two pixels outside is the untouched field, because there is no kernel to fade over.
        var inside = At(ui, 74, 74);
        var outside = At(ui, 78, 78);

        Assert.True(inside.R < 40, $"the silhouette is not solid: {inside}");
        Assert.True(outside.R > 240, $"a shadow with no blur has a soft edge: {outside}");
    }
}
