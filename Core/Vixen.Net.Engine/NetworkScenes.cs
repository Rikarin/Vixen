// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;
using Vixen.Ecs;
using Vixen.Engine.Scenes;
using Vixen.Net.Replication;
using Vixen.Net.Sessions;

namespace Vixen.Net.Engine;

/// <summary>A scene, as the wire names it.</summary>
/// <remarks>
///     <para>
///         <b>Not a <see cref="SceneHandle" />.</b> A handle is a number a <see cref="SceneManager" />
///         hands out in load order, so the same level is scene 2 on a server that loaded the lobby
///         first and scene 1 on a client that did not. The two have to agree on which scene an object
///         is in, and the thing they already agree on is what the scene is called.
///     </para>
///     <para>
///         So this is the hash of the scene's name, computed the same way the prefab id is computed
///         from an address and for the same reason: it is a pure function of authored content, so
///         neither end has to be told it.
///     </para>
/// </remarks>
/// <param name="Value">The hash. Zero is <see cref="None" />.</param>
public readonly record struct NetworkSceneId(uint Value) {
    /// <summary>No scene — an object that belongs to the session rather than to a level.</summary>
    public static NetworkSceneId None => default;

    /// <summary>Whether this names a scene.</summary>
    public bool IsValid => Value != 0;

    /// <summary>The id a scene name hashes to.</summary>
    /// <param name="name">The scene's name.</param>
    /// <returns>Its id.</returns>
    public static NetworkSceneId From(string name) => new(ReplicationRegistry.HashTypeName(name));

    /// <summary>The id a scene-placed object gets, without anybody allocating it one.</summary>
    /// <param name="index">Its place in the scene's own ordering of networked objects.</param>
    /// <returns>The id.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="index" /> is negative or past what one scene may hold.
    /// </exception>
    /// <remarks>
    ///     <para>
    ///         <b>Derived, so it needs no message.</b> A designer's crate exists on every peer the
    ///         moment the scene loads, before anybody has connected. Numbering those from the server
    ///         would mean a client cannot interact with its own scene until the server has told it
    ///         about every object in it — for a level of ten thousand props, a visible pause with
    ///         nothing to show for it.
    ///     </para>
    ///     <para>
    ///         Sixteen bits of scene and fifteen of index inside the baked band, which caps a scene at
    ///         32,768 networked objects. That is not a cap on props: it is a cap on props that
    ///         <i>replicate</i>, and a level with thirty thousand of those has a bandwidth problem
    ///         several orders of magnitude before it has a numbering one.
    ///     </para>
    ///     <para>
    ///         <b>The scene half is folded down to sixteen bits, so it can collide.</b> Two scene names
    ///         colliding would be two levels whose objects answer to the same ids — which only matters
    ///         if both are loaded at once, and which <c>NetworkSceneMap</c> refuses when it happens.
    ///     </para>
    /// </remarks>
    public NetworkId BakedId(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, MaxBakedObjects);

        var scene = (Value ^ (Value >> 16)) & 0xFFFF;

        return new(NetworkId.FirstBaked | (scene << 15) | (uint)index);
    }

    /// <summary>How many networked objects one scene may have placed in it.</summary>
    public const int MaxBakedObjects = 1 << 15;

    /// <inheritdoc />
    public override string ToString() =>
        Value == 0 ? "no scene" : string.Create(CultureInfo.InvariantCulture, $"scene {Value:x8}");
}

/// <summary>Which local scene each networked scene is, on this peer.</summary>
/// <remarks>
///     The join between a name both ends know and a handle only this one does. Nothing here is sent:
///     it is filled in as this peer loads scenes, and a spawn naming a scene that is not in it is a
///     spawn for a level this peer has not finished loading.
/// </remarks>
public sealed class NetworkSceneMap {
    readonly Dictionary<uint, SceneHandle> byNetworkId = [];
    readonly Dictionary<int, NetworkSceneId> byHandle = [];

    /// <summary>How many scenes are mapped.</summary>
    public int Count => byNetworkId.Count;

