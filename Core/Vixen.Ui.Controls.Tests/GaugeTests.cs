// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>The dial half of doc 49 § 7.1's rank 6: a level indicator's reading, drawn as an arc.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The geometry is asserted against the picture, with closed-form oracles.</b> An annulus
///         sector's area is proportional to its angle, so the lit area at half a reading is half the
///         lit area at a whole one, whatever the palette, the size or the arc's width. And a quarter
///         of the reading lies wholly in the dial's left half and starts below its middle, which is
///         what "opens at the bottom and fills clockwise from its lower left" means in pixels — a dial
///         that ran the other way, or began at the top, fails it.
///     </para>
///     <para>
///         The arc is made thick (a fifth of the side) so that the counts are hundreds of pixels
///         rather than tens, which keeps the antialiased rim a small share of any of them.
///     </para>
/// </remarks>
public class GaugeTests {
    const int Side = 64;
    const int Centre = Side / 2;

    /// <summary>
    ///     ⚠ <b>The lit area is the reading's share of the dial's area</b>, because a sector's area is
    ///     proportional to its angle.
    /// </summary>
    [Fact]
    public void The_lit_area_is_the_readings_share_of_the_dial() {
        using var ui = Opened();
        var gauge = ui.Add<Gauge>("gauge");

        gauge.Value = 1f;
        ui.Frame();
        var full = Lit(ui).Count;

        // A floor on the instrument before any ratio is taken: a dial that drew nothing, or drew it
        // in a colour this does not count, makes every ratio below a division by zero or by noise.
        Assert.True(full > 500, $"a full dial lit only {full} pixels");

        gauge.Value = 0.5f;
        ui.Frame();
        Assert.InRange(Lit(ui).Count / (float) full, 0.47f, 0.53f);

        gauge.Value = 0.25f;
        ui.Frame();
        Assert.InRange(Lit(ui).Count / (float) full, 0.22f, 0.28f);

        gauge.Value = 0f;
        ui.Frame();
        Assert.Empty(Lit(ui));
    }

    /// <summary>
    ///     ⚠ <b>It opens at the bottom and fills clockwise from its lower left</b> — a speedometer,
    ///     and not a clock hand starting at twelve.
    /// </summary>
    /// <remarks>
    ///     With three quarters of a turn the dial starts at 135° (lower left, y pointing down) and a
    ///     quarter of the reading reaches 202.5°, just past the left edge. So every lit pixel is left
    ///     of the middle and some are below it. A counter-clockwise dial lights the lower <i>right</i>;
    ///     one starting at the top lights the upper right.
    /// </remarks>
    [Fact]
    public void A_quarter_reading_lies_in_the_lower_left_and_the_gap_is_at_the_bottom() {
        using var ui = Opened();
        var gauge = ui.Add<Gauge>("gauge");

        gauge.Value = 0.25f;
        ui.Frame();

        var quarter = Lit(ui);

        Assert.NotEmpty(quarter);
        Assert.All(quarter, pixel => Assert.True(pixel.X < Centre, $"({pixel.X}, {pixel.Y}) is right of the middle"));
        Assert.Contains(quarter, pixel => pixel.Y > Centre + 4);

        // And full, the open quarter is at the bottom: nothing lit straight below the middle, and
        // the top of the dial lit.
        gauge.Value = 1f;
        ui.Frame();

        var full = Lit(ui);

        Assert.DoesNotContain(full, pixel => pixel.X == Centre && pixel.Y > Centre);
        Assert.Contains(full, pixel => pixel.X == Centre && pixel.Y < Centre);
    }

    /// <summary>
    ///     ⚠ <b>The level is the level indicator's, by the same arithmetic</b> — including the lone
    ///     falling line of #1353 — and it reaches the element as the same two classes.
    /// </summary>
    [Fact]
    public void The_level_is_a_level_indicators_and_reaches_the_theme_as_a_class() {
        using var ui = Opened();
        var gauge = ui.Add<Gauge>("gauge");
        var bar = ui.Add<LevelIndicator>("bar");

        foreach (var (warning, critical, direction) in new[] {
                     (0.7f, 0.9f, LevelDirection.Inferred),
                     (0.3f, 0.1f, LevelDirection.Inferred),
                     (float.NaN, 0.1f, LevelDirection.Inferred),
                     (float.NaN, 0.1f, LevelDirection.Falling)
                 }) {
            gauge.Warning = bar.Warning = warning;
            gauge.Critical = bar.Critical = critical;
            gauge.Direction = bar.Direction = direction;

            foreach (var value in new[] { 0f, 0.05f, 0.1f, 0.3f, 0.5f, 0.7f, 0.9f, 1f }) {
                gauge.Value = bar.Value = value;
                Assert.Equal(bar.Level, gauge.Level);
            }
        }

        gauge.Warning = 0.6f;
        gauge.Critical = 0.9f;
        gauge.Direction = LevelDirection.Inferred;

        gauge.Value = 0.7f;
        ui.Frame();
        Assert.True(gauge.HasClass("warning"));
        Assert.False(gauge.HasClass("critical"));

        gauge.Value = 0.95f;
        ui.Frame();
        Assert.False(gauge.HasClass("warning"));
        Assert.True(gauge.HasClass("critical"));

        gauge.Value = 0.2f;
        ui.Frame();
        Assert.False(gauge.HasClass("warning"));
        Assert.False(gauge.HasClass("critical"));
    }

