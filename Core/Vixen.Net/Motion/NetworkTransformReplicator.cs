// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Net.Messaging;
using Vixen.Net.Replication;

namespace Vixen.Net.Motion;

/// <summary>Replicates <see cref="NetworkTransform" />.</summary>
/// <remarks>
///     Written by hand rather than generated, because the component ships with the engine and the
///     engine's own package does not run the generator over itself. It is also the worked example the
///     <see cref="IComponentReplicator" /> documentation promises: a rotation in 32 bits, a position
///     in 48, and a teleport counter in 8.
/// </remarks>
public sealed class NetworkTransformReplicator : IComponentReplicator {
    /// <summary>How many bits each position axis costs.</summary>
    public const int PositionBits = 16;

    /// <summary>The range positions are quantized into: ±1000 metres, to three centimetres.</summary>
    public static QuantizeRange PositionRange { get; } = new(-1000f, 1000f, PositionBits);

    /// <inheritdoc />
    public ComponentTypeId ComponentType => ComponentType<NetworkTransform>.Id;

    /// <inheritdoc />
    public uint TypeId { get; } = ReplicationRegistry.HashTypeName("Vixen.Net.Motion.NetworkTransform");

    /// <inheritdoc />
    public string TypeName => "Vixen.Net.Motion.NetworkTransform";

    /// <inheritdoc />
    public Channel Channel => Channel.Unreliable;

    /// <summary>
    ///     Ahead of most things when the budget runs out: a wrong position is visible and a late score
    ///     is not.
    /// </summary>
    public int Priority => 20;

    /// <inheritdoc />
    public QueryDescription ChangedQuery { get; } =
        new QueryDescription().RequireChanged([ComponentType<NetworkTransform>.Id]);

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         Three position axes, then a rotation, then the teleport counter — the order
    ///         <see cref="Write" /> puts them in, which is the only thing that makes this correct.
    ///     </para>
    ///     <para>
    ///         The rotation is four lanes rather than one, because smallest-three is two bits naming
    ///         the dropped component and three quantized levels. Splitting it is what lets a turning
    ///         object cost a few bits: the index is the same from one tick to the next while the
    ///         object keeps turning the same way, and the three levels move a little. On the tick the
    ///         index changes, all four lanes change and the rotation costs slightly more than sending
    ///         it whole — which is the right trade, because that tick is rare and the others are
    ///         every tick.
    ///     </para>
    /// </remarks>
    public ReadOnlySpan<WireLane> Lanes => Layout;

    // An array rather than a collection expression: a span of a struct with more than one field
    // cannot be a read-only blob in metadata, so the expression form would be a stack allocation
    // that this property is not allowed to hand out.
    static readonly WireLane[] Layout =
    [
        new(PositionBits, true),
        new(PositionBits, true),
        new(PositionBits, true),
        new(2, false),
        new(MathCodec.RotationBits, true),
        new(MathCodec.RotationBits, true),
        new(MathCodec.RotationBits, true),
        new(8, true)
    ];

    /// <inheritdoc />
    public bool Has(World world, Entity entity) => world.Has<NetworkTransform>(entity);

    /// <inheritdoc />
    public void Write(World world, Entity entity, ref BitWriter writer) {
        ref readonly var value = ref world.Read<NetworkTransform>(entity);
        writer.WriteVector3(value.Position, PositionRange);
        writer.WriteRotation(value.Rotation);
        writer.Write(value.TeleportCount, 8);
    }

    /// <inheritdoc />
    public bool Apply(World world, Entity entity, ref BitReader reader) {
        if (!reader.TryReadVector3(PositionRange, out var position)
            || !reader.TryReadRotation(out var rotation)
            || !reader.TryRead(8, out var teleports)) {
            return false;
        }

        var value = new NetworkTransform {
            Position = position,
            Rotation = rotation,
            TeleportCount = (byte)teleports
        };

        if (world.Has<NetworkTransform>(entity)) {
            world.Set(entity, value);
        } else {
            world.Add(entity, value);
        }

        return true;
    }
}
