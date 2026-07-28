// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Vixen.Core;
using Vixen.Ecs;
using Vixen.Net.Messaging;
using Vixen.Net.Replication;

namespace Vixen.Net.Animation;

/// <summary>A pose on the wire, for the poses that cannot be derived.</summary>
/// <remarks>
///     <para>
///         <b>The fallback, and it is meant to look expensive.</b> <see cref="NetworkAnimator" />
///         sends a dozen values and lets the receiver produce the pose, which works because an
///         ordinary state machine reaches the same state from the same parameters. This sends the
///         pose, for the cases where that is false: a ragdoll driven by the local solver, IK against
///         local geometry, procedural motion with a random number generator in it. Every one of those
///         produces a different pose on every machine from identical inputs, and no amount of care
///         with parameters fixes it.
///     </para>
///     <para>
///         <b>Rotations only, because a skeleton is rigid.</b> Bone lengths do not change, so a
///         joint's translation is its bind pose and sending it would be sending a constant sixty times
///         a second. The exceptions — stretchy cartoon limbs, a squash-and-stretch rig — are rare
///         enough to be worth costing nothing here and being handled by the game that has one. Where
///         the <i>character</i> is remains <c>NetworkTransform</c>'s answer, as it is for everything
///         else.
///     </para>
///     <para>
///         <b>A selected subset, not the skeleton.</b> A humanoid rig is sixty joints and a ragdoll is
///         driven by about sixteen; the fingers follow whatever the hand does and nobody watching a
///         corpse fall can tell. <see cref="NetworkBoneSelection" /> says which joints these are, and
///         it is not replicated because it comes from the same content on both peers — the same
///         argument the prefab id makes.
///     </para>
///     <para>
///         <b>What it costs, stated rather than discovered.</b> Twenty-four bones at 32 bits is 776
///         bits whole, or about 15 kbit/s per character at twenty updates a second. The delta codec
///         takes most of that back for a pose that is partly still — a bone whose packed rotation is
///         unchanged costs one bit — but a ragdoll in free fall is every bone moving and pays close to
///         the full price. That is the trade, and it is the reason the animator replicates its inputs.
///     </para>
/// </remarks>
[DataContract]
public struct NetworkBones {
    /// <summary>How many of the block the sender meant, which is its selection's length.</summary>
    /// <remarks>
    ///     Redundant — the receiver's own selection says the same thing — and kept for eight bits
    ///     because that is what turns the two ends disagreeing about a character's rig from a pose
    ///     quietly applied to the wrong joints into a number that can be watched. See
    ///     <c>NetworkBonesApplySystem.MismatchedCount</c>.
    /// </remarks>
    public byte Count;

    /// <summary>The rotations, packed as the wire packs them.</summary>
    /// <remarks>
    ///     <b>Stored packed rather than as quaternions</b>, which is two things at once. A bone that
    ///     did not move is then bit-identical to last tick, so the delta codec spends one bit on it
    ///     rather than comparing two floats that differ in their last place; and the component is a
    ///     quarter of the size in a chunk, which matters when it is the largest thing a character
    ///     carries. <see cref="MathCodec.UnpackRotation" /> is the way back.
    /// </remarks>
    public RotationBlock Rotations;

    /// <summary>Twenty-four packed rotations, inline.</summary>
    [InlineArray(NetworkBonesReplicator.MaxBones)]
    public struct RotationBlock {
        uint element;
    }
}

/// <summary>Which joints of a skeleton a <see cref="NetworkBones" /> is about.</summary>
/// <remarks>
///     <para>
///         <b>Not replicated.</b> It comes from the character's own content — the same rig, the same
///         prefab, the same ragdoll setup — so both peers compute it rather than one telling the
///         other. Sending it would be sending a table that cannot differ, once per character, and
///         inviting the case where it does.
///     </para>
///     <para>
///         Managed, because it is an array and a component in a chunk cannot be. That is the same
///         reason <c>AnimatorComponent</c> is managed, and it means these are reached one entity at a
///         time rather than swept — which is what a per-character animation cost looks like anyway.
///     </para>
/// </remarks>
public struct NetworkBoneSelection {
    /// <summary>The joint indices, in the order the block holds them.</summary>
    public int[]? Joints;
}

