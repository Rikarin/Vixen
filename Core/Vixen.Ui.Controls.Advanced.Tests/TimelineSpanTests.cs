// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>Intervals rather than instants: the bar, its ramps, its two grips, and the loop point.</summary>
public class TimelineSpanTests {
    static Timeline Built(AdvancedFixture fixture, float duration = 4f) {
        var timeline = fixture.Add<Timeline>();

        timeline.Duration = duration;
        timeline.SnapToFrames = false;

        var track = timeline.AddTrack("contacts", TimelineTrackKind.Spans);

        track.AddSpan(1f, 2f);

        fixture.Update();
        timeline.Refresh();
        fixture.Update();

        fixture.Document.Focus(timeline);
        return timeline;
    }

    // ── The shape ────────────────────────────────────────────────────────────

    /// <summary>⚠ The drawn ramp is the applied ramp, or the bar lies about the one thing it shows.</summary>
    [Fact]
    public void The_activation_ramps_in_holds_and_ramps_out() {
        var span = new TimelineSpan(1f, 3f) { EaseIn = 0.5f, EaseOut = 1f };

        Assert.Equal(0f, span.Activation(0.9f, 4f), 4);
        Assert.Equal(0f, span.Activation(1f, 4f), 4);
        Assert.Equal(0.5f, span.Activation(1.25f, 4f), 4);
        Assert.Equal(1f, span.Activation(1.5f, 4f), 4);
        Assert.Equal(1f, span.Activation(2f, 4f), 4);
        Assert.Equal(0.5f, span.Activation(2.5f, 4f), 4);
        Assert.Equal(0f, span.Activation(3f, 4f), 4);
        Assert.Equal(0f, span.Activation(3.1f, 4f), 4);
    }

    /// <summary>A peak below one is a span that never fully applies, and the bar is that much shorter.</summary>
    [Fact]
    public void The_peak_caps_the_whole_bar() {
        var span = new TimelineSpan(0f, 2f) { Peak = 0.4f, EaseIn = 0.5f };

        Assert.Equal(0.2f, span.Activation(0.25f, 4f), 4);
        Assert.Equal(0.4f, span.Activation(1f, 4f), 4);
    }

    /// <summary>
    ///     ⚠ <b>A span whose end precedes its start straddles the loop</b>, which is the ordinary case
    ///     for anything authored against a cycle. Its length runs the long way round.
    /// </summary>
    [Fact]
    public void A_span_across_the_loop_point_is_live_at_both_ends_of_the_timeline() {
        var span = new TimelineSpan(3.5f, 0.5f);

        Assert.True(span.Wraps);
        Assert.Equal(1f, span.Length(4f), 4);

        Assert.Equal(1f, span.Activation(3.75f, 4f), 4);
        Assert.Equal(1f, span.Activation(0.25f, 4f), 4);
        Assert.Equal(0f, span.Activation(2f, 4f), 4);
    }

    /// <summary>The ramps of a wrapping span are measured the long way round too.</summary>
    [Fact]
    public void A_wrapping_spans_ramps_run_across_the_seam() {
        var span = new TimelineSpan(3.5f, 0.5f) { EaseIn = 0.5f, EaseOut = 0.5f };

        // Half a second in is 4.0, i.e. 0.0 — the seam itself, and the top of the ramp.
        Assert.Equal(0.5f, span.Activation(3.75f, 4f), 4);
        Assert.Equal(1f, span.Activation(0f, 4f), 4);
        Assert.Equal(0.5f, span.Activation(0.25f, 4f), 4);
    }

    [Fact]
    public void Spans_stay_in_start_order() {
        var track = new TimelineTrack("t", TimelineTrackKind.Spans);

        track.AddSpan(2f, 3f);
        track.AddSpan(0.5f, 1f);
        track.AddSpan(1f, 1.5f);

        Assert.Equal([0.5f, 1f, 2f], track.Spans.Select(static span => span.Begin));
    }

    // ── The grips ────────────────────────────────────────────────────────────

    [Fact]
    public void The_ends_are_grips_and_the_middle_moves_the_whole_bar() {
        using var fixture = new AdvancedFixture();
        var timeline = Built(fixture);
        var span = timeline.Tracks[0].Spans[0];
        var y = LaneY(timeline, 0);

        Assert.Same(span, timeline.SpanAt(timeline.ToScreen(1f), y, out var begin));
        Assert.Equal(SpanGrip.Begin, begin);

        Assert.Same(span, timeline.SpanAt(timeline.ToScreen(2f), y, out var end));
        Assert.Equal(SpanGrip.End, end);

        Assert.Same(span, timeline.SpanAt(timeline.ToScreen(1.5f), y, out var body));
        Assert.Equal(SpanGrip.Body, body);

        Assert.Null(timeline.SpanAt(timeline.ToScreen(2.8f), y, out var none));
        Assert.Equal(SpanGrip.None, none);
    }

