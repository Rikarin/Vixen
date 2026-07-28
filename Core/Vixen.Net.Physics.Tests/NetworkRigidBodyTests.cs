// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Net.Messaging;
using Vixen.Net.Motion;
using Vixen.Net.Replication;
using Xunit;
using PhysicsAngularVelocity = global::Vixen.Physics.Ecs.AngularVelocity;
using PhysicsLinearVelocity = global::Vixen.Physics.Ecs.LinearVelocity;

namespace Vixen.Net.Physics.Tests;

/// <summary>Networked rigid bodies: what goes on the wire, and how a remote one is steered.</summary>
public sealed class NetworkRigidBodyTests {
    const float Step = 1f / 30f;

    /// <summary>A body at rest says so once and then costs its unchanged bits.</summary>
    /// <remarks>
    ///     The bandwidth decision, and the reason the rest flag exists rather than being inferred
    ///     from a small velocity. Most objects in most scenes are asleep; a velocity dithering in its
    ///     last quantisation step would pay full price for every one of them, for ever.
    /// </remarks>
    [Fact]
    public void ABodyAtRestIsPublishedAsExactlyZero() {
        using var world = new World("rigid-rest");
        var capture = new NetworkRigidBodyCaptureSystem();

        var entity = world.Create(
            new NetworkId(1),
            default(NetworkRigidBody),
            new PhysicsLinearVelocity { Value = new(0.001f, -0.002f, 0.0005f) },
            new PhysicsAngularVelocity { Value = new(0.001f, 0f, 0f) }
        );

        capture.Publish(world);

        var body = world.Read<NetworkRigidBody>(entity);

        Assert.True(body.IsResting);
        Assert.Equal(Vector3.Zero, body.LinearVelocity);
        Assert.Equal(Vector3.Zero, body.AngularVelocity);
        Assert.Equal(1, capture.RestingCount);
    }

    [Fact]
    public void AMovingBodyPublishesItsVelocity() {
        using var world = new World("rigid-moving");
        var capture = new NetworkRigidBodyCaptureSystem();

        var entity = world.Create(
            new NetworkId(1),
            default(NetworkRigidBody),
            new PhysicsLinearVelocity { Value = new(3f, -9.8f, 0f) },
            new PhysicsAngularVelocity { Value = new(0f, 2f, 0f) }
        );

        capture.Publish(world);

        var body = world.Read<NetworkRigidBody>(entity);

        Assert.False(body.IsResting);
        Assert.Equal(new Vector3(3f, -9.8f, 0f), body.LinearVelocity);
        Assert.Equal(new Vector3(0f, 2f, 0f), body.AngularVelocity);
    }

    /// <summary>The velocity survives the wire to within its quantisation and no worse.</summary>
    [Fact]
    public void AVelocityRoundTripsWithinItsQuantisation() {
        using var world = new World("rigid-wire");
        var buffer = new byte[64];
        var replicator = new NetworkRigidBodyReplicator();

        var entity = world.Create(
            new NetworkId(1),
            new NetworkRigidBody { LinearVelocity = new(12.5f, -3.25f, 0.125f), AngularVelocity = new(1f, 2f, 3f) }
        );

        var writer = new BitWriter(buffer);
        replicator.Write(world, entity, ref writer);
        Assert.True(writer.TryFinish(out var bits));

        using var receiving = new World("rigid-wire-client");
        var arrived = receiving.Create(new NetworkId(1));
        var reader = new BitReader(bits);

        Assert.True(replicator.Apply(receiving, arrived, ref reader));

        var got = receiving.Read<NetworkRigidBody>(arrived);
        var tolerance = NetworkRigidBodyReplicator.VelocityRange.MaxError * 2f;

        Assert.Equal(12.5f, got.LinearVelocity.X, tolerance);
        Assert.Equal(-3.25f, got.LinearVelocity.Y, tolerance);
        Assert.Equal(3f, got.AngularVelocity.Z, tolerance);
    }

    /// <summary>A body behind where it should be is steered there, and does not overshoot.</summary>
    /// <remarks>
    ///     <para>
    ///         The claim critical damping makes, and the one worth a test rather than a comment: the
    ///         correction converges and never passes the target. An underdamped spring would show up
    ///         here as the error changing sign, which is the crate visibly wobbling around where it
    ///         ought to be.
    ///     </para>
    ///     <para>
    ///         The loop integrates by hand rather than stepping Jolt, because what is under test is
    ///         the correction the system computes — putting a solver in the middle would be testing
    ///         Jolt's integrator as well, and it has its own tests.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ABodyBehindIsSteeredToTheAuthorityWithoutOvershooting() {
        using var world = new World("rigid-correct");
        var correction = new NetworkRigidBodyCorrectionSystem();

        var entity = Corrected(
            world,
            new NetworkRigidBody { LinearVelocity = Vector3.Zero },
            new NetworkTransform { Position = new(1f, 0f, 0f), Rotation = Quaternion.Identity },
            new LocalTransform { Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One }
        );

