// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>The capacity readout doc 49 § 7.1 ranks sixth, and what it means that a progress bar does not.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The arithmetic is asserted against the picture and not against the property.</b> A
///         <see cref="RangeBase" /> draws its own fill — there is no element for the filled part, so
///         "half the bar is lit" is a claim only the pixels can settle, exactly as a slider's thumb
///         position is. The oracle is closed-form rather than a committed image: a reading of a half
///         covers half the rail, and four blocks at a half are two, both of which are true of any
///         theme and any width.
///     </para>
///     <para>
///         ⚠ <b>The fixture switches the corner radius off.</b> The theme rounds the ends by three
///         pixels, so the rail's last column is antialiased against the background and a count of
///         lit columns is two or three short of the number the arithmetic promises — which turns a
///         closed-form oracle back into a tolerance nobody can read. The radius is what
///         <c>ControlVisualTests</c> is for.
///     </para>
/// </remarks>
public class LevelIndicatorTests {
    const int Width = 100;
    const int Height = 12;

    /// <summary>
    ///     ⚠ <b>The thresholds' order is the direction, and this is the whole design of the
    ///     control.</b>
    /// </summary>
    /// <remarks>
    ///     A disk is worse as it fills and a battery is worse as it empties, and both are level
    ///     indicators. With two lines set the pair says which way it runs and nothing else need be;
    ///     <see cref="LevelIndicator.Direction" /> is left at its default here on purpose.
    /// </remarks>
    [Fact]
    public void The_pair_of_thresholds_says_which_way_the_reading_gets_worse() {
        using var ui = Opened();
        var disk = ui.Add<LevelIndicator>("disk");

        disk.Warning = 0.7f;
        disk.Critical = 0.9f;

        disk.Value = 0.5f;
        Assert.Equal(LevelReading.Ordinary, disk.Level);

        disk.Value = 0.7f;
        Assert.Equal(LevelReading.Warning, disk.Level);

        disk.Value = 0.95f;
        Assert.Equal(LevelReading.Critical, disk.Level);

        // The same three readings against a battery, whose thresholds run the other way.
        var battery = ui.Add<LevelIndicator>("battery");

        battery.Warning = 0.3f;
        battery.Critical = 0.1f;

        battery.Value = 0.5f;
        Assert.Equal(LevelReading.Ordinary, battery.Level);

        battery.Value = 0.3f;
        Assert.Equal(LevelReading.Warning, battery.Level);

        battery.Value = 0.05f;
        Assert.Equal(LevelReading.Critical, battery.Level);

        // ⚠ And a full battery is ordinary, which is the assertion a single `>=` for both
        // comparisons fails: it would report every charged battery as critical.
        battery.Value = 1f;
        Assert.Equal(LevelReading.Ordinary, battery.Level);
    }

    /// <summary>A threshold nobody set fires at nothing, and one set alone is a ceiling.</summary>
    [Fact]
    public void An_unset_threshold_never_fires_and_a_lone_one_reads_upward() {
        using var ui = Opened();
        var meter = ui.Add<LevelIndicator>("meter");

        meter.Value = 1f;
        Assert.Equal(LevelReading.Ordinary, meter.Level);

        meter.Warning = 0.8f;
        Assert.Equal(LevelReading.Warning, meter.Level);

        meter.Value = 0.1f;
        Assert.Equal(LevelReading.Ordinary, meter.Level);
    }

    /// <summary>
    ///     ⚠ <b>A lone <c>Critical</c> reads critical at a full charge until the direction is stated
    ///     (#1353).</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The assertion #1353 said nothing made out loud: one line and the reading at its
    ///         maximum. Inferred, a lone line is a ceiling, so a battery configured with only
    ///         <c>Critical = 0.1</c> is critical when full — the capacity reading, kept because every
    ///         disk and memory indicator with one line depends on it.
    ///     </para>
    ///     <para>
    ///         Then the same indicator told that it falls, and the class asserted as well as the
    ///         property: <see cref="LevelIndicator.Level" /> is computed on every read, so it would
    ///         come out right even if changing the direction never reached the element — the class is
    ///         what the theme colours, and it is only rewritten by a change hook.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_lone_critical_line_reads_critical_at_a_full_charge_until_the_direction_says_it_falls() {
        using var ui = Opened();
        var battery = ui.Add<LevelIndicator>("battery");

        battery.Critical = 0.1f;
        battery.Value = battery.Maximum;
        ui.Frame();

