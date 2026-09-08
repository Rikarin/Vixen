// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Engine.Scenes;
using Vixen.Net.Replication;
using Xunit;

namespace Vixen.Net.Engine.Tests;

/// <summary>A spawn into a scene says which scene, without anybody building a map by hand.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>NetworkSceneMap</c> had one construction site in the whole repository and it was a
///         test</b> (<see href="https://github.com/Rikarin/Vixen/issues/491" />), which is worse than
///         an unreached type: <c>NetworkSpawner.Spawn</c> read <c>SceneIds?.IdOf(scene)</c> and wrote
///         <see cref="NetworkSceneId.None" /> when there was no map — so every spawn into a scene
///         went out as "wherever the receiver puts it", holding the handle and the name the whole
///         time. Nothing said so, and the client happily built the instance into no scene.
///     </para>
///     <para>
///         The map is a pure function of the names the <see cref="SceneManager" /> already holds, so
///         there was never a decision for a caller to make. Both halves now make one when they are
///         not given one, and <see cref="NetworkSceneMap.TrackAll" /> is what keeps it honest.
///     </para>
/// </remarks>
public sealed class NetworkSceneMapTests : IDisposable {
    const string Address = "gameplay/prefabs/crate";

    readonly World server = new("scene-map-server");
    readonly World client = new("scene-map-client");
    readonly World authoring = new("scene-map-authoring");
    readonly NetworkPrefabRegistry prefabs = new();
    readonly NetworkIdAllocator ids = new();
    readonly Prefab prefab;

    public NetworkSceneMapTests() {
        prefab = BuildPrefab();
        prefabs.Register(Address, prefab);
    }

    public void Dispose() {
        prefab.Dispose();
        authoring.Dispose();
        server.Dispose();
        client.Dispose();
    }

    /// <summary>A spawner given a scene manager and no map names the scene anyway.</summary>
    /// <remarks>
    ///     The regression test for the whole issue. Before, this asserted zero — and read as correct,
    ///     because "no map" and "no scene" were spelled the same way.
    /// </remarks>
    [Fact]
    public void ASpawnIntoASceneNamesItWithoutAMapBeingSupplied() {
        var scenes = new SceneManager(server);
        var level = scenes.Create("Level1");
        var spawner = new NetworkSpawner(prefabs, ids) { Scenes = scenes };

        var root = spawner.Spawn(server, Address, at: null, level);

        Assert.Equal(NetworkSceneId.From("Level1").Value, server.Read<NetworkSpawn>(root).Scene);
        Assert.NotNull(spawner.SceneIds);
        Assert.Equal(1, spawner.SceneIds!.Count);

        // And the scene really did adopt it, which is the other half of the same call and the half
        // that already worked.
        Assert.Equal(level, scenes.SceneOf(root));
    }

    /// <summary>Without a scene manager there is nothing to name, and that stays true.</summary>
    /// <remarks>
    ///     The instrument check for the test above: a spawner with no way to learn a scene's name
    ///     must still write <see cref="NetworkSceneId.None" />, or the assertion up there is passing
    ///     because everything writes a non-zero id rather than because the map was built.
    /// </remarks>
    [Fact]
    public void ASpawnerWithNoScenesStillNamesNoScene() {
        var spawner = new NetworkSpawner(prefabs, ids);
        var root = spawner.Spawn(server, Address, at: null, new SceneHandle(7));

        Assert.Equal(0u, server.Read<NetworkSpawn>(root).Scene);
        Assert.Null(spawner.SceneIds);
        Assert.Equal(NetworkSceneId.None, spawner.IdOf(new SceneHandle(7)));
    }

