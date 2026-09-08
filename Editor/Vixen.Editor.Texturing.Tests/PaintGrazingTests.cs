// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Texturing.Painting;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     The grazing tilt and the chart's stretch, as one ellipse.
/// </summary>
/// <remarks>
///     <para>
///         <b>Issue <a href="https://github.com/Rikarin/Vixen/issues/1075">#1075</a>.</b> Two things
///         turn an artist's screen disc into an ellipse in the atlas.
///         <a href="https://github.com/Rikarin/Vixen/issues/1064">#1064</a> fixed the layout's half;
///         the tilt's half was still collapsed to <c>surface /= √cos θ</c>, which keeps the tilt
///         ellipse's <em>area</em> and throws its <em>shape</em> away. At 60° off the normal that
///         ellipse is 2:1 — the same order a 4:1 chart contributes — and it is worst at the
///         silhouette, which is where every stroke reaching round the far side of a shape is made.
///     </para>
///     <para>
///         ⚠ <b>The oracle is built from the triangle's own corners and never from
///         <c>PaintDensity</c>.</b> <see cref="Sampled" /> walks the rim of the screen disc, slides
///         each point down the ray onto the triangle's plane, writes it in the triangle's two edge
///         vectors, and reads off the atlas offset from the triangle's three texture coordinates.
///         That is the definition of the footprint, in the mesh data, with no basis, no singular
///         value and no matrix product anywhere in it — so it can disagree with the production
///         arithmetic, which a fixture that composed the same two 2×2s could not.
///     </para>
///     <para>
///         ⚠ <b>And the tilt axis is deliberately <em>not</em> the chart's stretch axis, which is
///         the whole discriminating power of the file.</b> When the two coincide the composed
///         ellipse is exactly the product of the two aspects, so an implementation that multiplied
///         the scalars passes — and that is the near miss, because it is one line and is right on
///         every fixture whose tilt runs down a coordinate axis of the layout. ⚠ <b>The arithmetic
///         actually replaced was a step short of even that</b>: it reported
///         <c>PaintDensity.Anisotropy</c>, the chart's aspect alone, so the tilt contributed nothing
///         to the shape at any angle.
///         <see cref="A_tilt_down_the_charts_own_stretch_axis_is_the_product_of_the_two_aspects" />
///         is that case, kept beside the others so the pair reads as the instrument rather than as
///         two cases of one thing.
///     </para>
/// </remarks>
public class PaintGrazingTests {
    /// <summary>The atlas the layouts here are measured in.</summary>
    const int Atlas = 512;

    /// <summary>A pane 512 render pixels tall.</summary>
    const int Pane = 512;

    /// <summary>How far down the view axis the facet's centre sits.</summary>
    const float Depth = 8f;

    /// <summary>How long the facet's two edges are, in world units.</summary>
    const float Extent = 4f;

    /// <summary>The brush, in render pixels — which is world units under the camera below.</summary>
    const float Brush = 1f;

    /// <summary>How many points of the screen disc's rim the oracle walks.</summary>
    /// <remarks>
    ///     ⚠ <b>Sized by the <em>angle</em> it has to resolve rather than by the extents.</b> The
    ///     semi-axes are extrema, so their sampling error falls as the square of the step and is
    ///     already invisible at a few hundred points; the long axis's <em>direction</em> is only as
    ///     good as the step itself. 65536 puts that at 1e-4 radians, two orders under what is
    ///     asserted.
    /// </remarks>
    const int Samples = 1 << 16;

    /// <summary>⚠ The composed ellipse is neither factor alone and neither is it their product.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The defect, measured against a footprint computed from the mesh.</b> A 4:1 chart
    ///         whose stretch runs 35° off the tilt's direction, viewed 55° off the normal: the tilt
    ///         is 1/cos 55° = 1.743:1, and the two compose to 5.49:1 rather than to the 6.97:1 the
    ///         scalars multiply to or the 4:1 the layout alone gives. All three are far enough apart
    ///         that no tolerance here can confuse them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The angle is the half that a scalar multiply cannot even express.</b> The
    ///         composed ellipse's long axis is 6° off the chart's own, because the tilt turns it;
    ///         the old arithmetic reported <c>PaintDensity.Orientation</c> unchanged whatever the
    ///         camera did.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(55f, 35f, 20f)]
    [InlineData(70f, -50f, 0f)]
    [InlineData(45f, 70f, -65f)]
    public void The_tilt_and_the_chart_compose_into_one_ellipse(float tilt, float stretch, float turn) {
        var (projection, points, layout) = Facet(tilt, stretch, turn, 40f, 10f);

        Assert.True(projection.TryHit(Along(), out var hit));

        var footprint = PaintFootprint.Ellipse(
            Eye(),
            Along(),
            hit,
            projection.Density(hit.Triangle, Atlas, Atlas),
            Brush
        );

        var (major, minor, angle) = Sampled(points, layout);

        Assert.True(footprint.IsMeasurable);
        Assert.Equal(Math.Sqrt(major * minor), footprint.Radius, 3);
        Assert.Equal(major / minor, footprint.Aspect, 3);
        Assert.Equal(0d, Math.Sin(angle - footprint.Angle), 3);

        // ⚠ And the two answers the old arithmetic could give are both wrong here, by margins a
        // reader can check: the layout's own 4:1, and the 4 · 1/cos θ the scalars multiply to.
        Assert.True(
            Math.Abs(footprint.Aspect - 4f) > 0.5f,
            $"the composed aspect {footprint.Aspect} is the chart's alone, so the tilt's shape is missing."
        );

        Assert.True(
            Math.Abs(footprint.Aspect - (4f / MathF.Cos(tilt * (MathF.PI / 180f)))) > 0.3f,
            $"the composed aspect {footprint.Aspect} is the product of the two scalars, which is only "
            + "right when the tilt runs down the chart's own stretch axis."
        );
    }

