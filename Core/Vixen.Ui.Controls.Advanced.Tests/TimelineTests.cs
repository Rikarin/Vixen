// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>Tracks, keys, the playhead, zoom, snapping and selection.</summary>
public class TimelineTests {
    static Timeline Built(AdvancedFixture fixture, int tracks = 3, int keys = 4) {
        var timeline = fixture.Add<Timeline>();

        for (var i = 0; i < tracks; i++) {
            var track = timeline.AddTrack($"track{i}");

            for (var j = 0; j < keys; j++) {
                track.Add(j * 0.5f);
            }
        }

        fixture.Update();
        timeline.Refresh();
        fixture.Update();

        fixture.Document.Focus(timeline);
        return timeline;
    }

    [Fact]
    public void Keys_stay_in_time_order() {
        var track = new TimelineTrack("t");

        track.Add(2f);
        track.Add(0.5f);
        track.Add(1f);

        Assert.Equal([0.5f, 1f, 2f], track.Keys.Select(static key => key.Time));
    }

    [Fact]
    public void Time_and_pixels_are_related_by_one_number() {
        using var fixture = new AdvancedFixture();
        var timeline = Built(fixture);

        timeline.PixelsPerSecond = 200f;
        timeline.TimeStart = 1f;

        var x = timeline.ToScreen(2f);

        Assert.Equal(timeline.Lanes.AbsoluteLeft + 200f, x, 2);
        Assert.Equal(2f, timeline.ToTime(x), 4);
    }

    [Fact]
    public void The_ruler_steps_through_one_two_and_five() {
        using var fixture = new AdvancedFixture();
        var timeline = Built(fixture);

        // ⚠ A tick every 0.037 seconds is arithmetically fine and unreadable. Every ruler anybody
        // has used walks a 1-2-5 ladder so the labels are numbers a person can hold.
        foreach (var scale in (float[]) [10f, 37f, 100f, 250f, 1_000f]) {
            timeline.PixelsPerSecond = scale;

            var step = timeline.TickStep;
            var mantissa = step / MathF.Pow(10f, MathF.Round(MathF.Log10(step)));

            Assert.True(
                new[] { 0.1f, 0.2f, 0.5f, 1f, 2f, 5f }.Any(allowed => MathF.Abs(mantissa - allowed) < 0.01f),
                $"step {step} at {scale} px/s"
            );
        }
    }

    [Fact]
    public void The_ruler_labels_only_what_is_on_screen() {
        using var fixture = new AdvancedFixture();
        var timeline = Built(fixture);

        timeline.PixelsPerSecond = 60f;
        timeline.Refresh();
        fixture.Update();

        var live = timeline.Ruler.Children.Count(static child => !child.HasClass("parked"));

        Assert.True(live is > 0 and < 40, $"{live} labels");
    }

    [Fact]
    public void Scrubbing_the_ruler_moves_the_playhead() {
        using var fixture = new AdvancedFixture();
        var timeline = Built(fixture);

        timeline.SnapToFrames = false;

        var times = new List<float>();
        timeline.TimeChanged += (_, time) => times.Add(time);

        var y = AdvancedFixture.Centre(timeline.Ruler).Y;
        var x = timeline.ToScreen(1.25f);

        fixture.Press(x, y);
        fixture.Move(timeline.ToScreen(2.5f), y);
        fixture.Release(timeline.ToScreen(2.5f), y);

        Assert.Equal(2.5f, timeline.Time, 2);
        Assert.True(times.Count >= 2);
    }

    [Fact]
    public void The_playhead_snaps_to_frames_rather_than_to_pixels() {
        using var fixture = new AdvancedFixture();
        var timeline = Built(fixture);

        timeline.FrameRate = 24f;
        timeline.SnapToFrames = true;

        var y = AdvancedFixture.Centre(timeline.Ruler).Y;
        var x = timeline.ToScreen(1.0f + (1f / 24f * 0.4f));

        fixture.Press(x, y);
        fixture.Release(x, y);

        // ⚠ A key between two frames plays on one of them anyway, so the snap that matters is the
        // frame rate. A pixel grid would put keys where no frame is.
        Assert.Equal(24f, timeline.Time * 24f, 3);
    }

    [Fact]
    public void The_playhead_stays_inside_the_duration() {
        using var fixture = new AdvancedFixture();
        var timeline = Built(fixture);

        timeline.Duration = 3f;

        timeline.Time = 100f;
        Assert.Equal(3f, timeline.Time);

        timeline.Time = -5f;
        Assert.Equal(0f, timeline.Time);
    }

