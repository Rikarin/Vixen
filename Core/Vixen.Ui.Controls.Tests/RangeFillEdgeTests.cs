// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>
///     ⚠ <b>No bar shows its track through the antialiased edge of its fill</b> — the defect
///     <see cref="Gauge" /> was found with in its first picture, measured on the four bars that shared
///     its draw order (#1366).
/// </summary>
/// <remarks>
///     <para>
///         Every bar drew the whole track and then the fill over it, so the two shapes shared the
///         fill's edge. If two shapes each cover an edge pixel by half, a quarter of the lower one
///         still shows: a track-coloured fringe along the fill's long edges and its rounded start, and
///         on a full bar a halo all the way round.
///     </para>
///     <para>
///         ⚠ <b>The oracle is the gauge's, and it is closed-form.</b> In the light palette the track
///         (<c>#e3e6eb</c>) is the only thing in the frame bright in red: the fill (<c>#3b6cf0</c>) is
///         59 and the harness's background nearly black. So over the span where only fill and
///         background should meet, a pixel with more red than the brighter of those two has track in
///         it. The span stops short of the fill's far end, where fill meeting track is the picture
///         and not a defect, and short of a slider's thumbs, which are white.
///     </para>
///     <para>
///         The bars are drawn thick — a 12 px rail, a 20 px bar — so the edges are rows of pixels
///         rather than one, and the fringe, when it is there, is dozens of pixels and not a stray
///         one.
///     </para>
/// </remarks>
public class RangeFillEdgeTests {
    const int Width = 200;
    const int Height = 60;

    /// <summary>
    ///     A slider's thumb: 41 px, so its rail is 12.3 px (three tenths of it) and both its long edges
    ///     fall between pixel rows — which is where a fringe can be. On whole rows there is no partial
    ///     pixel along the edge to show one, and the first version of this file passed on the defect.
    /// </summary>
    const float Thumb = 41f;

    const float RailLeft = 10f + (Thumb / 2f);
    const float RailWidth = 180f - Thumb;

    /// <summary>A bar's box: 180 × 20 at (10, 20), so its ends are 10 px round.</summary>
    /// <remarks>
    ///     ⚠ <b>A bar's long edges cannot be put between pixel rows, and that narrows the defect for
    ///     two of the four.</b> A progress bar and a level indicator fill their own box, and layout
    ///     snaps every box to whole device pixels (<c>LayoutTree.RoundToPixelGrid</c>), so their long
    ///     edges carry no partial pixel for a track to show through — asking for 20.3 and 19.4 gets
    ///     20 and 20. Their fringe is at the rounded ends. A slider's rail is a computed three tenths
    ///     of its thumb, centred, so its edges land wherever the arithmetic puts them.
    /// </remarks>
    const float BarLeft = 10f;
    const float BarWidth = 180f;
    const float BarRadius = 10f;

    [Fact]
    public void A_full_slider_shows_no_track_round_its_fill() {
        using var ui = Opened();
        var slider = ui.Add<Slider>();

        slider.Value = 1f;
        ui.Frame();

        AssertNoTrackIn(ui, 0, ThumbLeft(1f) - 2, "a full slider");
    }

    [Fact]
    public void A_partial_slider_shows_no_track_along_its_fill() {
        using var ui = Opened();
        var slider = ui.Add<Slider>();

        slider.Value = 0.6f;
        ui.Frame();

        AssertNoTrackIn(ui, 0, ThumbLeft(0.6f) - 2, "a slider at 0.6");
    }

    [Fact]
    public void A_range_slider_shows_no_track_along_its_span() {
        using var ui = Opened();
        var range = ui.Add<RangeSlider>();

        range.High = 1f;
        range.Low = 0f;
        ui.Frame();
        AssertNoTrackIn(ui, ThumbLeft(0f) + (int)Thumb + 2, ThumbLeft(1f) - 2, "a range slider spanning the rail");

        range.Low = 0.25f;
        range.High = 0.75f;
        ui.Frame();
        AssertNoTrackIn(ui, ThumbLeft(0.25f) + (int)Thumb + 2, ThumbLeft(0.75f) - 2, "a range slider from 0.25 to 0.75");
    }