    /// <summary>A tilt down the chart's own stretch axis <em>is</em> the product of the two aspects.</summary>
    /// <remarks>
    ///     ⚠ <b>The case the arithmetic this replaces would have passed, kept so the others are
    ///     legible as the discriminating ones.</b> With the tilt's direction and the chart's most
    ///     stretched surface direction aligned, the two maps are diagonal in one basis and their
    ///     product's singular values are the products of theirs. Every fixture whose tilt runs down
    ///     a coordinate axis of the layout is this case by accident.
    /// </remarks>
    [Fact]
    public void A_tilt_down_the_charts_own_stretch_axis_is_the_product_of_the_two_aspects() {
        const float Tilt = 55f;

        var (projection, _, _) = Facet(Tilt, 0f, 0f, 40f, 10f);

        Assert.True(projection.TryHit(Along(), out var hit));

        var footprint = PaintFootprint.Ellipse(
            Eye(),
            Along(),
            hit,
            projection.Density(hit.Triangle, Atlas, Atlas),
            Brush
        );

        Assert.Equal(4f / MathF.Cos(Tilt * (MathF.PI / 180f)), footprint.Aspect, 3);
    }

    /// <summary>A face-on hit is the layout's ellipse and nothing else.</summary>
    /// <remarks>
    ///     ⚠ <b>The degenerate direction, which is where a composition can quietly divide by the
    ///     length of a zero vector.</b> Looking straight down the normal there is no direction in
    ///     the tangent plane to stretch along, and the honest answer is the chart's own ellipse
    ///     unchanged — not a NaN, and not an arbitrary axis picked out of rounding.
    /// </remarks>
    [Fact]
    public void A_face_on_hit_is_the_layouts_ellipse_unchanged() {
        var (projection, _, _) = Facet(0f, 35f, 20f, 40f, 10f);

        Assert.True(projection.TryHit(Along(), out var hit));

        var density = projection.Density(hit.Triangle, Atlas, Atlas);
        var footprint = PaintFootprint.Ellipse(Eye(), Along(), hit, density, Brush);

        Assert.Equal(4f, footprint.Aspect, 3);
        Assert.Equal(0f, MathF.Sin(footprint.Angle - density.Orientation), 4);
        Assert.Equal(Brush * density.Area, footprint.Radius, 3);
    }

    /// <summary>A camera whose render pixel is one world unit, so a screen radius is a surface radius.</summary>
    /// <remarks>
    ///     Orthographic on purpose: the depth term is then exactly one at every tilt, so the numbers
    ///     below are about the ellipse and not about where the facet's centre ended up.
    /// </remarks>
    static PaintEye Eye() => PaintEye.Orthographic(Vector3.Zero, new(0f, 0f, 1f), Pane, Pane);

    /// <summary>The ray down the view axis, which every case here casts.</summary>
    static Ray Along() => new(Vector3.Zero, new(0f, 0f, 1f));

