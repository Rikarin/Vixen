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
/// </remarks>
public class PaintFootprintTests {
    /// <summary>A pane 512 render pixels tall.</summary>
    const int Pane = 512;

    /// <summary>A 90° vertical field of view, whose half-angle has a tangent of exactly one.</summary>
    /// <remarks>
    ///     Chosen for that: world units per pixel is then <c>2 · depth / 512</c>, so a hit 256 units
    ///     away is one world unit per pixel and every product below is readable.
    /// </remarks>
    static readonly float Ninety = MathF.PI / 2f;

    /// <summary>A hit facing the camera, so the grazing term is exactly one.</summary>
    /// <param name="depth">How far down the view axis it is.</param>
    /// <returns>The hit.</returns>
    static PaintHit FacingAt(float depth) =>
        new(0, new(1f, 0f, 0f), new(0.5f, 0.5f), new(0f, 0f, depth), new(0f, 0f, -1f), depth);

    /// <summary>A camera at the origin looking down positive Z.</summary>
    /// <returns>The eye.</returns>
    static PaintEye Eye() => PaintEye.Perspective(Vector3.Zero, new(0f, 0f, 1f), Ninety, Pane);

    /// <summary>The ray down the view axis, which every case here casts.</summary>
    static Ray Along() => new(Vector3.Zero, new(0f, 0f, 1f));

    /// <summary>The radius is the screen radius through both conversions, and nothing else.</summary>
    /// <remarks>
    ///     At 256 units the pixel is <c>2 · 256 / 512</c> = one world unit, so a ten-pixel brush is
    ///     ten units of surface, and at sixteen texels per unit that is 160 texels.
    /// </remarks>
    [Theory]
    [InlineData(10f, 16f, 160f)]
    [InlineData(10f, 1f, 10f)]
    [InlineData(2.5f, 16f, 40f)]
    public void The_radius_is_the_screen_radius_through_both_conversions(
        float screenRadius,
        float density,
        float expected
    ) =>
        Assert.Equal(
            expected,
            PaintFootprint.Radius(Eye(), Along(), FacingAt(256f), new(density, density), screenRadius),
            3
        );

    /// <summary>Twice as far away is twice the brush, under a perspective camera and not otherwise.</summary>
    /// <remarks>
    ///     ⚠ <b>The orthographic half is the instrument.</b> A conversion that ignored the camera
    ///     entirely would give the same answer at both depths, which is what the orthographic case
    ///     asserts — so the perspective case's doubling is a statement about the projection rather
    ///     than about arithmetic that happens to scale.
    /// </remarks>
    [Fact]
    public void Depth_doubles_a_perspective_brush_and_leaves_an_orthographic_one_alone() {
        PaintDensity density = new(16f, 16f);

        var near = PaintFootprint.Radius(Eye(), Along(), FacingAt(128f), density, 10f);
        var far = PaintFootprint.Radius(Eye(), Along(), FacingAt(256f), density, 10f);

        Assert.Equal(2f, far / near, 3);

        var flat = PaintEye.Orthographic(Vector3.Zero, new(0f, 0f, 1f), 512f, Pane);

        Assert.Equal(
            PaintFootprint.Radius(flat, Along(), FacingAt(128f), density, 10f),
            PaintFootprint.Radius(flat, Along(), FacingAt(256f), density, 10f),
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
        var isometric = PaintFootprint.Radius(Eye(), Along(), FacingAt(256f), new(16f, 16f), 10f);
        var squashed = PaintFootprint.Radius(Eye(), Along(), FacingAt(256f), new(16f, 4f), 10f);

        Assert.Equal(160f, isometric, 3);
        Assert.Equal(80f, squashed, 3);
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
        PaintDensity density = new(16f, 16f);

        // 60° off the view axis: cos θ = ½, so the area-equivalent radius is 1/√½ = √2 times the
        // face-on one.
        PaintHit tilted = new(
            0,
            new(1f, 0f, 0f),
            new(0.5f, 0.5f),
            new(0f, 0f, 256f),
            Vector3.Normalize(new(MathF.Sqrt(3f) / 2f, 0f, -0.5f)),
            256f
        );

        var facing = PaintFootprint.Radius(Eye(), Along(), FacingAt(256f), density, 10f);

        Assert.Equal(MathF.Sqrt(2f), PaintFootprint.Radius(Eye(), Along(), tilted, density, 10f) / facing, 3);

        // Edge-on, where the honest answer is unbounded and the useful one is not.
        PaintHit silhouette = new(
            0,
            new(1f, 0f, 0f),
            new(0.5f, 0.5f),
            new(0f, 0f, 256f),
            new(1f, 0f, 0f),
            256f
        );

        var grazing = PaintFootprint.Radius(Eye(), Along(), silhouette, density, 10f);

        Assert.True(float.IsFinite(grazing));
        Assert.Equal(facing / MathF.Sqrt(PaintFootprint.GrazingFloor), grazing, 3);
    }

    /// <summary>A triangle with no area in the atlas gets no radius rather than a plausible one.</summary>
    [Fact]
    public void An_unmeasurable_chart_gets_no_radius() {
        Assert.Equal(0f, PaintFootprint.Radius(Eye(), Along(), FacingAt(256f), new(16f, 0f), 10f), 6);
        Assert.Equal(0f, PaintFootprint.Radius(Eye(), Along(), PaintHit.None, new(16f, 16f), 10f), 6);
    }
}
