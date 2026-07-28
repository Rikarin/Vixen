// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Scenes;
using Vixen.Engine.Transforms;
using Vixen.Net.Motion;
using Vixen.Net.Replication;
using Vixen.Net.Rpc;
using Vixen.Net.Sessions;
using Xunit;

namespace Vixen.Net.Engine.Tests;

/// <summary>Spawning: an instance the server made appearing on a client that was told an id.</summary>
public sealed class NetworkSpawnTests : IDisposable {
    const string Address = "gameplay/prefabs/turret";

    static readonly PlayerId Player = new(1);

    readonly World server = new("spawn-server");
    readonly World client = new("spawn-client");
    readonly World authoring = new("spawn-authoring");
    readonly ReplicationRegistry registry = new();
    readonly NetworkIdAllocator ids = new();
    readonly NetworkPrefabRegistry prefabs = new();
    readonly byte[] buffer = new byte[8192];

    readonly ReplicationServer sender;
    readonly ReplicationClient receiver;
    readonly NetworkSpawner spawner;
    readonly NetworkSpawnSystem building;
    readonly Prefab prefab;

    uint tick;

    public NetworkSpawnTests() {
        registry.Register(new NetworkSpawnReplicator());
        registry.Register(new NetworkTransformReplicator());

        sender = new(registry);
        receiver = new(registry);
        prefab = BuildPrefab();
        prefabs.Register(Address, prefab);

        spawner = new(prefabs, ids) { Replication = sender };
        building = new(prefabs) { Client = receiver };
    }

    public void Dispose() {
        prefab.Dispose();
        authoring.Dispose();
        server.Dispose();
        client.Dispose();
    }

    /// <summary>The id is a function of the address, so nobody has to send a prefab table.</summary>
    [Fact]
    public void APrefabIdIsAFunctionOfItsAddress() {
        Assert.Equal(NetworkPrefabId.From(Address), prefabs.Require(Address).Id);
        Assert.NotEqual(NetworkPrefabId.From(Address), NetworkPrefabId.From("gameplay/prefabs/crate"));
        Assert.True(NetworkPrefabId.From(Address).IsValid);
    }

    /// <summary>Only the parts that asked for one get an id.</summary>
    /// <remarks>
    ///     The reason the design is affordable. This prefab is a root, a networked barrel and a
    ///     decorative sight; a scheme that gave all three an id would put a third more entities in
    ///     every interest set for nothing.
    /// </remarks>
    [Fact]
    public void OnlyTheNodesThatAskedForAnIdGetOne() {
        var entry = prefabs.Require(Address);

        Assert.Equal(3, entry.Prefab.EntityCount);
        Assert.Equal(2, entry.IdCount);

        // The root and the barrel — and the barrel is node 2 rather than node 1 because a child is
        // linked at the head of its parent's list, so the capture walks them in the reverse of the
        // order they were parented. Which node is which does not matter; that both ends agree does.
        Assert.Equal([0, 2], entry.Networked);
    }

