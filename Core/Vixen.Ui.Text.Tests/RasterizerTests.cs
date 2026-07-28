// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Numerics;
using Vixen.Ui.Text.Outlines;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Ui.Text.Tests;

/// <summary>
///     The rasteriser, judged by the one thing about a filled shape that has a closed form.
/// </summary>
/// <remarks>
///     <para>
///         <b>Green's theorem gives the exact area a path encloses</b>, straight from the control
///         points, without rasterising anything: ∮(x dy − y dx)/2 over each segment. For a Bézier
///         that integrand is a polynomial, so a four-point Gauss–Legendre quadrature evaluates it to
///         the last bit rather than approximately.
///     </para>
///     <para>
///         That makes it a real oracle rather than a second implementation of the same idea. It
///         shares no code and no reasoning with the scanline fill — it never asks where an edge
///         crosses a row — so the two agreeing says the fill covers the right region, and it holds
///         for every glyph in the corpus rather than for the handful anyone would write by hand.
///     </para>
///     <para>
///         ⚠ It says nothing about *where* the coverage is. Two shapes of equal area agree here, so
///         the placement tests below carry that half.
///     </para>
/// </remarks>
public class RasterizerTests {
    [Fact]
    public void A_rectangle_on_pixel_boundaries_is_covered_exactly() {
        var outline = Path(
            new OutlineSegment(OutlineVerb.Move, 2, 2),
            new OutlineSegment(OutlineVerb.Line, 10, 2),
            new OutlineSegment(OutlineVerb.Line, 10, 6),
            new OutlineSegment(OutlineVerb.Line, 2, 6),
            new OutlineSegment(OutlineVerb.Close, 0, 0)
        );

        var bitmap = GlyphRasterizer.Rasterize(outline, 16, 8, 1f, Vector2.Zero);

        Assert.Equal(32f, bitmap.Area, 3);
        Assert.Equal(1f, bitmap[5, 8 - 1 - 3], 3);   // inside
        Assert.Equal(0f, bitmap[0, 8 - 1 - 3], 3);   // left of it
        Assert.Equal(0f, bitmap[5, 8 - 1 - 7], 3);   // below it
    }

    /// <summary>
    ///     ⚠ Half a pixel of a shape is half a pixel of coverage. A rasteriser that rounded to whole
    ///     pixels would pass the test above and put a staircase down every diagonal in the interface.
    /// </summary>
    [Fact]
    public void A_partly_covered_pixel_gets_a_partial_value() {
        var outline = Path(
            new OutlineSegment(OutlineVerb.Move, 0, 0),
            new OutlineSegment(OutlineVerb.Line, 0.5f, 0),
            new OutlineSegment(OutlineVerb.Line, 0.5f, 1),
            new OutlineSegment(OutlineVerb.Line, 0, 1),
            new OutlineSegment(OutlineVerb.Close, 0, 0)
        );

        Assert.Equal(0.5f, GlyphRasterizer.Rasterize(outline, 1, 1, 1f, Vector2.Zero)[0, 0], 2);
    }

    /// <summary>
    ///     ⚠ A counter is a contour wound the other way, and every font relies on it. Even-odd fill
    ///     agrees with non-zero on one hole and disagrees the moment two contours overlap.
    /// </summary>
    [Fact]
    public void A_contour_wound_the_other_way_punches_a_hole() {
        var outline = Path(
            new OutlineSegment(OutlineVerb.Move, 0, 0),
            new OutlineSegment(OutlineVerb.Line, 12, 0),
            new OutlineSegment(OutlineVerb.Line, 12, 12),
            new OutlineSegment(OutlineVerb.Line, 0, 12),
            new OutlineSegment(OutlineVerb.Close, 0, 0),
            new OutlineSegment(OutlineVerb.Move, 4, 4),
            new OutlineSegment(OutlineVerb.Line, 4, 8),
            new OutlineSegment(OutlineVerb.Line, 8, 8),
            new OutlineSegment(OutlineVerb.Line, 8, 4),
            new OutlineSegment(OutlineVerb.Close, 0, 0)
        );

        var bitmap = GlyphRasterizer.Rasterize(outline, 12, 12, 1f, Vector2.Zero);

        Assert.Equal(144f - 16f, bitmap.Area, 2);
        Assert.Equal(0f, bitmap[6, 6], 3);
        Assert.Equal(1f, bitmap[1, 6], 3);
    }

