// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>SVG path data in, <see cref="PathBuilder" /> out.</summary>
/// <remarks>
///     ⚠ <b>What is asserted is the points, not the segment count.</b> An arc becomes some number of
///     cubics and a smooth curve becomes one — counting them would be a test of this implementation
///     rather than of the grammar, and it would have to be rewritten the first time the arc's
///     subdivision changed. Where a curve is asserted, it is asserted by where it passes.
/// </remarks>
public class SvgPathTests {
    static Vector2 End(PathBuilder path, int index) => path.Segments[index].P2;

    [Fact]
    public void A_move_and_a_line_are_the_points_they_name() {
        var path = SvgPath.Parse("M 10 20 L 30 40");

        Assert.Equal(2, path.Count);
        Assert.Equal(PathVerb.Move, path.Segments[0].Verb);
        Assert.Equal(new Vector2(10f, 20f), End(path, 0));
        Assert.Equal(PathVerb.Line, path.Segments[1].Verb);
        Assert.Equal(new Vector2(30f, 40f), End(path, 1));
    }

    /// <summary>
    ///     ⚠ <b>The case a split-on-whitespace parser gets wrong.</b> There is no separator between
    ///     the <c>2</c> and the <c>L</c>, and none between the <c>22</c> and the <c>h</c> — this is
    ///     the shape almost every icon set is minified into.
    /// </summary>
    [Fact]
    public void Commands_need_no_separator_from_the_numbers_around_them() {
        var path = SvgPath.Parse("M12 2L2 22h20z");

        Assert.Equal(4, path.Count);
        Assert.Equal(new Vector2(12f, 2f), End(path, 0));
        Assert.Equal(new Vector2(2f, 22f), End(path, 1));
        Assert.Equal(new Vector2(22f, 22f), End(path, 2));
        Assert.Equal(PathVerb.Close, path.Segments[3].Verb);
    }

    [Fact]
    public void Relative_commands_resolve_against_the_pen() {
        var path = SvgPath.Parse("m10 10 l5 0 l0 5");

        Assert.Equal(new Vector2(10f, 10f), End(path, 0));
        Assert.Equal(new Vector2(15f, 10f), End(path, 1));
        Assert.Equal(new Vector2(15f, 15f), End(path, 2));
    }

    [Fact]
    public void Horizontal_and_vertical_lines_keep_the_other_axis() {
        var path = SvgPath.Parse("M4 6 H12 V2 h-3 v4");

        Assert.Equal(new Vector2(12f, 6f), End(path, 1));
        Assert.Equal(new Vector2(12f, 2f), End(path, 2));
        Assert.Equal(new Vector2(9f, 2f), End(path, 3));
        Assert.Equal(new Vector2(9f, 6f), End(path, 4));
    }

    /// <summary>
    ///     ⚠ <b>A repeated moveto is a lineto, which is the one repetition rule that is not "the same
    ///     command again".</b> A polygon written as one <c>M</c> and its whole outline — which several
    ///     exporters emit — is otherwise a path of overlapping moves that draws nothing.
    /// </summary>
    [Fact]
    public void A_repeated_moveto_draws_lines() {
        var path = SvgPath.Parse("M0 0 1 1 2 2");

        Assert.Equal(3, path.Count);
        Assert.Equal(PathVerb.Move, path.Segments[0].Verb);
        Assert.Equal(PathVerb.Line, path.Segments[1].Verb);
        Assert.Equal(PathVerb.Line, path.Segments[2].Verb);
        Assert.Equal(new Vector2(2f, 2f), End(path, 2));
    }

    [Fact]
    public void A_repeated_command_takes_another_group_of_arguments() {
        var path = SvgPath.Parse("M0 0 L1 1 2 2 3 3");

        Assert.Equal(4, path.Count);
        Assert.Equal(new Vector2(3f, 3f), End(path, 3));
    }

    [Fact]
    public void Close_puts_the_pen_back_where_the_contour_began() {
        // ⚠ Where the *contour* began, not where the path did: the second subpath's relative move is
        // resolved against (10,10), so it lands at (12,10) rather than at the closing point.
        var path = SvgPath.Parse("M10 10 L20 20 Z m2 0 l1 0");

        Assert.Equal(new Vector2(12f, 10f), End(path, 3));
        Assert.Equal(new Vector2(13f, 10f), End(path, 4));
    }

    [Fact]
    public void Cubics_and_quadratics_carry_their_control_points() {
        var path = SvgPath.Parse("M0 0 C1 2 3 4 5 6 Q7 8 9 10");

        var cubic = path.Segments[1];

        Assert.Equal(PathVerb.Cubic, cubic.Verb);
        Assert.Equal(new Vector2(1f, 2f), cubic.P0);
        Assert.Equal(new Vector2(3f, 4f), cubic.P1);
        Assert.Equal(new Vector2(5f, 6f), cubic.P2);

        var quadratic = path.Segments[2];

        Assert.Equal(PathVerb.Quadratic, quadratic.Verb);
        Assert.Equal(new Vector2(7f, 8f), quadratic.P0);
        Assert.Equal(new Vector2(9f, 10f), quadratic.P2);
    }

