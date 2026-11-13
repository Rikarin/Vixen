// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Texturing.Painting;
using Xunit;

namespace Vixen.Texturing.Tests;

/// <summary>Flow jitter, scatter across the path and stamps that do not build up, on an atlas that is all surface.</summary>
public class PaintStampSettingsTests {
    const int Side = 256;
    const uint Red = 0xFF0000FFu;

    static readonly PaintBrush Hard = PaintBrush.Default with { Radius = 4f, Flow = 1f, Falloff = 0f };

    static PaintImage Paint(PaintBrush brush, params Vector2[] moves) {
        PaintImage image = new(Side, Side);
        PaintStroke stroke = new(image, PaintCoverage.Everywhere(Side, Side), brush, Red, 0);

        foreach (var move in moves) {
            stroke.MoveTo(move);
        }

        return image;
    }

    static int Alpha(PaintImage image, int x, int y) => (int)(image.At(x, y) >> 24);

    /// <summary>The middle of what is painted between two columns.</summary>
    static Vector2 Middle(PaintImage image, int from, int to) {
        var (sum, count) = (Vector2.Zero, 0);

        for (var y = 0; y < Side; y++) {
            for (var x = from; x < to; x++) {
                if (image.At(x, y) != 0u) {
                    sum += new Vector2(x + 0.5f, y + 0.5f);
                    count++;
                }
            }
        }

        Assert.True(count > 0, $"Nothing was painted between columns {from} and {to}.");
        return sum / count;
    }

    /// <summary>
    ///     A quarter flow dragged slowly: accumulating, the overlapping stamps build up to nearly all of it; at the
    ///     maximum, the stroke is a quarter wherever it went.
    /// </summary>
    [Fact]
    public void Stamps_that_take_the_maximum_never_build_past_the_flow() {
        var brush = Hard with { Radius = 12f, Flow = 0.25f, Spacing = 0.05f };
        var built = Paint(brush, new(60f, 128f), new(200f, 128f));
        var flat = Paint(brush with { Blend = PaintStampBlend.Maximum }, new(60f, 128f), new(200f, 128f));

        Assert.InRange(Alpha(built, 128, 128), 240, 255);
        Assert.InRange(Alpha(flat, 128, 128), 63, 65);
        Assert.InRange(Alpha(flat, 128, 134), 63, 65);
    }

    /// <summary>Stamps far enough apart to tell apart: each a different strength, and every one where it was.</summary>
    [Fact]
    public void Flow_jitter_varies_each_stamp_and_moves_none() {
        var brush = Hard with { Spacing = 4f, Blend = PaintStampBlend.Maximum };
        var steady = Paint(brush, new(20f, 128f), new(236f, 128f));
        var jittered = Paint(brush with { FlowJitter = 1f }, new(20f, 128f), new(236f, 128f));
        HashSet<int> strengths = [];

        for (var column = 20; column < 236; column += 16) {
            Assert.Equal(255, Alpha(steady, column, 128));
            Assert.Equal(Middle(steady, column - 8, column + 8), Middle(jittered, column - 8, column + 8));
            strengths.Add(Alpha(jittered, column, 128));
        }

        Assert.True(strengths.Count > 6, $"Thirteen stamps came in {strengths.Count} strengths.");
        Assert.DoesNotContain(0, strengths);
    }

    /// <summary>Scattered across a level path, a stamp moves up and down and never along it.</summary>
    [Fact]
    public void Scatter_across_the_path_keeps_each_stamp_at_its_place_along_it() {
        var brush = Hard with { Spacing = 4f, PositionJitter = 1f };
        var steady = Paint(brush with { PositionJitter = 0f }, new(20f, 128f), new(236f, 128f));
        var across = Paint(brush with { ScatterAxis = PaintScatterAxis.Across }, new(20f, 128f), new(236f, 128f));
        var around = Paint(brush, new(20f, 128f), new(236f, 128f));
        var (strayedAcross, strayedAlong) = (0f, 0f);

        for (var column = 36; column < 236; column += 16) {
            var home = Middle(steady, column - 8, column + 8);
            var moved = Middle(across, column - 8, column + 8);

            Assert.Equal(home.X, moved.X, 0.6f);
            strayedAcross = MathF.Max(strayedAcross, MathF.Abs(moved.Y - home.Y));
            strayedAlong = MathF.Max(strayedAlong, MathF.Abs(Middle(around, column - 8, column + 8).X - home.X));
        }

        Assert.True(strayedAcross > 1f, "No stamp strayed across the path, so the test says nothing.");
        Assert.True(strayedAlong > 1f, "Scatter both ways never moved a stamp along, so the comparison says nothing.");
    }

