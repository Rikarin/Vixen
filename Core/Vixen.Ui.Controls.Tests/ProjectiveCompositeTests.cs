// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Rendering;
using Vixen.Ui.Testing.Visual;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>
///     A composited group under a homography, drawn by the software rasteriser: the texel under a
///     pixel is the one the projection puts there, and a corner behind the eye is cut, not reflected.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 43 § A7, issue #548 — the software half, against a closed-form oracle.</b> The
///         geometry is built by hand rather than from a stylesheet, because nothing parses a
///         <c>perspective()</c> yet (#550): <see cref="UiTransform" /> has been able to express one
///         since #547, and what this file asserts is that the two things downstream of it — the
///         composite quad's vertices and the rasteriser that reads them — turn that matrix into the
///         right picture.
///     </para>
///     <para>
///         ⚠ <b>Every assertion is a pixel chosen to fail for the linear interpolation this
///         replaces, and a second one chosen to fail the other way.</b> The image of a square's
///         centre under a homography is the intersection of its image diagonals, which is on the
///         diagonal the two triangles share at a screen parameter that is <i>not</i> a half; an
///         interpolator that ignores <c>w</c> samples the texel at that parameter's linear position
///         instead, fourteen pixels away in the fixture below. So a marker painted at the centre
///         appears at the centre's image on the perspective-correct path and at the linear one's
///         wrong point on the other, and the two points are far enough apart that each test can ask
///         for ink at one and none at the other.
///     </para>
///     <para>
///         ⚠ <b>The software rasteriser and not the device, and <c>UiCompositingTests</c> is what
///         stops that being a hole</b> — the same split <c>TransformPaintTests</c> makes. What the
///         right picture <i>is</i> can be asserted against arithmetic and needs no GPU; that the
///         hardware draws the same one is a different claim and lives over there.
///     </para>
/// </remarks>
public class ProjectiveCompositeTests {
    const int Side = 200;

    static readonly Rectangle Viewport = new(0, 0, Side, Side);

    /// <summary>The group's box, whose centre is at (100, 100).</summary>
    static readonly Rectangle Box = new(40, 40, 120, 120);

    static readonly Vector2 Centre = new(100f, 100f);

    /// <summary>The group's field, dark enough that the marker's white is unmistakable.</summary>
    static readonly Color4 Field = new(0.2f, 0.2f, 0.2f, 1f);

    /// <summary>A perspective-shaped homography: <c>w</c> grows with <i>y</i>, so far edges shrink.</summary>
    static UiTransform Perspective(float k) => UiTransform.Identity with { M23 = k };

    /// <summary>
    ///     A white marker at the group's centre lands at the centre's image, which is not where a
    ///     linear interpolation puts it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The transform is <c>Perspective(k)</c> re-centred on the box, so the centre is a fixed
    ///         point and its image is (100, 100) — the diagonal intersection of the projected quad.
    ///         With <c>k = 0.004</c> the top-left corner has <c>w = 0.76</c> and the bottom-right
    ///         <c>w = 1.24</c>, which puts them at (21.1, 21.1) and (148.4, 148.4); the centre's image
    ///         is therefore at screen parameter 0.62 along that diagonal, and a linear interpolator
    ///         samples the element point at 0.62 of the way from corner to corner — (114.4, 114.4),
    ///         fourteen pixels from the marker. The linear interpolator paints the marker where its
    ///         own parameter is a half instead: (84.75, 84.75).
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves.</b> Ink at (100, 100) and none at (84, 84) is the perspective-correct
    ///         picture; the linear one has them the other way round. The same probe pair under an
    ///         affine is the control: there the two interpolations agree, the marker is at (100, 100)
    ///         on either, and the test is shown to be measuring the transform rather than itself.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_centre_of_a_group_under_a_perspective_is_drawn_at_the_centres_image() {
        var projective = Perspective(0.004f).About(Centre);

        // ⚠ No translation, because `About` re-centres the linear part and keeps the translation, and
        // the control's whole point is that the centre stays where the probe is.
        var affine = new UiTransform(1.1f, 0.1f, -0.15f, 0.9f, 0f, 0f).About(Centre);

        var seen = Render(projective);
        var control = Render(affine);

        // The control first: the affine's centre is fixed too, and there is no wrong point to check
        // against because both interpolations agree — so the marker is simply at the centre.
        Assert.True(IsMarker(control, 100, 100), "the affine control did not paint the marker at the centre.");
        Assert.False(IsMarker(control, 84, 84), "the affine control painted the marker off-centre.");

