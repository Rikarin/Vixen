// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Net.Messaging;
using Vixen.Net.Replication;

namespace Vixen.Net.Physics;

/// <summary>The motion a networked body carries beside its transform.</summary>
/// <remarks>
///     <para>
///         <b>Why velocity goes on the wire at all.</b> A transform alone makes a remote body a
///         puppet: the receiver has positions a tick apart and interpolates a straight line between
///         them, so a thrown crate travels in flat segments instead of an arc, and a tumbling one
///         stops tumbling between updates. Sending the velocity lets the receiver's own solver carry
///         the body between updates, which is what makes it still look like physics.
///     </para>
///     <para>
///         <b>Quantised harder than the position, deliberately.</b> A velocity is only ever used to
///         carry a body a fraction of a second, so an error of a centimetre a second moves it by
///         nothing anybody can see — where the same error in a position is the position being wrong.
///         Twelve bits over ±64 m/s is 3 cm/s, which is below what the interpolation contributes.
///     </para>
///     <para>
///         <b>The rest flag is the bandwidth decision.</b> Most objects in most scenes are asleep,
///         and a body that has come to rest has a velocity of zero for ever after — so the flag says
///         "stopped" once and the three velocity lanes go to their unchanged bit from then on. It is
///         also what the receiver needs in order to stop integrating and let the body settle rather
///         than creeping.
///     </para>
/// </remarks>
[DataContract]
public struct NetworkRigidBody {
    /// <summary>How fast it is moving, in metres a second.</summary>
    public Vector3 LinearVelocity;

    /// <summary>How fast it is turning, in radians a second about each axis.</summary>
    public Vector3 AngularVelocity;

    /// <summary>Whether the body has come to rest and is not being integrated.</summary>
    public bool IsResting;
}

/// <summary>How a receiving peer is allowed to correct a body it does not own.</summary>
/// <remarks>
///     <para>
///         <b>The whole design in one component.</b> A remote body is simulated locally from the
///         velocity it was last sent, so it drifts from the authority — and the question is what to
///         do about the difference. Snapping it every update is what makes networked physics look
///         like a slideshow. What this does instead is <b>push the body toward the authoritative
///         pose through the solver</b>, as a critically damped spring, so it arrives smoothly and
///         collides correctly on the way.
///     </para>
///     <para>
///         Critically damped because the alternatives are both wrong: underdamped overshoots and the
///         crate wobbles around where it should be; overdamped never quite arrives and the crate is
///         permanently slightly behind. Critical damping is the one that converges fastest without
///         passing the target, and it is the reason <see cref="PositionStrength" /> is expressed as a
///         frequency rather than as a fudge factor.
///     </para>
///     <para>
///         <b>And a spring cannot fix everything, which is what the snap thresholds are for.</b> A
///         body that respawned across the level, or one whose owner was disconnected for a second,
///         is not off by a spring's worth — pulling it there would take seconds and drag it through
///         every wall on the way. Past <see cref="HardSnapDistance" /> it is teleported, and that is
///         the honest answer rather than a failure of the smoothing.
///     </para>
/// </remarks>
[DataContract]
public struct NetworkRigidBodyCorrection {
    /// <summary>
    ///     How hard position error is corrected, as the natural frequency of a critically damped
    ///     spring, in radians a second.
    /// </summary>
    /// <remarks>
    ///     Higher converges faster and fights the local simulation harder. Around ten is a body that
    ///     visibly settles within a couple of tenths of a second, which is roughly the interpolation
    ///     delay and therefore about as fast as is worth going.
    /// </remarks>
    public float PositionStrength;

    /// <summary>The same, for rotation.</summary>
    public float RotationStrength;

    /// <summary>Past this much position error, the body is teleported rather than pulled.</summary>
    public float HardSnapDistance;

    /// <summary>Past this much rotation error, in radians, the body is snapped rather than pulled.</summary>
    public float HardSnapAngle;

    /// <summary>What a body's correction looks like out of the box.</summary>
    /// <remarks>
    ///     Two metres of hard-snap distance is about a body length: further than a collision or a
    ///     tenth of a second of drift can explain, and near enough that a snap is not a surprise.
    /// </remarks>
    public static NetworkRigidBodyCorrection Default => new() {
        PositionStrength = 10f,
        RotationStrength = 10f,
        HardSnapDistance = 2f,
        HardSnapAngle = MathF.PI / 2f
    };
}

