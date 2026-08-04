// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Live.Cluster;

/// <summary>The shadow of <see cref="ShardId" />.</summary>
/// <remarks>
///     <para>
///         <b>This file is how the vocabulary crosses a grain call without the assembly that declares
///         it knowing Orleans exists.</b>
///     </para>
///     <para>
///         ⚠ <b>This file is the price of ADR-017, paid in one place and worth it.</b>
///         <c>Vixen.Live.Abstractions</c> is the assembly a game client transitively references, so it
///         may not carry <c>[GenerateSerializer]</c> — that would put Orleans's own serializer, and
///         its attributes, into an iOS NativeAOT binary for the sake of types that are eleven scalar
///         fields. Orleans's answer to exactly this problem is a surrogate: a shadow struct it does
///         know how to write, and a converter between the two.
///     </para>
///     <para>
///         So the client's assembly stays a plain library, the cluster's assembly holds the mapping,
///         and neither knows about the other's constraints. The alternative — a second copy of
///         <c>ShardId</c> declared with Orleans attributes — is two types that mean the same thing
///         and drift, which is the failure the whole three-assembly split exists to prevent.
///     </para>
///     <para>
///         ⚠ <b>A type added to the vocabulary and not to this file fails at the first grain call
///         that carries it, not at compile time.</b> <c>ClusterSerializationTests</c> round-trips
///         every one of them through a real Orleans serializer for that reason.
///     </para>
/// </remarks>
[GenerateSerializer]
[Immutable]
public struct ShardIdSurrogate {
    /// <summary>The guid.</summary>
    [Id(0)]
    public Guid Value { get; set; }
}

/// <summary>Maps <see cref="ShardId" /> to something Orleans can write.</summary>
[RegisterConverter]
public sealed class ShardIdConverter : IConverter<ShardId, ShardIdSurrogate> {
    /// <inheritdoc />
    public ShardId ConvertFromSurrogate(in ShardIdSurrogate surrogate) => new(surrogate.Value);

    /// <inheritdoc />
    public ShardIdSurrogate ConvertToSurrogate(in ShardId value) => new() { Value = value.Value };
}

/// <summary>The shadow of <see cref="RealmInstanceId" />.</summary>
[GenerateSerializer]
[Immutable]
public struct RealmInstanceIdSurrogate {
    /// <summary>The backend's handle.</summary>
    [Id(0)]
    public string Value { get; set; }
}

/// <summary>Maps <see cref="RealmInstanceId" /> to something Orleans can write.</summary>
[RegisterConverter]
public sealed class RealmInstanceIdConverter : IConverter<RealmInstanceId, RealmInstanceIdSurrogate> {
    /// <inheritdoc />
    public RealmInstanceId ConvertFromSurrogate(in RealmInstanceIdSurrogate surrogate) => new(surrogate.Value);

    /// <inheritdoc />
    public RealmInstanceIdSurrogate ConvertToSurrogate(in RealmInstanceId value) => new() { Value = value.Value };
}

/// <summary>The shadow of <see cref="PlayerKey" />.</summary>
[GenerateSerializer]
[Immutable]
public struct PlayerKeySurrogate {
    /// <summary>The account.</summary>
    [Id(0)]
    public Guid Account { get; set; }

    /// <summary>The character on it.</summary>
    [Id(1)]
    public Guid Character { get; set; }
}

/// <summary>Maps <see cref="PlayerKey" /> to something Orleans can write.</summary>
[RegisterConverter]
public sealed class PlayerKeyConverter : IConverter<PlayerKey, PlayerKeySurrogate> {
    /// <inheritdoc />
    public PlayerKey ConvertFromSurrogate(in PlayerKeySurrogate surrogate) =>
        new(surrogate.Account, surrogate.Character);

    /// <inheritdoc />
    public PlayerKeySurrogate ConvertToSurrogate(in PlayerKey value) =>
        new() { Account = value.Account, Character = value.Character };
}

