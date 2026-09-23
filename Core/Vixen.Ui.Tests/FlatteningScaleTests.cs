// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Rendering;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>What a curve costs in <i>device</i> pixels, which is the unit the eye reads it in.</summary>
/// <remarks>
///     <para>
///         <b><see cref="UiGeometryBuilder.Tolerance" /> and <see cref="UiGeometryBuilder.Fringe" />
///         are authored in document pixels and spent inside the triangles</b>, and the projection
///         magnifies both along with everything else. So neither number is the quantity anybody cares
///         about: what a 2× display shows is the product of the number and the scale, and holding
///         <i>that</i> still is what <see cref="UiGeometryBuilder.ToleranceFor" /> and
///         <see cref="UiGeometryBuilder.FringeFor" /> are for.
///     </para>
///     <para>
///         ⚠ <b>Measured rather than asserted against a constant, and relative rather than absolute.</b>
///         The flattener's achieved error is whatever its subdivision lands on below the tolerance it
///         was given, so a fixture pinning "0.2" would be pinning the subdivision. The property that
///         is actually owed is an order: the device-pixel error at 2× must be no larger than at 1×.
///         Each fixture measures the unscaled arrangement too — the one that shipped until #1329 —
///         and that number is the defect, in the units the issue states it in.
///     </para>
/// </remarks>
public class FlatteningScaleTests {
    const float Radius = 50f;

    static readonly Vector2 Centre = new(80f, 80f);
    static readonly Rectangle Viewport = new(0f, 0f, 160f, 160f);

    static PathBuilder Circle() =>
        new PathBuilder().AddEllipse(
            new Rectangle(Centre.X - Radius, Centre.Y - Radius, Radius * 2f, Radius * 2f)
        );

    /// <summary>The worst distance between the flattened outline and the circle it replaces.</summary>
    /// <remarks>
    ///     ⚠ <b>Taken at the chord's midpoint, which is where a chord is furthest from its arc.</b>
    ///     That is the sagitta, and it is the quantity <see cref="UiGeometryBuilder.Tolerance" /> is
    ///     defined as a bound on — so this measures the flattener against its own contract rather
    ///     than against a vertex count, which would move with any change to how it subdivides.
    /// </remarks>
    static float ChordError(float tolerance) {
        var path = Circle();
        var points = new List<Vector2>();
        var contours = new List<Contour>();

        PathFlattener.Flatten(path.Segments, 0, path.Count, tolerance, points, contours);

        Assert.NotEmpty(contours);

        var worst = 0f;

        foreach (var contour in contours) {
            for (var i = 0; i < contour.Count; i++) {
                var from = points[contour.First + i];
                var to = points[contour.First + ((i + 1) % contour.Count)];
                var middle = (from + to) * 0.5f;

                worst = MathF.Max(worst, Radius - (middle - Centre).Length());
            }
        }

        return worst;
    }

    /// <summary>How wide the antialiasing band is, measured on the geometry that draws.</summary>
    /// <remarks>
    ///     ⚠ <b>The outline's own reach is subtracted, and that is not cosmetic.</b> A circle is four
    ///     cubics with the usual <c>4/3 tan(π/8)</c> constant — see <c>PathBuilder.AddEllipse</c> —
    ///     which bulges about <c>2.7e-4</c> of the radius outside the circle it approximates, or
    ///     0.013 document pixels at this radius. That is a property of the path and not of the
    ///     fringe, it does not scale with either number, and leaving it in would put a 2.6 % error on
    ///     a quantity whose defect is a factor of two. <c>Fringe = 0</c> is a supported value that
    ///     emits no strip at all, so the difference is the band by construction.
    /// </remarks>
    static float Band(float fringe) => FringeReach(fringe) - FringeReach(0f);

    /// <summary>How far past the circle the built geometry's outermost vertex sits.</summary>
    /// <remarks>
    ///     Read off the vertices the renderer draws rather than off the property, because the fringe
    ///     is emitted as a strip of ramp triangles offset outward from the outline — see
    ///     <c>PathTessellator.Feather</c> — and the reach is what the picture has, not what was asked
    ///     for.
    /// </remarks>
    static float FringeReach(float fringe) => BuiltWith(fringe).Reach;

    /// <summary>Builds the circle with this fringe and measures how far past it the geometry reaches.</summary>
    static (UiGeometry Geometry, float Reach) BuiltWith(float fringe) {
        var path = Circle();
        var list = new DrawList();

        list.BeginFrame();

        list.Add(
            new DrawCommand(DrawCommandKind.Path, 0f, 0f, 0f, 0f, Color4.White, 0f, 0f) {
                Offset = list.AddPath(path),
                Length = path.Count
            }
        );

        list.EndFrame();

        // ⚠ The tolerance is held at its 1× value on purpose: a finer flattening moves the outline
        // outward towards the true circle as well, and this fixture is about the fringe alone.
        var builder = new UiGeometryBuilder { Tolerance = UiGeometryBuilder.ToleranceFor(1f), Fringe = fringe };
        var geometry = builder.Build(list, new GlyphFieldCache(new GlyphAtlas(64, 64)), Viewport);

        Assert.NotEmpty(geometry.Draws);

        var reach = 0f;

        foreach (var vertex in geometry.Vertices) {
            reach = MathF.Max(reach, (vertex.Position - Centre).Length() - Radius);
        }

        return (geometry, reach);
    }