    [Fact]
    public void Clicking_a_key_selects_it_and_control_adds_to_it() {
        using var fixture = new AdvancedFixture();
        var timeline = Built(fixture);

        var first = timeline.Tracks[0].Keys[1];
        var second = timeline.Tracks[1].Keys[2];

        Press(fixture, timeline, 0, first.Time);
        Assert.Same(first, Assert.Single(timeline.Selection));

        Press(fixture, timeline, 1, second.Time, ModifierKeys.Control);
        Assert.Equal(2, timeline.Selection.Count);
    }

    [Fact]
    public void Dragging_a_selected_key_moves_every_selected_key() {
        using var fixture = new AdvancedFixture();
        var timeline = Built(fixture);

        timeline.SnapToFrames = false;
        timeline.SelectAll();

        var track = timeline.Tracks[0];
        var key = track.Keys[1];
        var before = track.Keys.Select(static entry => entry.Time).ToArray();

        var y = LaneY(timeline, 0);

        fixture.Press(timeline.ToScreen(key.Time), y);
        fixture.Move(timeline.ToScreen(key.Time + 0.25f), y);
        fixture.Release(timeline.ToScreen(key.Time + 0.25f), y);

        Assert.Equal(before.Select(static time => time + 0.25f), track.Keys.Select(static entry => entry.Time), new Approximately());
    }

    [Fact]
    public void A_dragged_key_lands_on_a_frame() {
        using var fixture = new AdvancedFixture();
        var timeline = Built(fixture);

        timeline.FrameRate = 10f;
        timeline.SnapToFrames = true;

        var key = timeline.Tracks[0].Keys[0];
        var y = LaneY(timeline, 0);

        fixture.Press(timeline.ToScreen(key.Time), y);
        fixture.Move(timeline.ToScreen(0.63f), y);
        fixture.Release(timeline.ToScreen(0.63f), y);

        Assert.Equal(0.6f, key.Time, 3);
    }

    [Fact]
    public void A_key_cannot_be_dragged_before_the_start() {
        using var fixture = new AdvancedFixture();
        var timeline = Built(fixture);

        var key = timeline.Tracks[0].Keys[0];
        var y = LaneY(timeline, 0);

        fixture.Press(timeline.ToScreen(key.Time), y);
        fixture.Move(timeline.ToScreen(-4f), y);
        fixture.Release(timeline.ToScreen(-4f), y);

        Assert.Equal(0f, key.Time);
    }

    [Fact]
    public void A_marquee_selects_the_keys_it_covers() {
        using var fixture = new AdvancedFixture();
        var timeline = Built(fixture);

        var y = LaneY(timeline, 0);
        var bottom = LaneY(timeline, 1);

        // ⚠ The press has to land inside the lanes — a marquee starts where the pointer went down,
        // and outside them the press means nothing. The release may be anywhere; it is captured.
        fixture.Press(timeline.ToScreen(0.6f), bottom + 4f);
        fixture.Move(timeline.ToScreen(-0.2f), y - 4f);
        fixture.Release(timeline.ToScreen(-0.2f), y - 4f);

        // Two keys on each of the first two tracks: 0.0 and 0.5.
        Assert.Equal(4, timeline.Selection.Count);
    }

    [Fact]
    public void Delete_removes_the_selection() {
        using var fixture = new AdvancedFixture();
        var timeline = Built(fixture);

        timeline.SelectAll();
        Assert.Equal(12, timeline.Selection.Count);

        fixture.Type(InputKey.Delete);

        Assert.All(timeline.Tracks, static track => Assert.Empty(track.Keys));
        Assert.Empty(timeline.Selection);
    }

    [Fact]
    public void A_double_click_adds_a_key_and_another_takes_it_away() {
        using var fixture = new AdvancedFixture();
        var timeline = Built(fixture);

        timeline.SnapToFrames = false;

        var y = LaneY(timeline, 0);
        var x = timeline.ToScreen(1.75f);

        fixture.Press(x, y);
        fixture.Release(x, y);
        fixture.Press(x, y);
        fixture.Release(x, y);

        Assert.Equal(5, timeline.Tracks[0].Keys.Count);

        fixture.Rest();

        fixture.Press(x, y);
        fixture.Release(x, y);
        fixture.Press(x, y);
        fixture.Release(x, y);

        Assert.Equal(4, timeline.Tracks[0].Keys.Count);
    }

