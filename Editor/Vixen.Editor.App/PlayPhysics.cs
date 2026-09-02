// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.SceneView;
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
    }
}
