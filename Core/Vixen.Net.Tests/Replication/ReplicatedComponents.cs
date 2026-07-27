// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Net.Messaging;
using Vixen.Net.Replication;

namespace Vixen.Net.Tests.Replication;

/// <summary>A position, quantized to three centimetres over a two-kilometre range.</summary>
[Replicated(Channel = Channel.Unreliable, Priority = 10)]
public struct ReplicatedPosition {
    /// <summary>Where it is, along X.</summary>
    [Quantize(-1000f, 1000f, 16)]
    public float X;

    /// <summary>Along Y.</summary>
    [Quantize(-1000f, 1000f, 16)]
    public float Y;

    /// <summary>Along Z.</summary>
    [Quantize(-1000f, 1000f, 16)]
    public float Z;
}

/// <summary>Health, which nobody wants approximated.</summary>
[Replicated(Channel = Channel.Reliable)]
public struct ReplicatedHealth {
    /// <summary>How much of it there is.</summary>
    public int Value;
}

/// <summary>
///     What <c>Vixen.Net.Generators</c> emits for <see cref="ReplicatedPosition" />, written by hand.
/// </summary>
/// <remarks>
///     Hand-written on purpose, and kept: it is the specification the generated one is checked
///     against, so "the generator emits what a careful person would have written" is a test rather
///     than a claim.
/// </remarks>
public sealed class PositionReplicator : IComponentReplicator {
    static readonly QuantizeRange Range = new(-1000f, 1000f, 16);

    /// <inheritdoc />
    public ComponentTypeId ComponentType => ComponentType<ReplicatedPosition>.Id;

    /// <inheritdoc />
    public uint TypeId { get; } = ReplicationRegistry.HashTypeName(typeof(ReplicatedPosition).FullName!);

    /// <inheritdoc />
    public string TypeName => typeof(ReplicatedPosition).FullName!;

    /// <inheritdoc />
    public Channel Channel => Channel.Unreliable;

    /// <inheritdoc />
    public int Priority => 10;

    /// <inheritdoc />
    public QueryDescription ChangedQuery { get; } =
        new QueryDescription().RequireChanged([ComponentType<ReplicatedPosition>.Id]);

    /// <inheritdoc />
    public bool Has(World world, Entity entity) => world.Has<ReplicatedPosition>(entity);

    /// <inheritdoc />
    public void Write(World world, Entity entity, ref BitWriter writer) {
        ref readonly var value = ref world.Read<ReplicatedPosition>(entity);
        writer.WriteQuantized(value.X, Range);
        writer.WriteQuantized(value.Y, Range);
        writer.WriteQuantized(value.Z, Range);
    }

    /// <inheritdoc />
    public bool Apply(World world, Entity entity, ref BitReader reader) {
        if (!reader.TryReadQuantized(Range, out var x)
            || !reader.TryReadQuantized(Range, out var y)
            || !reader.TryReadQuantized(Range, out var z)) {
            return false;
        }

        var value = new ReplicatedPosition { X = x, Y = y, Z = z };

        if (world.Has<ReplicatedPosition>(entity)) {
            world.Set(entity, value);
        } else {
            world.Add(entity, value);
        }

        return true;
    }
}

/// <summary>What the generator emits for <see cref="ReplicatedHealth" />, written by hand.</summary>
public sealed class HealthReplicator : IComponentReplicator {
    /// <inheritdoc />
    public ComponentTypeId ComponentType => ComponentType<ReplicatedHealth>.Id;

    /// <inheritdoc />
    public uint TypeId { get; } = ReplicationRegistry.HashTypeName(typeof(ReplicatedHealth).FullName!);

    /// <inheritdoc />
    public string TypeName => typeof(ReplicatedHealth).FullName!;

    /// <inheritdoc />
    public Channel Channel => Channel.Reliable;

    /// <inheritdoc />
    public int Priority => 0;

    /// <inheritdoc />
    public QueryDescription ChangedQuery { get; } =
        new QueryDescription().RequireChanged([ComponentType<ReplicatedHealth>.Id]);

    /// <inheritdoc />
    public bool Has(World world, Entity entity) => world.Has<ReplicatedHealth>(entity);

    /// <inheritdoc />
    public void Write(World world, Entity entity, ref BitWriter writer) =>
        writer.WriteInt32(world.Read<ReplicatedHealth>(entity).Value);

    /// <inheritdoc />
    public bool Apply(World world, Entity entity, ref BitReader reader) {
        if (!reader.TryReadInt32(out var value)) {
            return false;
        }

        var health = new ReplicatedHealth { Value = value };

        if (world.Has<ReplicatedHealth>(entity)) {
            world.Set(entity, health);
        } else {
            world.Add(entity, health);
        }

        return true;
    }
}
