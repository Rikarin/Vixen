// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Xr.Ecs;
using Vixen.Xr.Input;
using Xunit;

namespace Vixen.Xr.Tests;

/// <summary>The bridge from a runtime's reference space to a world where the player is somewhere else.</summary>
public sealed class XrTrackingSystemTests {
    [Fact]
    public void TheHeadLandsMidwayBetweenTheEyes() {
        using var world = new World();
        var session = Session(out var backend);

        using (backend) {
            session.HeadPose = new XrPose(new Vector3(0f, 1.7f, -3f), Quaternion.Identity);

            var system = Located(session);
            var entity = Tracked(world, XrTrackedDevice.Head);

            system.Publish(world);

            var transform = world.Read<LocalTransform>(entity);

            Assert.Equal(0f, transform.Position.X, 4);
            Assert.Equal(1.7f, transform.Position.Y, 4);
            Assert.Equal(-3f, transform.Position.Z, 4);
            Assert.True(world.Read<XrTrackedPose>(entity).IsTracked);
        }
    }

    [Fact]
    public void TheRigMovesEverythingTrackedWithIt() {
        // The reason the rig is an entity: teleporting the player is moving one transform, and the
        // headset and the hands follow without anything else being told.
        using var world = new World();
        var session = Session(out var backend);

        using (backend) {
            session.HeadPose = new XrPose(new Vector3(0f, 1.7f, 0f), Quaternion.Identity);

            var system = Located(session);

            Rig(world, new Vector3(10f, 0f, 5f), Quaternion.Identity, 1f);

            var entity = Tracked(world, XrTrackedDevice.Head);

            system.Publish(world);

            var position = world.Read<LocalTransform>(entity).Position;

            Assert.Equal(10f, position.X, 4);
            Assert.Equal(1.7f, position.Y, 4);
            Assert.Equal(5f, position.Z, 4);
        }
    }

    [Fact]
    public void ARotatedRigRotatesTheTrackedOffsetRatherThanAddingIt() {
        // A snap turn is the everyday case, and a rig that added positions works right up until
        // somebody uses one.
        using var world = new World();
        var session = Session(out var backend);

        using (backend) {
            session.HeadPose = new XrPose(new Vector3(1f, 0f, 0f), Quaternion.Identity);

            var system = Located(session);

            Rig(
                world,
                Vector3.Zero,
                Quaternion.FromAxisAngle(new Vector3(0f, 1f, 0f), MathF.PI / 2f),
                1f
            );

            var entity = Tracked(world, XrTrackedDevice.Head);

            system.Publish(world);

            // A quarter turn about Y takes +X to −Z.
            var position = world.Read<LocalTransform>(entity).Position;

            Assert.Equal(0f, position.X, 3);
            Assert.Equal(-1f, position.Z, 3);
        }
    }

    [Fact]
    public void AGameInCentimetresScalesAtTheRigAndNowhereElse() {
        using var world = new World();
        var session = Session(out var backend);

        using (backend) {
            session.HeadPose = new XrPose(new Vector3(0f, 1.7f, 0f), Quaternion.Identity);

            var system = Located(session);

            Rig(world, Vector3.Zero, Quaternion.Identity, 100f);

            var entity = Tracked(world, XrTrackedDevice.Head);

            system.Publish(world);

            Assert.Equal(170f, world.Read<LocalTransform>(entity).Position.Y, 3);
        }
    }

    [Fact]
    public void AnUntrackedControllerIsLeftWhereItWasLastSeen() {
        // Snapping a put-down controller to the rig's origin is worse than a hand that has stopped
        // moving, and it is what writing the identity when untracked does.
        using var world = new World();
        var session = Session(out var backend);

        using (backend) {
            var system = Located(session);
            var entity = Tracked(world, XrTrackedDevice.LeftHand);

            world.Set(entity, LocalTransform.At(new Vector3(4f, 5f, 6f)));
            system.Publish(world);

            Assert.Equal(new Vector3(4f, 5f, 6f), world.Read<LocalTransform>(entity).Position);
            Assert.False(world.Read<XrTrackedPose>(entity).IsTracked);
        }
    }

    [Fact]
    public void AHandFollowsItsPoseActionOnceOneIsGiven() {
        using var world = new World();
        var session = Session(out var backend);

        using (backend) {
            var set = new XrActionSet("gameplay");
            var pose = set.CreateAction("hand", XrActionType.Pose);

            pose.Publish(
                XrHand.Right,
                new XrActionState(IsActive: true, Pose: new XrPose(new Vector3(0.3f, 1f, -0.4f), Quaternion.Identity), IsTracked: true)
            );

            var system = Located(session);

            system.HandPoseAction = pose;

            var entity = Tracked(world, XrTrackedDevice.RightHand);

            system.Publish(world);

            Assert.Equal(0.3f, world.Read<LocalTransform>(entity).Position.X, 4);
        }
    }

    [Fact]
    public void RotationAndPositionAreAppliedIndependently() {
        using var world = new World();
        var session = Session(out var backend);

        using (backend) {
            session.HeadPose = new XrPose(
                new Vector3(0f, 1.7f, 0f),
                Quaternion.FromAxisAngle(new Vector3(0f, 1f, 0f), 0.5f)
            );

            var system = Located(session);
            var entity = world.Create();

            world.Add(entity, new XrTrackedPose { Device = XrTrackedDevice.Head, ApplyRotation = true });
            world.Add(entity, LocalTransform.At(new Vector3(9f, 9f, 9f)));

            system.Publish(world);

            var transform = world.Read<LocalTransform>(entity);

            Assert.Equal(new Vector3(9f, 9f, 9f), transform.Position);
            Assert.NotEqual(Quaternion.Identity, transform.Rotation);
        }
    }

    static NullXrSession Session(out NullXrBackend backend) {
        backend = new NullXrBackend();

        var session = (NullXrSession)backend.CreateSession(default, new XrSessionOptions());

        for (var index = 0; index < 8 && session.State != XrSessionState.Focused; index++) {
            session.PollEvents();
        }

        return session;
    }

    /// <summary>A system whose session has already located this frame's views, as a host would.</summary>
    static XrTrackingSystem Located(NullXrSession session) {
        session.BeginFrame(out var frame);
        session.LocateViews(in frame);
        session.EndFrame(in frame, []);

        return new XrTrackingSystem(session);
    }

    static Entity Tracked(World world, XrTrackedDevice device) {
        var entity = world.Create();

        world.Add(entity, XrTrackedPose.Following(device));
        world.Add(entity, LocalTransform.Identity);

        return entity;
    }

    static void Rig(World world, Vector3 position, Quaternion rotation, float unitsPerMetre) {
        var entity = world.Create();

        world.Add(entity, new XrOrigin { UnitsPerMetre = unitsPerMetre });
        world.Add(
            entity,
            new LocalTransform { Position = position, Rotation = rotation, Scale = Vector3.One }
        );
    }
}
