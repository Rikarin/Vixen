// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Scenes;
using Vixen.Engine.Transforms;
using Vixen.Net.Replication;
using Vixen.Net.Rpc;
using Vixen.Net.Rules;
using Vixen.Net.Sessions;

namespace Vixen.Net.Engine;

/// <summary>Spawns and despawns networked instances. Server-side.</summary>
/// <remarks>
///     <para>
///         <b>Instantiate here and the clients follow.</b> A spawn puts a
///         <see cref="NetworkSpawn" /> on the instance's root; the root is a replicated entity like
///         any other, so the snapshot carries it out, and a client's spawn system builds the same
///         prefab from the same id. Nothing here writes a packet — which is the point, because a
///         spawn that travelled by its own route would need its own answer to interest, to loss, and
///         to what a late joiner is owed, and the snapshot already has all three.
///     </para>
///     <para>
///         <b>Despawn is the same trick backwards.</b> Destroying the entity takes it out of what the
///         interest resolver returns, and leaving interest already means "drop it" — so a client
///         cannot tell destruction from walking over the horizon, and does not need to. What this adds
///         is telling <see cref="ReplicationServer" /> to stop tracking the ids, which is memory
///         rather than correctness.
///     </para>
/// </remarks>
public sealed class NetworkSpawner {
    readonly NetworkPrefabRegistry prefabs;
    readonly NetworkIdAllocator ids;
    readonly List<Entity> scratch = [];

    Entity[] created = [];

    /// <summary>Who owns what. Optional; without it a spawn's owner is only on the wire.</summary>
    public NetworkOwnership? Ownership { get; set; }

    /// <summary>Who may do what. Optional; without it the server may spawn and nobody else may.</summary>
    public NetworkRulesRegistry? Rules { get; set; }

    /// <summary>The replicator to tell about despawns, so it stops tracking them.</summary>
    public ReplicationServer? Replication { get; set; }

    /// <summary>The scenes this peer has loaded. Optional; without it spawns belong to no scene.</summary>
    public SceneManager? Scenes { get; set; }

    /// <summary>Which networked scene each local one is. Optional, and needed with <see cref="Scenes" />.</summary>
    public NetworkSceneMap? SceneIds { get; set; }

    /// <summary>How many instances have been spawned.</summary>
    public long SpawnedCount { get; private set; }

    /// <summary>How many have been despawned.</summary>
    public long DespawnedCount { get; private set; }

    /// <summary>Creates a spawner.</summary>
    /// <param name="prefabs">What may be spawned.</param>
    /// <param name="ids">Where ids come from.</param>
    public NetworkSpawner(NetworkPrefabRegistry prefabs, NetworkIdAllocator ids) {
        ArgumentNullException.ThrowIfNull(prefabs);
        ArgumentNullException.ThrowIfNull(ids);

        this.prefabs = prefabs;
        this.ids = ids;
    }

    /// <summary>Whether a player may ask for a spawn at all.</summary>
    /// <param name="requester">Who is asking, or <see cref="PlayerId.None" /> for the server itself.</param>
    /// <returns>Whether they may.</returns>
    /// <remarks>
    ///     <b>The default rule, and it can only be the default rule.</b> Everything else in
    ///     <see cref="NetworkRulesRegistry" /> is asked per object, and this question is asked before
    ///     there is an object to ask about — so a game that wants "clients may spawn projectiles but
    ///     not vehicles" enforces it in the RPC that receives the request, where the prefab is known.
    ///     Pretending a per-object rule could answer this would be worse than saying so.
    /// </remarks>
    public bool MaySpawn(PlayerId requester) =>
        !requester.IsValid || NetworkRules.Allows((Rules?.Default ?? NetworkRules.ServerAuthoritative).Spawn,
            requester,
            isOwner: false);

    /// <summary>Spawns an instance of a registered prefab.</summary>
    /// <param name="world">The server's world.</param>
    /// <param name="address">The prefab's address.</param>
    /// <param name="at">Where the root goes, or null to keep the prefab's own transform.</param>
    /// <param name="scene">Which scene it belongs to, or default for none.</param>
    /// <param name="owner">Who owns it, or <see cref="PlayerId.None" /> for the server.</param>
    /// <returns>The instance's root.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> or <paramref name="address" /> is null.</exception>
    /// <exception cref="ArgumentException">Nothing is registered under the address.</exception>
    public Entity Spawn(
        World world,
        string address,
        LocalTransform? at = null,
        SceneHandle scene = default,
        PlayerId owner = default
    ) {
        ArgumentNullException.ThrowIfNull(address);

        return Spawn(world, prefabs.Require(address), at, scene, owner);
    }

