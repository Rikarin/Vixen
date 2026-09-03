// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Ui.Testing.Tests;

/// <summary>What <c>border-style</c> actually puts on the screen.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The draw-list tests in <c>Vixen.Ui.Tests.BorderStyleTests</c> cannot see any of
///         this.</b> They say the right commands were asked for; they cannot say a stroked path
///         reached the tessellator, that the geometry builder kept its sub-paths apart, or that the
///         marks landed on the border box rather than half a thickness outside it. A dashed border
///         is a different <i>kind</i> of command from a solid one — a path stroke where the solid
///         case is a distance field — so it takes a different route through every stage below the
///         draw list, and that route is what this file exercises.
///     </para>
///     <para>
///         ⚠ <b>Judged by counting ink rather than by comparing pictures.</b> A dashed ring covers
///         strictly less than the solid ring of the same width and strictly more than nothing, and a
///         doubled one covers two thirds of its own width — closed forms, so there is no reference
///         PNG to bootstrap and no tone map or exposure to depend on. A third render deliberately
///         different from both — <c>border-style: none</c> — has to fail the same comparison, so two
///         blank images cannot satisfy it.
///     </para>
/// </remarks>
public class BorderStylePixelTests {
    const int Side = 64;

    static readonly Color4 Background = new(0f, 0f, 0f, 1f);

    /// <summary>A 40×24 box with a border in whatever style, rendered on black.</summary>
    static Bitmap Rendered(string declarations) {
        using var ui = UiTest.Create(Side, Side, new UiTestOptions { Background = Background });

        ui.Load(
            $$"""
            root  { width: {{Side}}px; height: {{Side}}px; align-items: flex-start; }
            .box  { width: 40px; height: 24px; border-color: #ff0000; {{declarations}} }
            """
        );

        ui.Create("div", ui.Document.Root, null, "box");
        ui.Frame();

        return ui.Capture();
    }

    /// <summary>How much red the picture holds, as a sum of the red channel over every pixel.</summary>
    /// <remarks>
    ///     ⚠ Summed rather than counted, so a mark that is antialiased at both ends contributes what
    ///     it actually covers. Counting pixels above a threshold would make the comparison depend on
    ///     where the threshold sat relative to a half-covered edge texel, which is exactly the
    ///     quantity a dash pattern changes the most of.
    /// </remarks>
    static float Ink(in Bitmap image) {
        var total = 0f;

        for (var y = 0; y < image.Height; y++) {
            for (var x = 0; x < image.Width; x++) {
                total += image.Pixels[image.Offset(x, y)] / 255f;
            }
        }

        return total;
    }

    [Fact]
    public void A_dashed_border_paints_less_than_a_solid_one_and_more_than_none() {
        var solid = Ink(Rendered("border-width: 2px;"));
        var dashed = Ink(Rendered("border-width: 2px; border-style: dashed;"));
        var dotted = Ink(Rendered("border-width: 2px; border-style: dotted;"));
        var blank = Ink(Rendered("border-width: 2px; border-style: none;"));

        // ⚠ The third render, and it is the one that makes the other two mean something. Without a
        // picture that must *fail* the comparison, "less than solid" is satisfied by a border that
        // did not draw at all — which is precisely what a stroked path that never reached the
        // tessellator would produce.
        Assert.Equal(0f, blank, 0.01f);

        Assert.True(solid > 0f, "the solid ring drew something");
        Assert.True(dashed > 0f, $"a dashed ring draws: {dashed}");
        Assert.True(dotted > 0f, $"a dotted ring draws: {dotted}");

        Assert.True(dashed < solid, $"a dashed ring covers less than a solid one: {dashed} of {solid}");
        Assert.True(dotted < solid, $"a dotted ring covers less than a solid one: {dotted} of {solid}");

        // A dot is a third of a dash against a gap of the same, so a dotted ring is about half a
        // dashed one's ink. Bounded either side rather than pinned, because antialiasing along
        // sixteen more mark ends is real and is not the thing under test.
        Assert.True(dotted < dashed, $"a dotted ring covers less than a dashed one: {dotted} of {dashed}");
    }

    [Fact]
    public void A_doubled_border_paints_about_two_thirds_of_its_width() {
        var solid = Ink(Rendered("border-width: 3px;"));
        var doubled = Ink(Rendered("border-width: 3px; border-style: double;"));

        // ⚠ The closed form. `double` splits the width into three and leaves the middle third out, so
        // the ink is two thirds of the solid ring's — bounded rather than pinned because the two
        // extra antialiased edges the gap introduces are worth a fraction of a pixel per side.
        Assert.InRange(doubled / solid, 0.55f, 0.8f);
    }

    [Fact]
    public void A_dashed_border_on_a_rounded_box_still_reaches_the_corners() {
        var rounded = Rendered("border-width: 2px; border-style: dashed; border-radius: 8px;");

        Assert.True(Ink(rounded) > 0f, "a dashed rounded ring draws");

        // ⚠ The property a dashed *band* could not have. The ring is walked by arc length along its
        // own centre line, so a mark lands on the curve — and the pixel that proves it is one inside
        // the corner's arc, which no straight edge of the box passes through. Nothing is asserted
        // about which mark: what is asserted is that the corner region is not empty, which it would
        // be if the walk had straightened the box.
        Assert.True(Corner(rounded) > 0f, "a mark lands on the corner arc rather than on a straight edge");
    }

    /// <summary>How much red sits in the top-left 8×8 corner, which is entirely arc on a radius of 8.</summary>
    static float Corner(in Bitmap image) {
        var total = 0f;

        for (var y = 0; y < 8; y++) {
            for (var x = 0; x < 8; x++) {
                total += image.Pixels[image.Offset(x, y)] / 255f;
            }
        }

        return total;
    }
}
