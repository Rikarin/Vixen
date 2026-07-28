// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Transforms;
using Vixen.Net.Engine;
using Vixen.Net.Motion;
using Vixen.Net.Replication;
using PhysicsAngularVelocity = global::Vixen.Physics.Ecs.AngularVelocity;
using PhysicsLinearVelocity = global::Vixen.Physics.Ecs.LinearVelocity;
using PhysicsTeleport = global::Vixen.Physics.Ecs.PhysicsTeleport;
using PhysicsWritebackSystem = global::Vixen.Physics.Ecs.PhysicsWritebackSystem;

namespace Vixen.Net.Physics;

/// <summary>Publishes a body's motion, on the peer that owns the simulation.</summary>
/// <remarks>
///     <para>
///         Runs after physics has written its results back, and before the replication capture reads
///         them. That ordering is the whole job — a tick out of place here publishes last tick's
///         motion, which is a systematic one-tick lag that nothing reports and that lag compensation
///         would then faithfully rewind to.
///     </para>
///     <para>
///         It also translates <c>PhysicsTeleport</c> into <see cref="NetworkTeleport" />, so a body
///         the simulation moved discontinuously is one the receiver refuses to interpolate across.
///         Two tags rather than one because the physics package cannot know about the network one,
///         and this is the package that knows about both.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.FixedUpdate)]
[UpdateAfter(typeof(PhysicsWritebackSystem))]
[UpdateBefore(typeof(NetworkTransformCaptureSystem))]
public sealed class NetworkRigidBodyCaptureSystem : SystemBase, IDeclaredAccess {
    /// <summary>Below this speed a body is called stopped and its velocity stops being sent.</summary>
    /// <remarks>
    ///     A centimetre a second, which is under the quantisation the velocity is sent at anyway — so
    ///     a body this slow was already sending zeroes and this only decides whether it says so. What
    ///     it buys is the receiver being told to stop integrating, which is the difference between a
    ///     crate that settles and a crate that creeps for the rest of the match.
    /// </remarks>
    public const float RestSpeed = 0.01f;

    readonly QueryDescription moving = new QueryDescription()
        .WithAll<NetworkRigidBody, NetworkId, PhysicsLinearVelocity, PhysicsAngularVelocity>();

    readonly QueryDescription teleported = new QueryDescription().WithAll<NetworkRigidBody, PhysicsTeleport>();

    readonly List<Entity> arriving = [];

    /// <inheritdoc />
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<PhysicsLinearVelocity>()
        .Read<PhysicsAngularVelocity>()
        .Read<PhysicsTeleport>()
        .Read<NetworkId>()
        .Write<NetworkRigidBody>()
        .Build();

    /// <summary>How many bodies have been published.</summary>
    public long PublishedCount { get; private set; }

    /// <summary>How many were at rest and therefore cost nothing.</summary>
    public long RestingCount { get; private set; }

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Publish(context.World);

        return dependency;
    }

    /// <summary>Publishes every networked body's motion.</summary>
    /// <param name="world">The world.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    public void Publish(World world) {
        ArgumentNullException.ThrowIfNull(world);

        arriving.Clear();

        foreach (var chunk in world.Chunks(teleported)) {
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                arriving.Add(entities[index]);
            }
        }

        foreach (var entity in arriving) {
            // The physics tag stays for whatever else reads it; this only adds the network's.
            if (!world.Has<NetworkTeleport>(entity)) {
                world.Add<NetworkTeleport>(entity);
            }
        }

        foreach (var chunk in world.Chunks(moving)) {
            var linear = chunk.ReadValues<PhysicsLinearVelocity>();
            var angular = chunk.ReadValues<PhysicsAngularVelocity>();
            var bodies = chunk.Values<NetworkRigidBody>();

            for (var index = 0; index < chunk.Count; index++) {
                var linearVelocity = linear[index].Value;
                var angularVelocity = angular[index].Value;

                var resting = linearVelocity.LengthSquared() < RestSpeed * RestSpeed
                    && angularVelocity.LengthSquared() < RestSpeed * RestSpeed;

                // Written as exactly zero when at rest rather than as something very small. The
                // delta encoder compares against what it sent last time, so a body that is asleep
                // has to keep producing the *same* value to cost its unchanged bit — and a
                // velocity dithering in the last quantisation step would pay full price for ever.
                bodies[index].LinearVelocity = resting ? Vector3.Zero : linearVelocity;
                bodies[index].AngularVelocity = resting ? Vector3.Zero : angularVelocity;
                bodies[index].IsResting = resting;

                if (resting) {
                    RestingCount++;
                }
            }

            PublishedCount += chunk.Count;
        }
    }
}