        // The perspective: the marker is at the centre's image, and NOT where a linear interpolation
        // of the texture coordinate would have put it.
        Assert.True(IsMarker(seen, 100, 100), "the marker is not at the image of the centre, so the texel there is the wrong one.");
        Assert.False(IsMarker(seen, 84, 84), "the marker is where a linear interpolation would put it.");
    }

    /// <summary>
    ///     A triangle with one corner behind the eye is cut against the eye plane and drawn on the
    ///     side of the edge the eye can see, not as the finite triangle its reflected corner spans.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Built as bare geometry rather than through the builder, because a corner behind the
    ///         eye is a vertex with a negative <c>w</c> and a projected position reflected through the
    ///         vanishing point — which is what <c>UiGeometryBuilder.Quad</c> writes and what the
    ///         rasteriser is handed. The homogeneous corners are <c>A = (20, 20, 1)</c>,
    ///         <c>B = (200, 20, 1)</c> and <c>C = (−55, −150, −0.5)</c>; <c>C</c> projects to
    ///         (110, 300), so the naive picture is the triangle A–B–(110, 300), below the edge A–B.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The correct picture is on the other side of that edge and runs to infinity.</b>
    ///         Every point of the plane triangle with <c>w &gt; 0</c> is <c>a·A + b·B + c·C</c> with
    ///         <c>a + b &gt; c / 2</c>, and its projected <i>y</i> is <c>20 − 140c / (a + b − c/2)</c>,
    ///         which is at most 20: the whole visible region lies above the edge, and it reaches the
    ///         top of the picture. So (110, 5) is inked — it is the point <c>a = b</c>,
    ///         <c>c ≈ 0.10</c> — and (110, 150), squarely inside the reflected triangle, is not. Both
    ///         are exactly wrong for a rasteriser that took a bound over the three projected positions.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_corner_behind_the_eye_is_clipped_and_the_reflected_triangle_is_not_drawn() {
        UiVertex[] vertices = [
            new(new(20f, 20f), Vector2.Zero, Color4.White, new(1f, 0f, 0f, 0f)),
            new(new(200f, 20f), Vector2.Zero, Color4.White, new(1f, 0f, 0f, 0f)),
            new(new(110f, 300f), Vector2.Zero, Color4.White, new(1f, 0f, 0f, 0f), -0.5f)
        ];

        uint[] indices = [0, 1, 2];

        var geometry = new UiGeometry(vertices, indices, [new UiDraw(BatchKind.PathFill, 0, 3, 0, Viewport)], []);
        var pixels = SoftwareUiRasterizer.RenderLinear(geometry, new GlyphAtlas(64, 64), Side, Side, Color4.Transparent);

        Assert.True(Alpha(pixels, 110, 5) > 0.99f, "the visible part of the triangle, above the edge A–B, was not drawn.");
        Assert.True(Alpha(pixels, 110, 150) < 0.01f, "the reflected triangle was drawn, which is the naive rasteriser's picture.");

        // And the whole of the in-front edge is inked, with nothing below it: the cut is against the
        // eye plane and not against something that happens to pass through A and B.
        Assert.True(Alpha(pixels, 60, 19) > 0.99f);
        Assert.True(Alpha(pixels, 60, 21) < 0.01f);
    }

    /// <summary>Draws the field with the marker at its centre, composited under <paramref name="placed" />.</summary>
    static float[] Render(UiTransform placed) {
        var list = new DrawList();

        list.BeginFrame();

        list.Add(
            new DrawCommand(DrawCommandKind.LayerPush, Box.X, Box.Y, Box.Width, Box.Height, Color4.White, 0f, 0f) {
                Transform = placed
            }
        );

        list.Add(new DrawCommand(DrawCommandKind.Rectangle, Box.X, Box.Y, Box.Width, Box.Height, Field, 0f, 0f));
        list.Add(new DrawCommand(DrawCommandKind.Rectangle, Centre.X - 3f, Centre.Y - 3f, 6f, 6f, Color4.White, 0f, 0f));
        list.Add(new DrawCommand(DrawCommandKind.LayerPop, 0f, 0f, 0f, 0f, Color4.White, 0f, 0f));
        list.EndFrame();

        var geometry = new UiGeometryBuilder().Build(list, new GlyphFieldCache(new GlyphAtlas(256, 256)), Viewport);

        // The instrument: a group was opened for the transform, so what is drawn below is its surface
        // composited through the quad and not the rectangles drawn directly.
        Assert.Single(geometry.Layers);

        return SoftwareUiRasterizer.RenderLinear(geometry, new GlyphAtlas(64, 64), Side, Side, new Color4(0f, 0f, 0f, 1f));
    }

    /// <summary>Whether the pixel is the marker's white rather than the field's grey.</summary>
    static bool IsMarker(float[] pixels, int x, int y) => pixels[((y * Side) + x) * 4] > 0.6f;

    static float Alpha(float[] pixels, int x, int y) => pixels[(((y * Side) + x) * 4) + 3];
}