        var previous = 1f;

        for (var tick = 0; tick < 60; tick++) {
            correction.Correct(world, Step);

            // Integrate the velocity the correction asked for, which is what the solver would do.
            ref var local = ref world.Get<LocalTransform>(entity);
            local.Position += world.Read<PhysicsLinearVelocity>(entity).Value * Step;

            var error = world.Read<NetworkTransform>(entity).Position.X - local.Position.X;

            Assert.True(error >= -0.001f, $"Overshot at tick {tick}: error {error}");
            Assert.True(error <= previous + 0.001f, $"Diverged at tick {tick}: {error} against {previous}");

            previous = error;
        }

        Assert.True(previous < 0.02f, $"Did not converge — {previous} m out after two seconds.");
        Assert.Equal(0, correction.SnappedCount);
    }

    /// <summary>A body far out is teleported, because no spring can fix that without flinging it.</summary>
    [Fact]
    public void ABodyBeyondTheSnapDistanceIsTeleported() {
        using var world = new World("rigid-snap");
        var correction = new NetworkRigidBodyCorrectionSystem();

        var entity = Corrected(
            world,
            new NetworkRigidBody { LinearVelocity = new(1f, 0f, 0f) },
            new NetworkTransform { Position = new(500f, 0f, 0f), Rotation = Quaternion.Identity },
            new LocalTransform { Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One }
        );

        correction.Correct(world, Step);

        Assert.Equal(new Vector3(500f, 0f, 0f), world.Read<LocalTransform>(entity).Position);
        Assert.Equal(1, correction.SnappedCount);

        // And it is marked, so nothing draws it sliding five hundred metres to get there.
        Assert.True(world.Has<global::Vixen.Physics.Ecs.PhysicsTeleport>(entity));

        // The velocity is the authority's, not a correction toward a target it has already reached.
        Assert.Equal(new Vector3(1f, 0f, 0f), world.Read<PhysicsLinearVelocity>(entity).Value);
    }

    /// <summary>A body already where it should be is left alone.</summary>
    /// <remarks>
    ///     The correction adds to the authority's velocity rather than replacing it, so a body in the
    ///     right place carries on with the motion it was sent — which is what lets a thrown crate arc
    ///     between updates instead of travelling in flat segments.
    /// </remarks>
    [Fact]
    public void ABodyInTheRightPlaceKeepsTheVelocityItWasSent() {
        using var world = new World("rigid-agreeing");
        var correction = new NetworkRigidBodyCorrectionSystem();

        var entity = Corrected(
            world,
            new NetworkRigidBody { LinearVelocity = new(0f, -9.8f, 4f) },
            new NetworkTransform { Position = new(2f, 3f, 4f), Rotation = Quaternion.Identity },
            new LocalTransform { Position = new(2f, 3f, 4f), Rotation = Quaternion.Identity, Scale = Vector3.One }
        );

        correction.Correct(world, Step);

        Assert.Equal(new Vector3(0f, -9.8f, 4f), world.Read<PhysicsLinearVelocity>(entity).Value);
    }

    /// <summary>A rotation is corrected the short way round.</summary>
    /// <remarks>
    ///     <c>q</c> and <c>-q</c> are the same rotation, so without the flip a body a hair past half
    ///     a turn is corrected the long way and spins most of a revolution to reach somewhere it was
    ///     already next to.
    /// </remarks>
    [Fact]
    public void ARotationIsCorrectedTheShortWayRound() {
        using var world = new World("rigid-rotation");
        var correction = new NetworkRigidBodyCorrectionSystem();

        var almost = Quaternion.FromAxisAngle(Vector3.UnitY, 3.0f);
        var target = Quaternion.FromAxisAngle(Vector3.UnitY, -3.0f);

        var entity = Corrected(
            world,
            default,
            new NetworkTransform { Position = Vector3.Zero, Rotation = target },
            new LocalTransform { Position = Vector3.Zero, Rotation = almost, Scale = Vector3.One }
        );

        correction.Correct(world, Step);

        // The two are about 0.28 rad apart the short way and 6.0 rad apart the long way. A
        // correction that took the long way would be an order of magnitude larger.
        var angular = world.Read<PhysicsAngularVelocity>(entity).Value.Length();

        Assert.True(angular < 5f, $"Corrected the long way round: {angular} rad/s.");
    }

    /// <summary>A body set up the way a receiving peer has one.</summary>
    /// <remarks>
    ///     World.Create takes four components at most, which is fewer than a corrected body needs —
    ///     so the rest are added. Worth a helper rather than four copies of the same six lines.
    /// </remarks>
    static Entity Corrected(World world, in NetworkRigidBody body, in NetworkTransform target, in LocalTransform local) {
        var entity = world.Create(new NetworkId(1), body, target, local);

        world.Add(entity, NetworkRigidBodyCorrection.Default);
        world.Add(entity, default(PhysicsLinearVelocity));
        world.Add(entity, default(PhysicsAngularVelocity));

        return entity;
    }
}
