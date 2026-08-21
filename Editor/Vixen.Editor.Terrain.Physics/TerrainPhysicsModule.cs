// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.SceneView;
using Vixen.Physics.Ecs;
using Vixen.Rendering.Terrain;
using Vixen.Terrain;
using Vixen.Terrain.Physics;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Editor.Terrain.Physics;

/// <summary>Gives the editor's play sessions terrain collision, and the sculpt tools a seam to push into.</summary>
/// <remarks>
///     <para>
///         <b>The reach half of <c>docs/plan/31</c> § D10.</b> <see cref="TerrainColliders" /> and
///         <see cref="TerrainColliderSystem" /> were both written and both tested, and nothing in the
///         product constructed either: <c>TerrainEdit.Colliders</c> was assigned in five test files
///         and nowhere else, so every assertion about a stroke naming the right tiles was one double
///         talking to another. This is the module that closes it — registered in
///         <c>EditorModules.Standard</c> beside Terrain, through the same door a third-party plugin
///         comes through.
///     </para>
///     <para>
///         ⚠ <b>Two contributions with two different lifetimes, and confusing them is the trap.</b>
///         The <see cref="ITerrainColliders" /> published into <c>PluginServices</c> lives as long as
///         the editor, because <c>TerrainModule.BindColliders</c> binds the first one it finds and
///         never re-reads — so a per-session adapter would leave the sculpt tools holding a disposed
///         Jolt world for every stroke after the first Stop. The <see cref="TerrainColliderSystem" />
///         behind it lives for one play session, because that is when there is a
///         <c>PhysicsScene</c> to build bodies in. The published object is a switch between the two.
///     </para>
///     <para>
///         ⚠ <b>It builds no simulation of its own and refuses to.</b> The <c>PhysicsScene</c> comes
///         from <c>PlaySession.TryGet</c> — the editor application provides one — and a session that
///         has none runs no collider system at all, which is <see cref="ITerrainColliders" />' own
///         "a terrain with no collision, not an error". A module that stood up a second physics world
///         would be a scene in which the ground and everything else collided with nothing, silently.
///     </para>
///     <para>
///         ⚠ <b>And it does not catch what <see cref="TerrainColliderSystem.Rebuild(TerrainMap, int, int)" />
///         throws.</b> An out-of-range tile index is a caller's bug that used to corrupt a *different*
///         tile in silence — see that method's own remarks — and a module that swallowed the
///         exception here would put the silence back with an extra step in front of it.
///     </para>
/// </remarks>
public sealed class TerrainPhysicsModule : IEditorPlugin {
    /// <summary>What the host activates it under, and what a plugin depending on it names.</summary>
    public const string ModuleId = "vixen.terrain.physics";

    /// <summary>What a plugin-management panel calls it.</summary>
    public const string ModuleName = "Terrain Collision";

    readonly PlayColliders colliders = new();

    /// <summary>What the sculpt tools push strokes into, whether or not anything is playing.</summary>
    /// <remarks>
    ///     Exposed for the same reason <see cref="TerrainColliders.Missed" /> is: the counters on it
    ///     are the only symptom the wiring has, and a test that could not read them would be a test
    ///     of the double again.
    /// </remarks>
    internal PlayColliders Colliders => colliders;

    /// <inheritdoc />
    public void Activate(PluginContext context) {
        ArgumentNullException.ThrowIfNull(context);

        var extensions = context.Services.Require<IEditorRegistry>();

        // ⚠ Published once, under the interface. `TerrainModule` resolves it in its per-frame follow
        // and keeps the first answer, so this has to be the object that outlives every session rather
        // than the one that owns this session's bodies.
        context.Services.Add<ITerrainColliders>(colliders);

        context.Owns(extensions.Add<IPlaySystems>(new PlayTerrainColliders(colliders, extensions)));
    }

    /// <inheritdoc />
    public void Deactivate() {
        // Nothing outside the registration scope: the session's own teardown drops the collider
        // system, and `PluginServices` has no removal to call.
    }
}

