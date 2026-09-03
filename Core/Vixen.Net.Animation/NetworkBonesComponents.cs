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

/// <summary>How many bits each bone of a pose is worth.</summary>
/// <remarks>
///     <para>
///         <b>A finger does not need what a spine needs.</b> A rotation costs two bits naming the
///         dropped component and three quantized levels, and ten bits a level is what a shoulder
///         wants — the error compounds down the chain, so the joint nearest the root is the one whose
///         precision everything below it inherits. A finger's own error reaches nothing, is a
///         centimetre at the tip, and is being watched by nobody. This is where a game says so.
///     </para>
///     <para>
///         <b>Indexed by slot in the selection, not by joint index</b>, because the wire layout is a
///         property of the replicator and the joint indices are a property of a rig. That has a
///         consequence worth stating rather than discovering: a game using a narrowed table must
///         order every character's <see cref="NetworkBoneSelection" /> the same way, and the natural
///         way is most-important-first. Slot 0 is then the pelvis on every rig in the game and the
///         table means the same thing for all of them.
///     </para>
///     <para>
///         ⚠ <b>It cannot live on <see cref="NetworkBoneSelection" />, which is where
///         <c>Vixen.Net.Animation</c>'s own README suggested it should.</b> The selection is
///         per-entity, and a per-entity precision is a wire format that varies per entity: the delta
///         codec checks a fixed lane width and the connection baselines are compared against one
///         layout, so nothing on either side could parse it. This is the same argument
///         <c>NetworkTransformAxes</c> makes for the mask being the replicator's rather than the
///         entity's, and it lands the same way.
///     </para>
/// </remarks>
public sealed class NetworkBonePrecision {
    /// <summary>The most a bone can be worth, which is what <c>MathCodec</c> packs.</summary>
    public const int MaxBits = MathCodec.RotationBits;

    /// <summary>The least. Sixteen levels over ±1/√2 is about five degrees a component.</summary>
    /// <remarks>
    ///     A floor rather than one bit, because below this the selector is most of the record and the
    ///     pose visibly steps. A game that wants a joint cheaper than this wants it out of the
    ///     selection, which costs nothing at all.
    /// </remarks>
    public const int MinBits = 4;

    readonly int[] bits;

    NetworkBonePrecision(int[] bits) {
        this.bits = bits;
        IsFull = Array.TrueForAll(bits, value => value == MaxBits);
        Suffix = IsFull ? string.Empty : string.Concat(Array.ConvertAll(bits, Symbol));
    }

    /// <summary>Every bone at full precision — what ships, and what costs 32 bits a bone.</summary>
    public static NetworkBonePrecision Full { get; } = Uniform(MaxBits);

    /// <summary>Whether this is <see cref="Full" />'s table, in which case the wire is unchanged.</summary>
    public bool IsFull { get; }

    /// <summary>
    ///     One character per slot, naming its width. Empty for <see cref="Full" />, so the shipped
    ///     replicator keeps the bare type name and the wire id it has today.
    /// </summary>
    public string Suffix { get; }

