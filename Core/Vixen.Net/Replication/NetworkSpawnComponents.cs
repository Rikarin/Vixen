// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;
using Vixen.Ecs;
using Vixen.Net.Messaging;

namespace Vixen.Net.Replication;

/// <summary>Which template an instance was stamped from.</summary>
/// <remarks>
///     <para>
///         <b>Derived from the addressable's address, not from a list somebody maintains.</b>
///         [08](../../../docs/plan/08-asset-pipeline-and-addressables.md) already gives every piece of
///         content a stable address and a deterministic build, so the id is a pure function of the
///         address and both ends compute the same one without ever exchanging it. The alternative —
///         the "network prefab list" every engine that lacks a content pipeline ends up with — is an
///         ordered array whose indices are the wire format, which desynchronises the moment two people
///         add a prefab in different orders on different branches.
///     </para>
///     <para>
///         <b>The address, not the content hash.</b> The catalog has both: a
///         <c>CatalogEntry.Address</c> that a human authored and an <c>ObjectId</c> of the bytes. The
///         content hash is the tempting one because it proves the two ends hold the same asset — and
///         it is wrong here, because it changes with every edit to the prefab, so every content patch
///         would renumber the wire. Whether the two ends hold the same content is a question to settle
///         once, in the handshake, rather than smear across every spawn.
///     </para>
/// </remarks>
/// <param name="Value">The hash. Zero is <see cref="None" />.</param>
public readonly record struct NetworkPrefabId(uint Value) {
    /// <summary>Not a prefab.</summary>
    public static NetworkPrefabId None => default;

    /// <summary>Whether this names one.</summary>
    public bool IsValid => Value != 0;

    /// <summary>The id an address hashes to.</summary>
    /// <param name="address">The addressable's address — <c>gameplay/prefabs/crate</c>.</param>
    /// <returns>Its id.</returns>
    /// <remarks>
    ///     <para>
    ///         The same FNV-1a the component manifest uses, for the same reason: it has to be
    ///         reproducible from a string in every process that ever runs, which rules out anything
    ///         seeded or randomised.
    ///     </para>
    ///     <para>
    ///         <b>Thirty-two bits, and the collision is caught rather than tolerated.</b> Two
    ///         addresses hashing alike would be two prefabs the wire cannot tell apart, so
    ///         <c>NetworkPrefabRegistry</c> refuses the second registration and names both. At two
    ///         hundred networked prefabs the odds of that are about five in a million and the fix is
    ///         renaming an asset; at ten thousand they are one in a hundred, which is the point at
    ///         which a project should be told to widen this rather than left to find out.
    ///     </para>
    /// </remarks>
    public static NetworkPrefabId From(string address) => new(ReplicationRegistry.HashTypeName(address));

    /// <inheritdoc />
    public override string ToString() =>
        Value == 0 ? "no prefab" : string.Create(CultureInfo.InvariantCulture, $"prefab {Value:x8}");
}

/// <summary>What an entity was spawned as, and where it belongs.</summary>
/// <remarks>
///     <para>
///         <b>A replicated component rather than a message of its own, and that is the whole design.</b>
///         Riding the snapshot means a spawn gets per-connection baselines, interest, resend-until-
///         acknowledged and budget shedding without any of it being written twice — and it means a
///         spawn and the state of the thing spawned cannot disagree about who can see what, because
///         one mechanism answered the question.
///     </para>
///     <para>
///         <b>It is written once and never changes</b>, which is what makes riding the snapshot
///         reliable enough. An unchanged record is re-sent until the connection acknowledges it and
///         then never again, so the guarantee is not "it was sent" but "it arrived" — which is the one
///         a spawn needs and the one an unreliable channel does not normally give.
///     </para>
///     <para>
///         <b>The highest priority in the registry</b>, so it is the first record about an entity in
///         any snapshot that carries both. That is not sufficient on its own — a state record whose
///         value keeps changing is not suppressed while the spawn is, so a client can hold a bare
///         entity for a few ticks after a lost snapshot — which is why the receiving side is written
///         to merge an instance onto a stand-in rather than to assume it is building from nothing.
///     </para>
/// </remarks>
[DataContract]
public struct NetworkSpawn {
    /// <summary>The template, as <see cref="NetworkPrefabId" />.</summary>
    public uint Prefab;