        Assert.Equal(LevelDirection.Inferred, battery.Direction);
        Assert.Equal(LevelReading.Critical, battery.Level);
        Assert.True(battery.HasClass("critical"));

        battery.Direction = LevelDirection.Falling;
        ui.Frame();

        Assert.Equal(LevelReading.Ordinary, battery.Level);
        Assert.False(battery.HasClass("critical"), "the direction changed and the theme was never told");

        battery.Value = 0.1f;
        Assert.Equal(LevelReading.Critical, battery.Level);

        battery.Value = 0.05f;
        Assert.Equal(LevelReading.Critical, battery.Level);

        // And stated back to rising, the same lone line is the ceiling the inferred default already
        // was — the default is not a third behaviour.
        battery.Direction = LevelDirection.Rising;
        Assert.Equal(LevelReading.Ordinary, battery.Level);

        battery.Value = battery.Maximum;
        Assert.Equal(LevelReading.Critical, battery.Level);
    }

    /// <summary>
    ///     ⚠ <b>A stated direction is obeyed against the pair, and the contradiction is loud rather
    ///     than silent.</b>
    /// </summary>
    /// <remarks>
    ///     The type used to refuse a direction property on the ground that one contradicting the
    ///     thresholds leaves a control "silently wrong and looking configured". This is that
    ///     contradiction, and it is not silent: a disk's rising pair told to fall reads critical at
    ///     an ordinary half-full reading.
    /// </remarks>
    [Fact]
    public void A_stated_direction_is_obeyed_against_the_pair_and_the_contradiction_shows() {
        using var ui = Opened();
        var disk = ui.Add<LevelIndicator>("disk");

        disk.Warning = 0.7f;
        disk.Critical = 0.9f;
        disk.Value = 0.5f;

        Assert.Equal(LevelReading.Ordinary, disk.Level);

        disk.Direction = LevelDirection.Falling;
        Assert.Equal(LevelReading.Critical, disk.Level);

        // Stating the direction the pair already implied changes nothing.
        disk.Direction = LevelDirection.Rising;
        Assert.Equal(LevelReading.Ordinary, disk.Level);

        disk.Value = 0.95f;
        Assert.Equal(LevelReading.Critical, disk.Level);
    }

    /// <summary>
    ///     ⚠ <b>The level reaches the stylesheet as a class, which is the only way the theme can own
    ///     the colour.</b>
    /// </summary>
    [Fact]
    public void The_level_is_written_on_the_element_for_the_theme_to_select_on() {
        using var ui = Opened();
        var meter = ui.Add<LevelIndicator>("meter");

        meter.Warning = 0.6f;
        meter.Critical = 0.9f;

        ui.Frame();
        Assert.False(meter.HasClass("warning"));
        Assert.False(meter.HasClass("critical"));

        meter.Value = 0.7f;
        ui.Frame();
        Assert.True(meter.HasClass("warning"));
        Assert.False(meter.HasClass("critical"));

        meter.Value = 0.95f;
        ui.Frame();
        Assert.False(meter.HasClass("warning"));
        Assert.True(meter.HasClass("critical"));

        // And back down again: a class that is only ever added is a control that never recovers.
        meter.Value = 0f;
        ui.Frame();
        Assert.False(meter.HasClass("warning"));
        Assert.False(meter.HasClass("critical"));
    }

    /// <summary>
    ///     ⚠ <b>A meter and not a progress bar, and it announces the reading rather than a
    ///     fraction.</b>
    /// </summary>
    /// <remarks>
    ///     "Progress bar, eighty-seven per cent" about a disk tells a listener a job is nearly
    ///     finished. The bounds of a meter are the capacity, so the number worth saying is the one
    ///     inside them — 446 of 512, not 0.87.
    /// </remarks>
    [Fact]
    public void A_meter_reports_its_role_and_its_reading_and_not_a_fraction() {
        using var ui = Opened();
        var meter = ui.Add<LevelIndicator>("meter");

        meter.Maximum = 512f;
        meter.Value = 446f;
        ui.Frame();

        Assert.Equal(AccessibleRole.Meter, meter.Role);
        Assert.Equal("446", meter.AccessibleValue);
        Assert.False(meter.AccessibleState.HasFlag(AccessibleStates.Focusable));
    }

    /// <summary>The closed-form oracle: the lit part of the rail is the reading's share of it.</summary>
    /// <remarks>
    ///     ⚠ Halving rather than an absolute count, because the absolute count is a claim about the
    ///     fixture's width and the halving is a claim about the control.
    /// </remarks>
    [Fact]
    public void The_lit_part_of_a_continuous_bar_is_the_readings_share_of_the_rail() {
        using var ui = Opened();
        var meter = ui.Add<LevelIndicator>("meter");

        meter.Value = 1f;
        ui.Frame();
        var full = Lit(ui);

        Assert.InRange(full, Width - 2, Width);

        meter.Value = 0.5f;
        ui.Frame();
        var half = Lit(ui);

        Assert.InRange((half * 2) - full, -2, 2);

        meter.Value = 0.25f;
        ui.Frame();

        Assert.InRange((Lit(ui) * 4) - full, -4, 4);

        meter.Value = 0f;
        ui.Frame();

        Assert.Equal(0, Lit(ui));
    }

    /// <summary>
    ///     ⚠ <b>The block boundary rounds rather than truncating, and the difference is one block at
    ///     every reading a meter actually sits on.</b>
    /// </summary>
    /// <remarks>
    ///     Four blocks at a half is two either way. Four at three-fifths is two truncating and two
    ///     rounding; four at seven-tenths is two truncating and <b>three</b> rounding — and a
    ///     truncating version only ever shows the last block at the very top of the range, so a
    ///     meter resting anywhere near a boundary reads one block low for as long as it rests there.
    /// </remarks>
    [Fact]
    public void A_segmented_bar_lights_whole_blocks_and_rounds_at_the_boundary() {
        using var ui = Opened();
        var meter = ui.Add<LevelIndicator>("meter");

        meter.Segments = 4;
        meter.Value = 0.5f;
        ui.Frame();

        Assert.Equal(2, Blocks(ui));

        meter.Value = 0.6f;
        ui.Frame();
        Assert.Equal(2, Blocks(ui));

        meter.Value = 0.7f;
        ui.Frame();
        Assert.Equal(3, Blocks(ui));

        meter.Value = 1f;
        ui.Frame();
        Assert.Equal(4, Blocks(ui));

        // ⚠ And the blocks are separated, which is what makes them blocks: a run count that came
        // back as one would mean the gap between them is not being drawn and the control is a
        // continuous bar wearing a property.
        meter.Value = 0.5f;
        ui.Frame();
        Assert.Equal(2, Runs(ui));
    }

    /// <summary>A negative count is a continuous bar rather than a crash.</summary>
    [Fact]
    public void A_negative_segment_count_is_refused_at_the_property() {
        using var ui = Opened();
        var meter = ui.Add<LevelIndicator>("meter");

        meter.Segments = -3;

        Assert.Equal(0, meter.Segments);
    }

    static UiTest Opened() =>
        ControlHarness.Open(
            Width,
            Height,
            $"level-indicator {{ position: absolute; left: 0px; top: 0px; width: {Width}px; height: 8px; border-radius: 0px; }}"
        );

    /// <summary>How many columns of the rail's middle row are drawn in the fill colour.</summary>
    /// <remarks>
    ///     ⚠ Read off the red channel, which is the one the two colours are far apart in: the
    ///     light palette's <c>--fill-color</c> is <c>#3b6cf0</c> and its <c>--track-color</c> is
    ///     <c>#e3e6eb</c>, so a threshold at the halfway mark separates them with a margin either
    ///     way rather than by a shade. A test that compared whole colours would be a test about the
    ///     palette.
    /// </remarks>
    static int Lit(UiTest ui) {
        var image = ui.Capture();
        var y = 4;
        var count = 0;

        for (var x = 0; x < image.Width; x++) {
            if (image.Pixels[image.Offset(x, y)] < 150) {
                count++;
            }
        }

        return count;
    }

    /// <summary>How many separated runs of lit columns there are along the rail's middle row.</summary>
    static int Runs(UiTest ui) {
        var image = ui.Capture();
        var y = 4;
        var runs = 0;
        var inside = false;

        for (var x = 0; x < image.Width; x++) {
            var lit = image.Pixels[image.Offset(x, y)] < 150;

            if (lit && !inside) {
                runs++;
            }

            inside = lit;
        }

        return runs;
    }

    /// <summary>How many whole blocks are lit, from the share of the rail they cover.</summary>
    /// <remarks>
    ///     Counted from the area rather than from the runs, so that the assertion is about which
    ///     blocks are lit and not about whether the gaps happened to land on a pixel boundary.
    /// </remarks>
    static int Blocks(UiTest ui) => (int)MathF.Round(Lit(ui) * 4f / Width);
}
