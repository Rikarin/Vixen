// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Ui.Testing.Tests;

/// <summary>Where an <c>inset</c> shadow's ink actually lands.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The draw-list tests cannot see any of this, and the two ways this feature fails are
///         both invisible to them.</b> <c>Vixen.Ui.Tests.DrawListTests</c> says the command carries the
///         box's own quad and a record saying it is inset; it cannot say the fragment stage took the
///         complement rather than the plain coverage, and it cannot say the complement was clipped to
///         the box's own outline. ⚠ The second of those was nearly written as a fixture that could not
///         fail: an inner shadow's quad IS the border box, so on a square box the geometry already
///         does the clipping and dropping the mask from the fragment stage changes nothing anybody can
///         measure. What the mask is actually for is a ROUNDED box, where the quad is still a
///         rectangle and only the distance field knows where the corner is — so that is the fixture,
///         and it was arrived at by sabotaging the square one and watching it stay green.
///     </para>
///     <para>
///         ⚠ <b>Judged against a closed form rather than a picture.</b> A zero-blur
///         <c>inset 0 0 0 6px</c> on a 40×24 box covers the box minus the box shrunk by six on every
///         side: 960 − 336 = <b>624</b> square points, exactly, with the whole boundary on an integer
///         and nothing to antialias. So there is no reference PNG to bootstrap, and the two failure
///         modes above land far outside a couple of pixels of that number — the unmasked one at
///         thousands, the plain-coverage one at 336.
///     </para>
///     <para>
///         The box is painted blue and the shadow red, so the red channel is the shadow's alone: the
///         element's own background contributes nothing to it, and the canvas behind is black.
///     </para>
/// </remarks>
public class InsetShadowPixelTests {
    const int Side = 64;

    const int BoxWidth = 40;
    const int BoxHeight = 24;

    static readonly Color4 Background = new(0f, 0f, 0f, 1f);

    /// <summary>A 40×24 blue box at the top left, with whatever shadow, on black.</summary>
    static Bitmap Rendered(string declarations) {
        using var ui = UiTest.Create(Side, Side, new UiTestOptions { Background = Background });

        ui.Load(
            $$"""
            root  { width: {{Side}}px; height: {{Side}}px; align-items: flex-start; }
            .box  {
                width: {{BoxWidth}}px; height: {{BoxHeight}}px; background-color: #0000ff;
                {{declarations}}
            }
            """
        );

        ui.Create("div", ui.Document.Root, null, "box");
        ui.Frame();

        return ui.Capture();
    }

    /// <summary>How much red the picture holds inside the box, and how much outside it.</summary>
    /// <remarks>
    ///     ⚠ Summed rather than counted, so a partly covered texel contributes what it covers. The
    ///     split is the whole assertion: an inner shadow's ink is inside the box by definition, and
    ///     the number outside it is what says the complement was masked.
    /// </remarks>
    static (float Inside, float Outside) Red(in Bitmap image) {
        var inside = 0f;
        var outside = 0f;

        for (var y = 0; y < image.Height; y++) {
            for (var x = 0; x < image.Width; x++) {
                var red = image.Pixels[image.Offset(x, y)] / 255f;

                if (x < BoxWidth && y < BoxHeight) {
                    inside += red;
                } else {
                    outside += red;
                }
            }
        }

        return (inside, outside);
    }

    /// <summary>An inset shadow covers the box minus the box shrunk by its spread.</summary>
    /// <remarks>
    ///     ⚠ <b>Two numbers, and each is a different way this can be wrong.</b> 624 rather than 0 says
    ///     the complement was taken at all, and 624 rather than 336 says it was taken the right way
    ///     round — an implementation that read the inner rectangle's plain coverage instead of its
    ///     complement paints the middle and leaves the ring, which is the same picture inverted and
    ///     the same total only if the ring happens to be half the box.
    /// </remarks>
    [Fact]
    public void An_inset_shadow_covers_the_box_minus_the_rectangle_its_spread_shrinks_it_to() {
        var (inside, outside) = Red(Rendered("box-shadow: inset 0 0 0 6px #ff0000;"));

        // 40 × 24 − 28 × 12. The tolerance is the 8-bit store rather than the shape: every edge of
        // both rectangles is on an integer, so there is no partly covered texel anywhere in it.
        Assert.InRange(inside, 620f, 628f);

        // The quad is the border box, so nothing can reach past it on a square box — see the rounded
        // fixture below for the assertion that the mask is doing work.
        Assert.Equal(0f, outside, 1);
    }

