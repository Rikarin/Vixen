// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Texturing.Painting;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     The stamp on a stretched chart: an ellipse, its long axis where the layout puts it.
/// </summary>
/// <remarks>
///     <para>
///         <b>Issue <a href="https://github.com/Rikarin/Vixen/issues/1064">#1064</a>.</b>
///         <c>PaintProjection.Density</c> has answered with two numbers since it was written, and the
///         stamp collapsed them to their geometric mean — so a chart stretched four to one was
///         painted with a brush twice too wide in one direction and twice too narrow in the other,
///         right in area and wrong in both directions.
///     </para>
///     <para>
///         ⚠ <b>Every fixture here has stretched or rotated coordinates, and that is the whole
///         instrument.</b> On a cube — and on <c>PaintProjectionTests.Plane</c>, and on any layout
///         whose triangles are conformal — the ellipse <em>is</em> a circle and every assertion below
///         is satisfied by the code this replaces. That is how the defect shipped: it is invisible in
///         the fixture anybody writes first.
///     </para>
///     <para>
///         ⚠ <b>And the axis-aligned fixtures cannot see the orientation half at all.</b> The long
///         axis in the atlas is the map's <em>left</em> singular vector; the issue said the right
///         one. They differ on any non-conformal map and they agree — both at zero, or both at a
///         right angle — on every fixture whose stretch runs down a coordinate axis, which is what
///         <see cref="A_rotated_stretch_reports_the_axis_it_is_actually_stretched_along" /> exists
///         to be.
///     </para>
/// </remarks>
public class PaintEllipseTests {
    const uint Opaque = 0xFF0000FFu;

    /// <summary>The atlas the density cases measure in.</summary>
    const int Size = 64;

    /// <summary>How far off the plane the rays here start.</summary>
    const float Away = 10f;

    /// <summary>The atlas the painted cases stamp into, big enough for a 4:1 ellipse of radius 16.</summary>
    const int Canvas = 128;

