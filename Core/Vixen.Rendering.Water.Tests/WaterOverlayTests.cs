// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Engine.Diagnostics;
using Vixen.Engine.Diagnostics.Overlays;
using Xunit;

namespace Vixen.Rendering.Water.Tests;

/// <summary>The two panels, and the shade that says a number is not where it should be.</summary>
/// <remarks>
///     ⚠ <b>Asserted through the accumulator's colours rather than through a property.</b> A panel
///     draws line segments, and what a person actually reads is which of them are amber — so a test
///     that checked an internal flag would be checking a different thing from the one the panel
///     communicates. <c>DebugDraw.ScreenLines</c> is what the renderer drains, and it carries the
///     colour each segment was asked for.
/// </remarks>
public sealed class WaterOverlayTests {
    static readonly Vector2 Screen = new(1280f, 720f);

    /// <summary>A fold that rebuilds every body every frame is warned about.</summary>
    /// <remarks>
    ///     ⚠ <b>The condition was <c>Rebuilt &gt; Bodies</c>, which cannot happen.</b>
    ///     <c>WaterZoneSystem.GatherBodies</c> counts a rebuilt body into <em>both</em> counters, so
    ///     the rebuilt count is bounded above by the body count and the amber shade could never be
    ///     drawn — a guard that reads as a working diagnostic and is a branch nothing takes. Sabotage
    ///     this by putting the <c>&gt;</c> back: the first assertion fails, and the second still
    ///     passes, which is what makes the pair the test rather than either alone.
    /// </remarks>
    [Fact]
    public void A_fold_that_rebuilds_everything_every_frame_is_shown_in_amber() {
        var theme = OverlayTheme.Default;

        Assert.Contains(Colours(new WaterStatistics { Bodies = 3, Rebuilt = 3 }), colour => colour == theme.Warning);

        // And an amortised fold is not. A panel that warned either way would be one people stop
        // reading, which is the same outcome as a warning that never fires.
        Assert.DoesNotContain(Colours(new WaterStatistics { Bodies = 3, Rebuilt = 0 }), colour => colour == theme.Warning);
    }

    /// <summary>An empty scene is not a fold that rebuilt everything it had.</summary>
    [Fact]
    public void A_scene_with_no_water_is_not_warned_about() {
        var theme = OverlayTheme.Default;

        Assert.DoesNotContain(Colours(new WaterStatistics()), colour => colour == theme.Warning);
    }

    /// <summary>Every colour the panel put on the screen for one set of numbers.</summary>
    static Color4[] Colours(WaterStatistics statistics) {
        var overlay = new WaterOverlay { Enabled = true, Statistics = statistics };
        var overlays = new DiagnosticOverlays();

        overlays.Add(overlay);

        var draw = new DebugDraw();
        overlays.Draw(draw, Screen, GameTime.Zero);

        var lines = draw.ScreenLines;
        var colours = new Color4[lines.Length];

        for (var index = 0; index < lines.Length; index++) {
            colours[index] = lines[index].Colour;
        }

        return colours;
    }
}
