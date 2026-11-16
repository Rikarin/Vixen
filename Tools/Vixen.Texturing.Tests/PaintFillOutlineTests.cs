// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Core.Mathematics;
using Vixen.Texturing.Painting;
using Xunit;

namespace Vixen.Texturing.Tests;

/// <summary>An outline's inside, which a rectangle or lasso fill asks of every triangle's points.</summary>
public class PaintFillOutlineTests {
    /// <summary>
    ///     The outline answers what testing every edge answers, for any outline and any point: its bands only spare
    ///     the edges a point's ray cannot cross. Corners on one height, points on a corner's height and points outside
    ///     are all drawn, since those are where a band's edges are decided.
    /// </summary>
    [Fact]
    [Trait("Category", "Property")]
    public void Its_bands_answer_what_every_edge_answers() {
        var coordinate = Gen.OneOf(Gen.Int[0, 20].Select(static whole => whole * 5f), Gen.Float[-10f, 110f]);
        var point = Gen.Select(coordinate, coordinate, static (x, y) => new Vector2(x, y));

        Gen.Select(point.Array[3, 80], point.Array[1, 40])
            .Sample(static (corners, points) => {
                    var outline = new PaintFillOutline(corners);

                    foreach (var at in points.Concat(corners)) {
                        Assert.Equal(EveryEdge(corners, at), outline.Contains(at));
                    }
                }
            );
    }

    /// <summary>A lasso of a thousand corners round a circle holds its middle and not what is past it.</summary>
    [Fact]
    public void A_long_lasso_holds_what_it_goes_round() {
        var corners = Enumerable.Range(0, 1000)
            .Select(static at => new Vector2(MathF.Cos(at * MathF.Tau / 1000f), MathF.Sin(at * MathF.Tau / 1000f)) * 50f
            )
            .ToArray();
        var outline = new PaintFillOutline(corners);

        Assert.True(outline.Contains(Vector2.Zero));
        Assert.True(outline.Contains(new(0f, 49f)));
        Assert.False(outline.Contains(new(0f, 51f)));
        Assert.False(outline.Contains(new(36f, 36f)));
    }

    /// <summary>
    ///     A point a hair left of the outline's leftmost corner is outside, though the edge-by-edge rule alone rounds a
    ///     crossing to its left: the edge from (35, 80.4) back to a corner at 9.2e-26 meets its height at 35 − 35, which
    ///     is 0 and not the 1.6e-17 it is. CsCheck seed <c>2adNpJwpnTs5</c> drew it (Vixen #1647).
    /// </summary>
    [Fact]
    public void A_point_left_of_every_corner_is_outside_whatever_an_edge_rounds_to() {
        Vector2[] corners = [
            new(9.22e-26f, -3.5819998e-17f), new(35f, 80.43599f), new(69.62059f, -5f), new(10f, 49.526146f)
        ];
        var outline = new PaintFillOutline(corners);
        var point = new Vector2(0f, 3.918e-24f);

        Assert.False(outline.Contains(point));
        Assert.False(EveryEdge(corners, point));
    }

    /// <summary>The even-odd rule, edge by edge, as the outline did before it kept bands.</summary>
    /// <remarks>
    ///     ⚠ <b>With the outline's bounds, as it had them then.</b> A point outside them is outside, which the rule alone
    ///     can miss: an edge's crossing is found from its far end, so a corner near zero beside one at 35 rounds the
    ///     crossing by about 35 × 2⁻²⁴, and a point between the true crossing and the rounded one counts it wrongly.
    /// </remarks>
    static bool EveryEdge(IReadOnlyList<Vector2> corners, Vector2 point) {
        var least = corners.Aggregate(new Vector2(float.MaxValue), Vector2.Min);
        var most = corners.Aggregate(new Vector2(float.MinValue), Vector2.Max);

        if (point.X < least.X || point.Y < least.Y || point.X > most.X || point.Y > most.Y) {
            return false;
        }

        var inside = false;

        for (int at = 0, before = corners.Count - 1; at < corners.Count; before = at++) {
            var (p, q) = (corners[at], corners[before]);

            if (p.Y > point.Y != q.Y > point.Y && point.X < (q.X - p.X) * (point.Y - p.Y) / (q.Y - p.Y) + p.X) {
                inside = !inside;
            }
        }

        return inside;
    }
}
