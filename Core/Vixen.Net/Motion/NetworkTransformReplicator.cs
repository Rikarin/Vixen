// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Net.Messaging;
using Vixen.Net.Replication;

namespace Vixen.Net.Motion;

/// <summary>Which parts of a <see cref="NetworkTransform" /> are worth sending.</summary>
/// <remarks>
///     <para>
///         A door that only rotates pays forty-eight bits a tick for a position that is the same
///         number it was when the level loaded, and a lift that only rises pays thirty-two for a
///         rotation. Naming the axes is what stops that.
///     </para>
///     <para>
///         <b>This is a property of the replicator, not of the entity.</b> Both ends have to agree
///         about a lane layout before a single bit is decodable, and the delta codec's baselines are
///         checked against one fixed width — so a mask that varied per entity would be a wire format
///         that varied per entity, which nothing on either side could parse. A game with doors and
///         players wanting different masks gives the doors a component of their own, exactly as
///         <see cref="NetworkTransform" />'s own remarks say a game with a bigger world does.
///     </para>
/// </remarks>
[Flags]
public enum NetworkTransformAxes {
    /// <summary>Nothing. Refused by the replicator rather than accepted.</summary>
    None = 0,

    /// <summary>Send the X of the position.</summary>
    PositionX = 1,

    /// <summary>Send the Y of the position.</summary>
    PositionY = 2,

    /// <summary>Send the Z of the position.</summary>
    PositionZ = 4,

    /// <summary>Send the whole position.</summary>
    Position = PositionX | PositionY | PositionZ,

    /// <summary>Send the rotation.</summary>
    Rotation = 8,

    /// <summary>Everything, which is what ships.</summary>
    All = Position | Rotation
}

/// <summary>Replicates <see cref="NetworkTransform" />.</summary>
/// <remarks>
///     <para>
///         Written by hand rather than generated, because the component ships with the engine and the
///         engine's own package does not run the generator over itself. It is also the worked example
///         the <see cref="IComponentReplicator" /> documentation promises: a rotation in 32 bits, a
///         position in 48, and a teleport counter in 8.
///     </para>
///     <para>
///         <b>A narrowed <see cref="Axes" /> renames the type on the wire, and that is the safety
///         property rather than a cosmetic one.</b> Two peers built with different masks would
///         disagree about every lane in the layout and would decode each other's transforms into
///         plausible wrong numbers — the failure that presents as objects drifting rather than as an
///         error. Folding the mask into <see cref="TypeName" /> means it folds into
///         <see cref="TypeId" /> and therefore into <c>ReplicationRegistry.ManifestHash</c>, so the
///         handshake refuses the connection instead. The unmasked default keeps the bare name, so
///         nothing that ships today changes its wire id.
///     </para>
/// </remarks>
public sealed class NetworkTransformReplicator : IComponentReplicator {
    /// <summary>How many bits each position axis costs.</summary>
    public const int PositionBits = 16;

    /// <summary>The range positions are quantized into: ±1000 metres, to three centimetres.</summary>
    public static QuantizeRange PositionRange { get; } = new(-1000f, 1000f, PositionBits);

    /// <inheritdoc />
    public ComponentTypeId ComponentType => ComponentType<NetworkTransform>.Id;

    /// <summary>Which axes this replicator sends. <see cref="NetworkTransformAxes.All" /> by default.</summary>
    public NetworkTransformAxes Axes { get; }

    /// <inheritdoc />
    public uint TypeId { get; }

    /// <inheritdoc />
    public string TypeName { get; }

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
    public ReadOnlySpan<WireLane> Lanes => layout;

    // An array rather than a collection expression: a span of a struct with more than one field
    // cannot be a read-only blob in metadata, so the expression form would be a stack allocation
    // that this property is not allowed to hand out.
    readonly WireLane[] layout;

    /// <summary>The replicator that sends everything — the shipped default.</summary>
    public NetworkTransformReplicator() : this(NetworkTransformAxes.All) { }