    /// <summary>
    ///     ⚠ <b>A smooth curve's first control point is the previous one reflected through the
    ///     pen.</b> The previous cubic ends at (5,6) with its second handle at (3,4), so the
    ///     reflection is (7,8).
    /// </summary>
    [Fact]
    public void A_smooth_cubic_reflects_the_previous_control_point() {
        var path = SvgPath.Parse("M0 0 C1 2 3 4 5 6 S9 10 11 12");

        var smooth = path.Segments[2];

        Assert.Equal(new Vector2(7f, 8f), smooth.P0);
        Assert.Equal(new Vector2(9f, 10f), smooth.P1);
    }

    /// <summary>
    ///     ⚠ <b>And reflects nothing at all when the previous command was not a curve of the same
    ///     family</b>, where the control point is the pen itself. The wrong answer here only shows on
    ///     paths that mix lines and smooth curves, which is why it is the rule that gets dropped.
    /// </summary>
    [Fact]
    public void A_smooth_cubic_after_a_line_uses_the_pen() {
        var path = SvgPath.Parse("M0 0 L4 4 S9 10 11 12");

        Assert.Equal(new Vector2(4f, 4f), path.Segments[2].P0);
    }

    [Fact]
    public void A_smooth_quadratic_reflects_its_own_family() {
        var path = SvgPath.Parse("M0 0 Q2 2 4 0 T8 0");

        // The quadratic's handle is (2,2) and it ends at (4,0), so the reflection is (6,-2).
        Assert.Equal(new Vector2(6f, -2f), path.Segments[2].P0);
    }

    [Fact]
    public void An_arc_ends_where_it_was_told_to() {
        var path = SvgPath.Parse("M10 0 A10 10 0 0 1 0 10");

        Assert.Equal(0f, End(path, path.Count - 1).X, 3);
        Assert.Equal(10f, End(path, path.Count - 1).Y, 3);
        Assert.All(path.Segments.Skip(1), segment => Assert.Equal(PathVerb.Cubic, segment.Verb));
    }

    /// <summary>A quarter circle passes through the point on the circle at 45°.</summary>
    /// <remarks>
    ///     ⚠ <b>This is what says the conversion is an arc rather than any old curve between the two
    ///     endpoints.</b> Every wrong implementation of F.6.5 still ends in the right place; what it
    ///     gets wrong is the middle.
    /// </remarks>
    [Fact]
    public void An_arc_follows_the_circle_it_names() {
        var path = SvgPath.Parse("M10 0 A10 10 0 0 1 0 10");
        var middle = At(path, 0.5f);
        var radius = MathF.Sqrt((middle.X * middle.X) + (middle.Y * middle.Y));

        Assert.Equal(10f, radius, 2);
        Assert.Equal(MathF.Sqrt(50f), middle.X, 1);
    }

    /// <summary>
    ///     ⚠ <b>Two endpoints, two radii and one rotation describe four arcs, and the flags pick
    ///     one.</b> Both of these end in the same place — every wrong implementation of F.6.5 does
    ///     that much — so what is asserted is how far each one travels to get there.
    /// </summary>
    [Fact]
    public void The_large_and_sweep_flags_choose_between_the_four_arcs() {
        var small = SvgPath.Parse("M10 0 A10 10 0 0 1 0 10");
        var large = SvgPath.Parse("M10 0 A10 10 0 1 1 0 10");
        var other = SvgPath.Parse("M10 0 A10 10 0 0 0 0 10");

        // The short way is the quarter that stays inside the square the endpoints span; the long way
        // is three quarters of a circle centred on the far corner and reaches twice as far out.
        Assert.True(Extent(small) <= 10.01f, $"the short arc reached {Extent(small)}");
        Assert.True(Extent(large) >= 19.9f, $"the long arc only reached {Extent(large)}");

        // And the sweep flag is the other axis of the choice: same length, other side.
        Assert.True(Extent(other) <= 10.01f);
        Assert.NotEqual(At(small, 0.5f).X, At(other, 0.5f).X, 1);

        Assert.All(
            new[] { small, large, other },
            path => Assert.Equal(10f, End(path, path.Count - 1).Y, 2)
        );
    }

    /// <summary>How far from the origin a path's furthest point is.</summary>
    static float Extent(PathBuilder path) {
        var furthest = 0f;

        for (var step = 0; step <= 32; step++) {
            var point = At(path, step / 32f);

            furthest = MathF.Max(furthest, MathF.Max(MathF.Abs(point.X), MathF.Abs(point.Y)));
        }

        return furthest;
    }