    /// <summary>
    ///     ⚠ <b>Two contours wound the same way overlap into one filled region, not into a hole.</b>
    ///     This is the case that tells non-zero from even-odd; the hole above does not, because both
    ///     rules agree when the inner contour runs the other way. Written after a sabotage swapping
    ///     the rules broke nothing — the comment claiming they differ was right and untested, and
    ///     the difference is not academic: <c>TestShapeLana</c> builds letters out of stacked
    ///     strokes, and even-odd punches a hole through every crossing.
    /// </summary>
    [Fact]
    public void Two_contours_wound_the_same_way_fill_their_overlap() {
        var outline = Path(
            new OutlineSegment(OutlineVerb.Move, 0, 0),
            new OutlineSegment(OutlineVerb.Line, 8, 0),
            new OutlineSegment(OutlineVerb.Line, 8, 8),
            new OutlineSegment(OutlineVerb.Line, 0, 8),
            new OutlineSegment(OutlineVerb.Close, 0, 0),
            new OutlineSegment(OutlineVerb.Move, 4, 4),
            new OutlineSegment(OutlineVerb.Line, 12, 4),
            new OutlineSegment(OutlineVerb.Line, 12, 12),
            new OutlineSegment(OutlineVerb.Line, 4, 12),
            new OutlineSegment(OutlineVerb.Close, 0, 0)
        );

        var bitmap = GlyphRasterizer.Rasterize(outline, 12, 12, 1f, Vector2.Zero);

        // Two 8x8 squares sharing a 4x4 corner: 128 - 16 filled, and the shared part is solid.
        Assert.Equal(112f, bitmap.Area, 2);
        Assert.Equal(1f, bitmap[6, 12 - 1 - 6], 3);
    }

    /// <summary>
    ///     ⚠ <b>An edge covers its y range half-open, or a vertex on a sample line is counted
    ///     twice.</b> Two edges meeting at a point both claim that row, the winding never returns to
    ///     zero, and the span runs off the end of the scanline. Contrived only in where the vertex
    ///     sits: fonts carry collinear points all the time, and this one is placed on a sample line
    ///     because that is the only place the rule can be observed at all — a sabotage closing the
    ///     range at both ends broke nothing until this existed.
    /// </summary>
    [Fact]
    public void A_vertex_that_lands_exactly_on_a_sample_line_is_counted_once() {
        // Sub-scanlines sit at (n + 0.5) / 16 within a row, so 0.03125 is the first of them exactly.
        var outline = Path(
            new OutlineSegment(OutlineVerb.Move, 1, 1),
            new OutlineSegment(OutlineVerb.Line, 1, 0.03125f),
            new OutlineSegment(OutlineVerb.Line, 1, 0),
            new OutlineSegment(OutlineVerb.Line, 3, 0),
            new OutlineSegment(OutlineVerb.Line, 3, 1),
            new OutlineSegment(OutlineVerb.Close, 0, 0)
        );

        Assert.Equal(2f, GlyphRasterizer.Rasterize(outline, 4, 1, 1f, Vector2.Zero).Area, 3);
    }

    [Fact]
    public void An_empty_outline_covers_nothing() =>
        Assert.Equal(0f, GlyphRasterizer.Rasterize(GlyphOutline.Empty, 8, 8, 1f, Vector2.Zero).Area);

    // ------------------------------------------------------------ The oracle