/// <summary>Builds one play session's terrain collision, over the simulation the session provides.</summary>
/// <param name="colliders">The switch the sculpt tools push through.</param>
/// <param name="extensions">Where the <c>ITerrainScene</c> contributions are.</param>
sealed class PlayTerrainColliders(PlayColliders colliders, IEditorRegistry extensions) : IPlaySystems {
    /// <inheritdoc />
    public void Attach(PlaySession session) {
        ArgumentNullException.ThrowIfNull(session);

        if (!session.TryGet<PhysicsScene>(out var scene) || scene is null) {
            return;
        }

        // ⚠ The last contribution wins, which is `EditorApplication.TerrainScene`'s own arrangement:
        // a second editor terrain source in one process is a test's, and a test's is the one that
        // should answer. No source at all is a session with no ground placed, which runs physics and
        // no collider system rather than throwing.
        if (extensions.All<ITerrainScene>() is not [.., var placed]) {
            return;
        }

        var system = new TerrainColliderSystem(scene, new ScenePlacements(placed));

        session.Loop.Add(system);

        // ⚠ Built once here rather than left to the system's first `EarlyUpdate`. A stroke made on
        // the frame Play was pressed would otherwise find a terrain the system has never heard of,
        // and `TerrainColliders` answers that by syncing — correct, but one frame of a stroke landing
        // on ground that has no shape yet.
        system.Sync();

        colliders.Attach(new TerrainColliders(system));
        session.OnStop(colliders.Detach);
        session.Runs("terrain collision");
    }

    /// <summary>An <c>ITerrainPlacements</c> over what the editor is drawing as ground.</summary>
    /// <remarks>
    ///     ⚠ <b>Asked every sync rather than snapshotted.</b> <c>ITerrainScene.Terrains</c> rebuilds
    ///     its list per call — that is its own documented contract — so moving a terrain entity with
    ///     the gizmo moves where the collider system puts the tile bodies, on the next frame, with
    ///     nobody having to say so.
    /// </remarks>
    sealed class ScenePlacements(ITerrainScene scene) : ITerrainPlacements {
        /// <inheritdoc />
        /// <remarks>
        ///     ⚠ <b>Asked once per iteration by <c>TerrainColliderSystem.Sync</c>'s loop condition,
        ///     and that is fine here rather than being ignored.</b> <c>ITerrainScene.Terrains</c>
        ///     already documents itself as read several times a frame — a four-pane layout asks four
        ///     — and a scene has a handful of terrains, not a hot loop's worth. Caching the list
        ///     instead would be a placement that stops following the gizmo the moment the cache and
        ///     the frame disagree about when it was taken.
        /// </remarks>
        public int PlacementCount => scene.Terrains().Count;

        /// <inheritdoc />
        public TerrainPlacement PlacementAt(int index) {
            var (terrain, origin) = scene.Terrains()[index];

            return new(terrain, origin);
        }
    }
}

/// <summary>The editor's <c>ITerrainColliders</c>: whatever is simulating, or nothing at all.</summary>
/// <remarks>
///     ⚠ <b>A switch rather than an implementation, because the two ends have different
///     lifetimes.</b> <c>TerrainModule.BindColliders</c> resolves the service once and keeps it; the
///     <see cref="TerrainColliderSystem" /> behind it exists only while a play session does. Nothing
///     else can be true at once: a service that changed identity per session would be one the toolset
///     had already bound the previous value of.
/// </remarks>
sealed class PlayColliders : ITerrainColliders {
    TerrainColliders? current;

    /// <summary>How many strokes landed while nothing was simulating.</summary>
    /// <remarks>
    ///     ⚠ <b>The ordinary case, and it is still worth counting.</b> Sculpting before anybody has
    ///     pressed Play is exactly what <see cref="ITerrainColliders" />' remarks call "a terrain with
    ///     no collision, not an error". The number matters the other way round: a session that is
    ///     playing while this climbs is a stroke reaching a switch that was never attached, which has
    ///     no other symptom.
    /// </remarks>
    public int Idle { get; private set; }

    /// <summary>What the current session's system missed, or zero when nothing is attached.</summary>
    public int Missed => current?.Missed ?? 0;

    /// <inheritdoc />
    public void Rebuild(TerrainMap terrain, int tileX, int tileZ) {
        if (current is { } live) {
            live.Rebuild(terrain, tileX, tileZ);

            return;
        }

        // ⚠ Null-checked here too, so an out-of-range *tile* is not the only thing a caller gets away
        // with while nothing is playing. `Rebuild` on a real system validates both.
        ArgumentNullException.ThrowIfNull(terrain);

        Idle++;
    }

    /// <inheritdoc />
    public void Rebuild(TerrainMap terrain, TerrainRect rect) {
        if (current is { } live) {
            live.Rebuild(terrain, rect);

            return;
        }

        ArgumentNullException.ThrowIfNull(terrain);

        Idle++;
    }

    /// <summary>Points the switch at a session's collider system.</summary>
    /// <param name="live">The adapter over it.</param>
    public void Attach(TerrainColliders live) {
        ArgumentNullException.ThrowIfNull(live);

        current = live;
    }

    /// <summary>Points it at nothing, because the session ended.</summary>
    public void Detach() => current = null;
}