    /// <summary>
    ///     ⚠ <b>The arc's flags are one character each and may run together with what follows.</b>
    ///     <c>0110 0</c> is flag 0, flag 1, then the point (10,0) — a parser that read the flags as
    ///     numbers would swallow the endpoint's first digit and put the arc somewhere else.
    /// </summary>
    [Fact]
    public void Arc_flags_run_together_with_the_endpoint() {
        var path = SvgPath.Parse("M0 0a1 1 0 0110 0");

        Assert.Equal(10f, End(path, path.Count - 1).X, 3);
        Assert.Equal(0f, End(path, path.Count - 1).Y, 3);
    }

    [Fact]
    public void Radii_too_small_for_the_endpoints_are_grown_rather_than_refused() {
        // A radius of 1 cannot reach twenty units away; F.6.6 scales it up to exactly half the chord.
        var path = SvgPath.Parse("M0 0 A1 1 0 0 1 20 0");

        Assert.Equal(20f, End(path, path.Count - 1).X, 3);
        Assert.Equal(10f, MathF.Abs(At(path, 0.5f).Y), 2);
    }

    [Fact]
    public void A_zero_radius_arc_is_a_straight_line() {
        var path = SvgPath.Parse("M0 0 A0 0 0 0 1 10 10");

        Assert.Equal(PathVerb.Line, path.Segments[1].Verb);
        Assert.Equal(new Vector2(10f, 10f), End(path, 1));
    }

    [Fact]
    public void Exponents_and_signs_are_read_without_separators() {
        var path = SvgPath.Parse("M1e1 2E-1L-3-4");

        Assert.Equal(10f, End(path, 0).X, 4);
        Assert.Equal(0.2f, End(path, 0).Y, 4);
        Assert.Equal(new Vector2(-3f, -4f), End(path, 1));
    }

    [Fact]
    public void Commas_and_whitespace_are_the_same_thing() {
        var spaced = SvgPath.Parse("M 1 2 L 3 4");
        var packed = SvgPath.Parse("M1,2L3,4");

        Assert.Equal(spaced.Segments, packed.Segments);
    }

    [Fact]
    public void Data_that_begins_with_a_curve_still_opens_a_contour() {
        // In error per the specification, and drawing from the origin is more use than throwing away
        // an icon set for one glyph an exporter trimmed too eagerly.
        var path = SvgPath.Parse("L5 5");

        Assert.Equal(PathVerb.Move, path.Segments[0].Verb);
        Assert.Equal(Vector2.Zero, End(path, 0));
        Assert.Equal(new Vector2(5f, 5f), End(path, 1));
    }

    [Theory]
    [InlineData("M0 0 K5 5")]
    [InlineData("M0 0 L")]
    [InlineData("M0 0 L5")]
    [InlineData("5 5")]
    [InlineData("M0 0 A1 1 0 5 1 10 0")]
    public void Data_that_cannot_be_read_says_so(string data) {
        Assert.Throws<SvgPathException>(() => SvgPath.Parse(data));
        Assert.Null(SvgPath.TryParse(data));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_at_all_is_nothing_rather_than_a_fault(string? data) => Assert.Null(SvgPath.TryParse(data));

    /// <summary>Where a path is at a fraction along its last contour, by flattening it.</summary>
    /// <remarks>
    ///     Deliberately crude — this exists to ask "does the curve pass near here", which is the only
    ///     question these tests have about a curve, and a precise answer would need the tessellator.
    /// </remarks>
    static Vector2 At(PathBuilder path, float fraction) {
        List<Vector2> points = [];
        var pen = Vector2.Zero;

        foreach (var segment in path.Segments) {
            switch (segment.Verb) {
                case PathVerb.Move:
                    pen = segment.P2;
                    points.Add(pen);

                    break;

                case PathVerb.Line:
                    pen = segment.P2;
                    points.Add(pen);

                    break;

                case PathVerb.Quadratic:
                case PathVerb.Cubic: {
                    var first = segment.Verb == PathVerb.Cubic ? segment.P0 : Vector2.Lerp(pen, segment.P0, 2f / 3f);
                    var second = segment.Verb == PathVerb.Cubic
                        ? segment.P1
                        : Vector2.Lerp(segment.P2, segment.P0, 2f / 3f);

                    for (var step = 1; step <= 16; step++) {
                        points.Add(Bezier(pen, first, second, segment.P2, step / 16f));
                    }

                    pen = segment.P2;
                    break;
                }

                default:
                    break;
            }
        }

        return points[Math.Clamp((int) (points.Count * fraction), 0, points.Count - 1)];
    }

    static Vector2 Bezier(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float t) {
        var s = 1f - t;

        return (a * (s * s * s)) + (b * (3f * s * s * t)) + (c * (3f * s * t * t)) + (d * (t * t * t));
    }
}