    /// <summary>One tilted facet whose layout is a rotated, stretched map of its own plane.</summary>
    /// <param name="tilt">How far off face-on the facet is turned, in degrees, about the vertical.</param>
    /// <param name="stretch">
    ///     Which way the chart stretches <b>on the surface</b>, in degrees from the tilt's own
    ///     direction. ⚠ Zero is the case a scalar multiply gets right.
    /// </param>
    /// <param name="turn">How far the whole layout is turned in the atlas, in degrees.</param>
    /// <param name="major">How many texels one unit along the stretched surface direction buys.</param>
    /// <param name="minor">And one unit across it.</param>
    /// <returns>The projection, and the corners and coordinates the oracle reads.</returns>
    /// <remarks>
    ///     The map is <c>R(turn) · diag(major, minor) · R(stretch)ᵀ</c> in the facet's own plane
    ///     basis, whose two axes are the tilt's direction and the axis it turns about — which are
    ///     exactly the two edges below, so the matrix's columns are the coordinate offsets divided
    ///     by the edge length.
    /// </remarks>
    static (PaintProjection Projection, Vector3[] Points, Vector2[] Layout) Facet(
        float tilt,
        float stretch,
        float turn,
        float major,
        float minor
    ) {
        var (lean, face) = MathF.SinCos(tilt * (MathF.PI / 180f));
        var (sinStretch, cosStretch) = MathF.SinCos(stretch * (MathF.PI / 180f));
        var (sinTurn, cosTurn) = MathF.SinCos(turn * (MathF.PI / 180f));

        // Leaning away from the camera along the first edge, and downwards along the second so the
        // winding puts the front face towards it.
        Vector3 along = new(face, 0f, lean);
        Vector3 across = new(0f, -1f, 0f);

        var origin = new Vector3(0f, 0f, Depth) - along - across;

        Vector3[] points = [origin, origin + (along * Extent), origin + (across * Extent)];

        // R(turn) · diag(major, minor) · R(stretch)ᵀ, written out.
        Vector2 first = new(
            (major * cosTurn * cosStretch) + (minor * sinTurn * sinStretch),
            (major * sinTurn * cosStretch) - (minor * cosTurn * sinStretch)
        );

        Vector2 second = new(
            (major * cosTurn * sinStretch) - (minor * sinTurn * cosStretch),
            (major * sinTurn * sinStretch) + (minor * cosTurn * cosStretch)
        );

        Vector2 corner = new(0.5f, 0.5f);

        Vector2[] layout = [
            corner,
            corner + (first * (Extent / Atlas)),
            corner + (second * (Extent / Atlas))
        ];

        return (PaintProjection.Over(points, layout, [0, 1, 2]), points, layout);
    }

    /// <summary>The footprint, walked round the rim of the screen disc.</summary>
    /// <param name="points">The facet's three corners.</param>
    /// <param name="layout">Their three texture coordinates.</param>
    /// <returns>The long and short semi-axes in texels, and the long one's angle in the atlas.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every step is the definition rather than the implementation.</b> A point on the
    ///         brush's rim is slid <em>down the ray</em> until it meets the facet's plane — which is
    ///         what makes the tilt an ellipse and not a scale — then written as weights on the
    ///         facet's own two edges, and those same weights read against the facet's own three
    ///         coordinates give the offset in the atlas. Nothing here knows that the answer is an
    ///         ellipse, that its axes are singular values, or that two maps were composed.
    ///     </para>
    ///     <para>
    ///         In doubles, because the extrema of a squared length are what is being read and the
    ///         two semi-axes here differ by a factor of five.
    ///     </para>
    /// </remarks>
    static (double Major, double Minor, double Angle) Sampled(Vector3[] points, Vector2[] layout) {
        var edge = points[1] - points[0];
        var slant = points[2] - points[0];
        var normal = Vector3.Normalize(Vector3.Cross(edge, slant));
        var ray = Vector3.Normalize(Along().Direction);
        var facing = Vector3.Dot(ray, normal);

        // The screen plane's own two axes: anything orthonormal and perpendicular to the ray.
        var first = Vector3.Normalize(Vector3.Cross(ray, MathF.Abs(ray.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        var second = Vector3.Cross(ray, first);

        // The 2×2 Gram matrix of the facet's edges, for writing a point of its plane in them.
        var ee = Vector3.Dot(edge, edge);
        var es = Vector3.Dot(edge, slant);
        var ss = Vector3.Dot(slant, slant);
        var determinant = (ee * ss) - (es * es);

        var d1 = (layout[1] - layout[0]) * Atlas;
        var d2 = (layout[2] - layout[0]) * Atlas;

        var major = 0d;
        var minor = double.PositiveInfinity;
        var angle = 0d;

        for (var step = 0; step < Samples; step++) {
            var (sin, cos) = MathF.SinCos(step * (MathF.Tau / Samples));
            var rim = ((first * cos) + (second * sin)) * Brush;

            // Down the ray onto the facet's plane. This is the whole of the grazing stretch.
            var landed = rim - (ray * (Vector3.Dot(rim, normal) / facing));

            var one = ((Vector3.Dot(edge, landed) * ss) - (Vector3.Dot(slant, landed) * es)) / determinant;
            var two = ((Vector3.Dot(slant, landed) * ee) - (Vector3.Dot(edge, landed) * es)) / determinant;

            var offset = (d1 * one) + (d2 * two);
            var reach = Math.Sqrt(((double)offset.X * offset.X) + ((double)offset.Y * offset.Y));

            if (reach > major) {
                major = reach;
                angle = Math.Atan2(offset.Y, offset.X);
            }

            minor = Math.Min(minor, reach);
        }

        return (major, minor, angle);
    }
}