    [Fact]
    public void A_progress_bar_shows_no_track_round_its_fill() {
        using var ui = Opened();
        var bar = ui.Add<ProgressBar>();

        bar.Value = 0.6f;
        ui.Frame();
        AssertNoTrackIn(ui, 0, BarEnd(0.6f) - (int)BarRadius - 2, "a progress bar at 0.6");

        bar.Value = 1f;
        ui.Frame();
        AssertNoTrackIn(ui, 0, Width, "a full progress bar");
    }

    /// <summary>An indeterminate sweep at the start of its travel, where its rounded start is the bar's.</summary>
    /// <remarks>
    ///     A sweep in the middle of the bar has track on both sides and, the bar's long edges being on
    ///     whole rows, nothing bordering background that a track could show through — that case is
    ///     clean with either draw order and proves nothing. At phase 0.2 it runs from 0 to 0.26, so
    ///     its start cap is the bar's own start and borders background, as a determinate fill's does.
    /// </remarks>
    [Fact]
    public void An_indeterminate_progress_bar_shows_no_track_round_its_sweep() {
        using var ui = Opened();
        var bar = ui.Add<ProgressBar>();

        bar.IsIndeterminate = true;
        bar.Phase = 0.2f;
        ui.Frame();

        AssertNoTrackIn(ui, 0, BarEnd(0.26f) - (int)BarRadius - 2, "an indeterminate sweep at its start");
    }

    [Fact]
    public void A_level_indicator_shows_no_track_round_its_fill() {
        using var ui = Opened();
        var level = ui.Add<LevelIndicator>();

        level.Value = 0.6f;
        ui.Frame();
        AssertNoTrackIn(ui, 0, BarEnd(0.6f) - (int)BarRadius - 2, "a level indicator at 0.6");

        level.Value = 1f;
        ui.Frame();
        AssertNoTrackIn(ui, 0, Width, "a full level indicator");
    }

    /// <summary>
    ///     ⚠ <b>The instrument: the same classifier, over the same columns, finds the track when there
    ///     is track there to find.</b>
    /// </summary>
    /// <remarks>
    ///     Without this every assertion above could be passing because the classifier sees nothing,
    ///     the palette changed, or the bars stopped drawing a track at all.
    /// </remarks>
    [Fact]
    public void The_classifier_finds_the_track_of_an_empty_bar() {
        using var ui = Opened();
        var bar = ui.Add<ProgressBar>();

        bar.Value = 0f;
        ui.Frame();

        var image = ui.Capture();
        var track = 0;

        for (var y = 0; y < image.Height; y++) {
            for (var x = 0; x < image.Width; x++) {
                if (image.Pixels[image.Offset(x, y)] > 120) {
                    track++;
                }
            }
        }

        // 180 × 20 less the rounding at the ends.
        Assert.True(track > 3000, $"an empty 180 × 20 bar shows only {track} pixels of track");
    }

    /// <summary>
    ///     ⚠ <b>And the join does not open a seam of background.</b> Drawing the track only where the
    ///     fill is not is the fix; drawing it as a second pill starting where the fill ends would
    ///     leave two round ends touching at a point, with the background showing between them.
    /// </summary>
    /// <remarks>
    ///     Along the middle half of the bar's thickness every pixel from end to end is fill, track, or
    ///     a blend of the two — never darker than the fill, which is the darker of them. Background
    ///     showing through at the join is exactly a pixel darker than both.
    /// </remarks>
    [Fact]
    public void A_partial_bar_has_no_seam_where_the_fill_meets_the_track() {
        using var ui = Opened();
        var bar = ui.Add<ProgressBar>();

        foreach (var value in (float[])[0.3f, 0.6f, 0.61f, 0.625f]) {
            bar.Value = value;
            ui.Frame();

            var image = ui.Capture();
            var middle = 30;
            var fill = Sum(image, BarLeft + BarRadius + 4, middle);

            for (var y = middle - 5; y < middle + 5; y++) {
                for (var x = (int)(BarLeft + BarRadius); x < (int)(BarLeft + BarWidth - BarRadius); x++) {
                    var sum = Sum(image, x, y);

                    Assert.True(
                        sum >= fill - 12,
                        $"at {value} the pixel ({x}, {y}) is {sum} against a fill of {fill}: background showing between the fill and the track"
                    );
                }
            }
        }
    }