/// <summary>Puts a pose on the wire.</summary>
public sealed class NetworkBonesReplicator : IComponentReplicator {
    /// <summary>How many bones one character may replicate.</summary>
    /// <remarks>
    ///     Enough for a ragdoll — pelvis, two spine joints, a head, four per arm and three per leg is
    ///     eighteen — with room over. Not enough for a whole humanoid rig, deliberately: a design that
    ///     let a caller send sixty would be one where the expensive choice is the easy one.
    /// </remarks>
    public const int MaxBones = 24;

    static readonly WireLane[] Layout = BuildLayout();

    /// <inheritdoc />
    public ComponentTypeId ComponentType => ComponentType<NetworkBones>.Id;

    /// <inheritdoc />
    public uint TypeId { get; } = ReplicationRegistry.HashTypeName("Vixen.Net.Animation.NetworkBones");

    /// <inheritdoc />
    public string TypeName => "Vixen.Net.Animation.NetworkBones";

    /// <summary>Unreliable. A pose is superseded by the next one, like a position.</summary>
    /// <remarks>
    ///     The opposite of <c>NetworkAnimatorParameters</c>, and for the opposite reason: a lost
    ///     parameter edge never heals because a state machine remembers, whereas a lost pose is a
    ///     twentieth of a second of a limb being where it was. Nothing accumulates.
    /// </remarks>
    public Channel Channel => Channel.Unreliable;

    /// <summary>Below the transform. Where a character <i>is</i> matters more than how it is folded.</summary>
    public int Priority => 20;

    /// <inheritdoc />
    public QueryDescription ChangedQuery { get; } =
        new QueryDescription().RequireChanged([ComponentType<NetworkBones>.Id]);

    /// <inheritdoc />
    public ReadOnlySpan<WireLane> Lanes => Layout;

    /// <inheritdoc />
    public bool Has(World world, Entity entity) {
        ArgumentNullException.ThrowIfNull(world);

        return world.Has<NetworkBones>(entity);
    }

    /// <inheritdoc />
    public void Write(World world, Entity entity, ref BitWriter writer) {
        ArgumentNullException.ThrowIfNull(world);

        ref readonly var value = ref world.Read<NetworkBones>(entity);

        writer.Write(value.Count, 8);

        // Every slot, not `Count` of them. A fixed width is what the delta codec's lane check
        // requires, and it is what lets a bone that did not move cost one bit — a variable-length
        // block would have to re-state its own shape whenever any of it moved.
        for (var index = 0; index < MaxBones; index++) {
            writer.Write(value.Rotations[index], 32);
        }
    }

    /// <inheritdoc />
    public bool Apply(World world, Entity entity, ref BitReader reader) {
        ArgumentNullException.ThrowIfNull(world);

        if (!reader.TryRead(8, out var count)) {
            return false;
        }

        if (!world.Has<NetworkBones>(entity)) {
            world.Add(entity, default(NetworkBones));
        }

        ref var value = ref world.Get<NetworkBones>(entity);
        value.Count = (byte)Math.Min(count, MaxBones);

        for (var index = 0; index < MaxBones; index++) {
            if (!reader.TryRead(32, out var rotation)) {
                return false;
            }

            value.Rotations[index] = rotation;
        }

        return true;
    }

    static WireLane[] BuildLayout() {
        var lanes = new WireLane[1 + MaxBones];
        lanes[0] = new("Count", 8, false);

        for (var index = 0; index < MaxBones; index++) {
            // Never offset. A packed rotation is two bits of selector and three quantized fields, so
            // the difference between two of them is not a small number when the bone turned a little
            // — it is a different selector and three unrelated fields. The codec's own "whole value"
            // code is the right answer and one bit is the right cost for a bone that did not move.
            lanes[index + 1] = new(
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Bone{index}"),
                32,
                false
            );
        }

        return lanes;
    }
}