    /// <summary>The receiving half waits for the scene and then builds into it, map or no map.</summary>
    /// <remarks>
    ///     ⚠ The reconcile has to run on the tick the scene stops being missing, not once at startup:
    ///     "the level is still loading" is the ordinary case, and a map filled once would answer
    ///     "not here" for the rest of the session.
    /// </remarks>
    [Fact]
    public void AClientBuildsIntoTheSceneOnceItHasLoadedIt() {
        var scenes = new SceneManager(client);
        var building = new NetworkSpawnSystem(prefabs) { Scenes = scenes };

        var standIn = client.Create(new NetworkId(4));

        client.Add(
            standIn,
            new NetworkSpawn {
                Prefab = prefabs.Require(Address).Id.Value, Scene = NetworkSceneId.From("Level1").Value
            }
        );

        Assert.Equal(0, building.Build(client));
        Assert.Equal(1, building.PendingCount);

        var level = scenes.Create("Level1");

        Assert.Equal(1, building.Build(client));
        Assert.Equal(0, building.PendingCount);
        Assert.Equal(3, scenes.CountIn(level));
        Assert.NotNull(building.SceneIds);
    }

    /// <summary>A client with no scene manager at all holds the spawn rather than misplacing it.</summary>
    [Fact]
    public void AClientWithNoScenesNeverBuildsASpawnThatNamesOne() {
        var building = new NetworkSpawnSystem(prefabs);
        var standIn = client.Create(new NetworkId(4));

        client.Add(
            standIn,
            new NetworkSpawn {
                Prefab = prefabs.Require(Address).Id.Value, Scene = NetworkSceneId.From("Level1").Value
            }
        );

        Assert.Equal(0, building.Build(client));
        Assert.Equal(1, building.PendingCount);
        Assert.Null(building.SceneIds);
    }

    /// <summary>The map is reconciled against what is loaded, so an unload takes its scene with it.</summary>
    [Fact]
    public void TrackAllFollowsTheSceneManagerBothWays() {
        var scenes = new SceneManager(server);
        var map = new NetworkSceneMap();

        var lobby = scenes.Create("Lobby");
        var level = scenes.Create("Level1");

        Assert.Equal(2, map.TrackAll(scenes));
        Assert.Equal(NetworkSceneId.From("Lobby"), map.IdOf(lobby));
        Assert.True(map.TryResolve(NetworkSceneId.From("Level1"), out var resolved));
        Assert.Equal(level, resolved);

        scenes.Unload(lobby);

        Assert.Equal(1, map.TrackAll(scenes));
        Assert.Equal(NetworkSceneId.None, map.IdOf(lobby));
        Assert.False(map.TryResolve(NetworkSceneId.From("Lobby"), out _));

        // Idempotent, because it runs every frame on the receiving side.
        Assert.Equal(1, map.TrackAll(scenes));
    }

    /// <summary>A handle the manager cannot name is skipped rather than hashed as the empty string.</summary>
    /// <remarks>
    ///     ⚠ <c>SceneManager.NameOf</c> answers <c>""</c> for a handle it does not know, and hashing
    ///     that would give every unnamed scene on every peer one id — the exact collision this type
    ///     throws to prevent, arriving by the back door and without an exception.
    /// </remarks>
    [Fact]
    public void AnUnnamedSceneIsNotTracked() {
        var scenes = new SceneManager(server);
        var map = new NetworkSceneMap();

        scenes.Create("");

        Assert.Equal(0, map.TrackAll(scenes));
        Assert.Equal(0, map.Count);
    }

    Prefab BuildPrefab() {
        var root = Hierarchy.CreateTransform(authoring, new() { Rotation = Quaternion.Identity });
        var body = Hierarchy.CreateTransform(authoring, new() { Rotation = Quaternion.Identity });
        var trim = Hierarchy.CreateTransform(authoring, new() { Rotation = Quaternion.Identity });

        // The opt-in: a zeroed NetworkId on a template node says this part wants addressing.
        authoring.Add(body, NetworkId.None);

        Hierarchy.SetParent(authoring, body, root);
        Hierarchy.SetParent(authoring, trim, root);

        return Prefab.CaptureFrom(authoring, root, "Crate");
    }
}
