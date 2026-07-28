// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Scenes;
using Vixen.Net.Replication;
using Vixen.Net.Rpc;
using Vixen.Net.Sessions;

namespace Vixen.Net.Engine;

/// <summary>Turns arrived spawns into instances. Client-side.</summary>
/// <remarks>
///     <para>
///         <b>A query, not a queue.</b> An entity carrying a <see cref="NetworkSpawn" /> and not a
///         <see cref="NetworkInstance" /> is work outstanding, and that is the whole of the state this
///         keeps — so a spawn for a scene that is still loading, or for a prefab whose bundle has not
///         arrived, is simply still outstanding next frame and builds itself the moment its
///         precondition is met. A queue would have needed a retry policy, a dead-letter rule and a way
///         to notice that neither ever fires.
///     </para>
///     <para>
///         <b>The entity it starts from is usually a stand-in it did not make.</b> A snapshot names an
///         entity by id and <see cref="ReplicationClient" /> makes a bare one for an id it has not
///         seen, so by the time a spawn is read the entity may already be carrying a position, a
///         velocity, and whatever else arrived in the same snapshot. Building the prefab over it —
///         rather than beside it — is what keeps that state, and the prefab's own defaults lose
///         wherever the two overlap. See <see cref="Prefab.InstantiateOnto" />.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.EarlyUpdate)]
public sealed class NetworkSpawnSystem : SystemBase, IDeclaredAccess {
    readonly NetworkPrefabRegistry prefabs;
    readonly QueryDescription pending = new QueryDescription().WithAll<NetworkSpawn, NetworkId>()
        .WithNone<NetworkInstance>();

    readonly List<Entity> waiting = [];

    Entity[] created = [];

    /// <inheritdoc />
    public SystemAccess Access { get; } = SystemAccess.Declare().Read<NetworkSpawn>().Read<NetworkId>().Build();

    /// <summary>The client to tell which entity each id became. Optional, and wanted in a real client.</summary>
    /// <remarks>
    ///     Without it the instance is built and the snapshot keeps addressing the stand-in, which no
    ///     longer exists — so every record after the spawn makes a second bare entity. It is optional
    ///     only because a test may not have a client.
    /// </remarks>
    public ReplicationClient? Client { get; set; }

    /// <summary>The scenes this peer has loaded. Optional; without it instances belong to no scene.</summary>
    public SceneManager? Scenes { get; set; }

    /// <summary>Which networked scene each local one is. Needed with <see cref="Scenes" />.</summary>
    public NetworkSceneMap? SceneIds { get; set; }

    /// <summary>Who owns what, kept in step with what the spawns say. Optional.</summary>
    public NetworkOwnership? Ownership { get; set; }

    /// <summary>How many instances have been built.</summary>
    public long BuiltCount { get; private set; }

    /// <summary>How many spawns were outstanding at the end of the last pass.</summary>
    /// <remarks>
    ///     Worth watching, and worth alerting on if it stays above zero. A few for a frame is content
    ///     or a scene still loading; a number that never comes down is a client that will never have
    ///     the prefab, which presents to the player as an object that is there on everyone else's
    ///     screen and not on theirs.
    /// </remarks>
    public int PendingCount { get; private set; }

    /// <summary>Creates the system.</summary>
    /// <param name="prefabs">What may be spawned. The same registry the server built.</param>
    public NetworkSpawnSystem(NetworkPrefabRegistry prefabs) {
        ArgumentNullException.ThrowIfNull(prefabs);
        this.prefabs = prefabs;
    }

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Build(context.World);

        return dependency;
    }

    /// <summary>Builds every instance whose spawn has arrived and whose prefab and scene are here.</summary>
    /// <param name="world">The client's world.</param>
    /// <returns>How many were built.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    public int Build(World world) {
        ArgumentNullException.ThrowIfNull(world);

        waiting.Clear();

        foreach (var chunk in world.Chunks(pending)) {
            waiting.AddRange(chunk.Entities);
        }

        var built = 0;
        PendingCount = 0;

        // Collected first, because building one is a structural change and the chunks are being
        // walked. The list is also why a stand-in destroyed by its own build cannot be visited twice.
        foreach (var standIn in waiting) {
            if (!world.IsAlive(standIn)) {
                continue;
            }

            if (TryBuild(world, standIn)) {
                built++;
            } else {
                PendingCount++;
            }
        }

        BuiltCount += built;

        return built;
    }

    bool TryBuild(World world, Entity standIn) {
        var spawn = world.Read<NetworkSpawn>(standIn);
        var id = world.Read<NetworkId>(standIn);

        if (!prefabs.TryGet(new NetworkPrefabId(spawn.Prefab), out var prefab)) {
            return false;
        }

        var scene = SceneHandle.None;

        if (spawn.Scene != 0) {
            // The scene is still loading, or this peer was never told to load it. Either way the
            // instance is not built yet: putting it in the world untagged would leave an object that
            // the scene's unload does not sweep, which outlives the level it belonged to.
            if (SceneIds is not { } map || !map.TryResolve(new(spawn.Scene), out scene)) {
                return false;
            }
        }

        if (created.Length < prefab.Prefab.EntityCount) {
            created = new Entity[prefab.Prefab.EntityCount];
        }

        var instance = created.AsSpan(0, prefab.Prefab.EntityCount);
        var root = prefab.Prefab.InstantiateOnto(world, standIn, instance);

        world.Add<NetworkInstance>(root);

        for (var index = 0; index < prefab.Networked.Length; index++) {
            var entity = instance[prefab.Networked[index]];
            var member = new NetworkId(id.Value + (uint)index);

            if (world.Has<NetworkId>(entity)) {
                world.Set(entity, member);
            } else {
                world.Add(entity, member);
            }

            // Including the root, whose id already pointed at the stand-in this replaced. Rebinding
            // it is what redirects every record after this one onto the instance.
            Client?.Bind(member, entity);
        }

        if (scene.IsValid && Scenes is { } manager) {
            manager.Adopt(scene, root);
        }

        if (spawn.Owner != 0) {
            Ownership?.SetOwner(id, new(spawn.Owner));
        }

        return true;
    }
}