    /// <summary>On a rounded box the shadow stops at the curve, which is what the mask is for.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the fixture the square one could not be.</b> An inner shadow's quad is the
    ///         border box, so on a square box the rasteriser never runs a fragment outside it and the
    ///         fragment stage's own mask is unobservable — dropping it leaves every assertion above
    ///         green. A rounded box is where the two disagree: the quad is still a rectangle and the
    ///         corner is only a corner to the distance field, so an unmasked complement fills the four
    ///         squares the curve cuts away with solid shadow colour.
    ///     </para>
    ///     <para>
    ///         The extreme corner texel is the assertion. At a 12-point radius the pixel at (0, 0) is
    ///         well outside the quarter-circle, so the element paints nothing there at all and the
    ///         canvas shows through — unless the shadow reached it, in which case it is the shadow's
    ///         colour at full strength, because that texel is as far outside the inner rectangle as
    ///         anything in the quad.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_rounded_boxs_inset_shadow_stops_at_the_curve() {
        var image = Rendered("border-radius: 12px; box-shadow: inset 0 0 0 6px #ff0000;");

        Assert.Equal(0, image.Pixels[image.Offset(0, 0)]);
        Assert.Equal(0, image.Pixels[image.Offset(BoxWidth - 1, 0)]);
        Assert.Equal(0, image.Pixels[image.Offset(0, BoxHeight - 1)]);
        Assert.Equal(0, image.Pixels[image.Offset(BoxWidth - 1, BoxHeight - 1)]);

        // ⚠ And it did draw: without this the four zeroes above are satisfied by a shadow that never
        // ran, which is the failure this whole file is about.
        var (inside, _) = Red(image);

        Assert.True(inside > 400f, $"a rounded box's inset shadow drew {inside} of red, so it did not draw");
    }

    /// <summary>Halving the spread halves nothing, and the closed form says what it does instead.</summary>
    /// <remarks>
    ///     ⚠ <b>A ring's area is not linear in its width, which is why this is a second fixture rather
    ///     than a scaling of the first.</b> A 3-point ring on a 40×24 box is 960 − 34 × 18 = 348, not
    ///     half of 624 — so an implementation that used the spread as an opacity, a blur or a
    ///     multiplier would satisfy a proportional assertion and fails this one.
    /// </remarks>
    [Fact]
    public void The_spread_is_the_rings_width_rather_than_a_scale_on_it() {
        var (narrow, _) = Red(Rendered("box-shadow: inset 0 0 0 3px #ff0000;"));

        Assert.InRange(narrow, 344f, 352f);
    }

    /// <summary>And an inset shadow is painted over the background rather than under it.</summary>
    /// <remarks>
    ///     ⚠ <b>The ordering half, and it is the half that makes the feature draw nothing at all when
    ///     it is wrong.</b> CSS Backgrounds 3 § 7.1.1 paints an outer shadow below the background and
    ///     an inner one above it; a list emitted whole before the background hides every inner shadow
    ///     behind the element's own fill, which on an opaque element is a picture identical to the one
    ///     with no declaration. The outer shadow of the same geometry is the control: it is behind the
    ///     opaque box, so none of its ink reaches the inside.
    /// </remarks>
    [Fact]
    public void An_inset_shadow_is_painted_over_the_background_and_an_outer_one_under_it() {
        var (inner, _) = Red(Rendered("box-shadow: inset 0 0 0 6px #ff0000;"));
        var (outer, outside) = Red(Rendered("box-shadow: 0 0 0 6px #ff0000;"));

        Assert.True(inner > 600f, $"an inset shadow drew {inner} of red inside the box, so it is behind the background");
        Assert.Equal(0f, outer, 1);

        // ⚠ The control's own ink is real and all of it is outside — and its closed form is not the
        // ring's, because the canvas cuts it and its corner is round. The 52 × 36 ring is clipped to
        // the top-left of a 64-point canvas, leaving 46 × 30 − 40 × 24 = 420, and `EmitOneShadow`
        // grows a square box's radius by the spread, so the one corner still on the canvas rounds off
        // 36 − 9π ≈ 7.7 of that.
        Assert.InRange(outside, 408f, 416f);
    }
}