    /// <summary>Spawns an instance of a prefab already looked up.</summary>
    /// <param name="world">The server's world.</param>
    /// <param name="prefab">The prefab.</param>
    /// <param name="at">Where the root goes, or null to keep the prefab's own transform.</param>
    /// <param name="scene">Which scene it belongs to, or default for none.</param>
    /// <param name="owner">Who owns it, or <see cref="PlayerId.None" /> for the server.</param>
    /// <returns>The instance's root.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public Entity Spawn(
        World world,
        NetworkPrefab prefab,
        LocalTransform? at = null,
        SceneHandle scene = default,
        PlayerId owner = default
    ) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(prefab);

        if (created.Length < prefab.Prefab.EntityCount) {
            created = new Entity[prefab.Prefab.EntityCount];
        }

        var instance = created.AsSpan(0, prefab.Prefab.EntityCount);
        var root = prefab.Prefab.Instantiate(world, instance, at);
        var first = ids.Reserve(prefab.IdCount);

        Number(world, prefab, instance, first);

        var sceneId = SceneIds is { } map ? map.IdOf(scene) : NetworkSceneId.None;

        world.Add(
            root,
            new NetworkSpawn { Prefab = prefab.Id.Value, Scene = sceneId.Value, Owner = owner.Value }
        );

        // The server's own copy is already built, so it carries the same "done" mark a client's does.
        // Without it the server's spawn system — if one is running, which it is in a listen server —
        // would try to instantiate the prefab a second time on top of itself.
        world.Add<NetworkInstance>(root);

        if (scene.IsValid && Scenes is { } manager) {
            manager.Adopt(scene, root);
        }

        if (owner.IsValid) {
            Ownership?.SetOwner(first, owner);
        }

        SpawnedCount++;

        return root;
    }

    /// <summary>Despawns an instance.</summary>
    /// <param name="world">The server's world.</param>
    /// <param name="root">The instance's root.</param>
    /// <returns>How many entities were destroyed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    public int Despawn(World world, Entity root) {
        ArgumentNullException.ThrowIfNull(world);

        if (!world.IsAlive(root)) {
            return 0;
        }

        scratch.Clear();
        Collect(world, root);

        foreach (var entity in scratch) {
            if (world.TryGet<NetworkId>(entity, out var id) && id.IsValid) {
                Replication?.Despawn(id);
                Ownership?.Forget(id);
                Rules?.Clear(id);
            }
        }

        // Deepest first, so a parent is never destroyed out from under a child whose own turn has not
        // come — SetParent on a dead parent is what that would cost.
        for (var index = scratch.Count - 1; index >= 0; index--) {
            Hierarchy.SetParent(world, scratch[index], Entity.Null);
            world.Destroy(scratch[index]);
        }

        DespawnedCount++;

        return scratch.Count;
    }

    /// <summary>Despawns every networked instance in a scene.</summary>
    /// <param name="world">The server's world.</param>
    /// <param name="scene">The scene.</param>
    /// <returns>How many instances were despawned.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    /// <remarks>
    ///     Call before <see cref="SceneManager.Unload" />. Unloading destroys the entities either way;
    ///     what this adds is telling the replicator that the ids are finished with, which unloading a
    ///     scene has no way to know.
    /// </remarks>
    public int DespawnScene(World world, SceneHandle scene) {
        ArgumentNullException.ThrowIfNull(world);

        var roots = new List<Entity>();

        foreach (var chunk in world.Chunks(new QueryDescription().WithAll<NetworkSpawn, SceneTag>())) {
            var tags = chunk.ReadValues<SceneTag>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                if (tags[index].SceneId == scene.Id) {
                    roots.Add(entities[index]);
                }
            }
        }

        foreach (var root in roots) {
            Despawn(world, root);
        }

        return roots.Count;
    }

    /// <summary>Gives an instance's networked nodes their ids.</summary>
    /// <remarks>
    ///     A node the template marked already carries a zeroed <see cref="NetworkId" /> — it was copied
    ///     with everything else — so it is set rather than added, and only the root can need adding.
    ///     The order is the registry's, which is the template's capture order, which is what the
    ///     receiving side walks too.
    /// </remarks>
    static void Number(World world, NetworkPrefab prefab, ReadOnlySpan<Entity> instance, NetworkId first) {
        for (var index = 0; index < prefab.Networked.Length; index++) {
            var entity = instance[prefab.Networked[index]];
            var id = new NetworkId(first.Value + (uint)index);

            if (world.Has<NetworkId>(entity)) {
                world.Set(entity, id);
            } else {
                world.Add(entity, id);
            }
        }
    }

    void Collect(World world, Entity entity) {
        scratch.Add(entity);

        foreach (var child in Hierarchy.ChildrenOf(world, entity)) {
            Collect(world, child);
        }
    }
}
