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
///     <para>
///         ⚠ <b>What this file asserts moved when the box quad grew, and the two paragraphs above are
///         history rather than the current subject.</b> Since #590 a box's quad reaches a pixel past
///         the box on every side, so <em>neither</em> of a box's own edges is a quad edge any more and
///         the tie rule cannot decide either of them: both come out at the half coverage the distance
///         field gives them, which is what the geometry says and what the device draws. So these
///         fixtures now read the <b>ink</b> — a box of integer width at a half-pixel coordinate sums
///         to that width across the row — and the tie rule they were written for is observable only
///         on a primitive whose quad <i>is</i> its boundary: an image, a glyph, a tessellated path.
///         The rule still runs on every one of those and on the diagonal every quad's two triangles
///         share, so the last two fixtures below read it there instead. ⚠ Measured rather than
///         assumed: closing the axis-aligned edges again — <c>return true</c> in <c>TopLeft</c>'s
///         <c>dy == 0f</c> branch, which is the defect #526 removed — left every <em>box</em>
///         assertion in this file and every other test in this assembly green.
///     </para>
///     <para>
///         ⚠ <b>And the primitive that reads the rule is a hard-edged path, not an image, which is
///         the one guess worth recording as wrong.</b> An image quad <i>is</i> its own boundary and
///         has no distance field, which makes it the obvious candidate — but this renderer draws
///         nothing for one. <c>UiGeometryBuilder.Image</c> emits the quad with a zero
///         <c>Shape</c>, and every image number but a composited group's names a texture the
///         software renderer has never seen, so the fragment falls through to <c>Solid</c> with a
///         coverage of zero. A path's fill triangles carry their coverage in the same slot and
///         carry <b>one</b>, so with <see cref="UiGeometryBuilder.Fringe" /> turned off they are
///         the frame's only primitive whose edge is decided by the fill rule and nothing else.
///     </para>
/// </remarks>
public class FillRuleTests {
    const int Side = 64;

    /// <summary>Opaque, so a doubly-shaded column is visible as a colour and not only as an alpha.</summary>
    static readonly Color4 Background = new(0f, 0f, 0f, 1f);

    static readonly Color4 Red = new(1f, 0f, 0f, 1f);

    static readonly Color4 Blue = new(0f, 0f, 1f, 1f);

    /// <summary>Half opaque, which is what makes a doubly-shaded sample a different colour.</summary>
    static readonly Color4 Translucent = new(1f, 0f, 0f, 0.5f);

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
    public void A_box_on_a_sample_centre_shades_both_of_its_edge_columns_by_half() {
        var image = Render(new DrawCommand(DrawCommandKind.Rectangle, 8.5f, 8.5f, 24f, 24f, Red, 0f, 0f));

        // Both edges land exactly on a sample — 8.5 and 32.5 — and the distance field is zero at
        // each, so each column comes out half covered. ⚠ The right one used to be *nothing*: the
        // quad ended there, the half-open rule gave the sample to the neighbour, and no fragment was
        // generated for the half of the column the box geometrically covers.
        Assert.InRange(Red8(image, 8, 20), 120, 136);
        Assert.InRange(Red8(image, 32, 20), 120, 136);

        // ⚠ And the columns inside them are whole, so "half" above is the ramp and not a box that
        // came out soft all the way through.
        Assert.Equal((byte)255, Red8(image, 9, 20));
        Assert.Equal((byte)255, Red8(image, 31, 20));

        // A pixel further out on each side is untouched: the margin is room for the ramp, not ink.
        Assert.Equal((byte)0, Red8(image, 7, 20));
        Assert.Equal((byte)0, Red8(image, 33, 20));

        // The same on the other axis, which is what catches a margin added to one of them.
        Assert.InRange(Red8(image, 20, 8), 120, 136);
        Assert.InRange(Red8(image, 20, 32), 120, 136);
        Assert.Equal((byte)0, Red8(image, 20, 33));
    }

