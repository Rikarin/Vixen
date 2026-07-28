// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Net.Messaging;
using Vixen.Net.Replication;

namespace Vixen.Net.Animation;

/// <summary>What a remote animator needs in order to animate itself.</summary>
/// <remarks>
///     <para>
///         <b>The inputs, not the output.</b> An animator turns a handful of parameters and a state
///         machine position into a pose of every bone, every frame. Sending the pose is sending the
///         <i>result</i> of a calculation the receiver is perfectly capable of doing — sixty bone
///         rotations a frame per character, against a dozen values that change when the player
///         presses something. The saving is not marginal; it is the difference between a networked
///         crowd and a networked pair.
///     </para>
///     <para>
///         <b>What it costs is a determinism assumption</b>, and it is worth stating rather than
///         discovering. The receiver reproduces the pose only if its animator reaches the same state
///         from the same parameters — so the same clips, the same transitions, the same conditions.
///         That is true of an ordinary state machine driven by gameplay and false of anything driven
///         by local physics, IK against local geometry, or a random number generator. Those want
///         <c>NetworkBones</c>, which is expensive and honest about it.
///     </para>
///     <para>
///         <b>The state and its time are sent as well as the parameters</b>, and not as a
///         belt-and-braces measure. A late joiner has no history to have derived the current state
///         from, a client whose parameter update was lost has a state machine one transition behind,
///         and neither heals on its own — a state machine's position is a function of every
///         parameter it has ever seen, so a single missed edge is permanent. The state is what makes
///         it self-correcting.
///     </para>
/// </remarks>
[DataContract]
public struct NetworkAnimator {
    /// <summary>Which state the first layer's machine is in.</summary>
    public ushort State;

    /// <summary>How far through that state it is, from 0 to 1.</summary>
    /// <remarks>
    ///     Eight bits on the wire. A quarter of a percent of a clip is under a millisecond of a
    ///     typical animation and far below what the interpolation between two updates contributes —
    ///     and unlike a position, being slightly wrong about it is invisible rather than a
    ///     misplacement.
    /// </remarks>
    public float NormalizedTime;

    /// <summary>How fast the animator is playing, so a slowed or stopped one looks the same.</summary>
    public float Speed;
}

/// <summary>An animator's parameters, as the wire carries them.</summary>
/// <remarks>
///     <para>
///         Separate from <see cref="NetworkAnimator" /> because the two change at completely
///         different rates, and a component is the unit of change the delta encoder rewards. A
///         normalised time changes every tick; a parameter changes when somebody presses a button.
///         Folding them together would make every parameter pay attention every frame.
///     </para>
///     <para>
///         <b>A fixed-width block rather than a list.</b> Sixteen parameters covers what an animator
///         in practice has, and a fixed layout is what lets the delta codec give an unchanged
///         parameter one bit — a variable-length list would have to re-state its own shape every
///         time any of it moved. A machine with more than sixteen is one whose extra parameters the
///         game should replicate itself, and the count says how many of these mean anything.
///     </para>
/// </remarks>
[DataContract]
public struct NetworkAnimatorParameters {
    /// <summary>How many parameters are in use.</summary>
    public byte Count;

    /// <summary>The values, as floats — an int reads as its value and a bool as zero or one.</summary>
    /// <remarks>
    ///     One representation rather than three, because <c>AnimationParameters</c> already converts
    ///     between them on read and the wire gains nothing from knowing which is which. What it
    ///     loses is precision on an <c>int</c> above 2²⁴, which is not a number any animator
    ///     parameter has ever held.
    /// </remarks>
    public ParameterBlock Values;

    /// <summary>Sixteen floats, inline.</summary>
    /// <remarks>
    ///     An inline array rather than a managed array, so the component stays a blittable value in
    ///     a chunk column — the same rule every other replicated component keeps, and the reason a
    ///     <c>Collider</c> holds a <c>ShapeId</c> rather than a shape.
    /// </remarks>
    [System.Runtime.CompilerServices.InlineArray(NetworkAnimatorReplicator.MaxParameters)]
    public struct ParameterBlock {
        float element;
    }
}

/// <summary>Puts an animator's parameters on the wire.</summary>
public sealed class NetworkAnimatorParametersReplicator : IComponentReplicator {
    static readonly WireLane[] Layout = BuildLayout();

    /// <inheritdoc />
    public ComponentTypeId ComponentType => ComponentType<NetworkAnimatorParameters>.Id;

    /// <inheritdoc />
    public uint TypeId { get; } =
        ReplicationRegistry.HashTypeName("Vixen.Net.Animation.NetworkAnimatorParameters");

    /// <inheritdoc />
    public string TypeName => "Vixen.Net.Animation.NetworkAnimatorParameters";

    /// <summary>Reliable, because a missed parameter edge does not heal.</summary>
    /// <remarks>
    ///     The opposite trade from a transform. A lost position is superseded a thirtieth of a second
    ///     later; a lost "jump was pressed" is a jump that never happens on one client and the state
    ///     machine is wrong from then on. The state in <see cref="NetworkAnimator" /> is the backstop,
    ///     and paying for reliability is cheaper than relying on the backstop.
    /// </remarks>
    public Channel Channel => Channel.Reliable;

