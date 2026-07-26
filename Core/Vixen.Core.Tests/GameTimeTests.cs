// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Core.Tests;

/// <summary>
///     Frame time. The two clocks — scaled for gameplay, unscaled for everything that must keep
///     running while the game is paused — are the whole point, so most of these tests are about
///     keeping them apart.
/// </summary>
public class GameTimeTests {
    static readonly TimeSpan Sixty = TimeSpan.FromSeconds(1.0 / 60.0);

    [Fact]
    public void Zero_starts_before_the_first_frame_at_normal_speed() {
        Assert.Equal(TimeSpan.Zero, GameTime.Zero.Total);
        Assert.Equal(0, GameTime.Zero.FrameCount);
        Assert.Equal(1f, GameTime.Zero.TimeScale);
        Assert.False(GameTime.Zero.IsPaused);
    }

    [Fact]
    public void Advancing_accumulates_the_total_and_counts_the_frame() {
        var time = GameTime.Zero.Advance(Sixty).Advance(Sixty).Advance(Sixty);

        Assert.Equal(3, time.FrameCount);
        Assert.Equal(Sixty * 3, time.Total);
        Assert.Equal(Sixty, time.Elapsed);
    }

    [Fact]
    public void Time_scale_stretches_the_gameplay_clock_and_leaves_the_wall_clock_alone() {
        var time = GameTime.Zero.Advance(Sixty, 0.5f);

        Assert.Equal(Sixty * 0.5, time.Elapsed);
        Assert.Equal(Sixty, time.UnscaledElapsed);
        Assert.Equal(Sixty * 0.5, time.Total);
        Assert.Equal(0.5f, time.TimeScale);
    }

    [Fact]
    public void A_paused_frame_still_advances_the_unscaled_clock() {
        var time = GameTime.Zero.Advance(Sixty).Advance(Sixty, 0f);

        Assert.True(time.IsPaused);
        Assert.Equal(TimeSpan.Zero, time.Elapsed);
        Assert.Equal(Sixty, time.UnscaledElapsed);

        // The total stopped where the pause began; the frame still happened.
        Assert.Equal(Sixty, time.Total);
        Assert.Equal(2, time.FrameCount);
    }

    [Fact]
    public void Seconds_accessors_match_the_timespans_they_come_from() {
        var time = GameTime.Zero.Advance(TimeSpan.FromMilliseconds(20), 0.5f);

        Assert.Equal(0.01f, time.DeltaSeconds, 6);
        Assert.Equal(0.02f, time.UnscaledDeltaSeconds, 6);
        Assert.Equal(0.01, time.TotalSeconds, 9);
    }

    [Fact]
    public void Time_does_not_run_backwards() {
        Assert.Throws<ArgumentOutOfRangeException>(() => GameTime.Zero.Advance(TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => GameTime.Zero.Advance(Sixty, -1f));
    }

    [Fact]
    public void Advancing_leaves_the_previous_value_untouched() {
        // It is a value type on purpose: no subsystem can advance time for another one.
        var first = GameTime.Zero.Advance(Sixty);
        var second = first.Advance(Sixty);

        Assert.Equal(1, first.FrameCount);
        Assert.Equal(2, second.FrameCount);
    }

    [Fact]
    public void ToString_is_readable_and_formats_into_a_buffer() {
        var time = GameTime.Zero.Advance(TimeSpan.FromMilliseconds(16.667));
        Span<char> buffer = stackalloc char[128];

        Assert.True(time.TryFormat(buffer, out var written));
        Assert.Equal(time.ToString(), new(buffer[..written]));
        Assert.Contains("frame 1", time.ToString(), StringComparison.Ordinal);
        Assert.Contains("16.67ms", time.ToString(), StringComparison.Ordinal);
    }
}