    /// <summary>Says that a loaded scene is a named networked one.</summary>
    /// <param name="name">The scene's name, which is what both ends agree on.</param>
    /// <param name="scene">The local handle.</param>
    /// <returns>The networked id.</returns>
    /// <exception cref="InvalidOperationException">
    ///     A different loaded scene already answers to that id.
    /// </exception>
    public NetworkSceneId Track(string name, SceneHandle scene) {
        ArgumentNullException.ThrowIfNull(name);

        var id = NetworkSceneId.From(name);

        if (byNetworkId.TryGetValue(id.Value, out var existing) && existing != scene) {
            throw new InvalidOperationException(
                $"'{name}' hashes to {id}, which scene {existing.Id} is already loaded as. Two scenes loaded at once "
                + "under one id would give their placed objects the same ids; rename one of them."
            );
        }

        byNetworkId[id.Value] = scene;
        byHandle[scene.Id] = id;

        return id;
    }

    /// <summary>Finds the local scene a networked id names.</summary>
    /// <param name="id">The networked id.</param>
    /// <param name="scene">The local handle, if this peer has it.</param>
    /// <returns>Whether it does.</returns>
    public bool TryResolve(NetworkSceneId id, out SceneHandle scene) => byNetworkId.TryGetValue(id.Value, out scene);

    /// <summary>The networked id of a local scene.</summary>
    /// <param name="scene">The local handle.</param>
    /// <returns>Its id, or <see cref="NetworkSceneId.None" /> if it is not tracked.</returns>
    public NetworkSceneId IdOf(SceneHandle scene) => byHandle.GetValueOrDefault(scene.Id);

    /// <summary>Forgets a scene that has been unloaded.</summary>
    /// <param name="scene">The local handle.</param>
    /// <returns>Whether it was tracked.</returns>
    public bool Forget(SceneHandle scene) {
        if (!byHandle.Remove(scene.Id, out var id)) {
            return false;
        }

        byNetworkId.Remove(id.Value);

        return true;
    }
}

/// <summary>Tells each player about the scenes they have loaded, and nothing else.</summary>
/// <remarks>
///     <para>
///         The first resolver in [16](../../../docs/plan/16-networking.md)'s chain, and the one that
///         does the most for the least: a player in the lobby is not sent the contents of a level they
///         are not in, whatever the distance grid would have said about it.
///     </para>
///     <para>
///         <b>An entity in no scene is told to everybody.</b> Not an oversight — an object created
///         outside a scene is one that belongs to the session rather than to a level, and the failure
///         mode of the other choice is objects silently invisible because nobody remembered to tag
///         them. A resolver whose default is "vanish" is one that is debugged by everybody who uses
///         it.
///     </para>
/// </remarks>
public sealed class SceneInterestResolver : IInterestResolver {
    static readonly QueryDescription Networked = new QueryDescription().RequireAll([ComponentType<NetworkId>.Id]);

    readonly Dictionary<uint, HashSet<int>> byPlayer = [];

    /// <summary>Records that a player has a scene loaded.</summary>
    /// <param name="player">Who.</param>
    /// <param name="scene">Which scene.</param>
    public void Enter(PlayerId player, SceneHandle scene) {
        if (!byPlayer.TryGetValue(player.Value, out var scenes)) {
            scenes = [];
            byPlayer[player.Value] = scenes;
        }

        scenes.Add(scene.Id);
    }

    /// <summary>Records that a player no longer has a scene loaded.</summary>
    /// <param name="player">Who.</param>
    /// <param name="scene">Which scene.</param>
    /// <returns>Whether they had it.</returns>
    public bool Leave(PlayerId player, SceneHandle scene) =>
        byPlayer.TryGetValue(player.Value, out var scenes) && scenes.Remove(scene.Id);

    /// <summary>Forgets a player who has gone.</summary>
    /// <param name="player">Who.</param>
    public void Forget(PlayerId player) => byPlayer.Remove(player.Value);

    /// <summary>Which scenes a player has loaded.</summary>
    /// <param name="player">Who.</param>
    /// <returns>How many.</returns>
    public int CountFor(PlayerId player) => byPlayer.TryGetValue(player.Value, out var scenes) ? scenes.Count : 0;

    /// <inheritdoc />
    public void Resolve(World world, PlayerId player, List<Entity> observed) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(observed);

        var scenes = byPlayer.GetValueOrDefault(player.Value);

        foreach (var chunk in world.Chunks(Networked)) {
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                if (!world.TryGet<SceneTag>(entities[index], out var tag)) {
                    observed.Add(entities[index]);

                    continue;
                }

                if (scenes is not null && scenes.Contains(tag.SceneId)) {
                    observed.Add(entities[index]);
                }
            }
        }
    }
}