    /// <summary>
    ///     Every contour of every drawn glyph of every embedded font, rasterised, against the area
    ///     its own control points say it encloses.
    /// </summary>
    /// <param name="font">Which font.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Per contour, not per glyph, and that is the oracle's limit rather than a
    ///         convenience.</b> Green's theorem measures the <i>algebraic</i> area, so a region two
    ///         contours both cover counts twice; a non-zero fill measures the <i>covered</i> area, so
    ///         it counts once. The two therefore part company exactly where a glyph's contours
    ///         overlap — which is not exotic: <c>TestShapeLana</c> glyph 340 is nine contours all
    ///         wound the same way, and 22 % of its algebraic area is counted more than once. Found by
    ///         the whole-glyph comparison failing on one font out of fourteen.
    ///     </para>
    ///     <para>
    ///         Comparing contour by contour removes the multiplicity and makes the check exact
    ///         again — and stronger, since that glyph now contributes nine comparisons rather than
    ///         one. The whole-glyph invariant that survives is the inequality below.
    ///     </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(OutlineTests.Fonts), MemberType = typeof(OutlineTests))]
    public void Rasterised_coverage_agrees_with_the_area_the_curves_enclose(string font) {
        var face = TestFonts.Load(font);
        var checkedContours = 0;
        var failures = new List<string>();

        for (ushort glyph = 0; glyph < face.GlyphCount; glyph++) {
            var outline = face.GetOutline(glyph);
            if (outline.IsEmpty) {
                continue;
            }

            var whole = outline.Bounds();
            var scale = 48f / Math.Max(1f, Math.Max(whole.Width, whole.Height));
            var contour = 0;

            foreach (var piece in Contours(outline)) {
                contour++;
                var exact = Math.Abs(SignedArea(piece));
                var bounds = piece.Bounds();

                var width = (int)Math.Ceiling(bounds.Width * scale) + 2;
                var height = (int)Math.Ceiling(bounds.Height * scale) + 2;
                if (width <= 2 || height <= 2 || exact <= 0) {
                    continue;
                }

                checkedContours++;

                var origin = new Vector2(bounds.MinX - (1 / scale), bounds.MinY - (1 / scale));
                var covered = GlyphRasterizer.Rasterize(piece, width, height, scale, origin).Area / (scale * scale);

                // Antialiasing error lives on the boundary, so the allowance follows the perimeter a
                // cell of this size implies rather than the area. What it has to catch — a lost
                // contour, an inverted winding, a flattening that cuts corners — is wrong by far
                // more than a percent.
                var allowance = (0.02f * exact) + (2f * (bounds.Width + bounds.Height) / scale);
                if (Math.Abs(covered - exact) > allowance) {
                    failures.Add($"glyph {glyph} contour {contour}: exact {exact:F0}, covered {covered:F0}");
                }
            }
        }

        Assert.True(checkedContours > 0, $"{font} produced nothing to rasterise");
        Assert.Empty(failures);
    }

    /// <summary>
    ///     ⚠ And the whole-glyph statement that does survive overlap: a non-zero fill can never
    ///     cover more than its contours enclose separately, because covering a region twice still
    ///     fills it once.
    /// </summary>
    [Theory]
    [MemberData(nameof(OutlineTests.Fonts), MemberType = typeof(OutlineTests))]
    public void A_glyph_never_covers_more_than_its_contours_enclose_separately(string font) {
        var face = TestFonts.Load(font);
        var failures = new List<string>();

        for (ushort glyph = 0; glyph < face.GlyphCount; glyph++) {
            var outline = face.GetOutline(glyph);
            if (outline.IsEmpty) {
                continue;
            }

            var bounds = outline.Bounds();
            var scale = 48f / Math.Max(1f, Math.Max(bounds.Width, bounds.Height));
            var width = (int)Math.Ceiling(bounds.Width * scale) + 2;
            var height = (int)Math.Ceiling(bounds.Height * scale) + 2;
            if (width <= 2 || height <= 2) {
                continue;
            }

            var separately = Contours(outline).Sum(piece => Math.Abs(SignedArea(piece)));
            if (separately <= 0) {
                continue;
            }

            var origin = new Vector2(bounds.MinX - (1 / scale), bounds.MinY - (1 / scale));
            var covered = GlyphRasterizer.Rasterize(outline, width, height, scale, origin).Area / (scale * scale);

            var allowance = (0.02f * separately) + (2f * (bounds.Width + bounds.Height) / scale);
            if (covered > separately + allowance) {
                failures.Add($"glyph {glyph}: covered {covered:F0} over {separately:F0}");
            }
        }

        Assert.Empty(failures);
    }

    /// <summary>Splits an outline into one outline per contour.</summary>
    static List<GlyphOutline> Contours(GlyphOutline outline) {
        var contours = new List<GlyphOutline>();
        var current = ImmutableArray.CreateBuilder<OutlineSegment>();

        foreach (var segment in outline.Segments) {
            if (segment.Verb == OutlineVerb.Move && current.Count > 0) {
                contours.Add(new GlyphOutline(current.ToImmutable()));
                current.Clear();
            }

            current.Add(segment);
        }

        if (current.Count > 0) {
            contours.Add(new GlyphOutline(current.ToImmutable()));
        }

        return contours;
    }

