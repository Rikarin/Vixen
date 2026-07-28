// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Net;
using Vixen.Net.Messaging;
using Vixen.Net.Replication;

namespace Vixen.Net.Generators.Tests;

/// <summary>
///     A replicated component declared in this project, so the generator runs over it as part of
///     building the tests and the emitted replicator is compiled in.
/// </summary>
[Replicated(Channel = Channel.Unreliable, Priority = 7)]
public struct GeneratedTransform {
    /// <summary>Quantized: three centimetres over two kilometres.</summary>
    [Quantize(-1000f, 1000f, 16)]
    public float X;

    /// <summary>The same.</summary>
    [Quantize(-1000f, 1000f, 16)]
    public float Y;

    /// <summary>Not quantized, so it costs a whole float.</summary>
    public float Yaw;

    /// <summary>An integer field, to prove the widths.</summary>
    public int Frame;

    /// <summary>A byte.</summary>
    public byte Team;

    /// <summary>A flag, which costs one bit.</summary>
    public bool Grounded;
}

/// <summary>A pose: a quantized position, and a rotation sent smallest-three.</summary>
[Replicated(Priority = 20)]
public struct GeneratedPose {
    /// <summary>Where it is, to three centimetres over two kilometres.</summary>
    [Quantize(-1000f, 1000f, 16)]
    public Vector3 Position;

    /// <summary>Which way it faces. No range to declare — a unit quaternion already has one.</summary>
    public Quaternion Rotation;
}

/// <summary>A replicated component with default settings, to check the defaults are the defaults.</summary>
[Replicated]
public struct GeneratedScore {
    /// <summary>The score.</summary>
    public uint Value;
}

/// <summary>
///     What the generator should have emitted for <see cref="GeneratedTransform" />, written by hand.
/// </summary>
/// <remarks>
///     The claim "the generator emits what a careful person would have written" is worth exactly as
///     much as the test that checks it. This is that test's other half: the two are run against the
///     same values and their bits are compared.
/// </remarks>
public sealed class HandWrittenTransformReplicator : IComponentReplicator {
    static readonly QuantizeRange Range = new(-1000f, 1000f, 16);

    /// <inheritdoc />
    public ComponentTypeId ComponentType => ComponentType<GeneratedTransform>.Id;

    /// <inheritdoc />
    public uint TypeId { get; } = ReplicationRegistry.HashTypeName(typeof(GeneratedTransform).FullName!);

    /// <inheritdoc />
    public string TypeName => typeof(GeneratedTransform).FullName!;

    /// <inheritdoc />
    public Channel Channel => Channel.Unreliable;

    /// <inheritdoc />
    public int Priority => 7;

    /// <inheritdoc />
    public QueryDescription ChangedQuery { get; } =
        new QueryDescription().RequireChanged([ComponentType<GeneratedTransform>.Id]);

    /// <inheritdoc />
    public bool Has(World world, Entity entity) => world.Has<GeneratedTransform>(entity);

    /// <inheritdoc />
    public void Write(World world, Entity entity, ref BitWriter writer) {
        ref readonly var value = ref world.Read<GeneratedTransform>(entity);
        writer.WriteQuantized(value.X, Range);
        writer.WriteQuantized(value.Y, Range);
        writer.WriteSingle(value.Yaw);
        writer.WriteInt32(value.Frame);
        writer.Write(value.Team, 8);
        writer.WriteBool(value.Grounded);
    }

    /// <inheritdoc />
    public bool Apply(World world, Entity entity, ref BitReader reader) {
        if (!reader.TryReadQuantized(Range, out var x)
            || !reader.TryReadQuantized(Range, out var y)
            || !reader.TryReadSingle(out var yaw)
            || !reader.TryReadInt32(out var frame)
            || !reader.TryRead(8, out var team)
            || !reader.TryReadBool(out var grounded)) {
            return false;
        }

        var value = new GeneratedTransform {
            X = x,
            Y = y,
            Yaw = yaw,
            Frame = frame,
            Team = (byte)team,
            Grounded = grounded
        };

        if (world.Has<GeneratedTransform>(entity)) {
            world.Set(entity, value);
        } else {
            world.Add(entity, value);
        }

        return true;
    }
}
