// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Numerics;
using Vixen.Ui.Text.Outlines;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Ui.Text.Tests;

/// <summary>
///     The distance field, judged by reconstructing the shape back out of it.
/// </summary>
/// <remarks>
///     <para>
///         <b>A golden image only says the output has not changed.</b> What matters about a distance
///         field is whether the glyph a shader draws from it is the glyph the outline describes, so
///         that is the question asked: threshold the median at the halfway mark and compare, pixel by
///         pixel, against the rasteriser filling the same outline. Two routes to one shape, sharing
///         only the outline.
///     </para>
///     <para>
///         ⚠ <b>The disagreement is expected on the boundary and nowhere else.</b> A thresholded
///         field is binary and a rasterised edge is antialiased, so every pixel the outline passes
///         through is a legitimate difference. The comparison ignores pixels whose coverage is
///         partial and demands exactness of the rest — which is the interior and the exterior,
///         where a field that had lost a contour or inverted a sign would show.
///     </para>
/// </remarks>
public class DistanceFieldTests {
    [Theory]
    [MemberData(nameof(OutlineTests.Fonts), MemberType = typeof(OutlineTests))]
    public void A_field_reconstructs_the_shape_the_rasteriser_fills(string font) {
        var face = TestFonts.Load(font);
        var checkedGlyphs = 0;
        var failures = new List<string>();

        // Sampled rather than exhaustive — a field is far dearer than an outline — but stepped by
        // the font's own size so a four-glyph font contributes its four rather than none.
        var stride = (ushort)Math.Max(1, face.GlyphCount / 60);

        for (ushort glyph = 0; glyph < face.GlyphCount; glyph += stride) {
            var outline = face.GetOutline(glyph);
            if (outline.IsEmpty) {
                continue;
            }

            var bounds = outline.Bounds();
            if (bounds.Width <= 0 || bounds.Height <= 0) {
                continue;
            }

            const int Cell = 32;
            const int Padding = 4;

            var scale = (float)Cell / Math.Max(bounds.Width, bounds.Height);
            var width = (int)Math.Ceiling(bounds.Width * scale) + (2 * Padding);
            var height = (int)Math.Ceiling(bounds.Height * scale) + (2 * Padding);
            var origin = new Vector2(bounds.MinX - (Padding / scale), bounds.MinY - (Padding / scale));

            var filled = GlyphRasterizer.Rasterize(outline, width, height, scale, origin);
            var field = DistanceField.Generate(outline, width, height, scale, origin);

            checkedGlyphs++;
            var wrong = 0;
            var judged = 0;

            for (var y = 0; y < height; y++) {
                for (var x = 0; x < width; x++) {
                    var coverage = filled[x, y];
                    if (coverage is > 0.02f and < 0.98f) {
                        continue;                        // the boundary, where the two disagree by design
                    }

                    judged++;
                    if (field.Median(x, y) >= 0.5f != coverage >= 0.5f) {
                        wrong++;
                    }
                }
            }

            if (wrong > 0) {
                failures.Add($"glyph {glyph}: {wrong} of {judged} pixels disagree");
            }
        }

        Assert.True(checkedGlyphs > 0, $"{font} produced no glyphs to encode");
        Assert.Empty(failures);
    }

    /// <summary>
    ///     ⚠ <b>The claim three channels exist for, and it only shows under magnification.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A distance field is not read at the resolution it was stored at — that is the point of
    ///         one. It is sampled and interpolated, and <b>interpolation is where a single channel
    ///         loses a corner</b>: distance to the nearest edge is smooth across one, so the value
    ///         between two texels slides past the corner instead of turning at it. At the stored
    ///         resolution both fields agree, which is why the first version of this test asserted
    ///         something about one pixel and proved nothing.
    ///     </para>
    ///     <para>
    ///         So: store a square at 16×16, reconstruct it at 8× by bilinear sampling, and count the
    ///         pixels that come out on the wrong side. The baseline is the true signed distance to
    ///         the rectangle — a closed form, owing nothing to this code — sampled and interpolated
    ///         exactly the same way, which is what a single-channel field would have held.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_median_keeps_under_magnification_a_corner_that_one_channel_loses() {
        const int Stored = 16;
        const float Range = 4f;

        var square = Path(
            new OutlineSegment(OutlineVerb.Move, 4, 4),
            new OutlineSegment(OutlineVerb.Line, 12, 4),
            new OutlineSegment(OutlineVerb.Line, 12, 12),
            new OutlineSegment(OutlineVerb.Line, 4, 12),
            new OutlineSegment(OutlineVerb.Close, 0, 0)
        );

