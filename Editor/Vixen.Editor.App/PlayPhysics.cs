// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.SceneView;
using Vixen.Engine.Diagnostics;
using Vixen.Engine.Diagnostics.Overlays;
using Vixen.Physics.Ecs;

namespace Vixen.Editor.App;

/// <summary>The simulation an in-editor play session runs, and nothing an editing one does.</summary>
/// <remarks>
///     <para>
///         <b>[31 § D10](../../docs/plan/31-terrain-grass-and-trees.md)'s last blocker, answered from
///         the application rather than from a module.</b> The editor holds no <c>PhysicsScene</c> and
///         nothing under <c>Editor/</c> referenced <c>Vixen.Physics</c>; the reason given was that a
///         scene published into <c>PluginServices</c> would be a world nothing calls
///         <c>Synchronize</c> on. <c>PlayModeController</c> steps a real <c>EngineLoop</c> now, so
///         the objection is answered — and this is what puts the four fixed-step passes and the
///         render-time interpolation into it, through the same <c>AddPhysics</c> a game calls.
///     </para>
///     <para>
///         ⚠ <b>Created on Play and destroyed on Stop, which is the decision rather than an
///         implementation detail.</b> A body that falls while somebody is dragging a gizmo is a scene
///         that edits itself, and an editor whose ground settles a centimetre every time you look at
///         it is one where nothing can be placed. Simulating only inside a session also puts every
///         body <em>inside</em> the snapshot: <c>WorldSnapshot.Capture</c> runs before this attaches
///         and <c>Restore</c> clears the world, so the entities a collider system created leave with
///         everything else the session made rather than being saved into somebody's level.
///     </para>
///     <para>
///         ⚠ <b>Pause and Step Frame need nothing here.</b> <c>PlayModeController.Tick</c> is what
///         decides whether the loop runs at all, so a paused session simply does not call
///         <c>Frame</c> — the accumulator stops being advanced and the simulation holds where it is.
///         A physics pass that read a "paused" flag of its own would be a second opinion about the
///         same question, and the two would disagree on the frame a step was consumed.
///     </para>
///     <para>
///         ⚠ <b>Provided to the session, not published into <c>PluginServices</c>.</b> That bag has
///         no removal, so a per-session object put in it is a handle to a disposed native world for
///         every reader after the first Stop — the failure this file exists to avoid, one layer over.
///         <c>PlaySession.Provide</c> has the scene's own lifetime, and a later contribution — terrain
///         collision, buoyancy, navigation — asks for it there.
///     </para>
///     <para>
///         ⚠ <b>And "later" is now declared rather than arranged.</b> <c>[Provides]</c> is what
///         <c>PlayTerrainColliders</c>' <c>[RunsAfter(typeof(PhysicsScene))]</c> sorts against.
///         Before it, the only thing putting this first was that <c>EditorApplication</c> registers
///         it before any module activates — a real dependency between two assemblies, held together
///         by the sequence of two unrelated lines and a comment.
///     </para>
/// </remarks>
[Provides(typeof(PhysicsScene))]
[RunsAfter(typeof(DebugDraw))]
sealed class PlayPhysics : IPlaySystems {
    /// <inheritdoc />
    public void Attach(PlaySession session) {
        ArgumentNullException.ThrowIfNull(session);

        // ⚠ Over the world being edited rather than a world of its own. A body's transform is a
        // `LocalTransform` on the entity the person authored, so a second world would simulate a copy
        // and write the results where nothing draws them.
        var scene = session.Owns(new PhysicsScene(session.World));

        session.Loop.AddPhysics(scene);

        // The contract others ask for, under its own type: one simulation per scene.
        session.Provide(scene);
        session.Runs("physics");

        Overlay(session, scene);
    }

    /// <summary>Adds the collider overlay, switched off, when the session has somewhere to draw it.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 13 § Diagnostic overlays' physics panel, in the editor</b>
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/1247">#1247</a>). Everything it draws
    ///         — collider wireframes, contact points, constraint anchors, broad-phase bounds, body
    ///         axes — has existed and been tested since <c>PhysicsDebugDraw</c> was written, and
    ///         `git grep AddPhysicsOverlay` over <c>*.cs</c> and <c>*.vxml</c> found the extension, one
    ///         test and <c>Samples/13</c>. ⚠ This is the overlay somebody reaches for when a character
    ///         walks at half speed or a body never teleports — two defects this repository has already
    ///         had, both diagnosed without it — and the editor's Play is where they are standing when
    ///         they hit either.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Conditional, and <see cref="PlaySession.TryGet{T}" /> answering false is the
    ///         ordinary case rather than a failure.</b> A session run by a harness with no viewport
    ///         has no accumulator and wants none; the simulation still runs, exactly as it did.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Taken out of the registry on Stop, and it must be.</b> The system holds the
    ///         <c>PhysicsScene</c> this session owns and <c>DiagnosticOverlays.Add</c> refuses a
    ///         second overlay of the same name — so a registration left behind would make the second
    ///         Play throw, and a toggle flipped afterwards would reach a system whose native world had
    ///         been destroyed.
    ///     </para>
    /// </remarks>
    static void Overlay(PlaySession session, PhysicsScene scene) {
        if (!session.TryGet<DebugDraw>(out var draw) || draw is null) {
            return;
        }

        session.TryGet<DiagnosticOverlays>(out var overlays);
        session.Loop.AddPhysicsOverlay(scene, draw, overlays);

        if (overlays is not null) {
            session.OnStop(() => overlays.Remove(PhysicsDebugDrawSystem.OverlayName));
        }

        // ⚠ Named separately from "physics", because the two answer different questions: the
        // simulation is running either way, and what this line says is that there is something to
        // switch on. `PlayModeController.Running` is read out to the person who pressed Play.
        session.Runs("collider overlay");
    }
}