    /// <inheritdoc />
    public int Priority => 25;

    /// <inheritdoc />
    public QueryDescription ChangedQuery { get; } =
        new QueryDescription().RequireChanged([ComponentType<NetworkAnimatorParameters>.Id]);

    /// <inheritdoc />
    public ReadOnlySpan<WireLane> Lanes => Layout;

    /// <inheritdoc />
    public bool Has(World world, Entity entity) => world.Has<NetworkAnimatorParameters>(entity);

    /// <inheritdoc />
    public void Write(World world, Entity entity, ref BitWriter writer) {
        ref readonly var value = ref world.Read<NetworkAnimatorParameters>(entity);

        writer.Write(value.Count, 8);

        for (var index = 0; index < NetworkAnimatorReplicator.MaxParameters; index++) {
            writer.WriteSingle(value.Values[index]);
        }
    }

    /// <inheritdoc />
    public bool Apply(World world, Entity entity, ref BitReader reader) {
        if (!reader.TryRead(8, out var count)) {
            return false;
        }

        if (!world.Has<NetworkAnimatorParameters>(entity)) {
            world.Add(entity, default(NetworkAnimatorParameters));
        }

        ref var value = ref world.Get<NetworkAnimatorParameters>(entity);
        value.Count = (byte)Math.Min(count, NetworkAnimatorReplicator.MaxParameters);

        for (var index = 0; index < NetworkAnimatorReplicator.MaxParameters; index++) {
            if (!reader.TryReadSingle(out var parameter)) {
                return false;
            }

            value.Values[index] = parameter;
        }

        return true;
    }

    static WireLane[] BuildLayout() {
        var lanes = new WireLane[1 + NetworkAnimatorReplicator.MaxParameters];
        lanes[0] = new("Count", 8, false);

        for (var index = 0; index < NetworkAnimatorReplicator.MaxParameters; index++) {
            // Whole floats and no offset. A parameter is a speed, an angle or a flag with no declared
            // range, so there is nothing to quantise into — and the delta codec still gives an
            // unchanged one a single bit, which is what most of them are on most ticks.
            lanes[index + 1] = new(
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Parameter{index}"),
                32,
                false
            );
        }

        return lanes;
    }
}

/// <summary>Puts an animator's state machine position on the wire.</summary>
public sealed class NetworkAnimatorReplicator : IComponentReplicator {
    /// <summary>How many parameters are replicated.</summary>
    public const int MaxParameters = 16;

    /// <summary>How many bits the normalised time costs.</summary>
    public const int TimeBits = 8;

    /// <summary>The range a normalised time lives in.</summary>
    public static QuantizeRange TimeRange { get; } = new(0f, 1f, TimeBits);

    /// <summary>The range a playback speed lives in.</summary>
    /// <remarks>
    ///     Negative for a clip played backwards, which is a real thing a state machine does, and
    ///     capped at eight because past that nobody can tell what is being animated anyway.
    /// </remarks>
    public static QuantizeRange SpeedRange { get; } = new(-8f, 8f, 10);

    static readonly WireLane[] Layout = [
        new("State", 16, false),
        new("NormalizedTime", TimeBits, true),
        new("Speed", 10, true)
    ];

    /// <inheritdoc />
    public ComponentTypeId ComponentType => ComponentType<NetworkAnimator>.Id;

    /// <inheritdoc />
    public uint TypeId { get; } = ReplicationRegistry.HashTypeName("Vixen.Net.Animation.NetworkAnimator");

    /// <inheritdoc />
    public string TypeName => "Vixen.Net.Animation.NetworkAnimator";

    /// <summary>Unreliable. The state repeats every tick, so a lost one is a tick late.</summary>
    public Channel Channel => Channel.Unreliable;

    /// <inheritdoc />
    public int Priority => 24;

    /// <inheritdoc />
    public QueryDescription ChangedQuery { get; } =
        new QueryDescription().RequireChanged([ComponentType<NetworkAnimator>.Id]);

    /// <inheritdoc />
    public ReadOnlySpan<WireLane> Lanes => Layout;

    /// <inheritdoc />
    public bool Has(World world, Entity entity) => world.Has<NetworkAnimator>(entity);

    /// <inheritdoc />
    public void Write(World world, Entity entity, ref BitWriter writer) {
        ref readonly var value = ref world.Read<NetworkAnimator>(entity);

        writer.Write(value.State, 16);
        writer.WriteQuantized(value.NormalizedTime, TimeRange);
        writer.WriteQuantized(value.Speed, SpeedRange);
    }

    /// <inheritdoc />
    public bool Apply(World world, Entity entity, ref BitReader reader) {
        if (!reader.TryRead(16, out var state)
            || !reader.TryReadQuantized(TimeRange, out var time)
            || !reader.TryReadQuantized(SpeedRange, out var speed)) {
            return false;
        }

        if (!world.Has<NetworkAnimator>(entity)) {
            world.Add(entity, default(NetworkAnimator));
        }

        ref var value = ref world.Get<NetworkAnimator>(entity);
        value.State = (ushort)state;
        value.NormalizedTime = time;
        value.Speed = speed;

        return true;
    }
}
