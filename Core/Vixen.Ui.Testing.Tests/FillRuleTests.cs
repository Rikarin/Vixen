// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Ui.Rendering;
using Vixen.Ui.Testing.Visual;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Ui.Testing.Tests;

/// <summary>Which quad owns a sample that lands exactly on the edge between two of them.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The tie, and only the tie.</b> Every assertion here is about a coordinate that puts a
///         box edge exactly on a pixel centre; nothing else in the suite does, because
///         <c>RasterizerTests</c> and every committed screenshot sit on integer coordinates where the
///         question does not arise. That is what let <c>SoftwareUiRasterizer</c> keep a
///         <i>closed</i> test on axis-aligned edges — shading the column on its right and the row
///         below it as well as its own — for as long as it did.
///     </para>
///     <para>
///         ⚠ <b>Why the device's rule and not the geometrically generous one.</b> Closing the right
///         edge is arguably the prettier answer: a box from 8.5 to 32.5 really does cover half of
///         column 32, and the half-open rule throws that half away. It is still the wrong answer
///         <i>here</i>, twice over. A quad's coverage is antialiased by the distance field
///         <i>inside</i> the quad, so two abutting boxes that both shade their shared column blend
///         over each other and reach 0.75 where a single box reaches 1 — the same double-shading the
///         diagonal fix removed, left in place on the axis-aligned edges. And this renderer's whole
///         contract is to be a model of what ships: the device opens the right and bottom edges, so
///         a closed test here makes every committed picture a picture of a frame no screen ever
///         shows. Measured before this changed: 54 pixels of 16,384 differing by up to
///         <b>107 levels of 255</b> against the device on a single box, which was the largest known
///         disagreement on the box path.
///     </para>
///     <para>
///         ⚠ <b>Asymmetry is the whole assertion.</b> "Axis-aligned edges are excluded" and "axis-aligned
///         edges are included" both make a lone box look plausible; only a fixture that reads the
///         left edge <i>and</i> the right one, the top <i>and</i> the bottom, can tell the rule from
///         either of the two blanket answers.
///     </para>
/// </remarks>
public class FillRuleTests {
    const int Side = 64;

    /// <summary>Opaque, so a doubly-shaded column is visible as a colour and not only as an alpha.</summary>
    static readonly Color4 Background = new(0f, 0f, 0f, 1f);

    static readonly Color4 Red = new(1f, 0f, 0f, 1f);

    static readonly Color4 Blue = new(0f, 0f, 1f, 1f);

    /// <summary>
    ///     A box whose right edge lands on a sample centre does not shade that column, and its left
    ///     edge on a sample centre does.
    /// </summary>
    /// <remarks>
    ///     ⚠ Both halves, because a rule that opened <i>every</i> axis-aligned edge would satisfy the
    ///     first assertion and lose the box's whole left column — which is the mirror-image defect and
    ///     just as invisible on the integer coordinates the rest of the suite uses.
    /// </remarks>
    [Fact]
    public void A_right_edge_on_a_sample_centre_is_open_and_a_left_edge_is_closed() {
        var image = Render(new DrawCommand(DrawCommandKind.Rectangle, 8.5f, 8.5f, 24f, 24f, Red, 0f, 0f));

        // The right edge is at x = 32.5, exactly on column 32's sample. The device gives that sample
        // to whatever is drawn to the right of the seam, so this box leaves it untouched.
        Assert.Equal((byte)0, Red8(image, 32, 20));

        // ⚠ And the column inside it is fully shaded, so "untouched" above is the fill rule and not a
        // box that came out a column short at both ends.
        Assert.Equal((byte)255, Red8(image, 31, 20));

        // The left edge is at x = 8.5, also exactly on a sample. That one is closed, and the distance
        // field is zero there, so the column comes out at half coverage rather than none.
        Assert.InRange(Red8(image, 8, 20), 120, 136);

        // The same on the other axis, which is what catches a rule applied to one of them.
        Assert.Equal((byte)0, Red8(image, 20, 32));
        Assert.Equal((byte)255, Red8(image, 20, 31));
        Assert.InRange(Red8(image, 20, 8), 120, 136);
    }

    /// <summary>Two boxes meeting on a sample centre shade the column they share exactly once.</summary>
    /// <remarks>
    ///     ⚠ <b>Against the right-hand box drawn alone, which is the closed form.</b> "The seam is not
    ///     red" would pass against a renderer that dropped the seam entirely, and "the seam is blue"
    ///     would pass against one that shaded it twice in the same colour. Requiring the pair to draw
    ///     the seam exactly as the second box draws it by itself says both things at once: the first
    ///     box contributed nothing there, and the second contributed everything it would have anyway.
    ///     The check that the seam is not background is what stops the comparison being two blank
    ///     columns agreeing.
    /// </remarks>
    [Fact]
    public void Two_boxes_abutting_on_a_sample_centre_shade_the_seam_once() {
        var left = new DrawCommand(DrawCommandKind.Rectangle, 8.5f, 8f, 24f, 24f, Red, 0f, 0f);
        var right = new DrawCommand(DrawCommandKind.Rectangle, 32.5f, 8f, 24f, 24f, Blue, 0f, 0f);

        var both = Render(left, right);
        var alone = Render(right);

        for (var y = 12; y < 28; y++) {
            Assert.Equal(Pixel(alone, 32, y), Pixel(both, 32, y));
        }

        // ⚠ The instrument. Two columns of background agree perfectly, and so would two columns the
        // rule had thrown away — the seam has to be a pixel the right-hand box actually drew.
        Assert.NotEqual((byte)0, Pixel(both, 32, 20).B);
    }

    /// <summary>Renders one frame of boxes at the size the fixtures above assume.</summary>
    static Bitmap Render(params DrawCommand[] commands) {
        var list = new DrawList();
        list.BeginFrame();

        foreach (var command in commands) {
            list.Add(command);
        }

        // ⚠ Without this there are no batches, and a frame with nothing in it satisfies every
        // assertion about a colour that should not be present.
        list.EndFrame();

        var cache = new GlyphFieldCache(new GlyphAtlas(64, 64));
        var geometry = new UiGeometryBuilder().Build(list, cache, new Rectangle(0, 0, Side, Side));

        Assert.NotEmpty(geometry.Draws);

        return SoftwareUiRasterizer.Render(geometry, cache.Atlas, Side, Side, Background);
    }

    static (byte R, byte G, byte B, byte A) Pixel(in Bitmap image, int x, int y) {
        var offset = image.Offset(x, y);

        return (
            image.Pixels[offset],
            image.Pixels[offset + 1],
            image.Pixels[offset + 2],
            image.Pixels[offset + 3]
        );
    }

    static byte Red8(in Bitmap image, int x, int y) => Pixel(image, x, y).R;
}