    /// <summary>The scene, as <c>NetworkSceneId</c>. Zero means "wherever the receiver puts it".</summary>
    public uint Scene;

    /// <summary>Who owns it, as <c>PlayerId</c>. Zero is the server.</summary>
    public uint Owner;
}

/// <summary>Marks an instance the receiving side has already built.</summary>
/// <remarks>
///     Separate from <see cref="NetworkSpawn" /> and deliberately <i>not</i> replicated: it is the
///     local answer to "have I done this yet", and the two ends have different answers. An entity
///     carrying a spawn and not this one is work outstanding, which is what makes the spawn system a
///     query rather than a list of things it is waiting on.
/// </remarks>
public struct NetworkInstance : ITagComponent;

/// <summary>Puts a spawn on the wire.</summary>
public sealed class NetworkSpawnReplicator : IComponentReplicator {
    /// <summary>Ahead of everything, so an instance exists before anything describes it.</summary>
    /// <remarks>
    ///     Records are written in descending priority, so this one precedes every state record in the
    ///     same snapshot. It also means a spawn is the last thing the budget sheds, which is the right
    ///     way round: shedding a position costs a stale position, and shedding a spawn costs every
    ///     position after it.
    /// </remarks>
    public const int SpawnPriority = 1000;

    static readonly WireLane[] Layout = [new("Prefab", 32, false), new("Scene", 32, false), new("Owner", 32, false)];

    /// <inheritdoc />
    public ComponentTypeId ComponentType => ComponentType<NetworkSpawn>.Id;

    /// <inheritdoc />
    public uint TypeId { get; } = ReplicationRegistry.HashTypeName("Vixen.Net.Replication.NetworkSpawn");

    /// <inheritdoc />
    public string TypeName => "Vixen.Net.Replication.NetworkSpawn";

    /// <inheritdoc />
    /// <remarks>
    ///     Reliable, and the redundancy is deliberate. The baseline machinery already re-sends this
    ///     until it is acknowledged, so the channel is not what makes it arrive — it is what makes it
    ///     arrive <i>the first time</i>, which is the difference between an object appearing now and
    ///     an object appearing in a round trip.
    /// </remarks>
    public Channel Channel => Channel.ReliableUnordered;

    /// <inheritdoc />
    public int Priority => SpawnPriority;

    /// <inheritdoc />
    public QueryDescription ChangedQuery { get; } =
        new QueryDescription().RequireChanged([ComponentType<NetworkSpawn>.Id]);

    /// <inheritdoc />
    public ReadOnlySpan<WireLane> Lanes => Layout;

    /// <inheritdoc />
    public bool Has(World world, Entity entity) {
        ArgumentNullException.ThrowIfNull(world);

        return world.Has<NetworkSpawn>(entity);
    }

    /// <inheritdoc />
    public void Write(World world, Entity entity, ref BitWriter writer) {
        ArgumentNullException.ThrowIfNull(world);

        ref readonly var value = ref world.Read<NetworkSpawn>(entity);

        writer.WriteUInt32(value.Prefab);
        writer.WriteUInt32(value.Scene);
        writer.WriteUInt32(value.Owner);
    }

    /// <inheritdoc />
    public bool Apply(World world, Entity entity, ref BitReader reader) {
        ArgumentNullException.ThrowIfNull(world);

        if (!reader.TryReadUInt32(out var prefab)
            || !reader.TryReadUInt32(out var scene)
            || !reader.TryReadUInt32(out var owner)) {
            return false;
        }

        var value = new NetworkSpawn { Prefab = prefab, Scene = scene, Owner = owner };

        if (world.Has<NetworkSpawn>(entity)) {
            world.Set(entity, value);
        } else {
            world.Add(entity, value);
        }

        return true;
    }
}
