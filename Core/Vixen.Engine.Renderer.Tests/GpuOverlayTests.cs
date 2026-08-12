// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Engine.Diagnostics;
using Vixen.Engine.Diagnostics.Overlays;
using Vixen.Graphics;
using Xunit;

namespace Vixen.Engine.Renderer.Tests;

/// <summary>What the GPU panel shows, and the three ways it refuses to lie about it.</summary>
/// <remarks>
///     ⚠ <b>A panel is a picture and these are not a substitute for one.</b> What they can hold is
///     the part a picture cannot: that a row keeps its place while the costs behind it move, that a
///     nested scope is not added to its parent, and that a frame nobody measured says so instead of
///     drawing zeroes. The picture is in the sample.
/// </remarks>
public sealed class GpuOverlayTests {
    // A period of a million nanoseconds per tick makes one tick one millisecond, so every number in
    // these tests is the number that reaches the panel.
    const float Period = 1_000_000f;

    static readonly Vector2 Screen = new(1280f, 720f);

    /// <summary>An empty panel because the profiler is off must not look like a free frame.</summary>
    [Fact]
    public void A_run_without_the_profiler_says_so_rather_than_drawing_zeroes() {
        var overlay = new GpuOverlay { Enabled = true };
        var draw = Draw(overlay, GameTime.Zero);

        Assert.Equal(0, overlay.DrawnRows);
        Assert.Empty(overlay.VisiblePasses);

        // It still drew: a panel with one line of explanation, which is the whole point of the
        // branch. Nothing on screen would be indistinguishable from an overlay that is switched off.
        Assert.True(draw.ScreenCount > 0);
    }

    /// <summary>The rows are the expensive passes, and they are in the frame's order.</summary>
    [Fact]
    public void The_rows_are_the_expensive_passes_in_the_frames_own_order() {
        var overlay = new GpuOverlay { Enabled = true, Available = true };

        // Thirty passes — comfortably more expensive ones than there are rows — with the expensive
        // ones scattered through the frame rather than gathered at one end, so "in cost order" and
        // "in frame order" are two different answers.
        var scopes = new List<GpuScope>();
        var clock = 0ul;

        for (var index = 0; index < 30; index++) {
            var cost = index % 2 == 0 ? 10ul : 1ul;

            scopes.Add(new($"pass{index:00}", 0, clock, clock + cost));
            clock += cost;
        }

        overlay.Frame = new(1, scopes, Period);
        Draw(overlay, GameTime.Zero);

        Assert.Equal(GpuOverlay.MaxRows, overlay.VisiblePasses.Count);

        // Every row is one of the ten-millisecond passes — the cut did its job…
        foreach (var name in overlay.VisiblePasses) {
            Assert.Equal(0, int.Parse(name["pass".Length..]) % 2);
        }

        // …and they come out in the order the graph declared them, which is what makes a row stay
        // where the eye left it. A panel sorted by cost would put them in an order that changes.
        var order = overlay.VisiblePasses.Select(name => int.Parse(name["pass".Length..])).ToList();

        Assert.Equal(order.OrderBy(value => value), order);
    }

    /// <summary>A row does not move when the pass under it becomes the expensive one.</summary>
    /// <remarks>
    ///     ⚠ <b>The claim the panel is built around.</b> GPU readings move by more than ten percent
    ///     between two frames of a still camera, so a panel that ranked its rows by this frame's cost
    ///     would shuffle continuously — and a table whose rows swap places is one nobody can watch
    ///     while moving. This is that property, stated as a test rather than as a comment.
    /// </remarks>
    [Fact]
    public void A_row_keeps_its_place_when_the_two_passes_swap_costs() {
        var overlay = new GpuOverlay { Enabled = true, Available = true };

        overlay.Frame = new(1, [new("shadows", 0, 0, 8), new("lighting", 0, 8, 10)], Period);
        Draw(overlay, GameTime.Zero);

        Assert.Equal(["shadows", "lighting"], overlay.VisiblePasses);

        // The second frame reverses which of the two is expensive, and nothing about the layout may
        // follow it.
        overlay.Frame = new(2, [new("shadows", 0, 0, 2), new("lighting", 0, 2, 12)], Period);
        Draw(overlay, GameTime.Zero);

        Assert.Equal(["shadows", "lighting"], overlay.VisiblePasses);
    }