    [Fact]
    public void Removing_a_track_takes_its_keys_out_of_the_selection() {
        using var fixture = new AdvancedFixture();
        var timeline = Built(fixture);

        timeline.SelectAll();
        timeline.Remove(timeline.Tracks[0]);

        // Otherwise Delete would try to remove keys from a track that is no longer there.
        Assert.Equal(8, timeline.Selection.Count);
        Assert.Equal(2, timeline.Tracks.Count);
    }

    [Fact]
    public void Only_the_tracks_on_screen_have_header_elements() {
        using var fixture = new AdvancedFixture(css: "timeline { height: 200px; }");
        var timeline = Built(fixture, tracks: 500, keys: 1);

        Assert.Equal(500, timeline.Tracks.Count);
        Assert.True(timeline.HeaderRows.Count < 20, $"realised {timeline.HeaderRows.Count} headers");
    }

    [Fact]
    public void Scrolling_rebinds_the_headers_rather_than_making_new_ones() {
        using var fixture = new AdvancedFixture(css: "timeline { height: 200px; }");
        var timeline = Built(fixture, tracks: 500, keys: 1);

        var before = timeline.HeaderRows.Count;
        var element = timeline.HeaderRows[0];

        timeline.ScrollTop = timeline.TrackHeight * 100f;
        fixture.Update();

        Assert.Equal(before, timeline.HeaderRows.Count);
        Assert.Same(element, timeline.HeaderRows[0]);
        Assert.Equal("track100", timeline.HeaderRows[0].Track?.Name);
    }

    [Fact]
    public void The_wheel_zooms_about_the_pointer_and_shift_scrolls_the_tracks() {
        using var fixture = new AdvancedFixture(css: "timeline { height: 200px; }");
        var timeline = Built(fixture, tracks: 100, keys: 1);

        var before = timeline.ToTime(400f);

        fixture.Document.Dispatch(new WheelEvent { X = 400f, Y = 150f, DeltaY = -300f, Timestamp = TimeSpan.Zero });
        fixture.Update();

        Assert.True(timeline.PixelsPerSecond > 100f);
        Assert.Equal(before, timeline.ToTime(400f), 3);

        fixture.Document.Dispatch(
            new WheelEvent { X = 400f, Y = 150f, DeltaY = 120f, Modifiers = ModifierKeys.Shift, Timestamp = TimeSpan.Zero }
        );

        fixture.Update();

        // ⚠ Shift for the axis a timeline rarely needs, because the plain gesture is the one wanted
        // a hundred times an hour.
        Assert.Equal(120f, timeline.ScrollTop, 2);
    }

    [Fact]
    public void Zoom_to_fit_puts_the_whole_duration_on_screen() {
        using var fixture = new AdvancedFixture();
        var timeline = Built(fixture);

        timeline.Duration = 12f;
        timeline.ZoomToFit();

        Assert.Equal(0f, timeline.TimeStart);
        Assert.Equal(12f, timeline.ToTime(timeline.Lanes.Bounds.Right), 2);
    }

    [Fact]
    public void The_arrow_keys_step_a_frame_at_a_time() {
        using var fixture = new AdvancedFixture();
        var timeline = Built(fixture);

        timeline.FrameRate = 25f;
        timeline.Time = 1f;

        fixture.Type(InputKey.Right);
        Assert.Equal(1f + (1f / 25f), timeline.Time, 4);

        fixture.Type(InputKey.Left);
        Assert.Equal(1f, timeline.Time, 4);

        fixture.Type(InputKey.Home);
        Assert.Equal(0f, timeline.Time);

        fixture.Type(InputKey.End);
        Assert.Equal(timeline.Duration, timeline.Time);
    }

    static float LaneY(Timeline timeline, int track) =>
        timeline.Lanes.AbsoluteTop + (track * timeline.TrackHeight) + (timeline.TrackHeight * 0.5f) - timeline.ScrollTop;

    static void Press(AdvancedFixture fixture, Timeline timeline, int track, float time, ModifierKeys modifiers = ModifierKeys.None) {
        var x = timeline.ToScreen(time);
        var y = LaneY(timeline, track);

        fixture.Press(x, y, modifiers: modifiers);
        fixture.Release(x, y, modifiers: modifiers);
    }

    sealed class Approximately : IEqualityComparer<float> {
        public bool Equals(float left, float right) => MathF.Abs(left - right) < 0.01f;

        public int GetHashCode(float value) => 0;
    }
}