        var field = DistanceField.Generate(square, Stored, Stored, 1f, Vector2.Zero, Range);

        // The same square as a plain distance field, from the closed form for a rectangle.
        var plain = new float[Stored * Stored];
        for (var y = 0; y < Stored; y++) {
            for (var x = 0; x < Stored; x++) {
                var point = new Vector2(x + 0.5f, Stored - 1 - y + 0.5f);
                plain[(y * Stored) + x] = Math.Clamp((RectangleDistance(point) / Range) + 0.5f, 0f, 1f);
            }
        }

        // ⚠ <b>Measured across the edge, a little below the corner — not out along the diagonal.</b>
        // Two earlier attempts measured the wrong thing. Counting misclassified pixels hides the
        // effect, because a plain field's corner error is a fraction of a texel and any band wide
        // enough to ignore boundary noise swallows it. And the diagonal is the one direction where
        // the three channels are symmetric about the corner and none of them can help: there the
        // median is a plain field, exactly. What the channels buy is that the *edges* stay straight
        // right up to the corner, so that is what this walks into.
        const float TrueEdge = 12f;
        const float NearCorner = 11.9f;

        var medianCrossing = Crossing(x => Sample(field, Across(x, NearCorner)));
        var plainCrossing = Crossing(x => Bilinear(plain, Stored, Stored, Across(x, NearCorner)));

        Assert.Equal(TrueEdge, medianCrossing, 1);
        Assert.True(
            plainCrossing < medianCrossing - 0.15f,
            $"the plain field kept the edge too ({plainCrossing:F3} against {medianCrossing:F3}), "
            + "so this proves nothing"
        );