    /// <summary>A spike leaves a mark that is still there several frames later.</summary>
    [Fact]
    public void A_spike_leaves_a_peak_that_outlives_the_frame_it_happened_in() {
        var overlay = new GpuOverlay { Enabled = true, Available = true };
        var step = new GameTime(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(1d / 60d), 0, 1f);

        overlay.Frame = new(1, [new("gather", 0, 0, 2)], Period);
        Draw(overlay, step);

        overlay.Frame = new(2, [new("gather", 0, 0, 9)], Period);
        Draw(overlay, step);

        overlay.Frame = new(3, [new("gather", 0, 0, 2)], Period);
        Draw(overlay, step);

        // A sixtieth of a second into a three-second decay has taken almost nothing off it, which is
        // the property: the mark is still there when somebody looks down at the panel.
        Assert.True(overlay.PeakOf("gather") > 8d, $"the peak fell to {overlay.PeakOf("gather")}");

        // And it does come down, rather than pinning the bar for the rest of the run.
        var seconds = new GameTime(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(4d), 0, 1f);

        overlay.Frame = new(4, [new("gather", 0, 0, 2)], Period);
        Draw(overlay, seconds);

        Assert.True(overlay.PeakOf("gather") < 3d, $"the peak stayed at {overlay.PeakOf("gather")}");
    }

    /// <summary>Work the passes do not account for is reported rather than hidden.</summary>
    [Fact]
    public void Gpu_work_outside_every_pass_is_reported_as_unattributed() {
        var overlay = new GpuOverlay { Enabled = true, Available = true };

        // Two passes of two milliseconds each, in a frame that spans ten: six milliseconds happened
        // somewhere the timeline does not describe.
        overlay.Frame = new(1, [new("a", 0, 0, 2), new("b", 0, 8, 10)], Period);
        Draw(overlay, GameTime.Zero);

        Assert.Equal(0.6f, overlay.UnattributedFraction, 3);
    }

    /// <summary>A nested scope is not added to its parent when the remainder is worked out.</summary>
    /// <remarks>
    ///     ⚠ <b>The arithmetic that turns this row into noise if it is got wrong.</b> A nested
    ///     scope's span lies <em>inside</em> its parent's — the screen-probe gather times five
    ///     dispatches one level down — so summing every scope reports more GPU time than the frame
    ///     has, and the remainder comes out negative on a frame that is fully accounted for. Level
    ///     zero alone is the only sum that means anything, and this is a frame where the two answers
    ///     differ by a factor of two.
    /// </remarks>
    [Fact]
    public void A_nested_scope_is_not_counted_against_the_frame_twice() {
        var overlay = new GpuOverlay { Enabled = true, Available = true };

        overlay.Frame = new(
            1,
            [new("gather", 0, 0, 10), new("gather.trace", 1, 1, 9), new("gather.filter", 1, 9, 10)],
            Period
        );

        Draw(overlay, GameTime.Zero);

        // The one level-zero pass fills the frame exactly. Summing all three would attribute
        // nineteen milliseconds to a ten-millisecond frame.
        Assert.Equal(0f, overlay.UnattributedFraction, 3);

        // And the children still get rows, because "what is the most expensive thing in this frame"
        // is a question a stage inside a pass can be the answer to.
        Assert.Equal(3, overlay.DrawnRows);
    }

    /// <summary>A dropped scope is said out loud, because a short timeline looks like a fast frame.</summary>
    [Fact]
    public void An_overflowed_pool_is_shown_rather_than_left_to_look_like_a_short_frame() {
        var overlay = new GpuOverlay { Enabled = true, Available = true };

        overlay.Frame = new(1, [new("a", 0, 0, 4)], Period);

        var quiet = Draw(overlay, GameTime.Zero).ScreenCount;

        overlay.Dropped = 7;

        var loud = Draw(overlay, GameTime.Zero).ScreenCount;

        // One more row of text, which is the row saying the breakdown is incomplete. A panel that
        // drew the same thing either way would make its own truncation authoritative.
        Assert.True(loud > quiet, $"{loud} segments with a dropped scope against {quiet} without");
    }

    static DebugDraw Draw(GpuOverlay overlay, in GameTime time) {
        var overlays = new DiagnosticOverlays();
        overlays.Add(overlay);

        var draw = new DebugDraw();
        overlays.Draw(draw, Screen, time);

        return draw;
    }
}