    /// <summary>A box of integer width at a half-pixel coordinate has that width's worth of ink.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The closed form, and the reason this file exists in its present shape.</b> Coverage
    ///         is conserved: however the edges fall across the sample grid, the alpha summed along a
    ///         row through a box of width <c>w</c> is <c>w</c>. Nothing about which sample the
    ///         rasteriser awarded to whom appears in that statement, which is what makes it worth
    ///         asserting — a picture can be wrong in a way no per-pixel expectation anticipates, and
    ///         still cannot hide from the total.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It read <c>w − 0.5</c> before the box quad was grown</b>, for every box on a
    ///         half-pixel coordinate in the engine. On a 24-pixel box that is 2 % and invisible; on a
    ///         <b>one-pixel hairline it is half the primitive</b>, which is why the outliner's tree
    ///         connectors were drawn at half intensity and why the hairline is asserted here as well
    ///         as the box. A width of one is the case where the defect is the whole of the ink.
    ///     </para>
    ///     <para>
    ///         The tolerance is quantisation and nothing else: each column is a byte, so a row of
    ///         twenty-six of them can be off by a fiftieth of a level's worth in total. A defect this
    ///         is written against moves it by 0.5.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(24f)]
    [InlineData(3f)]
    [InlineData(1f)]
    public void A_box_at_a_half_pixel_has_its_own_widths_worth_of_ink(float width) {
        var image = Render(new DrawCommand(DrawCommandKind.Rectangle, 18.5f, 8f, width, 24f, Red, 0f, 0f));

        var ink = 0.0;

        for (var x = 0; x < Side; x++) {
            ink += Red8(image, x, 20) / 255.0;
        }

        Assert.Equal(width, ink, 0.05);
    }

    /// <summary>Two boxes meeting on a sample centre each contribute their half of the seam.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>What this used to assert was that the seam is drawn exactly once — and it was, by
    ///         throwing the left-hand box's half of it away.</b> The two boxes cover the shared column
    ///         half each, so the honest statement is that both are in it: the seam is the right box's
    ///         half over the left box's half, and the background left showing through is a quarter
    ///         rather than the half it was.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A quarter and not nothing, and that is a cost this change accepts rather than
    ///         removes.</b> Two independently antialiased primitives that abut cannot composite to
    ///         full coverage — 0.5 over 0.5 is 0.75, whatever order they arrive in — so a seam on a
    ///         half-pixel boundary is a shade light. The alternative measured worse: before the quad
    ///         grew, the same seam was <em>half</em> background, and every isolated hairline in the
    ///         frame lost half its ink to buy it.
    ///     </para>
    ///     <para>
    ///         The comparison against the right-hand box drawn alone is what makes each claim
    ///         falsifiable. "The seam is bluer than the box alone" would pass against a renderer that
    ///         shaded it twice in blue; requiring the blue to be <i>exactly</i> what the box draws by
    ///         itself, and the red to be exactly what the left box's ramp adds under it, says which
    ///         primitive put what there.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Two_boxes_abutting_on_a_sample_centre_each_shade_their_half_of_the_seam() {
        var left = new DrawCommand(DrawCommandKind.Rectangle, 8.5f, 8f, 24f, 24f, Red, 0f, 0f);
        var right = new DrawCommand(DrawCommandKind.Rectangle, 32.5f, 8f, 24f, 24f, Blue, 0f, 0f);

        var both = Render(left, right);
        var alone = Render(right);

        for (var y = 12; y < 28; y++) {
            // The right box contributes exactly what it contributes on its own: it is drawn last, over
            // whatever is there, and half coverage of blue is half coverage of blue.
            Assert.Equal(Pixel(alone, 32, y).B, Pixel(both, 32, y).B);

            // And the left box is under it — a quarter of the column, being its own half attenuated by
            // the blue over it. This is the half that was being discarded.
            Assert.InRange(Pixel(both, 32, y).R, 56, 72);
            Assert.Equal((byte)0, Pixel(alone, 32, y).R);
        }

