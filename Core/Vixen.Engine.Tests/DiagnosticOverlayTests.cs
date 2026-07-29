// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Core;
using Vixen.Core.Diagnostics;
using Vixen.Core.Mathematics;
using Vixen.Engine.Diagnostics;
using Vixen.Engine.Diagnostics.Overlays;
using Xunit;

namespace Vixen.Engine.Tests;

/// <summary>The overlay framework: what gets drawn, where, and what happens when it does not fit.</summary>
public sealed class DiagnosticOverlayTests {
    static readonly Vector2 Screen = new(1280f, 720f);

    [Fact]
    public void OnlyEnabledOverlaysAreDrawn() {
        var overlays = new DiagnosticOverlays();
        var one = new Recording("one");
        var two = new Recording("two") { Enabled = false };

        overlays.Add(one);
        overlays.Add(two);
        overlays.Draw(new(), Screen, GameTime.Zero);

        Assert.Equal(1, one.Drawn);
        Assert.Equal(0, two.Drawn);
        Assert.Equal(1, overlays.DrawnCount);
    }

    /// <summary>
    ///     Names are what the console types and what a settings file writes down, so two overlays
    ///     answering to one would be a toggle that silently flips the wrong one.
    /// </summary>
    [Fact]
    public void TwoOverlaysMayNotShareAName() {
        var overlays = new DiagnosticOverlays();
        overlays.Add(new Recording("stats"));

        Assert.Throws<ArgumentException>(() => overlays.Add(new Recording("STATS")));
    }

    [Fact]
    public void SetFlipsWhenNotToldWhich() {
        var overlays = new DiagnosticOverlays();
        overlays.Add(new Recording("one") { Enabled = false });

        Assert.True(overlays.Set("one"));
        Assert.False(overlays.Set("one"));
        Assert.True(overlays.Set("ONE", true));
        Assert.Null(overlays.Set("nothing"));
    }

    [Fact]
    public void TheMasterSwitchStopsEverything() {
        var overlays = new DiagnosticOverlays { Enabled = false };
        var one = new Recording("one");

        overlays.Add(one);
        overlays.Draw(new(), Screen, GameTime.Zero);

        Assert.Equal(0, one.Drawn);
    }

    /// <summary>An accumulator that is off is off for the overlays too, in one place.</summary>
    [Fact]
    public void ADisabledAccumulatorDrawsNoOverlays() {
        var overlays = new DiagnosticOverlays();
        var draw = new DebugDraw { Enabled = false };
        var one = new Recording("one");

        overlays.Add(one);
        overlays.Draw(draw, Screen, GameTime.Zero);

        Assert.Equal(0, one.Drawn);
    }

    /// <summary>Panels pinned to one corner stack; panels in different corners do not.</summary>
    [Fact]
    public void PanelsStackPerCorner() {
        var overlays = new DiagnosticOverlays();
        var first = new Recording("first") { Anchor = OverlayAnchor.TopLeft };
        var second = new Recording("second") { Anchor = OverlayAnchor.TopLeft };
        var elsewhere = new Recording("elsewhere") { Anchor = OverlayAnchor.TopRight };

        overlays.Add(first);
        overlays.Add(second);
        overlays.Add(elsewhere);
        overlays.Draw(new(), Screen, GameTime.Zero);

        Assert.True(second.Region.Origin.Y > first.Region.Origin.Y, "the second panel did not stack below the first");
        Assert.Equal(first.Region.Origin.Y, elsewhere.Region.Origin.Y, 3);
        Assert.True(elsewhere.Region.Origin.X > first.Region.Origin.X, "the right-anchored panel is not on the right");
    }

    /// <summary>Turning one off closes the gap rather than leaving one.</summary>
    [Fact]
    public void DisablingAPanelClosesTheGap() {
        var overlays = new DiagnosticOverlays();
        var first = new Recording("first");
        var second = new Recording("second");

        overlays.Add(first);
        overlays.Add(second);
        overlays.Draw(new(), Screen, GameTime.Zero);

        var stacked = second.Region.Origin.Y;

        first.Enabled = false;
        overlays.Draw(new(), Screen, GameTime.Zero);

        Assert.True(second.Region.Origin.Y < stacked, "the second panel did not move up");
        Assert.Equal(first.Region.Origin.Y, second.Region.Origin.Y, 3);
    }