/// <summary>Pulls a body that is not ours toward where the authority says it is.</summary>
/// <remarks>
///     <para>
///         <b>Through the solver, not around it.</b> The received pose is not written onto the body.
///         What is written is a <i>velocity</i> that would carry the body there, so it arrives under
///         the simulation's own rules — colliding with what is in the way, resting on what it lands
///         on, and being pushed back by anything it cannot move through. Setting the transform
///         instead is what makes networked physics look like objects teleporting through each other.
///     </para>
///     <para>
///         <b>Critically damped.</b> The correction velocity is <c>error × frequency</c>, which is
///         the critically damped solution for a spring with no overshoot: fastest convergence that
///         never passes the target. Underdamped and the crate oscillates around its true position;
///         overdamped and it never quite arrives. The frequency is
///         <see cref="NetworkRigidBodyCorrection.PositionStrength" /> and is a property of the object
///         rather than a global, because a crate and a car want different answers.
///     </para>
///     <para>
///         <b>And the snap, which is not a failure of the spring.</b> A body that respawned, or whose
///         owner dropped for a second, is metres out — and a spring strong enough to fix that in one
///         tick is a spring that would fling everything else across the level. Past the threshold it
///         is teleported, and the teleport is marked so the receiver's own interpolation does not
///         smear it.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.FixedUpdate)]
[UpdateBefore(typeof(PhysicsWritebackSystem))]
public sealed class NetworkRigidBodyCorrectionSystem : SystemBase, IDeclaredAccess {
    readonly QueryDescription corrected = new QueryDescription()
        .WithAll<NetworkRigidBody, NetworkRigidBodyCorrection, NetworkTransform, LocalTransform>();

    readonly List<Entity> snapping = [];

    /// <inheritdoc />
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<NetworkRigidBody>()
        .Read<NetworkRigidBodyCorrection>()
        .Read<NetworkTransform>()
        .Write<LocalTransform>()
        .Write<PhysicsLinearVelocity>()
        .Write<PhysicsAngularVelocity>()
        .Build();

    /// <summary>How many bodies have been nudged toward the authority.</summary>
    public long CorrectedCount { get; private set; }

    /// <summary>How many were too far out to nudge and were snapped.</summary>
    /// <remarks>
    ///     Worth watching. A snap is correct and is also the thing a player sees as a glitch, so a
    ///     rate that climbs is either a connection falling apart or a correction strength too low for
    ///     how fast the game's objects actually move.
    /// </remarks>
    public long SnappedCount { get; private set; }

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Correct(context.World, context.Time.DeltaSeconds);

        return dependency;
    }

    /// <summary>Corrects every body that is not simulated here.</summary>
    /// <param name="world">The world.</param>
    /// <param name="step">The fixed step, in seconds.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    public void Correct(World world, float step) {
        ArgumentNullException.ThrowIfNull(world);

        if (!(step > 0f)) {
            return;
        }

        snapping.Clear();

        foreach (var chunk in world.Chunks(corrected)) {
            var bodies = chunk.ReadValues<NetworkRigidBody>();
            var settings = chunk.ReadValues<NetworkRigidBodyCorrection>();
            var target = chunk.ReadValues<NetworkTransform>();
            var locals = chunk.Values<LocalTransform>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                var offset = target[index].Position - locals[index].Position;
                var distance = offset.Length();

                if (distance > settings[index].HardSnapDistance) {
                    snapping.Add(entities[index]);

                    continue;
                }

                // error × frequency: the critically damped correction velocity, added to the motion
                // the authority says the body has. The body keeps its own momentum and is steered.
                var correction = offset * settings[index].PositionStrength;
                var wanted = bodies[index].LinearVelocity + correction;

                if (world.Has<PhysicsLinearVelocity>(entities[index])) {
                    world.Get<PhysicsLinearVelocity>(entities[index]).Value = wanted;
                }

                if (world.Has<PhysicsAngularVelocity>(entities[index])) {
                    world.Get<PhysicsAngularVelocity>(entities[index]).Value =
                        bodies[index].AngularVelocity + AngularError(locals[index].Rotation, target[index].Rotation, settings[index]);
                }

                CorrectedCount++;
            }
        }

        foreach (var entity in snapping) {
            ref var local = ref world.Get<LocalTransform>(entity);
            ref readonly var wanted = ref world.Read<NetworkTransform>(entity);

            local.Position = wanted.Position;
            local.Rotation = wanted.Rotation;

            if (world.Has<PhysicsLinearVelocity>(entity)) {
                world.Get<PhysicsLinearVelocity>(entity).Value = world.Read<NetworkRigidBody>(entity).LinearVelocity;
            }

            if (world.Has<PhysicsAngularVelocity>(entity)) {
                world.Get<PhysicsAngularVelocity>(entity).Value = world.Read<NetworkRigidBody>(entity).AngularVelocity;
            }

            // Marked, so nothing draws the body sliding to where it was teleported.
            if (!world.Has<PhysicsTeleport>(entity)) {
                world.Add<PhysicsTeleport>(entity);
            }

            SnappedCount++;
        }
    }

    /// <summary>The angular velocity that would rotate <paramref name="from" /> onto <paramref name="to" />.</summary>
    /// <remarks>
    ///     The rotation between the two, taken the short way round — <c>q</c> and <c>-q</c> being the
    ///     same rotation, without the flip a body a hair past half a turn would be corrected the
    ///     long way and spin most of a revolution to get somewhere it already nearly was.
    /// </remarks>
    static Vector3 AngularError(Quaternion from, Quaternion to, in NetworkRigidBodyCorrection settings) {
        var shortest = Quaternion.Dot(from, to) < 0f ? new Quaternion(-to.X, -to.Y, -to.Z, -to.W) : to;
        var difference = Quaternion.Normalize(shortest * Quaternion.Conjugate(from));

        var axis = new Vector3(difference.X, difference.Y, difference.Z);
        var sine = axis.Length();

        if (sine < 1e-6f) {
            return Vector3.Zero;
        }

        var angle = 2f * MathF.Atan2(sine, difference.W);

        return axis / sine * angle * settings.RotationStrength;
    }
}