    /// <summary>
    ///     ⚠ <b>A short bar is all grip and no body</b>, so the reach shrinks with the bar rather than
    ///     the two ends overlapping and the middle becoming unreachable.
    /// </summary>
    [Fact]
    public void A_very_short_span_still_has_a_middle() {
        using var fixture = new AdvancedFixture();
        var timeline = Built(fixture);

        var track = timeline.Tracks[0];
        var span = track.Spans[0];

        track.Move(span, 1f, 1.05f);

        var y = LaneY(timeline, 0);

        Assert.Same(span, timeline.SpanAt(timeline.ToScreen(1f), y, out var begin));
        Assert.Equal(SpanGrip.Begin, begin);

        Assert.Same(span, timeline.SpanAt(timeline.ToScreen(1.05f), y, out var end));
        Assert.Equal(SpanGrip.End, end);

        Assert.Same(span, timeline.SpanAt(timeline.ToScreen(1.025f), y, out var body));
        Assert.Equal(SpanGrip.Body, body);
    }

    [Fact]
    public void Dragging_an_end_moves_only_that_end() {
        using var fixture = new AdvancedFixture();
        var timeline = Built(fixture);
        var span = timeline.Tracks[0].Spans[0];

        Drag(fixture, timeline, 0, 2f, 2.5f);

        Assert.Equal(1f, span.Begin, 3);
        Assert.Equal(2.5f, span.End, 3);
    }

    [Fact]
    public void Dragging_the_middle_moves_both_ends_and_keeps_the_length() {
        using var fixture = new AdvancedFixture();
        var timeline = Built(fixture);
        var span = timeline.Tracks[0].Spans[0];

        Drag(fixture, timeline, 0, 1.5f, 2.25f);

        Assert.Equal(1.75f, span.Begin, 3);
        Assert.Equal(2.75f, span.End, 3);
    }

    /// <summary>A bar dragged off the front stops at it with its length intact.</summary>
    [Fact]
    public void Dragging_the_middle_off_the_front_keeps_the_length() {
        using var fixture = new AdvancedFixture();
        var timeline = Built(fixture);
        var span = timeline.Tracks[0].Spans[0];

        Drag(fixture, timeline, 0, 1.5f, -2f);

        Assert.Equal(0f, span.Begin, 3);
        Assert.Equal(1f, span.End, 3);
    }

    /// <summary>
    ///     ⚠ <b>Dragging an end past the other is how a wrapping span is authored</b>, so it wraps
    ///     rather than being refused — otherwise the loop point is a place nobody can mark.
    /// </summary>
    [Fact]
    public void Dragging_an_end_past_the_other_makes_the_span_wrap() {
        using var fixture = new AdvancedFixture();
        var timeline = Built(fixture);
        var span = timeline.Tracks[0].Spans[0];

        Drag(fixture, timeline, 0, 2f, 0.5f);

        Assert.True(span.Wraps);
        Assert.Equal(1f, span.Begin, 3);
        Assert.Equal(0.5f, span.End, 3);
    }

    /// <summary>A span already across the seam slides round it rather than piling up at an edge.</summary>
    [Fact]
    public void A_wrapping_span_slides_round_the_seam() {
        using var fixture = new AdvancedFixture();
        var timeline = Built(fixture);

        var track = timeline.Tracks[0];
        var span = track.Spans[0];

        track.Move(span, 3.5f, 0.5f);
        Drag(fixture, timeline, 0, 3.75f, 0.25f);

        Assert.Equal(0f, span.Begin, 3);
        Assert.Equal(1f, span.End, 3);
    }

    [Fact]
    public void A_dragged_end_lands_on_a_frame() {
        using var fixture = new AdvancedFixture();
        var timeline = Built(fixture);
        var span = timeline.Tracks[0].Spans[0];

        timeline.FrameRate = 10f;
        timeline.SnapToFrames = true;

        Drag(fixture, timeline, 0, 2f, 2.63f);

        Assert.Equal(2.6f, span.End, 3);
    }

    // ── Selection ────────────────────────────────────────────────────────────

    [Fact]
    public void A_press_selects_a_span_and_reports_the_move_once() {
        using var fixture = new AdvancedFixture();
        var timeline = Built(fixture);
        var span = timeline.Tracks[0].Spans[0];

        var moves = 0;
        timeline.SpanMoved += (_, _) => moves++;

        Drag(fixture, timeline, 0, 1.5f, 1.9f);

        Assert.Same(span, Assert.Single(timeline.SpanSelection));
        Assert.Equal(1, moves);
    }