    /// <summary>
    ///     ⚠ <b>Standing up, too</b> — fraction zero is the bottom of a vertical bar, so the fill runs
    ///     up from its lower end and the track is the piece above it.
    /// </summary>
    /// <remarks>
    ///     The track is drawn in pieces placed by fraction, and a piece placed from the wrong end is a
    ///     track drawn over the fill rather than beside it: this is where that would show.
    /// </remarks>
    [Fact]
    public void A_standing_bar_shows_no_track_round_its_fill() {
        using var ui = ControlHarness.Open(
            Width,
            Width,
            "progress-bar { position: absolute; left: 20px; top: 10px; width: 20px; height: 180px; }"
        );

        var bar = ui.Add<ProgressBar>();
        bar.Orientation = Orientation.Vertical;
        bar.Value = 0.6f;
        ui.Frame();

        // The fill is the bottom 108 px, from y = 82 to 190: everything below its rounded top.
        AssertNoTrackIn(ui, 0, Width, "a standing bar at 0.6", top: 82 + (int)BarRadius + 2);

        bar.Value = 1f;
        ui.Frame();
        AssertNoTrackIn(ui, 0, Width, "a full standing bar");
    }

    static int Sum(Bitmap image, float x, int y) {
        var at = image.Offset((int)x, y);

        return image.Pixels[at] + image.Pixels[at + 1] + image.Pixels[at + 2];
    }

    /// <summary>
    ///     Asserts that between two columns, from a row down, no pixel carries more red than fill and
    ///     background can explain, and that the columns really are fill and not an empty stretch.
    /// </summary>
    static void AssertNoTrackIn(UiTest ui, int from, int to, string what, int top = 0) {
        var image = ui.Capture();
        from = Math.Max(0, from);
        to = Math.Min(image.Width, to);
        top = Math.Clamp(top, 0, image.Height);

        var background = image.Pixels[image.Offset(1, 1)];
        var reds = new Dictionary<int, int>();
        var filled = 0;

        for (var y = top; y < image.Height; y++) {
            for (var x = from; x < to; x++) {
                var at = image.Offset(x, y);

                if (image.Pixels[at + 2] - image.Pixels[at] > 100) {
                    filled++;
                    reds[image.Pixels[at]] = reds.GetValueOrDefault(image.Pixels[at]) + 1;
                }
            }
        }

        // A floor on the instrument: a span with no fill in it makes "no track" true of an empty bar.
        Assert.True(filled > (to - from) * 4, $"{what}: only {filled} fill pixels between x = {from} and {to}");

        // ⚠ The fill's red is the commonest red among blue pixels, and not the largest. A fringe pixel
        // mostly covered by the fill is still blue enough to pass the test above, so the largest red
        // among them is the defect's own value, and a ceiling built on it lets the defect through.
        var fillRed = reds.MaxBy(static pair => pair.Value).Key;

        var ceiling = Math.Max(fillRed, background) + 3;
        var halo = new List<(int X, int Y, int R)>();

        for (var y = top; y < image.Height; y++) {
            for (var x = from; x < to; x++) {
                var red = image.Pixels[image.Offset(x, y)];

                if (red > ceiling) {
                    halo.Add((x, y, red));
                }
            }
        }

        Assert.True(
            halo.Count == 0,
            $"{what}: {halo.Count} pixels show the track through the fill's edge, the first at ({halo.FirstOrDefault().X}, {halo.FirstOrDefault().Y}) with red {halo.FirstOrDefault().R} over {ceiling}"
        );
    }

    /// <summary>Where a slider's thumb box starts, at a reading.</summary>
    static int ThumbLeft(float fraction) => (int)MathF.Floor(RailLeft + (RailWidth * fraction) - (Thumb / 2f));

    /// <summary>Where a bar's fill ends, at a reading.</summary>
    static int BarEnd(float fraction) => (int)MathF.Round(BarLeft + (BarWidth * fraction));

    static UiTest Opened() =>
        ControlHarness.Open(
            Width,
            Height,
            $"slider, range-slider {{ position: absolute; left: 10px; top: 10px; width: 180px; height: 40px; --thumb-size: {Thumb}px; }} "
            + "progress-bar, level-indicator { position: absolute; left: 10px; top: 20px; width: 180px; height: 20px; }"
        );
}