    /// <summary>A replicator that sends only the named axes.</summary>
    /// <param name="axes">What to send. Every peer in the session needs the same value.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="axes" /> is <see cref="NetworkTransformAxes.None" />, which would be a
    ///     replicator that spends eight bits a tick saying nothing moved.
    /// </exception>
    public NetworkTransformReplicator(NetworkTransformAxes axes) {
        if (axes is NetworkTransformAxes.None || (axes & ~NetworkTransformAxes.All) != 0) {
            throw new ArgumentOutOfRangeException(
                nameof(axes),
                axes,
                "A NetworkTransform replicator has to send at least one axis and cannot send an axis "
                + "the component does not have. None would put a teleport counter on the wire every "
                + "tick to describe a value nobody sent."
            );
        }

        Axes = axes;
        TypeName = axes is NetworkTransformAxes.All
            ? "Vixen.Net.Motion.NetworkTransform"
            : $"Vixen.Net.Motion.NetworkTransform[{Suffix(axes)}]";
        TypeId = ReplicationRegistry.HashTypeName(TypeName);

        var lanes = new List<WireLane>(8);

        if ((axes & NetworkTransformAxes.PositionX) != 0) {
            lanes.Add(new("Position.X", PositionBits, true));
        }

        if ((axes & NetworkTransformAxes.PositionY) != 0) {
            lanes.Add(new("Position.Y", PositionBits, true));
        }

        if ((axes & NetworkTransformAxes.PositionZ) != 0) {
            lanes.Add(new("Position.Z", PositionBits, true));
        }

        if ((axes & NetworkTransformAxes.Rotation) != 0) {
            lanes.Add(new("Rotation.Dropped", 2, false));
            lanes.Add(new("Rotation.A", MathCodec.RotationBits, true));
            lanes.Add(new("Rotation.B", MathCodec.RotationBits, true));
            lanes.Add(new("Rotation.C", MathCodec.RotationBits, true));
        }

        // Always. It is what says a change was a jump rather than a movement, and a masked axis is
        // the case where that matters most: a lift told only its Y still has to be able to say the
        // Y it just sent is a different floor rather than a fall.
        lanes.Add(new("TeleportCount", 8, true));

        layout = [.. lanes];
    }

    /// <inheritdoc />
    public bool Has(World world, Entity entity) => world.Has<NetworkTransform>(entity);

