// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Testing;
using Vixen.Engine.Diagnostics;
using Vixen.Engine.Diagnostics.Overlays;
using Vixen.Engine.Transforms;
using Vixen.Physics.Bodies;
using Vixen.Physics.Ecs;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The physics overlay, in the head somebody is standing in when they need it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Everything the overlay draws was built, tested and unreachable from an editor</b>
///         (<a href="https://github.com/Rikarin/Vixen/issues/1247">#1247</a>).
///         <c>PhysicsSystems.AddPhysicsOverlay</c> needs a <see cref="DebugDraw" /> and a
///         <see cref="DiagnosticOverlays" />; a <c>PlaySession</c> seeded exactly two services — the
///         loop and the world — so a contribution that wanted to add the overlay had nothing to hand
///         it, and the extension's only production caller in this repository was
///         <c>Samples/13-ThirdPersonShooter</c>.
///     </para>
///     <para>
///         ⚠ <b>The assertion is the geometry and not the registration</b>, because a registration is
///         satisfied by an overlay wired to an accumulator nothing reads — which is exactly the state
///         the whole feature was in. What is asserted is that segments arrive at the list every pane
///         draws from, that they arrive only once the overlay is switched on, and that Stop takes
///         them away again.
///     </para>
/// </remarks>
public class PlayOverlayTests {
    /// <summary>A play session registers the overlay, switched off, under the name the console types.</summary>
    [Fact]
    public void A_play_session_registers_the_collider_overlay_switched_off() {
        using var session = EditorSession.Start();

        Assert.Empty(session.Editor.PlayOverlays.Registered);

        session.Run("play.play");
        session.Frames(2);

        var play = session.Editor.PlayMode.Session;

        Assert.NotNull(play);
        Assert.Empty(session.Editor.PlayMode.Refused);

        // The two services the session had nowhere to get, asked for the way a contribution asks.
        Assert.True(play.TryGet<DebugDraw>(out _));
        Assert.True(play.TryGet<DiagnosticOverlays>(out _));

        Assert.Contains("collider overlay", play.Running);

        var overlay = Assert.Single(session.Editor.PlayOverlays.Registered);

        Assert.Equal(PhysicsDebugDrawSystem.OverlayName, overlay.Name);

        // ⚠ Off, which is the state every build ships in: the wireframes are a few thousand lines for
        // a modest scene. What the wiring buys is that the switch exists at all.
        Assert.False(overlay.Enabled);
    }

    /// <summary>Switching it on puts collider geometry on the list every pane draws.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A differential inside one run, not an absolute.</b> Empty is what a session with
    ///         the overlay off produces and is also what a completely unwired editor produces, so the
    ///         only assertion that separates them is the same session going from empty to non-empty
    ///         because a name was switched on.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Switched on through <c>Set(name)</c> and not through the system.</b> That is
    ///         exactly what <c>overlay physics on</c> does and what a menu item would do — a test
    ///         holding the system would prove the system and say nothing about whether anything in
    ///         the editor can reach it, which is the half that was missing.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Switching_the_overlay_on_puts_colliders_on_the_panes_line_list() {
        using var session = EditorSession.Start();

        session.Run("play.play");
        session.Frames(2);

        var play = session.Editor.PlayMode.Session;

        Assert.NotNull(play);
        Assert.True(play.TryGet<PhysicsScene>(out var physics));
        Assert.NotNull(physics);

        // A body to draw. Through the session's own scene, so this is the simulation the overlay
        // holds rather than a second one.
        var crate = physics.Entities.Create(LocalTransform.At(new Vector3(0f, 5f, 0f)));

        physics.Entities.Add(crate, Collider.Of(physics.Shapes.Box(0.5f)));
        physics.Entities.Add(crate, RigidBody.Dynamic());

        session.Frames(2);

        // False before the work: the overlay is registered and off, and a body is being stepped.
        Assert.Empty(session.Editor.PlayDiagnosticLines);

        Assert.True(session.Editor.PlayOverlays.Set(PhysicsDebugDrawSystem.OverlayName, true));

        session.Frames(2);

        Assert.NotEmpty(session.Editor.PlayDiagnosticLines);

        // ⚠ Pairs, because a line pass takes two vertices per segment. An odd count is a drain that
        // wrote one end of something.
        Assert.Equal(0, session.Editor.PlayDiagnosticLines.Count % 2);

        // And every pane is pointed at that one list rather than at a copy or at nothing.
        Assert.NotEmpty(session.Viewports);

        foreach (var pane in session.Viewports) {
            Assert.Same(session.Editor.PlayDiagnosticLines, pane.Diagnostics);
        }

        // ⚠ Stop empties it, and that is not tidiness: the accumulator outlives the session, so a
        // pane that kept the last frame would draw a set of colliders standing where the bodies were
        // — which reads as a simulation that is still running.
        session.Run("play.stop");
        session.Frames(2);

        Assert.Empty(session.Editor.PlayDiagnosticLines);
    }

    /// <summary>And a second Play works, which a registration left behind would have stopped.</summary>
    /// <remarks>
    ///     ⚠ <b><c>DiagnosticOverlays.Add</c> throws on a duplicate name</b>, deliberately — two
    ///     overlays answering to one name is a toggle that flips whichever was registered first. The
    ///     overlay holds the session's <c>PhysicsScene</c>, so it has to leave with it; without the
    ///     removal on Stop this would come back as a refused contribution and physics would silently
    ///     stop running on the second press.
    /// </remarks>
    [Fact]
    public void Stopping_and_playing_again_registers_the_overlay_once() {
        using var session = EditorSession.Start();

        session.Run("play.play");
        session.Frames(2);
        session.Run("play.stop");
        session.Frames(2);

        Assert.Empty(session.Editor.PlayOverlays.Registered);

        session.Run("play.play");
        session.Frames(2);

        Assert.Empty(session.Editor.PlayMode.Refused);
        Assert.Single(session.Editor.PlayOverlays.Registered);
    }
}