    /// <summary>
    ///     ⚠ A panel that does not fit is refused rather than clipped: nothing here clips, so a panel
    ///     drawn past the bottom of the screen would be invisible while still taking its turn.
    /// </summary>
    [Fact]
    public void APanelThatDoesNotFitIsEmpty() {
        var overlays = new DiagnosticOverlays();
        var one = new Recording("one") { Rows = 200 };

        overlays.Add(one);
        overlays.Draw(new(), new(320f, 200f), GameTime.Zero);

        Assert.True(one.Region.IsEmpty);
    }

    /// <summary>A frame draws into the accumulator it was handed and nothing else.</summary>
    [Fact]
    public void DrawingProducesScreenGeometryAndNoWorldGeometry() {
        var overlays = new DiagnosticOverlays();
        overlays.Add(new FrameStatsOverlay());

        var draw = new DebugDraw();
        overlays.Draw(draw, Screen, GameTime.Zero);

        Assert.True(draw.ScreenCount > 0);
        Assert.Equal(0, draw.Count);
        Assert.Equal(0, draw.TextCount);
    }

    [Fact]
    public void TheStatsOverlayTracksTheFrameTime() {
        var stats = new FrameStatsOverlay();
        var overlays = new DiagnosticOverlays();
        overlays.Add(stats);

        var time = GameTime.Zero;

        for (var frame = 0; frame < 30; frame++) {
            time = time.Advance(TimeSpan.FromMilliseconds(20d));
            overlays.Draw(new(), Screen, time);
        }

        Assert.Equal(20f, stats.SmoothedMilliseconds, 1);
        Assert.Equal(20f, stats.PeakMilliseconds, 1);

        stats.Reset();

        Assert.Equal(0f, stats.PeakMilliseconds);
    }

    /// <summary>
    ///     The peak is what a stall shows up in. An average that swallowed it would report the same
    ///     number for a steady frame and for one with a spike in it.
    /// </summary>
    [Fact]
    public void TheStatsOverlayRemembersASpike() {
        var stats = new FrameStatsOverlay();
        var overlays = new DiagnosticOverlays();
        overlays.Add(stats);

        var time = GameTime.Zero.Advance(TimeSpan.FromMilliseconds(120d));
        overlays.Draw(new(), Screen, time);

        for (var frame = 0; frame < 20; frame++) {
            time = time.Advance(TimeSpan.FromMilliseconds(16d));
            overlays.Draw(new(), Screen, time);
        }

        Assert.Equal(120f, stats.PeakMilliseconds, 1);
        Assert.True(stats.SmoothedMilliseconds < 30f, "the average never recovered from the spike");
    }

    [Fact]
    public void TheLogOverlayReadsTheTailAndFiltersIt() {
        var sink = new RingBufferSink(64);
        var logger = sink.CreateLogger("Vixen.Test.Thing");

        // Through ILogger.Log rather than the LogInformation extensions, which the analyzer bars in
        // favour of [LoggerMessage] — a source-generated method per line is the right shape for a
        // subsystem and overbuilt for two records in a test.
        Write(logger, LogLevel.Information, "quiet");
        Write(logger, LogLevel.Error, "loud");

        var overlay = new LogOverlay(sink) { Enabled = true, MinimumLevel = LogLevel.Error };
        var overlays = new DiagnosticOverlays();
        overlays.Add(overlay);

        var draw = new DebugDraw();
        overlays.Draw(draw, Screen, GameTime.Zero);

        // The filter is the overlay's, not the sink's: turning the panel down to errors must not stop
        // the ring recording what the crash reporter will want.
        Assert.Equal(2, sink.Count);
        Assert.True(draw.ScreenCount > 0);
    }

    static void Write(ILogger logger, LogLevel level, string message) =>
        logger.Log(level, default, message, null, static (state, _) => state);

    /// <summary>Records the region it was given so the layout can be asserted without a picture.</summary>
    sealed class Recording(string name) : IDiagnosticOverlay {
        public string Name => name;
        public OverlayAnchor Anchor { get; set; } = OverlayAnchor.TopLeft;
        public bool Enabled { get; set; } = true;
        public int Rows { get; set; } = 3;
        public int Drawn { get; private set; }
        public OverlayRegion Region { get; private set; }

        public void Draw(OverlaySurface surface, in GameTime time) {
            Drawn++;
            Region = surface.Panel(Anchor, 200f, Rows, name);
        }
    }
}