    /// <inheritdoc />
    public void Write(World world, Entity entity, ref BitWriter writer) {
        ref readonly var value = ref world.Read<NetworkTransform>(entity);

        if ((Axes & NetworkTransformAxes.PositionX) != 0) {
            writer.WriteQuantized(value.Position.X, PositionRange);
        }

        if ((Axes & NetworkTransformAxes.PositionY) != 0) {
            writer.WriteQuantized(value.Position.Y, PositionRange);
        }

        if ((Axes & NetworkTransformAxes.PositionZ) != 0) {
            writer.WriteQuantized(value.Position.Z, PositionRange);
        }

        if ((Axes & NetworkTransformAxes.Rotation) != 0) {
            writer.WriteRotation(value.Rotation);
        }

        writer.Write(value.TeleportCount, 8);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <b>A masked axis keeps whatever the receiver already had, and it is deliberately not
    ///     zero.</b> A door replicating a rotation and nothing else has its position from the prefab
    ///     that built it; writing a fresh <see cref="NetworkTransform" /> would put every one of them
    ///     at the world origin — a zeroed field whose zero is a perfectly valid position, which is
    ///     how this class of bug always looks.
    /// </remarks>
    public bool Apply(World world, Entity entity, ref BitReader reader) {
        var had = world.Has<NetworkTransform>(entity);
        var value = had ? world.Read<NetworkTransform>(entity) : default;
        var position = value.Position;

        if ((Axes & NetworkTransformAxes.PositionX) != 0) {
            if (!reader.TryReadQuantized(PositionRange, out var x)) {
                return false;
            }

            position = new(x, position.Y, position.Z);
        }

        if ((Axes & NetworkTransformAxes.PositionY) != 0) {
            if (!reader.TryReadQuantized(PositionRange, out var y)) {
                return false;
            }

            position = new(position.X, y, position.Z);
        }

        if ((Axes & NetworkTransformAxes.PositionZ) != 0) {
            if (!reader.TryReadQuantized(PositionRange, out var z)) {
                return false;
            }

            position = new(position.X, position.Y, z);
        }

        value.Position = position;

        if ((Axes & NetworkTransformAxes.Rotation) != 0) {
            if (!reader.TryReadRotation(out var rotation)) {
                return false;
            }

            value.Rotation = rotation;
        } else if (!had) {
            // A rotation nobody sends is the identity rather than the zero quaternion, which is not a
            // rotation at all and composes every matrix under it to nothing.
            value.Rotation = Quaternion.Identity;
        }

        if (!reader.TryRead(8, out var teleports)) {
            return false;
        }

        value.TeleportCount = (byte)teleports;

        if (had) {
            world.Set(entity, value);
        } else {
            world.Add(entity, value);
        }

        return true;
    }

    static string Suffix(NetworkTransformAxes axes) {
        var name = string.Empty;

        if ((axes & NetworkTransformAxes.PositionX) != 0) {
            name += "X";
        }

        if ((axes & NetworkTransformAxes.PositionY) != 0) {
            name += "Y";
        }

        if ((axes & NetworkTransformAxes.PositionZ) != 0) {
            name += "Z";
        }

        if ((axes & NetworkTransformAxes.Rotation) != 0) {
            name += "R";
        }

        return name;
    }
}

/// <summary>Replicates <see cref="NetworkParent" />: which frame a transform is quoted in.</summary>
/// <remarks>
///     <para>
///         <b>Reliable and high priority, for <c>NetworkSpawn</c>'s reasons exactly.</b> It is written
///         once when somebody mounts and once when they get off, so the baseline machinery suppresses
///         it on every tick in between and it costs nothing to keep sending. What it must not do is
///         arrive after the transforms that depend on it, so it outranks
///         <see cref="NetworkTransformReplicator" /> in the same snapshot.
///     </para>
///     <para>
///         ⚠ <b>Outranking the transform is not the same as arriving before it.</b> Priority orders
///         one snapshot; a lost packet, an interest change or the budget can still deliver a rider's
///         position while its frame is outstanding, and on the first snapshot after a mount the
///         vehicle itself may not be spawned here yet. That case is real and is handled where it can
///         be — <c>NetworkTransformApplySystem</c> holds the rider still until the frame exists.
///     </para>
/// </remarks>
public sealed class NetworkParentReplicator : IComponentReplicator {
    /// <summary>Ahead of every transform and behind a spawn.</summary>
    public const int ParentPriority = 500;

    static readonly WireLane[] Layout = [new("Parent", 32, false)];

    /// <inheritdoc />
    public ComponentTypeId ComponentType => ComponentType<NetworkParent>.Id;

    /// <inheritdoc />
    public uint TypeId { get; } = ReplicationRegistry.HashTypeName("Vixen.Net.Motion.NetworkParent");

    /// <inheritdoc />
    public string TypeName => "Vixen.Net.Motion.NetworkParent";

    /// <inheritdoc />
    public Channel Channel => Channel.ReliableUnordered;

    /// <inheritdoc />
    public int Priority => ParentPriority;

    /// <inheritdoc />
    public QueryDescription ChangedQuery { get; } =
        new QueryDescription().RequireChanged([ComponentType<NetworkParent>.Id]);

    /// <inheritdoc />
    public ReadOnlySpan<WireLane> Lanes => Layout;

    /// <inheritdoc />
    public bool Has(World world, Entity entity) {
        ArgumentNullException.ThrowIfNull(world);

        return world.Has<NetworkParent>(entity);
    }

    /// <inheritdoc />
    public void Write(World world, Entity entity, ref BitWriter writer) {
        ArgumentNullException.ThrowIfNull(world);

        writer.WriteUInt32(world.Read<NetworkParent>(entity).Value);
    }

    /// <inheritdoc />
    public bool Apply(World world, Entity entity, ref BitReader reader) {
        ArgumentNullException.ThrowIfNull(world);

        if (!reader.TryReadUInt32(out var parent)) {
            return false;
        }

        var value = new NetworkParent { Value = parent };

        if (world.Has<NetworkParent>(entity)) {
            world.Set(entity, value);
        } else {
            world.Add(entity, value);
        }

        return true;
    }
}