    /// <summary>
    ///     ⚠ <b>A full dial is fill and background with no track showing round its rim</b>, because
    ///     the track is not drawn under the fill.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Found in the first picture of this control rather than by any count: drawn whole and
    ///         then covered, the track showed through every antialiased pixel of the fill's edge — a
    ///         grey halo round a full dial and a light pixel at a partial one's start, since two
    ///         shapes that both cover a pixel by half leave a quarter of the lower one visible.
    ///     </para>
    ///     <para>
    ///         The track is the only light thing in the frame: its red channel is far above both the
    ///         fill's and the harness background's, so any pixel with more red than the brighter of
    ///         those two has some track in it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_full_dial_shows_no_track_through_the_edge_of_its_fill() {
        using var ui = Opened();
        var gauge = ui.Add<Gauge>("gauge");

        gauge.Value = 0f;
        ui.Frame();

        // The instrument: the track is there to be seen, and it is bright in red.
        Assert.Contains(Pixels(ui), pixel => pixel.R > 120);

        gauge.Value = 1f;
        ui.Frame();

        var image = ui.Capture();
        var fill = image.Pixels[image.Offset(Centre, 3)];
        var background = image.Pixels[image.Offset(Centre, Centre)];
        var ceiling = Math.Max(fill, background) + 3;

        var halo = Pixels(ui).Where(pixel => pixel.R > ceiling).ToList();

        Assert.True(halo.Count == 0, $"{halo.Count} pixels of a full dial show the track, the first at ({halo.FirstOrDefault().X}, {halo.FirstOrDefault().Y}) with red {halo.FirstOrDefault().R} over {ceiling}");
    }

    /// <summary>A meter, announced by its reading, and not a tab stop.</summary>
    [Fact]
    public void A_gauge_is_a_meter_that_announces_its_reading() {
        using var ui = Opened();
        var gauge = ui.Add<Gauge>("gauge");

        gauge.Maximum = 120f;
        gauge.Value = 87f;
        ui.Frame();

        Assert.Equal(AccessibleRole.Meter, gauge.Role);
        Assert.Equal("87", gauge.AccessibleValue);
        Assert.False(gauge.AccessibleState.HasFlag(AccessibleStates.Focusable));
    }

    /// <summary>A sweep of nothing would draw nothing and look like a gauge that failed to load.</summary>
    [Fact]
    public void The_sweep_is_held_between_a_tenth_of_a_turn_and_a_whole_one() {
        using var ui = Opened();
        var gauge = ui.Add<Gauge>("gauge");

        gauge.Sweep = 0f;
        Assert.Equal(0.1f, gauge.Sweep);

        gauge.Sweep = 3f;
        Assert.Equal(1f, gauge.Sweep);
    }

    static UiTest Opened() =>
        ControlHarness.Open(
            Side,
            Side,
            $"gauge {{ position: absolute; left: 0px; top: 0px; width: {Side}px; height: {Side}px; --arc-width: 13px; }} "
            + "level-indicator { position: absolute; left: 0px; top: 0px; width: 1px; height: 1px; }"
        );

    /// <summary>The pixels drawn mostly in the fill colour.</summary>
    /// <remarks>
    ///     <para>
    ///         The light palette's <c>--fill-color</c> is <c>#3b6cf0</c>, its <c>--track-color</c>
    ///         <c>#e3e6eb</c> and the harness's background nearly black, so blue exceeding red by a
    ///         hundred levels is the fill and nothing else, whether the rasterizer stores it linear or
    ///         encoded.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Blue over red, and not "red low and blue high".</b> The first version of this
    ///         classifier counted the track's antialiased rim — a grey of about 138, 143, 151, which is
    ///         under 150 in red and over it in blue — so an empty dial had lit pixels.
    ///     </para>
    /// </remarks>
    static List<(int X, int Y)> Lit(UiTest ui) =>
        Pixels(ui).Where(static pixel => pixel.B - pixel.R > 100).Select(static pixel => (pixel.X, pixel.Y)).ToList();

    static List<(int X, int Y, int R, int B)> Pixels(UiTest ui) {
        var image = ui.Capture();
        var pixels = new List<(int X, int Y, int R, int B)>(image.Width * image.Height);

        for (var y = 0; y < image.Height; y++) {
            for (var x = 0; x < image.Width; x++) {
                var at = image.Offset(x, y);
                pixels.Add((x, y, image.Pixels[at], image.Pixels[at + 2]));
            }
        }

        return pixels;
    }
}
