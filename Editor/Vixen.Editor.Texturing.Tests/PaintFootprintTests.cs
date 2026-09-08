// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Texturing.Painting;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     A brush radius in screen pixels, turned into one in texels of the atlas.
/// </summary>
/// <remarks>
///     <para>
///         <b>Issue <a href="https://github.com/Rikarin/Vixen/issues/574">#574</a>'s first correction,
///         with a third step it does not name.</b> That comment says the conversion is two multiplies
///         and that <c>UvDensity</c> is only one of them; the missing one is screen to world at the
///         hit's depth. Between those two is a third that neither doc 48 nor the issue mentions and
///         that every case below isolates: the <b>grazing stretch</b>, because a disc on the screen
///         lands on a tilted surface as an ellipse.
///     </para>
///     <para>
///         ⚠ <b>Every expected number here is arithmetic, not a recorded output.</b> The camera's
///         field of view, the pane height and the density are all chosen so the answer is a round
///         number a reader can check in their head — which is what stops this file being a
///         regression harness for whatever the code happened to do the day it was written.
///     </para>
///     <para>
///         ⚠ <b>And every hit and every density is a real one, off a real triangle, which it was not
///         until <a href="https://github.com/Rikarin/Vixen/issues/1075">#1075</a>.</b> A hit used to
///         be written here with a normal in it and a density with two scalars in it, and the two
///         were free to describe different triangles — which is exactly what stopped the tilt and
///         the layout being composable. <see cref="Facet" /> builds one triangle and asks
///         <c>PaintProjection</c> for both, so a fixture cannot express a state production cannot
///         reach.
///     </para>
///     <para>
///         ⚠ <b>This file measures the brush's <em>size</em> and deliberately not its shape.</b>
///         Composing the tilt with the layout leaves every number below unchanged — the tilt
///         matrix's determinant is <c>1 / cos θ</c>, so the equal-area radius is what it always was
///         — which makes these cases the check that the fix moved the shape and nothing else.
///         <c>PaintEllipseTests</c> is where the shape is asserted.
///     </para>
/// </remarks>
public class PaintFootprintTests {
    /// <summary>A pane 512 render pixels tall.</summary>
    const int Pane = 512;

    /// <summary>The atlas the layouts below are measured in.</summary>
    const int Atlas = 512;

    /// <summary>How wide across the tilt axis the facet is, in world units.</summary>
    /// <remarks>Big enough that the ray down the view axis lands well inside it at every tilt.</remarks>
    const float Extent = 4f;

    /// <summary>A 90° vertical field of view, whose half-angle has a tangent of exactly one.</summary>
    /// <remarks>
    ///     Chosen for that: world units per pixel is then <c>2 · depth / 512</c>, so a hit 256 units
    ///     away is one world unit per pixel and every product below is readable.
    /// </remarks>
    static readonly float Ninety = MathF.PI / 2f;

    /// <summary>The radius is the screen radius through both conversions, and nothing else.</summary>
    /// <remarks>
    ///     At 256 units the pixel is <c>2 · 256 / 512</c> = one world unit, so a ten-pixel brush is
    ///     ten units of surface, and at sixteen texels per unit that is 160 texels.
    /// </remarks>
    /// <param name="screenRadius">How wide the brush is, in render pixels.</param>
    /// <param name="density">How many texels of the atlas one unit of surface buys, both ways.</param>
    /// <param name="expected">The radius in texels.</param>
    [Theory]
    [InlineData(10f, 16f, 160f)]
    [InlineData(10f, 1f, 10f)]
    [InlineData(2.5f, 16f, 40f)]
    public void The_radius_is_the_screen_radius_through_both_conversions(
        float screenRadius,
        float density,
        float expected
    ) =>
        Assert.Equal(expected, Radius(Eye(), 256f, 0f, density, density, screenRadius), 3);