    /// <summary>
    ///     The area a path encloses, by Green's theorem, exactly.
    /// </summary>
    /// <remarks>
    ///     ⚠ Four-point Gauss–Legendre is <b>exact</b> here rather than close: it integrates
    ///     polynomials up to degree seven without error, and the integrand for a cubic is degree
    ///     five. Sampling the curve instead would make this a second approximation agreeing with the
    ///     first, which is what an oracle must not be.
    /// </remarks>
    internal static float SignedArea(GlyphOutline outline) {
        var total = 0.0;
        var cursor = Vector2.Zero;
        var start = Vector2.Zero;

        foreach (var segment in outline.Segments) {
            switch (segment.Verb) {
                case OutlineVerb.Move:
                    total += Line(cursor, start);
                    cursor = start = new Vector2(segment.X0, segment.Y0);
                    break;

                case OutlineVerb.Line: {
                    var to = new Vector2(segment.X0, segment.Y0);
                    total += Line(cursor, to);
                    cursor = to;
                    break;
                }

                case OutlineVerb.Quadratic: {
                    var control = new Vector2(segment.X0, segment.Y0);
                    var to = new Vector2(segment.X1, segment.Y1);
                    total += Gauss(t => Quadratic(cursor, control, to, t), t => QuadraticSlope(cursor, control, to, t));
                    cursor = to;
                    break;
                }

                case OutlineVerb.Cubic: {
                    var first = new Vector2(segment.X0, segment.Y0);
                    var second = new Vector2(segment.X1, segment.Y1);
                    var to = new Vector2(segment.X2, segment.Y2);
                    total += Gauss(
                        t => Cubic(cursor, first, second, to, t),
                        t => CubicSlope(cursor, first, second, to, t)
                    );

                    cursor = to;
                    break;
                }

                case OutlineVerb.Close:
                    total += Line(cursor, start);
                    cursor = start;
                    break;

                default:
                    break;
            }
        }

        return (float)(total + Line(cursor, start));
    }

    static double Line(Vector2 from, Vector2 to) => ((from.X * to.Y) - (to.X * from.Y)) / 2.0;

    // Four-point Gauss–Legendre on [0, 1].
    static readonly double[] Nodes = [
        (1 - 0.8611363115940526) / 2,
        (1 - 0.3399810435848563) / 2,
        (1 + 0.3399810435848563) / 2,
        (1 + 0.8611363115940526) / 2
    ];

    static readonly double[] Weights = [
        0.3478548451374538 / 2,
        0.6521451548625461 / 2,
        0.6521451548625461 / 2,
        0.3478548451374538 / 2
    ];

    static double Gauss(Func<double, Vector2> point, Func<double, Vector2> slope) {
        var total = 0.0;
        for (var i = 0; i < Nodes.Length; i++) {
            var p = point(Nodes[i]);
            var d = slope(Nodes[i]);
            total += Weights[i] * ((p.X * d.Y) - (p.Y * d.X));
        }

        return total / 2.0;
    }

    static Vector2 Quadratic(Vector2 a, Vector2 b, Vector2 c, double t) {
        var u = 1 - t;
        return (float)(u * u) * a + (float)(2 * u * t) * b + (float)(t * t) * c;
    }

    static Vector2 QuadraticSlope(Vector2 a, Vector2 b, Vector2 c, double t) =>
        (float)(2 * (1 - t)) * (b - a) + (float)(2 * t) * (c - b);

    static Vector2 Cubic(Vector2 a, Vector2 b, Vector2 c, Vector2 d, double t) {
        var u = 1 - t;
        return (float)(u * u * u) * a
               + (float)(3 * u * u * t) * b
               + (float)(3 * u * t * t) * c
               + (float)(t * t * t) * d;
    }

    static Vector2 CubicSlope(Vector2 a, Vector2 b, Vector2 c, Vector2 d, double t) {
        var u = 1 - t;
        return (float)(3 * u * u) * (b - a) + (float)(6 * u * t) * (c - b) + (float)(3 * t * t) * (d - c);
    }

    static GlyphOutline Path(params OutlineSegment[] segments) =>
        new(ImmutableArray.Create(segments));
}