/// <summary>The shadow of <see cref="RealmVersion" />.</summary>
[GenerateSerializer]
[Immutable]
public struct RealmVersionSurrogate {
    /// <summary>The assembly version.</summary>
    [Id(0)]
    public string Build { get; set; }

    /// <summary>The catalog's build hash.</summary>
    [Id(1)]
    public ulong Content { get; set; }
}

/// <summary>Maps <see cref="RealmVersion" /> to something Orleans can write.</summary>
[RegisterConverter]
public sealed class RealmVersionConverter : IConverter<RealmVersion, RealmVersionSurrogate> {
    /// <inheritdoc />
    public RealmVersion ConvertFromSurrogate(in RealmVersionSurrogate surrogate) =>
        new(surrogate.Build, surrogate.Content);

    /// <inheritdoc />
    public RealmVersionSurrogate ConvertToSurrogate(in RealmVersion value) =>
        new() { Build = value.Build, Content = value.Content };
}

/// <summary>The shadow of <see cref="RealmEndpoint" />.</summary>
[GenerateSerializer]
[Immutable]
public struct RealmEndpointSurrogate {
    /// <summary>The host.</summary>
    [Id(0)]
    public string Host { get; set; }

    /// <summary>The port.</summary>
    [Id(1)]
    public int Port { get; set; }
}

/// <summary>Maps <see cref="RealmEndpoint" /> to something Orleans can write.</summary>
[RegisterConverter]
public sealed class RealmEndpointConverter : IConverter<RealmEndpoint, RealmEndpointSurrogate> {
    /// <inheritdoc />
    public RealmEndpoint ConvertFromSurrogate(in RealmEndpointSurrogate surrogate) =>
        new(surrogate.Host, surrogate.Port);

    /// <inheritdoc />
    public RealmEndpointSurrogate ConvertToSurrogate(in RealmEndpoint value) =>
        new() { Host = value.Host, Port = value.Port };
}

/// <summary>The shadow of <see cref="ShardKey" />.</summary>
[GenerateSerializer]
[Immutable]
public struct ShardKeySurrogate {
    /// <summary>The map's address.</summary>
    [Id(0)]
    public string Map { get; set; }

    /// <summary>The latency zone.</summary>
    [Id(1)]
    public string Region { get; set; }

    /// <summary>The version pair.</summary>
    [Id(2)]
    public RealmVersion Version { get; set; }
}

/// <summary>Maps <see cref="ShardKey" /> to something Orleans can write.</summary>
[RegisterConverter]
public sealed class ShardKeyConverter : IConverter<ShardKey, ShardKeySurrogate> {
    /// <inheritdoc />
    public ShardKey ConvertFromSurrogate(in ShardKeySurrogate surrogate) =>
        new(surrogate.Map, surrogate.Region, surrogate.Version);

    /// <inheritdoc />
    public ShardKeySurrogate ConvertToSurrogate(in ShardKey value) =>
        new() { Map = value.Map, Region = value.Region, Version = value.Version };
}

/// <summary>The shadow of <see cref="ShardCapacity" />.</summary>
[GenerateSerializer]
[Immutable]
public struct ShardCapacitySurrogate {
    /// <summary>Where placement stops preferring it.</summary>
    [Id(0)]
    public int SoftCap { get; set; }

    /// <summary>Where it admits nobody.</summary>
    [Id(1)]
    public int HardCap { get; set; }
}

/// <summary>Maps <see cref="ShardCapacity" /> to something Orleans can write.</summary>
[RegisterConverter]
public sealed class ShardCapacityConverter : IConverter<ShardCapacity, ShardCapacitySurrogate> {
    /// <inheritdoc />
    public ShardCapacity ConvertFromSurrogate(in ShardCapacitySurrogate surrogate) =>
        new(surrogate.SoftCap, surrogate.HardCap);

    /// <inheritdoc />
    public ShardCapacitySurrogate ConvertToSurrogate(in ShardCapacity value) =>
        new() { SoftCap = value.SoftCap, HardCap = value.HardCap };
}