    /// <summary>A unit right triangle in the z = 0 plane whose layout is a rotated, squashed map of it.</summary>
    /// <param name="angle">Which way the stretched axis points in the atlas, in radians.</param>
    /// <param name="along">How much coordinate range one unit of surface buys along it.</param>
    /// <param name="across">And across it.</param>
    /// <returns>The projection.</returns>
    /// <remarks>
    ///     ⚠ <b>The two edges are the world x and y axes on purpose.</b> <c>Density</c> builds its
    ///     plane basis from the triangle's own first edge, so a fixture whose first edge is the x
    ///     axis makes that basis the world's — which is what lets the expected angle below be the
    ///     angle written here rather than one composed with a basis the fixture cannot see.
    /// </remarks>
    static PaintProjection Rotated(float angle, float along, float across) {
        var (sin, cos) = MathF.SinCos(angle);

        Vector2 Layout(float x, float y) =>
            new(0.5f + ((x * along * cos) - (y * across * sin)), 0.5f + ((x * along * sin) + (y * across * cos)));

        return PaintProjection.Over(
            [new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f)],
            [Layout(0f, 0f), Layout(1f, 0f), Layout(0f, 1f)],
            [0, 1, 2]
        );
    }

    /// <summary>A ray straight down at a point of that triangle.</summary>
    /// <param name="x">Where, along its first axis.</param>
    /// <param name="y">Along its second.</param>
    /// <returns>The ray.</returns>
    static Ray Down(float x, float y) => new(new(x, y, Away), new(0f, 0f, -1f));

    /// <summary>A camera whose pixel is one world unit, so a screen radius reads as a surface radius.</summary>
    static PaintEye Eye() => PaintEye.Orthographic(new(0.5f, 0.5f, Away), new(0f, 0f, -1f), Size, Size);

    /// <summary>⚠ The long axis of the footprint is the one the layout actually stretches.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The one case an axis-aligned fixture cannot be.</b> Write the triangle's
    ///         surface-to-atlas map as <c>M = UΣVᵀ</c>. The ellipse a screen disc becomes lives in the
    ///         atlas, so its axes are <c>U</c>'s columns; <c>V</c>'s columns are directions on the
    ///         <em>surface</em> and answer a different question. This fixture is <c>M = R·S</c> with
    ///         <c>S</c> diagonal, so <c>U = R</c> and <c>V = I</c> — the two answers are 30° and 0°,
    ///         and nothing that reads the wrong one can pass.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Squash the same amount down a coordinate axis instead and both answers are
    ///         zero</b>, which is <see cref="A_squash_down_a_coordinate_axis_points_along_it" />
    ///         below — kept precisely so the pair reads as the instrument rather than as two cases of
    ///         the same thing.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(0f)]
    [InlineData(MathF.PI / 6f)]
    [InlineData(-MathF.PI / 3f)]
    public void A_rotated_stretch_reports_the_axis_it_is_actually_stretched_along(float angle) {
        var projection = Rotated(angle, 0.5f, 0.125f);

        Assert.True(projection.TryHit(Down(0.2f, 0.2f), out var hit));

        var density = projection.Density(hit.Triangle, Size, Size);

        Assert.Equal(Size * 0.5f, density.Major, 3);
        Assert.Equal(Size * 0.125f, density.Minor, 3);
        Assert.Equal(4f, density.Anisotropy, 3);

        // The ellipse has no front and no back, so an axis at φ and one at φ + π are the same axis.
        Assert.Equal(0f, MathF.Sin(density.Orientation - angle), 3);
    }

    /// <summary>A stretch down a coordinate axis points along that axis, which is the easy half.</summary>
    /// <remarks>
    ///     ⚠ <b>Here so that the rotated case above is legible as the discriminating one.</b> This
    ///     assertion is satisfied by reading either singular vector, and by several answers that are
    ///     not a singular vector at all.
    /// </remarks>
    [Theory]
    [InlineData(0.25f, 0f)]
    [InlineData(4f, MathF.PI / 2f)]
    public void A_squash_down_a_coordinate_axis_points_along_it(float squash, float expected) {
        var projection = PaintProjectionTests.Stretched(squash);

        Assert.True(projection.TryHit(new(new(0.5f, 0.5f, Away), new(0f, 0f, -1f)), out var hit));

        var density = projection.Density(hit.Triangle, Size, Size);

        Assert.Equal(0f, MathF.Sin(density.Orientation - expected), 3);
    }

    /// <summary>⚠ The footprint a projector hands over carries the chart's shape and not only its area.</summary>
    /// <remarks>
    ///     <b>The caller half.</b> <c>PaintProjector.Begin</c> is what a viewport asks at pointer-down,
    ///     and what it returns is what goes on <c>PaintBrush.Radius</c>, <c>Aspect</c> and
    ///     <c>AspectAngle</c>. Before this it returned one number, so every stamp of every projected
    ///     drag was a disc whatever the layout did.
    /// </remarks>
    [Fact]
    public void A_projector_reports_the_chart_it_landed_on_as_an_ellipse() {
        var projection = Rotated(MathF.PI / 6f, 0.5f, 0.125f);
        PaintProjector projector = new(projection, Size, Size);

        Assert.True(projector.Begin(Eye(), Down(0.2f, 0.2f), 4f, out var footprint));

        Assert.True(footprint.IsMeasurable);
        Assert.Equal(4f, footprint.Aspect, 3);
        Assert.Equal(0f, MathF.Sin(footprint.Angle - (MathF.PI / 6f)), 3);

        // The size is unchanged by the shape: four pixels of a one-unit-per-pixel camera is four
        // units of surface, and the equal-area density here is √(32 · 8) = 16 texels per unit.
        Assert.Equal(4f * MathF.Sqrt(Size * 0.5f * (Size * 0.125f)), footprint.Radius, 2);
    }

    /// <summary>⚠ A 4:1 stamp reaches twice as far along its long axis and half as far across it.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The defect itself, measured on the painted texels rather than on the arithmetic.</b>
    ///         The old stamp was a disc of the equal-area radius, so both of these numbers were
    ///         <c>radius</c> — the brush painted past where the artist swept in one direction by the
    ///         full square root of the anisotropy, and left a sliver in the other.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It also proves the footprint rectangle grew with the ellipse.</b>
    ///         <c>PaintBrush.FootprintOf</c> is what the stamp loop iterates; a footprint still sized
    ///         from the equal-area radius would clip the long axis at exactly <c>radius</c> and the
    ///         painted shape would be a disc with two flat ends — which reads as a smaller brush and
    ///         not as a broken one.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_stretched_stamp_paints_an_ellipse_and_not_a_disc() {
        const float Radius = 16f;

        var stretched = Painted(PaintStrokeTests.Hard(Radius) with { Aspect = 4f, AspectAngle = 0f });
        var round = Painted(PaintStrokeTests.Hard(Radius));

        // √4 = 2, so 32 along the long axis and 8 across it. Half a texel of slack either way: the
        // boundary is tested at texel centres and the last covered one straddles it.
        Assert.Equal(Radius * 2f, stretched.Along, 0.6d);
        Assert.Equal(Radius / 2f, stretched.Across, 0.6d);

        Assert.Equal(Radius, round.Along, 0.6d);
        Assert.Equal(Radius, round.Across, 0.6d);

        // ⚠ And the area is the one thing that must *not* change, which is what makes the radius
        // still mean what `PaintDensity.Area` measured: the semi-axes are r√a and r/√a, whose
        // product is r² for every aspect. Within a percent — the two shapes discretise differently.
        Assert.Equal(1d, (double)stretched.Texels / round.Texels, 0.02d);
    }

    /// <summary>The ellipse turns with its angle rather than staying flat in the atlas.</summary>
    /// <remarks>
    ///     ⚠ <b>The case a stamp that read <c>Aspect</c> and ignored <c>AspectAngle</c> would
    ///     pass.</b> Squeezing the offset along a fixed axis is most of the arithmetic and is right
    ///     for every chart whose stretch happens to run along the atlas's own axes — which is the
    ///     fixture above. At 90° the two semi-axes swap, so the same brush must reach twice as far
    ///     up and half as far across.
    /// </remarks>
    [Fact]
    public void A_stretched_stamp_turns_with_the_chart() {
        const float Radius = 16f;

        var turned = Painted(PaintStrokeTests.Hard(Radius) with { Aspect = 4f, AspectAngle = MathF.PI / 2f });

        Assert.Equal(Radius / 2f, turned.Along, 0.6d);
        Assert.Equal(Radius * 2f, turned.Across, 0.6d);
    }

    /// <summary>A brush left at its default aspect stamps exactly what it always did.</summary>
    /// <remarks>
    ///     ⚠ <b>Zero and one both mean a disc, and the zero is the one that matters.</b>
    ///     <c>PaintBrush</c> is a struct: <c>new()</c> and every <c>with</c> written before
    ///     <c>Aspect</c> existed leave it at zero, and an aspect of zero read literally squeezes the
    ///     stamp to nothing — a brush that silently paints nowhere, in every 2D stroke in the editor.
    /// </remarks>
    [Fact]
    public void An_unset_aspect_is_a_disc_rather_than_a_collapsed_ellipse() {
        var unset = Painted(PaintStrokeTests.Hard(12f) with { Aspect = 0f });
        var one = Painted(PaintStrokeTests.Hard(12f) with { Aspect = 1f });

        Assert.True(unset.Texels > 0, "an unset aspect painted nothing at all.");
        Assert.Equal(one.Texels, unset.Texels);
        Assert.Equal(one.Along, unset.Along, 3);
    }

    /// <summary>What one stamp of a brush covered, as reaches and a texel count.</summary>
    /// <param name="brush">The brush, whose radius and aspect are the subject.</param>
    /// <returns>The extents from the stamp's centre and how many texels it painted.</returns>
    static (double Along, double Across, int Texels) Painted(PaintBrush brush) {
        PaintImage image = new(Canvas, Canvas);
        PaintStroke stroke = new(image, PaintCoverage.Everywhere(Canvas, Canvas), brush, Opaque, gutter: 0);

        stroke.MoveTo(new(Canvas / 2f, Canvas / 2f));

        var along = 0d;
        var across = 0d;
        var texels = 0;

        for (var y = 0; y < Canvas; y++) {
            for (var x = 0; x < Canvas; x++) {
                if (image[(y * Canvas) + x] == 0u) {
                    continue;
                }

                texels++;

                if (y == Canvas / 2) {
                    along = Math.Max(along, Math.Abs((x + 0.5) - (Canvas / 2f)));
                }

                if (x == Canvas / 2) {
                    across = Math.Max(across, Math.Abs((y + 0.5) - (Canvas / 2f)));
                }
            }
        }

        return (along, across, texels);
    }
}