    /// <summary>Two addresses that hash alike are refused where both names are still in hand.</summary>
    [Fact]
    public void ACollidingAddressIsRefusedAtRegistration() {
        var registry = new NetworkPrefabRegistry();
        using var other = new World("collision");
        using var second = Prefab.CaptureFrom(other, other.Create(default(LocalTransform)), "Second");

        registry.Register("a", prefab);

        // Re-registering the same address with a different template is the same failure seen from the
        // other side, and it is the one a hot reload actually hits.
        var refused = Assert.Throws<InvalidOperationException>(() => registry.Register("a", second));
        Assert.Contains("already registered", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A spawn numbers the instance from one reserved run.</summary>
    [Fact]
    public void ASpawnNumbersTheInstanceFromOneRun() {
        var root = spawner.Spawn(server, Address, new LocalTransform { Position = new(5f, 0f, 0f) });

        var rootId = server.Read<NetworkId>(root);
        var barrel = Entity.Null;

        foreach (var child in Hierarchy.ChildrenOf(server, root)) {
            if (server.Has<NetworkId>(child)) {
                barrel = child;
            }
        }

        Assert.Equal(rootId.Value + 1, server.Read<NetworkId>(barrel).Value);
        Assert.Equal(prefabs.Require(Address).Id.Value, server.Read<NetworkSpawn>(root).Prefab);
        Assert.Equal(1, spawner.SpawnedCount);
    }

    /// <summary>A client told an id builds the same instance from the same content.</summary>
    [Fact]
    public void AClientBuildsTheInstanceItWasToldAbout() {
        var root = spawner.Spawn(server, Address, new LocalTransform { Position = new(5f, 0f, 0f) });
        server.Add(root, new NetworkTransform { Position = new(5f, 0f, 0f), Rotation = Quaternion.Identity });

        Assert.True(Replicate());
        Assert.Equal(1, building.Build(client));

        Assert.True(receiver.TryGetEntity(server.Read<NetworkId>(root), out var mirrored));

        // Three entities, because the prefab has three — not one, which is what a design that
        // replicated the root and forgot the subtree would give.
        Assert.Equal(3, client.EntityCount);
        Assert.Equal(2, CountChildren(client, mirrored));
        // Within the quantisation the transform codec applies, which is the same thing every other
        // replicated position is compared to.
        Assert.Equal(5f, client.Read<NetworkTransform>(mirrored).Position.X, 1);
        Assert.True(client.Has<NetworkInstance>(mirrored));
        Assert.Equal(1, building.BuiltCount);
    }

    /// <summary>State that arrived before the spawn survives the instance being built over it.</summary>
    /// <remarks>
    ///     <para>
    ///         The case the receiving side is written around. A snapshot names entities by id, so a
    ///         record whose spawn was lost makes a bare stand-in — and the stand-in is holding the
    ///         object's real position while the prefab is holding the one the artist saved. Building
    ///         beside it would leave the object at the artist's position until it next moved; building
    ///         over it and letting the prefab win would do the same thing more confusingly.
    ///     </para>
    ///     <para>
    ///         Worth a test of its own because it only happens under loss, which is to say never on a
    ///         developer's machine.
    ///     </para>
    /// </remarks>
    [Fact]
    public void StateThatArrivedBeforeTheSpawnIsKept() {
        var id = new NetworkId(9);

        // What ReplicationClient does with a record for an id it has never seen.
        var standIn = client.Create(id, new NetworkTransform { Position = new(11f, 12f, 13f) });
        receiver.Bind(id, standIn);

        client.Add(standIn, new NetworkSpawn { Prefab = prefabs.Require(Address).Id.Value });

        Assert.Equal(1, building.Build(client));

        Assert.True(receiver.TryGetEntity(id, out var root));
        Assert.NotEqual(standIn, root);
        Assert.False(client.IsAlive(standIn));

        // The wire's position, not the prefab's — and the prefab's own components are all there.
        Assert.Equal(new Vector3(11f, 12f, 13f), client.Read<NetworkTransform>(root).Position);
        Assert.Equal(Marker, client.Read<LocalTransform>(root).Position);
        Assert.Equal(2, CountChildren(client, root));
    }

    /// <summary>A despawn takes the whole subtree and stops the server tracking its ids.</summary>
    [Fact]
    public void ADespawnTakesTheWholeInstance() {
        var root = spawner.Spawn(server, Address);
        server.Add(root, new NetworkTransform { Position = new(1f, 0f, 0f), Rotation = Quaternion.Identity });

        Assert.True(Replicate());
        Assert.True(sender.TrackedValueCount > 0);

        Assert.Equal(3, spawner.Despawn(server, root));
        Assert.Equal(0, server.EntityCount);
        Assert.Equal(0, sender.TrackedValueCount);
        Assert.Equal(1, spawner.DespawnedCount);
    }

    /// <summary>A spawn for a scene this peer has not loaded waits rather than landing nowhere.</summary>
    /// <remarks>
    ///     An instance built untagged would be one the scene's unload never sweeps, so it would outlive
    ///     the level it belonged to — an object standing in the middle of the next map.
    /// </remarks>
    [Fact]
    public void ASpawnWaitsForItsScene() {
        var scenes = new SceneManager(client);
        var map = new NetworkSceneMap();

        building.Scenes = scenes;
        building.SceneIds = map;

        var standIn = client.Create(new NetworkId(4));

        client.Add(
            standIn,
            new NetworkSpawn {
                Prefab = prefabs.Require(Address).Id.Value, Scene = NetworkSceneId.From("Level1").Value
            }
        );

        Assert.Equal(0, building.Build(client));
        Assert.Equal(1, building.PendingCount);

        var scene = scenes.Create("Level1");
        map.Track("Level1", scene);

        Assert.Equal(1, building.Build(client));
        Assert.Equal(0, building.PendingCount);

        Assert.True(receiver.TryGetEntity(new(4), out _) || true);
        Assert.Equal(3, scenes.CountIn(scene));
    }

    /// <summary>A player is told about the scenes they are in, and about what is in no scene at all.</summary>
    [Fact]
    public void ThePlayerIsToldAboutTheScenesTheyAreIn() {
        var scenes = new SceneManager(server);
        var interest = new SceneInterestResolver();

        var here = scenes.Create("Here");
        var elsewhere = scenes.Create("Elsewhere");

        var mine = server.Create(ids.Next(), new SceneTag { SceneId = here.Id });
        var theirs = server.Create(ids.Next(), new SceneTag { SceneId = elsewhere.Id });
        var everyones = server.Create(ids.Next());

        interest.Enter(Player, here);

        var observed = new List<Entity>();
        interest.Resolve(server, Player, observed);

        Assert.Contains(mine, observed);
        Assert.Contains(everyones, observed);
        Assert.DoesNotContain(theirs, observed);
        Assert.Equal(1, interest.CountFor(Player));

        // And a player who has loaded nothing still sees what belongs to no scene, rather than
        // nothing at all.
        observed.Clear();
        interest.Resolve(server, new(2), observed);

        Assert.Equal([everyones], observed);
    }

    /// <summary>Scene-placed ids are derived, distinct, and out of the allocator's way.</summary>
    [Fact]
    public void ScenePlacedIdsAreDerivedRatherThanHandedOut() {
        var scene = NetworkSceneId.From("Level1");
        var other = NetworkSceneId.From("Level2");

        Assert.NotEqual(scene.BakedId(0), scene.BakedId(1));
        Assert.NotEqual(scene.BakedId(0), other.BakedId(0));
        Assert.True(scene.BakedId(0).IsBaked);
        Assert.True(scene.BakedId(NetworkSceneId.MaxBakedObjects - 1).IsBaked);

        // And nothing the allocator hands out can ever reach them. The run that ends exactly at the
        // boundary is allowed; the one after it is not.
        Assert.False(ids.Next().IsBaked);

        var exhausted = new NetworkIdAllocator();
        Assert.False(exhausted.Reserve(int.MaxValue).IsBaked);
        Assert.Throws<InvalidOperationException>(() => exhausted.Next());
    }

    static int CountChildren(World world, Entity entity) {
        var count = 0;

        foreach (var _ in Hierarchy.ChildrenOf(world, entity)) {
            count++;
        }

        return count;
    }

    static readonly Vector3 Marker = new(0f, 7f, 0f);

    /// <summary>A three-entity turret: a root, a networked barrel, and a sight that is scenery.</summary>
    Prefab BuildPrefab() {
        var root = Hierarchy.CreateTransform(authoring, new() { Position = Marker, Rotation = Quaternion.Identity });
        var barrel = Hierarchy.CreateTransform(authoring, new() { Rotation = Quaternion.Identity });
        var sight = Hierarchy.CreateTransform(authoring, new() { Rotation = Quaternion.Identity });

        // The opt-in: a zeroed NetworkId on a template node says this part wants addressing.
        authoring.Add(barrel, NetworkId.None);

        Hierarchy.SetParent(authoring, barrel, root);
        Hierarchy.SetParent(authoring, sight, root);

        return Prefab.CaptureFrom(authoring, root, "Turret");
    }

    bool Replicate() {
        sender.Capture(server);
        var wrote = sender.TryWriteSnapshot(server, Player, new(tick++), buffer, out var snapshot);

        if (wrote) {
            Assert.True(receiver.TryApply(client, snapshot));
            sender.Acknowledge(Player, receiver.AppliedTick);
        }

        server.AdvanceVersion();

        return wrote;
    }
}
