// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Testing;
using Vixen.Physics.Ecs;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>A play session's overlay panels, painted by the interface over the focused pane (#1264).</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The decision, made on Jiu's behalf: option (3).</b> The same
///         <c>IDiagnosticOverlay.Draw</c> data a game's HUD is drawn from, laid out against the focused
///         pane's size, stroked by the interface's vector painter — and never written into the shared
///         world-line accumulator every pane draws, where it would land in every pane at one pane's
///         coordinates. Focused rather than all, because four panes repeating one panel is noise.
///     </para>
///     <para>
///         ⚠ <b>Asserted by what each pane's layer stroked</b>, a differential inside one run: nothing
///         with the overlay off, the focused pane's panel with it on, nothing in the other pane, and
///         the panel following the focus.
///     </para>
/// </remarks>
public class PlayOverlayPaintingTests {
    [Fact]
    public void The_focused_pane_paints_the_overlay_panel_and_another_pane_does_not() {
        using var session = EditorSession.Start();

        session.Open("scene");
        session.Run("scene.panes-quad");
        session.Frames(2);

        session.Run("play.play");
        session.Frames(2);

        var panes = session.Viewports;

        Assert.True(panes.Count >= 2, $"the layout has {panes.Count} pane(s)");

        var first = panes[0];
        var second = panes[1];

        Assert.True(session.Document.Focus(first.Control));
        session.Settle();
        session.Frames(2);

        // False before the work: a session runs and every overlay is off, so nothing is painted.
        Assert.All(panes, pane => Assert.Equal(0, session.Editor.PaneOverlays.Painted(pane)));

        Assert.True(session.Editor.PlayOverlays.Set(PhysicsDebugDrawSystem.OverlayName, true));
        session.Frames(2);

        Assert.True(session.Editor.PaneOverlays.Painted(first) > 0, "the focused pane painted no overlay panel");
        Assert.Equal(0, session.Editor.PaneOverlays.Painted(second));

        // ⚠ And the shared accumulator carries none of it: its screen half is what a pane cannot
        // place, and the panel would land in every pane at one pane's coordinates.
        Assert.True(session.Editor.PlayMode.Session!.TryGet<Vixen.Engine.Diagnostics.DebugDraw>(out var shared));
        Assert.Equal(0, shared!.ScreenLines.Length);
        Assert.Equal(0, session.Editor.PlayDiagnosticLines.Count % 2);

        // The panel follows the focus rather than staying where it was first drawn.
        Assert.True(session.Document.Focus(second.Control));
        session.Settle();
        session.Frames(2);

        Assert.Equal(0, session.Editor.PaneOverlays.Painted(first));
        Assert.True(session.Editor.PaneOverlays.Painted(second) > 0, "the newly focused pane painted nothing");

        // And Stop takes it away: a panel left behind would read as a session still running.
        session.Run("play.stop");
        session.Frames(2);

        Assert.All(session.Viewports, pane => Assert.Equal(0, session.Editor.PaneOverlays.Painted(pane)));
    }

    /// <summary>The panel is laid out against the focused pane's own size, in the pane's place.</summary>
    /// <remarks>
    ///     A panel laid out against the window, or against another pane, would be stroked at the right
    ///     size in the wrong place — so every painted point is asserted to be inside the pane it is
    ///     painted over.
    /// </remarks>
    [Fact]
    public void Every_painted_point_is_inside_the_focused_pane() {
        using var session = EditorSession.Start();

        session.Open("scene");
        session.Run("scene.panes-quad");
        session.Frames(2);
        session.Run("play.play");
        session.Frames(2);

        var pane = session.Viewports[^1];

        Assert.True(session.Document.Focus(pane.Control));
        session.Settle();

        Assert.True(session.Editor.PlayOverlays.Set(PhysicsDebugDrawSystem.OverlayName, true));
        session.Frames(2);

        var bounds = pane.Control.Bounds;
        var points = session.Editor.PaneOverlays.PaintedPoints(pane);

        Assert.NotEmpty(points);

        foreach (var point in points) {
            Assert.True(
                point.X >= bounds.X - 0.5f && point.X <= bounds.X + bounds.Width + 0.5f
                && point.Y >= bounds.Y - 0.5f && point.Y <= bounds.Y + bounds.Height + 0.5f,
                $"{point} is outside the pane at {bounds}"
            );
        }
    }
}