/// <summary>Puts a networked body's motion on the wire beside its transform.</summary>
/// <remarks>
///     <para>
///         Its own replicator rather than fields on <c>NetworkTransformReplicator</c>, for the
///         reason the delta encoder rewards: a component is the unit of change, and a body's
///         position changes every tick while its rest flag changes twice in a match. Folding them
///         together would mean the flag's lane paying attention every time the body moved.
///     </para>
///     <para>
///         Unreliable, like the transform beside it. A velocity that was missed is superseded by the
///         next one a thirtieth of a second later, and the correction spring is what covers the gap
///         — which is precisely what it is for.
///     </para>
/// </remarks>
public sealed class NetworkRigidBodyReplicator : IComponentReplicator {
    /// <summary>How many bits each velocity axis costs.</summary>
    public const int VelocityBits = 12;

    /// <summary>The range a replicated velocity lives in, in metres or radians a second.</summary>
    /// <remarks>
    ///     ±64 covers a thrown grenade, a falling body at terminal velocity in any sane gravity, and
    ///     a wheel spinning fast enough to look continuous. A projectile faster than this should not
    ///     be a rigid body at all — it should be a raycast, for the same reason it would tunnel.
    /// </remarks>
    public static QuantizeRange VelocityRange { get; } = new(-64f, 64f, VelocityBits);

    static readonly WireLane[] Layout = [
        new("Linear.X", VelocityBits, true),
        new("Linear.Y", VelocityBits, true),
        new("Linear.Z", VelocityBits, true),
        new("Angular.X", VelocityBits, true),
        new("Angular.Y", VelocityBits, true),
        new("Angular.Z", VelocityBits, true),
        new("IsResting", 1, false)
    ];

    /// <inheritdoc />
    public ComponentTypeId ComponentType => ComponentType<NetworkRigidBody>.Id;

    /// <inheritdoc />
    public uint TypeId { get; } = ReplicationRegistry.HashTypeName("Vixen.Net.Physics.NetworkRigidBody");

    /// <inheritdoc />
    public string TypeName => "Vixen.Net.Physics.NetworkRigidBody";

    /// <inheritdoc />
    public Channel Channel => Channel.Unreliable;

    /// <summary>
    ///     Below the transform's. A body whose position arrived without its velocity is drawn in the
    ///     right place and carried badly for one tick; the other way round it is carried well to the
    ///     wrong place.
    /// </summary>
    public int Priority => 15;

    /// <inheritdoc />
    public QueryDescription ChangedQuery { get; } =
        new QueryDescription().RequireChanged([ComponentType<NetworkRigidBody>.Id]);

    /// <inheritdoc />
    public ReadOnlySpan<WireLane> Lanes => Layout;

    /// <inheritdoc />
    public bool Has(World world, Entity entity) => world.Has<NetworkRigidBody>(entity);

    /// <inheritdoc />
    public void Write(World world, Entity entity, ref BitWriter writer) {
        ref readonly var value = ref world.Read<NetworkRigidBody>(entity);

        writer.WriteQuantized(value.LinearVelocity.X, VelocityRange);
        writer.WriteQuantized(value.LinearVelocity.Y, VelocityRange);
        writer.WriteQuantized(value.LinearVelocity.Z, VelocityRange);
        writer.WriteQuantized(value.AngularVelocity.X, VelocityRange);
        writer.WriteQuantized(value.AngularVelocity.Y, VelocityRange);
        writer.WriteQuantized(value.AngularVelocity.Z, VelocityRange);
        writer.WriteBool(value.IsResting);
    }

    /// <inheritdoc />
    public bool Apply(World world, Entity entity, ref BitReader reader) {
        if (!reader.TryReadQuantized(VelocityRange, out var linearX)
            || !reader.TryReadQuantized(VelocityRange, out var linearY)
            || !reader.TryReadQuantized(VelocityRange, out var linearZ)
            || !reader.TryReadQuantized(VelocityRange, out var angularX)
            || !reader.TryReadQuantized(VelocityRange, out var angularY)
            || !reader.TryReadQuantized(VelocityRange, out var angularZ)
            || !reader.TryReadBool(out var resting)) {
            return false;
        }

        if (!world.Has<NetworkRigidBody>(entity)) {
            world.Add(entity, default(NetworkRigidBody));
        }

        ref var value = ref world.Get<NetworkRigidBody>(entity);
        value.LinearVelocity = new(linearX, linearY, linearZ);
        value.AngularVelocity = new(angularX, angularY, angularZ);
        value.IsResting = resting;

        return true;
    }
}