    /// <summary>One thing is selected at a time, because the panel beside a timeline shows one thing.</summary>
    [Fact]
    public void Selecting_a_span_clears_the_key_selection_and_the_other_way_round() {
        using var fixture = new AdvancedFixture();
        var timeline = Built(fixture);

        var keys = timeline.AddTrack("values");
        var key = keys.Add(1f);

        timeline.Select(key);
        Assert.Single(timeline.Selection);
        Assert.Empty(timeline.SpanSelection);

        timeline.Select(timeline.Tracks[0].Spans[0]);
        Assert.Empty(timeline.Selection);
        Assert.Single(timeline.SpanSelection);
    }

    /// <summary>A band over the middle of a long bar is a band somebody drew over that bar.</summary>
    [Fact]
    public void A_marquee_catches_a_span_it_only_overlaps() {
        using var fixture = new AdvancedFixture();
        var timeline = Built(fixture);

        var y = LaneY(timeline, 0);

        // ⚠ The press has to start on empty track, or it takes hold of the bar and the gesture is a
        // drag — which would leave the span selected too, and the test would pass saying nothing.
        fixture.Press(timeline.ToScreen(3f), y - 6f);
        fixture.Move(timeline.ToScreen(1.6f), y + 6f);
        fixture.Release(timeline.ToScreen(1.6f), y + 6f);

        Assert.Single(timeline.SpanSelection);
        Assert.Equal(1f, timeline.Tracks[0].Spans[0].Begin, 3);
    }

    [Fact]
    public void Delete_removes_a_selected_span() {
        using var fixture = new AdvancedFixture();
        var timeline = Built(fixture);

        timeline.Select(timeline.Tracks[0].Spans[0]);
        fixture.Type(InputKey.Delete);

        Assert.Empty(timeline.Tracks[0].Spans);
        Assert.Empty(timeline.SpanSelection);
    }

    // ── Adding and removing ──────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ <b>A double-tap on empty span track asks rather than adds.</b> What the new span
    ///     <em>is</em> — which constraint, which sub-clip — is not something a timeline can invent.
    /// </summary>
    [Fact]
    public void A_double_click_on_an_empty_stretch_asks_for_a_span() {
        using var fixture = new AdvancedFixture();
        var timeline = Built(fixture);

        var asked = 0f;
        var times = 0;

        timeline.SpanRequested += (_, track, time) => {
            asked = time;
            times++;

            track.AddSpan(time, time + 0.5f);
        };

        DoubleTap(fixture, timeline, 0, 3f);

        Assert.Equal(1, times);
        Assert.Equal(3f, asked, 3);
        Assert.Equal(2, timeline.Tracks[0].Spans.Count);
    }

    [Fact]
    public void A_double_click_on_a_span_takes_it_away() {
        using var fixture = new AdvancedFixture();
        var timeline = Built(fixture);

        DoubleTap(fixture, timeline, 0, 1.5f);

        Assert.Empty(timeline.Tracks[0].Spans);
    }

    /// <summary>Nothing is added when nobody is listening, which is what makes the event a request.</summary>
    [Fact]
    public void An_unheard_request_adds_nothing() {
        using var fixture = new AdvancedFixture();
        var timeline = Built(fixture);

        DoubleTap(fixture, timeline, 0, 3f);

        Assert.Single(timeline.Tracks[0].Spans);
    }

    [Fact]
    public void Removing_a_track_takes_its_spans_out_of_the_selection() {
        using var fixture = new AdvancedFixture();
        var timeline = Built(fixture);

        timeline.SelectAll();
        Assert.Single(timeline.SpanSelection);

        timeline.Remove(timeline.Tracks[0]);
        Assert.Empty(timeline.SpanSelection);
    }

    static float LaneY(Timeline timeline, int track) =>
        timeline.Lanes.AbsoluteTop + (track * timeline.TrackHeight) + (timeline.TrackHeight * 0.5f) - timeline.ScrollTop;

    static void Drag(AdvancedFixture fixture, Timeline timeline, int track, float from, float to) {
        var y = LaneY(timeline, track);

        fixture.Press(timeline.ToScreen(from), y);
        fixture.Move(timeline.ToScreen(to), y);
        fixture.Release(timeline.ToScreen(to), y);
    }

    static void DoubleTap(AdvancedFixture fixture, Timeline timeline, int track, float time) {
        var x = timeline.ToScreen(time);
        var y = LaneY(timeline, track);

        fixture.Press(x, y);
        fixture.Release(x, y);
        fixture.Press(x, y);
        fixture.Release(x, y);
    }
}
