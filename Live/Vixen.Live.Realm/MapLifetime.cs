// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Engine.Scenes;

namespace Vixen.Live.Realms;

/// <summary>Where the map is in its life.</summary>
public enum MapState : byte {
    /// <summary>The host is opening it. The realm is not a placement candidate.</summary>
    Loading = 0,

    /// <summary>It is in the world. The realm may report ready.</summary>
    Ready = 1,

    /// <summary>Draining: the map is still simulating, and nobody new arrives.</summary>
    Quiescing = 2,

    /// <summary>Gone.</summary>
    Unloaded = 3
}

/// <summary>A shard is a map, and a map is content. This is the join, and it is deliberately thin.</summary>
/// <remarks>
///     <para>
///         Doc 27 § The scene-management join describes a chain in which every link already exists:
///         <c>RealmSpec.Map</c> is an addressable address, <c>AssetManager</c> resolves it to a
///         <c>SceneAsset</c>, <c>SceneManager</c> loads that into a world, and
///         <c>NetworkSceneId</c> is the hash of the scene's <em>name</em> — so a client that has
///         loaded the map already agrees with the realm about what the props are before a packet
///         arrives.
///     </para>
///     <para>
///         ⚠ <b>So this class does not load anything, and that is the point.</b> The host already
///         opens <c>AppConfig.StartupScene</c> before <c>OnInitialise</c>, reports its own failures
///         and survives them; a realm that loaded the map a second way would be a second code path
///         for content failures, tested half as often. What is left over — and what the realm
///         genuinely needs — is the question "is the map up yet", because that is what separates
///         <see cref="ShardState.Starting" /> from <see cref="ShardState.Ready" />, and nothing in
///         the host answers it.
///     </para>
/// </remarks>
public sealed class MapLifetime {
    /// <summary>The address the shard was told to be.</summary>
    public string Address { get; }

    /// <summary>The scene's name — the address's leaf, which is what <c>NetworkSceneId</c> hashes.</summary>
    public string SceneName { get; }

    /// <summary>Where it is.</summary>
    public MapState State { get; private set; } = MapState.Loading;

    /// <summary>The loaded scene, once there is one.</summary>
    public SceneHandle Scene { get; private set; } = SceneHandle.None;

    /// <summary>Whether the realm may report itself ready.</summary>
    public bool IsReady => State is MapState.Ready or MapState.Quiescing;

    /// <summary>Names the map a shard carries.</summary>
    /// <param name="key">The shard's key, whose map address and leaf name this reads.</param>
    public MapLifetime(ShardKey key) {
        Address = key.Map;
        SceneName = key.SceneName;
    }

    /// <summary>Looks for the map among the scenes the host has loaded.</summary>
    /// <param name="scenes">The scene manager, or <see langword="null" /> for a head with no world.</param>
    /// <returns>Whether the map is up.</returns>
    /// <remarks>
    ///     <para>
    ///         Matched by name rather than by handle because the handle belongs to
    ///         <c>VixenApplication.StartupScene</c>, which a <c>Game</c> cannot reach — and matching
    ///         by name is not a workaround, it is the same identity the wire uses.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A realm whose map never appears never becomes ready</b>, and that is the correct
    ///         failure. It will be started, it will not be placed on, and the orchestrator will
    ///         eventually stop it — which is a shard that quietly did nothing rather than a shard
    ///         that admitted players into an empty world.
    ///     </para>
    /// </remarks>
    public bool Resolve(SceneManager? scenes) {
        if (State != MapState.Loading || scenes is null) {
            return IsReady;
        }

        foreach (var loaded in scenes.Loaded) {
            if (string.Equals(scenes.NameOf(loaded), SceneName, StringComparison.Ordinal)) {
                Scene = loaded;
                State = MapState.Ready;

                return true;
            }
        }

        return false;
    }

    /// <summary>Says the map is up, when the realm loaded it some other way.</summary>
    /// <param name="scene">The scene.</param>
    /// <remarks>
    ///     The seam for a realm whose map is not a startup scene — a generated map, a persistent
    ///     shard rehydrating authored state (doc 27 § Shard kinds). It exists so that such a realm
    ///     does not have to fake a scene name to become ready.
    /// </remarks>
    public void Ready(SceneHandle scene) {
        Scene = scene;
        State = MapState.Ready;
    }

    /// <summary>Stops taking arrivals. The map keeps simulating.</summary>
    /// <remarks>
    ///     Doc 27 § Drain: a drained shard moves its players out, it does not disconnect them. So
    ///     quiescing changes nothing about the simulation and everything about admission — which is
    ///     why this is one field and not a mode the whole realm runs in.
    /// </remarks>
    public void Quiesce() {
        if (State == MapState.Ready) {
            State = MapState.Quiescing;
        }
    }

    /// <summary>Takes the map out of the world.</summary>
    /// <param name="scenes">The scene manager, or <see langword="null" />.</param>
    /// <returns>How many entities went with it.</returns>
    public int Unload(SceneManager? scenes) {
        var removed = 0;

        if (scenes is not null && Scene.IsValid) {
            removed = scenes.Unload(Scene);
        }

        Scene = SceneHandle.None;
        State = MapState.Unloaded;

        return removed;
    }
}