    /// <summary>How many bits the bone in a slot is worth.</summary>
    /// <param name="slot">Which slot of the selection, from zero.</param>
    /// <returns>Its width, between <see cref="MinBits" /> and <see cref="MaxBits" />.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot" /> is not a slot.</exception>
    public int this[int slot] {
        get {
            ArgumentOutOfRangeException.ThrowIfNegative(slot);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, bits.Length);

            return bits[slot];
        }
    }

    /// <summary>The same width for every bone.</summary>
    /// <param name="bits">How many bits a component is worth.</param>
    /// <returns>The table.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="bits" /> is outside <see cref="MinBits" />..<see cref="MaxBits" />.
    /// </exception>
    public static NetworkBonePrecision Uniform(int bits) {
        Check(bits, nameof(bits));

        var table = new int[NetworkBonesReplicator.MaxBones];
        Array.Fill(table, bits);

        return new(table);
    }

    /// <summary>A width per slot, shortest-first, with the rest left at full precision.</summary>
    /// <param name="bits">
    ///     One width per slot of the selection. Fewer than <c>MaxBones</c> is allowed and the
    ///     remaining slots stay at <see cref="MaxBits" /> — a table that named the first eight is a
    ///     game saying something about those eight and nothing about the rest.
    /// </param>
    /// <returns>The table.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A width is outside the allowed range, or there are too many.</exception>
    public static NetworkBonePrecision For(ReadOnlySpan<int> bits) {
        if (bits.Length > NetworkBonesReplicator.MaxBones) {
            throw new ArgumentOutOfRangeException(
                nameof(bits),
                bits.Length,
                $"A pose carries at most {NetworkBonesReplicator.MaxBones} bones, so a precision table "
                + "cannot name more."
            );
        }

        var table = new int[NetworkBonesReplicator.MaxBones];
        Array.Fill(table, MaxBits);

        for (var index = 0; index < bits.Length; index++) {
            Check(bits[index], nameof(bits));
            table[index] = bits[index];
        }

        return new(table);
    }

    static void Check(int bits, string name) {
        if (bits is < MinBits or > MaxBits) {
            throw new ArgumentOutOfRangeException(
                name,
                bits,
                $"A bone's rotation is between {MinBits} and {MaxBits} bits a component. Below the "
                + "floor the pose steps visibly and the selector is most of the record; above the "
                + "ceiling there is nothing left to send, because that is what the codec packs."
            );
        }
    }

    // 4..10 as '4'..':' would be unreadable, so ten is 'A'. One character a slot keeps the suffix
    // — and therefore the type name a handshake hashes — the same length as the table it describes.
    static string Symbol(int bits) => bits == MaxBits ? "A" : bits.ToString(System.Globalization.CultureInfo.InvariantCulture);
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

    readonly WireLane[] layout;

    /// <summary>The replicator that sends every bone whole — the shipped default.</summary>
    public NetworkBonesReplicator() : this(NetworkBonePrecision.Full) { }

    /// <summary>A replicator that spends fewer bits on the bones a game says matter less.</summary>
    /// <param name="precision">What each slot is worth. Every peer in the session needs the same table.</param>
    /// <exception cref="ArgumentNullException"><paramref name="precision" /> is null.</exception>
    /// <remarks>
    ///     <b>A narrowed table renames the type on the wire, and that is the safety property rather
    ///     than a cosmetic one.</b> Two peers built with different tables would disagree about every
    ///     lane width in the layout and would decode each other's poses into plausible wrong
    ///     rotations — the failure that presents as characters folding rather than as an error.
    ///     Folding the table into <see cref="TypeName" /> means it folds into <see cref="TypeId" />
    ///     and therefore into <c>ReplicationRegistry.ManifestHash</c>, so the handshake refuses the
    ///     connection instead. <see cref="NetworkBonePrecision.Full" /> keeps the bare name, so
    ///     nothing that ships today changes its wire id. This is <c>NetworkTransformReplicator</c>'s
    ///     argument, and it is the same one.
    /// </remarks>
    public NetworkBonesReplicator(NetworkBonePrecision precision) {
        ArgumentNullException.ThrowIfNull(precision);

        Precision = precision;
        TypeName = precision.IsFull
            ? "Vixen.Net.Animation.NetworkBones"
            : $"Vixen.Net.Animation.NetworkBones[{precision.Suffix}]";

        TypeId = ReplicationRegistry.HashTypeName(TypeName);
        layout = BuildLayout(precision);
    }

    /// <inheritdoc />
    public ComponentTypeId ComponentType => ComponentType<NetworkBones>.Id;

    /// <summary>What each slot is worth. <see cref="NetworkBonePrecision.Full" /> by default.</summary>
    public NetworkBonePrecision Precision { get; }

    /// <inheritdoc />
    public uint TypeId { get; }

    /// <inheritdoc />
    public string TypeName { get; }

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
    public ReadOnlySpan<WireLane> Lanes => layout;

    /// <inheritdoc />
    public bool Has(World world, Entity entity) {
        ArgumentNullException.ThrowIfNull(world);

        return world.Has<NetworkBones>(entity);
    }

    /// <summary>Drops the low bits of each of a packed rotation's three levels.</summary>
    /// <param name="packed">A rotation as <c>MathCodec.PackRotation</c> packs it.</param>
    /// <param name="bits">How many bits a level keeps.</param>
    /// <returns>The same rotation in <c>2 + 3 × bits</c> bits.</returns>
    /// <remarks>
    ///     <b>Truncation in the integer domain, not a second quantization of the float.</b> Going back
    ///     through <c>UnpackRotation</c> and re-encoding would re-normalise, and a re-normalised
    ///     quaternion is not the one that was packed — so a bone that did not move would come out
    ///     with different bits and cost the delta codec a whole lane instead of one bit, which is the
    ///     property the packed storage exists for. Dropping bits off a level cannot do that: equal
    ///     inputs stay equal.
    /// </remarks>
    public static uint Narrow(uint packed, int bits) {
        if (bits >= NetworkBonePrecision.MaxBits) {
            return packed;
        }

        var drop = NetworkBonePrecision.MaxBits - bits;
        var mask = (1u << NetworkBonePrecision.MaxBits) - 1;
        var result = packed & 3u;

        for (var level = 0; level < 3; level++) {
            var value = (packed >> (2 + (level * NetworkBonePrecision.MaxBits))) & mask;
            result |= (value >> drop) << (2 + (level * bits));
        }

        return result;
    }

    /// <summary>Puts a narrowed rotation back into the packed layout the component holds.</summary>
    /// <param name="narrow">What <see cref="Narrow" /> produced.</param>
    /// <param name="bits">The same width it was narrowed to.</param>
    /// <returns>A packed rotation.</returns>
    /// <remarks>
    ///     <b>The middle of the interval, not its floor.</b> A narrowed level stands for a run of
    ///     <c>2^drop</c> full-precision ones, and shifting it back up alone would pick the smallest
    ///     of them every time — a bias towards −1/√2 on all three components at once, which is a
    ///     systematic lean rather than noise. The midpoint halves the worst-case error and centres
    ///     it. It also round-trips: narrowing what this widens gives back what went in, so a peer
    ///     that receives a pose and re-sends it does not lose a second helping of precision.
    /// </remarks>
    public static uint Widen(uint narrow, int bits) {
        if (bits >= NetworkBonePrecision.MaxBits) {
            return narrow;
        }

        var drop = NetworkBonePrecision.MaxBits - bits;
        var mask = (1u << bits) - 1;
        var result = narrow & 3u;

        for (var level = 0; level < 3; level++) {
            var value = (narrow >> (2 + (level * bits))) & mask;
            result |= ((value << drop) | (1u << (drop - 1))) << (2 + (level * NetworkBonePrecision.MaxBits));
        }

        return result;
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
            var bits = Precision[index];
            writer.Write(Narrow(value.Rotations[index], bits), 2 + (3 * bits));
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
            var bits = Precision[index];

            if (!reader.TryRead(2 + (3 * bits), out var rotation)) {
                return false;
            }

            value.Rotations[index] = Widen(rotation, bits);
        }

        return true;
    }

    static WireLane[] BuildLayout(NetworkBonePrecision precision) {
        var lanes = new WireLane[1 + MaxBones];
        lanes[0] = new("Count", 8, false);

        for (var index = 0; index < MaxBones; index++) {
            // Never offset. A packed rotation is two bits of selector and three quantized fields, so
            // the difference between two of them is not a small number when the bone turned a little
            // — it is a different selector and three unrelated fields. The codec's own "whole value"
            // code is the right answer and one bit is the right cost for a bone that did not move.
            lanes[index + 1] = new(
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Bone{index}"),
                2 + (3 * precision[index]),
                false
            );
        }

        return lanes;
    }
}