        // ⚠ The instrument. Two columns of background agree perfectly, and so would two columns the
        // rule had thrown away — the seam has to be a pixel the right-hand box actually drew.
        Assert.NotEqual((byte)0, Pixel(both, 32, 20).B);
    }

    /// <summary>A hard-edged quad owns the top and left edges it lands on, and not the other two.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Both halves of the asymmetry on one primitive, which is the only shape of fixture
    ///         that can tell the rule from either blanket answer.</b> "Every axis-aligned edge is
    ///         closed" satisfies the two inclusive assertions and "every one is open" satisfies the
    ///         two exclusive ones; only asking for the pair together says the top row is in and the
    ///         bottom row is out.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the answers are 255 and 0 rather than something near them.</b> A path's fill
    ///         triangles carry a coverage of one and nothing else — no distance field, no ramp — so a
    ///         sample either belongs to the primitive or does not, and a tie decided the wrong way is
    ///         a whole row of ink appearing or vanishing rather than a level or two of drift. That is
    ///         what a box could do before #590 grew its quad past its own edges and what it can no
    ///         longer do; see the remarks on the class.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_hard_edged_path_owns_the_top_and_left_edges_it_lands_on_and_not_the_other_two() {
        var image = RenderPath(new Rectangle(8.5f, 8.5f, 24f, 24f), Red);

        // The top edge runs left to right, so it is the top one and its row is shaded; the bottom
        // edge runs back the other way and its row belongs to whatever is under the path.
        Assert.Equal((byte)255, Red8(image, 20, 8));
        Assert.Equal((byte)0, Red8(image, 20, 32));

        // The same on the other axis, decided by the other branch of the rule: a left edge is
        // traversed upwards and is inclusive, a right edge downwards and is not.
        Assert.Equal((byte)255, Red8(image, 8, 20));
        Assert.Equal((byte)0, Red8(image, 32, 20));

        // ⚠ The instrument. Two empty rows agree just as well as two the rule threw away, so the
        // rows and columns *inside* the four edges have to be ink for the four answers above to mean
        // anything — and the ones outside have to be background, or the picture is simply bigger than
        // the path and every exclusive assertion is being satisfied by an accident of position.
        Assert.Equal((byte)255, Red8(image, 20, 9));
        Assert.Equal((byte)255, Red8(image, 20, 31));
        Assert.Equal((byte)255, Red8(image, 9, 20));
        Assert.Equal((byte)255, Red8(image, 31, 20));

        Assert.Equal((byte)0, Red8(image, 20, 7));
        Assert.Equal((byte)0, Red8(image, 20, 33));
        Assert.Equal((byte)0, Red8(image, 7, 20));
        Assert.Equal((byte)0, Red8(image, 33, 20));
    }

    /// <summary>The diagonal two triangles share is shaded once, not once per triangle.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Translucent, because that is the only way the fault is visible at all.</b>
    ///         Blending an opaque colour over itself returns it, so a diagonal shaded twice is
    ///         pixel-identical to one shaded once for every fill in the engine that is not
    ///         see-through — and a composited group is see-through by construction, which is where
    ///         this was originally found as a 0.75 line down a 40×40 box at half opacity.
    ///     </para>
    ///     <para>
    ///         The rectangle's corners are on sample centres so that its diagonal passes through one
    ///         too: the trapezoid the tessellator emits is split from <c>(8.5, 8.5)</c> to
    ///         <c>(32.5, 32.5)</c>, which is a slope of exactly one through the centre of every pixel
    ///         whose column and row are equal. Every other rectangle in the suite misses those
    ///         samples and cannot see this.
    ///     </para>
    ///     <para>
    ///         ⚠ The comparison is against a pixel of the <i>same</i> interior rather than against a
    ///         constant, so a change to the colour or the blend cannot make it pass by moving both.
    ///         The absolute range is what stops it passing on an empty frame, where the two agree
    ///         perfectly at zero.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(13)]
    [InlineData(20)]
    [InlineData(29)]
    public void The_diagonal_a_paths_two_triangles_share_is_shaded_once(int offset) {
        var image = RenderPath(new Rectangle(8.5f, 8.5f, 24f, 24f), Translucent);

        var diagonal = Red8(image, offset, offset);
        var interior = Red8(image, offset - 4, offset);

        // Half of red over black, quantised — and a tie taken by both triangles would put 0.75 here,
        // which is 191 and nowhere near the range.
        Assert.InRange(diagonal, 124, 132);
        Assert.InRange(interior, 124, 132);

        // ⚠ Within a level, not equal: the coverage rides in a vertex attribute and is interpolated
        // by barycentrics, so an interior that is uniformly one arrives as 127 or 128 depending on
        // where the sample falls. A doubly-shaded diagonal is sixty levels away from either.
        Assert.InRange(Math.Abs(diagonal - interior), 0, 1);
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

    /// <summary>Renders one filled rectangular path with nothing softening its edges.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="UiGeometryBuilder.Fringe" /> is zero, and that is what makes the picture
    ///     an answer about the fill rule rather than about the tessellator's antialiasing.</b> The
    ///     fringe is a strip of ramp triangles laid along the outline; with it on, every edge below
    ///     would come out part-covered whichever way the tie fell, which is exactly the blur that
    ///     took a box's edges out of this file's reach when its quad grew. Zero is a supported value
    ///     — <c>PathTessellator.FillFringe</c> returns without emitting anything — so this is the
    ///     shipping fill path with its antialiasing turned off, not a second code path.
    /// </remarks>
    static Bitmap RenderPath(Rectangle rectangle, Color4 colour) {
        var list = new DrawList();
        list.BeginFrame();

        var path = new PathBuilder();
        path.AddRectangle(rectangle);

        list.Add(
            new DrawCommand(DrawCommandKind.Path, 0f, 0f, 0f, 0f, colour, 0f, 0f) {
                Offset = list.AddPath(path),
                Length = path.Count
            }
        );

        list.EndFrame();

        var cache = new GlyphFieldCache(new GlyphAtlas(64, 64));
        var builder = new UiGeometryBuilder { Fringe = 0f };
        var geometry = builder.Build(list, cache, new Rectangle(0, 0, Side, Side));

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