    /// <summary>
    ///     The numbers a frame records about its own density are the ones its vertices were built
    ///     with — so a host's scale checked against them is checked against the triangles.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>#1343's closed form.</b> A game HUD drives its own builder, and
    ///         <c>UiRenderFeature.Soft</c> compares <see cref="UiGeometry.Tolerance" /> and
    ///         <see cref="UiGeometry.Fringe" /> against the scale the interface is drawn at. That is
    ///         only worth anything if those two are the numbers spent inside the triangles rather
    ///         than a default beside them — so the band is measured off the vertices, at the two
    ///         fringes a host at 1× and at 2× would set, and has to equal what the geometry says.
    ///     </para>
    ///     <para>
    ///         Shown at the scale each was built for, both bands are half a device pixel; the 1×
    ///         build shown at 2× — the arrangement <c>Soft</c> exists to report — is a whole one.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_frame_records_the_density_its_vertices_were_built_at() {
        var (atOne, reachAtOne) = BuiltWith(UiGeometryBuilder.FringeFor(1f));
        var (atTwo, reachAtTwo) = BuiltWith(UiGeometryBuilder.FringeFor(2f));
        var outline = FringeReach(0f);

        Assert.Equal(reachAtOne - outline, atOne.Fringe, 1e-3f);
        Assert.Equal(reachAtTwo - outline, atTwo.Fringe, 1e-3f);
        Assert.Equal(UiGeometryBuilder.ToleranceFor(1f), atOne.Tolerance);

        Assert.Equal(0.5f, atOne.Fringe * 1f, 1e-3f);
        Assert.Equal(0.5f, atTwo.Fringe * 2f, 1e-3f);
        Assert.Equal(1f, atOne.Fringe * 2f, 1e-3f);
    }

    /// <summary>A curve built for a 2× surface is no further off, in the pixels it is shown in.</summary>
    [Fact]
    public void The_flattening_error_in_device_pixels_does_not_grow_with_the_scale() {
        var atOne = ChordError(UiGeometryBuilder.ToleranceFor(1f));
        var atTwo = ChordError(UiGeometryBuilder.ToleranceFor(2f)) * 2f;

        // The instrument first: a flattener that ignored its tolerance would make every number here
        // equal, and the order below would then be satisfied by the defect.
        Assert.True(atOne > 0f, $"a flattened circle has to miss the arc somewhere; measured {atOne}");
        Assert.True(
            ChordError(UiGeometryBuilder.ToleranceFor(2f)) < atOne,
            "halving the tolerance has to flatten more finely"
        );

        Assert.True(atTwo <= atOne + 1e-4f, $"2× is coarser than 1×: {atTwo} device pixels against {atOne}");

        // ⚠ And this is what shipped: the same geometry, shown at twice the size, is twice as far off
        // the curve — 0.4 device pixels of chord error where 0.2 was authored.
        var unscaled = atOne * 2f;

        Assert.True(unscaled > atOne, $"the defect is not measurable here: {unscaled} against {atOne}");
    }

    /// <summary>And its antialiasing band is the same width, rather than twice it.</summary>
    /// <remarks>
    ///     ⚠ <b>The closed-form half of the picture this issue asks for.</b> The fringe's reach past
    ///     the outline is a length the vertices carry, so "the band must not grow with the scale" is
    ///     checkable without rendering anything — and a band that doubles is exactly the softness
    ///     that reads as woolly rather than as a fault.
    /// </remarks>
    [Fact]
    public void The_fringe_band_in_device_pixels_does_not_grow_with_the_scale() {
        var atOne = Band(UiGeometryBuilder.FringeFor(1f));
        var atTwo = Band(UiGeometryBuilder.FringeFor(2f)) * 2f;

        Assert.Equal(0.5f, atOne, 1e-3f);
        Assert.Equal(atOne, atTwo, 1e-3f);

        // What shipped: half a *document* pixel on a 2× surface is a whole device pixel each side,
        // which is the two-pixel band the issue names.
        Assert.Equal(1f, Band(UiGeometryBuilder.FringeFor(1f)) * 2f, 1e-3f);
    }

    /// <summary>Zero is not a scale, and answering as though it were one would throw downstream.</summary>
    /// <remarks>
    ///     <c>PathFlattener.Flatten</c> refuses a tolerance of zero or less, so a host whose window
    ///     reports no scale yet — which is what <c>UiWindowSurface.Scale</c> guards against — would
    ///     otherwise turn a missing number into an exception inside the tessellator.
    /// </remarks>
    [Fact]
    public void A_scale_that_is_not_positive_is_treated_as_one() {
        Assert.Equal(UiGeometryBuilder.ToleranceFor(1f), UiGeometryBuilder.ToleranceFor(0f));
        Assert.Equal(UiGeometryBuilder.FringeFor(1f), UiGeometryBuilder.FringeFor(-2f));
    }
}
