// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.SceneView;
using Vixen.Engine.Diagnostics;
using Vixen.Engine.Diagnostics.Overlays;

namespace Vixen.Editor.App;

/// <summary>The two things a play session's overlays need, which nothing was handing them.</summary>
/// <remarks>
///     <para>
///         <b>The seam
///         <a href="https://github.com/Rikarin/Vixen/issues/1247">#1247</a> asks for, from the side
///         that can own it.</b> An overlay wants somewhere to put geometry (a <see cref="DebugDraw" />)
///         and somewhere to be turned on by name (a <see cref="DiagnosticOverlays" />); a
///         <c>PlaySession</c> seeded exactly two services, the loop and the world, so a contribution
///         that wanted to add one had nothing to hand it. <c>PhysicsSystems.AddPhysicsOverlay</c> —
///         the whole physics wireframe, built, tested and reachable from a game since it was written
///         — was therefore called by one production caller in this repository, and it is a sample.
///     </para>
///     <para>
///         ⚠ <b>Both objects outlive the session and neither is created here.</b> The accumulator is
///         drained into the panes by <c>EditorApplication</c> after the loop has stepped, and the
///         registry has to keep a toggle across a Stop and a second Play — an overlay switched on and
///         forgotten by pressing Stop is a diagnostic you have to switch on again for every attempt
///         at the thing you are diagnosing. What <em>is</em> per session is the overlay: the physics
///         one holds the <c>PhysicsScene</c>, which is destroyed on Stop, so it is registered by
///         <see cref="PlayPhysics" /> and taken back out there.
///     </para>
///     <para>
///         ⚠ <b>A contribution rather than two lines in <c>PlaySession</c>'s constructor.</b> The
///         controller that builds a session is in <c>Vixen.Editor.SceneView</c>, which has no
///         accumulator and no viewport to drain one into; the application does. Registering it first
///         is not what orders it — <c>[Provides]</c> is, and <see cref="PlayPhysics" /> declares
///         <c>[RunsAfter(typeof(DebugDraw))]</c> against it.
///     </para>
/// </remarks>
[Provides(typeof(DebugDraw))]
[Provides(typeof(DiagnosticOverlays))]
sealed class PlayDiagnostics(DebugDraw draw, DiagnosticOverlays overlays) : IPlaySystems {
    /// <inheritdoc />
    public void Attach(PlaySession session) {
        ArgumentNullException.ThrowIfNull(session);

        // ⚠ Emptied rather than trusted. The accumulator is the editor's and outlives the session, so
        // whatever the last session's final frame drew is still in it — a Stop leaves a set of
        // wireframes standing where the bodies were, which reads as a simulation that is still
        // running.
        draw.Clear();

        session.Provide(draw);
        session.Provide(overlays);

        // ⚠ And emptied again on the way out, for the same reason from the other end: `Release` runs
        // before the world is restored, so this is the last frame in which those lines mean anything.
        session.OnStop(draw.Clear);
    }
}