        // ⚠ And the far corner, where the last run of the contour meets the first. Colouring the
        // runs by counting modulo three gives four corners the sequence RG, GB, BR, RG, so that one
        // join alone has both sides the same — and a sabotage doing exactly that passed while only
        // the near corner was scanned.
        Assert.Equal(4f, CrossingLeft(x => Sample(field, Across(x, 4.1f))), 1);
    }

    /// <summary>A point at a given height, as texel coordinates.</summary>
    static (float U, float V) Across(float x, float y) => (x - 0.5f, 16 - 0.5f - y);

    /// <summary>Where, walking right, the reconstructed field falls through the halfway mark.</summary>
    static float Crossing(Func<float, float> sample) {
        for (var x = 8f; x < 16f; x += 0.001f) {
            if (sample(x) < 0.5f) {
                return x;
            }
        }

        return 16f;
    }

    /// <summary>The same, walking left.</summary>
    static float CrossingLeft(Func<float, float> sample) {
        for (var x = 8f; x > 0f; x -= 0.001f) {
            if (sample(x) < 0.5f) {
                return x;
            }
        }

        return 0f;
    }

    /// <summary>
    ///     ⚠ <b>A sharp point, which is where measuring distance to the segment rather than to its
    ///     line shows.</b> Past the end of an edge the two answers diverge by however far round the
    ///     corner the point is, so a right angle barely tells them apart — the square above does not
    ///     — and a wedge tells them apart at once, because almost everything near the tip is past
    ///     the end of both edges. Serifs, stem ends and the apex of an <c>A</c> are all this shape.
    /// </summary>
    [Fact]
    public void A_sharp_point_keeps_its_tip() {
        var wedge = Path(
            new OutlineSegment(OutlineVerb.Move, 6, 16),
            new OutlineSegment(OutlineVerb.Line, 60, 12),
            new OutlineSegment(OutlineVerb.Line, 60, 20),
            new OutlineSegment(OutlineVerb.Close, 0, 0)
        );

        var field = DistanceField.Generate(wedge, 64, 32, 1f, Vector2.Zero, 4f);

        // Walking right along the wedge's axis, the shape starts at its tip.
        var tip = 64f;
        for (var x = 0f; x < 60f; x += 0.001f) {
            if (Sample(field, (x - 0.5f, 32 - 0.5f - 16f)) >= 0.5f) {
                tip = x;
                break;
            }
        }

        Assert.Equal(6f, tip, 0);
    }

    /// <summary>Signed distance to the 4..12 square, positive inside. A closed form.</summary>
    static float RectangleDistance(Vector2 point) {
        var dx = Math.Min(point.X - 4f, 12f - point.X);
        var dy = Math.Min(point.Y - 4f, 12f - point.Y);

        if (dx >= 0 && dy >= 0) {
            return Math.Min(dx, dy);
        }

        // Outside: the distance to the nearest edge, or to the corner when past both.
        var ox = Math.Max(-dx, 0f);
        var oy = Math.Max(-dy, 0f);
        return -MathF.Sqrt((ox * ox) + (oy * oy));
    }

    static float Sample(DistanceFieldBitmap field, (float U, float V) at) => Sample(field, at.U, at.V);

    static float Sample(DistanceFieldBitmap field, float u, float v) {
        var r = Bilinear(field.Channels, field.Width, field.Height, u, v, 3, 0);
        var g = Bilinear(field.Channels, field.Width, field.Height, u, v, 3, 1);
        var b = Bilinear(field.Channels, field.Width, field.Height, u, v, 3, 2);
        return Math.Max(Math.Min(r, g), Math.Min(Math.Max(r, g), b));
    }

    static float Bilinear(float[] data, int width, int height, (float U, float V) at) =>
        Bilinear(data, width, height, at.U, at.V);

    static float Bilinear(float[] data, int width, int height, float u, float v, int stride = 1, int channel = 0) {
        var x0 = Math.Clamp((int)MathF.Floor(u), 0, width - 1);
        var y0 = Math.Clamp((int)MathF.Floor(v), 0, height - 1);
        var x1 = Math.Clamp(x0 + 1, 0, width - 1);
        var y1 = Math.Clamp(y0 + 1, 0, height - 1);

        var fx = Math.Clamp(u - x0, 0f, 1f);
        var fy = Math.Clamp(v - y0, 0f, 1f);

        float At(int x, int y) => data[(((y * width) + x) * stride) + channel];

        var top = (At(x0, y0) * (1 - fx)) + (At(x1, y0) * fx);
        var bottom = (At(x0, y1) * (1 - fx)) + (At(x1, y1) * fx);
        return (top * (1 - fy)) + (bottom * fy);
    }

    [Fact]
    public void A_pixel_deep_inside_saturates_and_one_far_outside_bottoms_out() {
        var square = Path(
            new OutlineSegment(OutlineVerb.Move, 8, 8),
            new OutlineSegment(OutlineVerb.Line, 24, 8),
            new OutlineSegment(OutlineVerb.Line, 24, 24),
            new OutlineSegment(OutlineVerb.Line, 8, 24),
            new OutlineSegment(OutlineVerb.Close, 0, 0)
        );

        var field = DistanceField.Generate(square, 32, 32, 1f, Vector2.Zero);

        Assert.Equal(1f, field.Median(16, 32 - 1 - 16), 3);
        Assert.Equal(0f, field.Median(1, 32 - 1 - 1), 3);
    }

    [Fact]
    public void An_empty_outline_encodes_as_entirely_outside() {
        var field = DistanceField.Generate(GlyphOutline.Empty, 8, 8, 1f, Vector2.Zero);
        Assert.All(field.Channels, value => Assert.Equal(0f, value));
    }

    /// <summary>
    ///     ⚠ A contour with no corner at all — a bowl, a dot, an <c>o</c> — must come out with all
    ///     three channels equal, which makes the field an ordinary one there. Alternating along a
    ///     smooth curve would put a seam in the middle of it.
    /// </summary>
    [Fact]
    public void A_contour_with_no_corner_uses_all_three_channels_together() {
        var circle = Path(
            new OutlineSegment(OutlineVerb.Move, 16, 4),
            new OutlineSegment(OutlineVerb.Cubic, 22.6f, 4, 28, 9.4f, 28, 16),
            new OutlineSegment(OutlineVerb.Cubic, 28, 22.6f, 22.6f, 28, 16, 28),
            new OutlineSegment(OutlineVerb.Cubic, 9.4f, 28, 4, 22.6f, 4, 16),
            new OutlineSegment(OutlineVerb.Cubic, 4, 9.4f, 9.4f, 4, 16, 4),
            new OutlineSegment(OutlineVerb.Close, 0, 0)
        );

        var field = DistanceField.Generate(circle, 32, 32, 1f, Vector2.Zero);

        for (var y = 0; y < 32; y++) {
            for (var x = 0; x < 32; x++) {
                var (r, g, b) = field[x, y];
                Assert.Equal(r, g, 4);
                Assert.Equal(g, b, 4);
            }
        }
    }

    static GlyphOutline Path(params OutlineSegment[] segments) => new(ImmutableArray.Create(segments));
}