    /// <summary>Twice as far away is twice the brush, under a perspective camera and not otherwise.</summary>
    /// <remarks>
    ///     ⚠ <b>The orthographic half is the instrument.</b> A conversion that ignored the camera
    ///     entirely would give the same answer at both depths, which is what the orthographic case
    ///     asserts — so the perspective case's doubling is a statement about the projection rather
    ///     than about arithmetic that happens to scale.
    /// </remarks>
    [Fact]
    public void Depth_doubles_a_perspective_brush_and_leaves_an_orthographic_one_alone() {
        var near = Radius(Eye(), 128f, 0f, 16f, 16f, 10f);
        var far = Radius(Eye(), 256f, 0f, 16f, 16f, 10f);

        Assert.Equal(2f, far / near, 3);

        var flat = PaintEye.Orthographic(Vector3.Zero, new(0f, 0f, 1f), 512f, Pane);

        Assert.Equal(
            Radius(flat, 128f, 0f, 16f, 16f, 10f),
            Radius(flat, 256f, 0f, 16f, 16f, 10f),
            4
        );
    }

    /// <summary>A stretched chart and an isometric one do not get the same brush.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>⚠ Doc 48 § M9's "looks right on a test cube", as an assertion.</b> Both hits are
    ///         the same point at the same depth under the same camera with the same brush; only the
    ///         layout under them differs. A conversion sized from the hit texel — or from anything
    ///         that is not the triangle's Jacobian — cannot tell them apart, and would paint a
    ///         quarter-sized stroke on the squashed half of an ordinary cylindrical unwrap.
    ///     </para>
    ///     <para>
    ///         The squashed chart carries a quarter of the coordinate range vertically, so its
    ///         area-equivalent density is <c>√(16 · 4)</c> = 8 against 16 — a brush exactly half the
    ///         size, computed rather than measured.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_squashed_chart_gets_a_different_brush_from_an_isometric_one() {
        Assert.Equal(160f, Radius(Eye(), 256f, 0f, 16f, 16f, 10f), 3);
        Assert.Equal(80f, Radius(Eye(), 256f, 0f, 16f, 4f, 10f), 3);
    }

    /// <summary>A surface tilted away from the viewer gets a bigger brush, up to a cap.</summary>
    /// <remarks>
    ///     ⚠ <b>The cap is the assertion that matters.</b> At the silhouette the true ellipse is
    ///     unbounded — a screen disc on an edge-on surface covers an unbounded strip of it — so a
    ///     conversion without <c>PaintFootprint.GrazingFloor</c> answers with infinity for the last
    ///     ray of every stroke that reaches round a shape, and an infinite radius makes
    ///     <c>PaintStroke</c> scan the whole atlas for one stamp. The 60° case is what proves the
    ///     term is applied at all rather than only clamped.
    /// </remarks>
    [Fact]
    public void A_tilted_surface_stretches_the_brush_and_a_silhouette_does_not_unbound_it() {
        var facing = Radius(Eye(), 256f, 0f, 16f, 16f, 10f);

        // 60° off the view axis: cos θ = ½, so the area-equivalent radius is 1/√½ = √2 times the
        // face-on one — and it stays that whether the tilt is a shape or an area factor, which is
        // why this case survived #1075 unchanged.
        Assert.Equal(MathF.Sqrt(2f), Radius(Eye(), 256f, MathF.PI / 3f, 16f, 16f, 10f) / facing, 3);

        // ⚠ 87° and not 90°: at 90° the ray lies in the facet's own plane and there is no hit to
        // measure, which is the honest reason the floor exists at all. The cosine there is 0.052,
        // comfortably under the floor.
        var grazing = Radius(Eye(), 256f, 87f * (MathF.PI / 180f), 16f, 16f, 10f);

        Assert.True(float.IsFinite(grazing));

        // ⚠ The ratio and not the texel count: 506 texels carries its float noise in the third
        // decimal, and an absolute tolerance there would be a fact about the arithmetic's rounding
        // rather than about the floor.
        Assert.Equal(1f / MathF.Sqrt(PaintFootprint.GrazingFloor), grazing / facing, 4);
    }

    /// <summary>A triangle with no area in the atlas gets no radius rather than a plausible one.</summary>
    [Fact]
    public void An_unmeasurable_chart_gets_no_radius() {
        Assert.Equal(0f, Radius(Eye(), 256f, 0f, 16f, 0f, 10f), 6);

        var (_, density) = Facet(256f, 0f, 16f, 16f);

        Assert.Equal(0f, PaintFootprint.Radius(Eye(), Along(), PaintHit.None, density, 10f), 6);
    }

    /// <summary>A camera at the origin looking down positive Z.</summary>
    /// <returns>The eye.</returns>
    static PaintEye Eye() => PaintEye.Perspective(Vector3.Zero, new(0f, 0f, 1f), Ninety, Pane);

    /// <summary>The ray down the view axis, which every case here casts.</summary>
    static Ray Along() => new(Vector3.Zero, new(0f, 0f, 1f));

    /// <summary>The whole conversion, off one real triangle.</summary>
    /// <param name="eye">The camera.</param>
    /// <param name="depth">How far down the view axis the facet is.</param>
    /// <param name="tilt">How far off face-on it is turned, in radians, about the vertical.</param>
    /// <param name="major">How many texels one unit along the tilt's own direction buys.</param>
    /// <param name="minor">And one unit across it.</param>
    /// <param name="screenRadius">How wide the brush is, in render pixels.</param>
    /// <returns>The radius in texels.</returns>
    static float Radius(PaintEye eye, float depth, float tilt, float major, float minor, float screenRadius) {
        var (hit, density) = Facet(depth, tilt, major, minor);

        return PaintFootprint.Radius(eye, Along(), hit, density, screenRadius);
    }

    /// <summary>One triangle in front of the camera, and what the projection says about it.</summary>
    /// <param name="depth">How far down the view axis its centre is.</param>
    /// <param name="tilt">How far off face-on it is turned, in radians, about the vertical.</param>
    /// <param name="major">How many texels one unit along the tilt's own direction buys.</param>
    /// <param name="minor">And one unit across it.</param>
    /// <returns>The hit the view ray makes on it, and the map from its plane to the atlas.</returns>
    /// <remarks>
    ///     ⚠ <b>The two in-plane edges are orthogonal and unit, so the layout below <em>is</em> the
    ///     map.</b> <c>Density</c> builds its basis from the triangle's first edge and the in-plane
    ///     perpendicular to it, which for this facet are exactly the tilt's direction and the axis
    ///     it turns about — so <paramref name="major" /> and <paramref name="minor" /> read out as
    ///     the singular values with no basis in between for a reader to have to compose.
    /// </remarks>
    static (PaintHit Hit, PaintDensity Density) Facet(float depth, float tilt, float major, float minor) {
        var (sin, cos) = MathF.SinCos(tilt);

        // Turned about the vertical, so the facet leans away from the camera along its first edge.
        Vector3 along = new(cos, 0f, sin);

        // Downwards rather than up, so the winding puts the front face towards the camera.
        Vector3 across = new(0f, -1f, 0f);

        var origin = new Vector3(0f, 0f, depth) - along - across;
        Vector2 corner = new(0.5f, 0.5f);

        var projection = PaintProjection.Over(
            [origin, origin + (along * Extent), origin + (across * Extent)],
            [
                corner,
                corner + new Vector2(Extent * major / Atlas, 0f),
                corner + new Vector2(0f, Extent * minor / Atlas)
            ],
            [0, 1, 2]
        );

        Assert.True(projection.TryHit(Along(), out var hit), "the view ray missed the facet.");

        return (hit, projection.Density(hit.Triangle, Atlas, Atlas));
    }
}