    /// <summary>One dab in the middle, under a pen.</summary>
    static PaintImage Dabbed(PaintBrush brush, PaintPen pen) {
        PaintImage image = new(Side, Side);
        PaintStroke stroke = new(image, PaintCoverage.Everywhere(Side, Side), brush, Red, 0);

        stroke.Dab(new(128f, 128f), null, pen);
        return image;
    }

    /// <summary>How many columns and rows the paint spans.</summary>
    static (int Wide, int High) Extent(PaintImage image) {
        var (left, right, top, bottom) = (Side, -1, Side, -1);

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                if (image.At(x, y) == 0u) {
                    continue;
                }

                (left, right) = (Math.Min(left, x), Math.Max(right, x));
                (top, bottom) = (Math.Min(top, y), Math.Max(bottom, y));
            }
        }

        return right < 0 ? (0, 0) : (right - left + 1, bottom - top + 1);
    }

    /// <summary>A pen laid flat paints the whole radius; held upright, it paints what the tilt size leaves.</summary>
    [Fact]
    public void A_pen_laid_over_paints_wider_than_one_held_upright() {
        var brush = Hard with { Radius = 20f, TiltSize = 0.5f };
        var flat = Extent(Dabbed(brush, new(1f, Tilt: 1f)));
        var upright = Extent(Dabbed(brush, PaintPen.Upright));
        var untouched = Extent(Dabbed(brush with { TiltSize = 0f }, PaintPen.Upright));

        Assert.InRange(flat.Wide, 38, 42);
        Assert.InRange(upright.Wide, 18, 22);
        Assert.Equal(flat, untouched);
    }

    /// <summary>
    ///     A stretched stamp lies along x; the pen's barrel turned a quarter lays it along y, its stretch with it, and
    ///     a brush that does not follow the pen ignores the turn.
    /// </summary>
    [Fact]
    public void A_pen_turns_the_whole_stamp_as_the_brush_says() {
        var brush = Hard with { Radius = 16f, Aspect = 4f };
        var turned = new PaintPen(1f, Tilt: 0.5f, Azimuth: 0f, Twist: MathF.PI / 2f);
        var still = Extent(Dabbed(brush, PaintPen.Upright));
        var twisted = Extent(Dabbed(brush with { PenAngle = PaintPenAngle.Twist }, turned));
        var leaned = Extent(Dabbed(brush with { PenAngle = PaintPenAngle.Tilt }, turned));
        var ignored = Extent(Dabbed(brush, turned));

        Assert.True(still.Wide > still.High * 3, $"The stretched stamp is {still.Wide}×{still.High}.");
        Assert.Equal((still.High, still.Wide), twisted);
        Assert.Equal(still, leaned);
        Assert.Equal(still, ignored);
    }

    /// <summary>A drag whose pen turns through 0 turns its stamps the short way, through 0 and not through π.</summary>
    [Fact]
    public void A_pen_turns_between_two_places_the_short_way_round() {
        var from = new PaintPen(1f, Twist: MathF.Tau - 0.2f);
        var to = new PaintPen(0f, Twist: 0.2f);
        var middle = from.Towards(to, 0.5f);

        Assert.Equal(0.5f, middle.Pressure, 1e-5f);
        Assert.Equal(0f, MathF.IEEERemainder(middle.Twist, MathF.Tau), 1e-4f);
    }
}
