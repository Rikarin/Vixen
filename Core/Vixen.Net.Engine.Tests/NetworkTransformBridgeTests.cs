// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Net.Motion;
using Vixen.Net.Replication;
using Xunit;

namespace Vixen.Net.Engine.Tests;

/// <summary>The seam between the engine's transform and the one the wire carries.</summary>
public sealed class NetworkTransformBridgeTests {
    [Fact]
    public void AServerPublishesWhatMoved() {
        using var world = new World("bridge-server");
        var capture = new NetworkTransformCaptureSystem();

        var entity = world.Create(
            new NetworkId(1),
            new LocalTransform { Position = new(1f, 2f, 3f), Rotation = Quaternion.Identity },
            default(NetworkTransform)
        );

        world.AdvanceVersion();
        capture.Publish(world);

        Assert.Equal(new Vector3(1f, 2f, 3f), world.Read<NetworkTransform>(entity).Position);
        Assert.Equal(1, capture.PublishedCount);
    }

    /// <summary>A scene where nothing moved costs nothing, which is why the ECS has change versions.</summary>
    /// <remarks>
    ///     The property worth having a test for rather than a comment. A bridge that swept every
    ///     networked entity every tick would be the single most expensive thing in a match with a
    ///     thousand static props in it, and it would be invisible until somebody profiled.
    /// </remarks>
    [Fact]
    public void AnEntityThatDidNotMove_IsNotVisitedAgain() {
        using var world = new World("bridge-idle");
        var capture = new NetworkTransformCaptureSystem();

        world.Create(
            new NetworkId(1),
            new LocalTransform { Position = new(1f, 0f, 0f), Rotation = Quaternion.Identity },
            default(NetworkTransform)
        );

        world.AdvanceVersion();
        capture.Publish(world);
        Assert.Equal(1, capture.PublishedCount);

        for (var tick = 0; tick < 10; tick++) {
            world.AdvanceVersion();
            capture.Publish(world);
        }

        Assert.Equal(1, capture.PublishedCount);
    }

    [Fact]
    public void AClientAppliesWhatArrived() {
        using var world = new World("bridge-client");
        var apply = new NetworkTransformApplySystem();

        var entity = world.Create(
            new NetworkId(1),
            new LocalTransform { Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = new(2f, 2f, 2f) },
            default(NetworkTransform)
        );

        world.AdvanceVersion();
        world.Get<NetworkTransform>(entity).Position = new(4f, 5f, 6f);
        apply.Apply(world);

        Assert.Equal(new Vector3(4f, 5f, 6f), world.Read<LocalTransform>(entity).Position);

        // Scale is the prefab's and survives, because NetworkTransform does not carry one.
        Assert.Equal(new Vector3(2f, 2f, 2f), world.Read<LocalTransform>(entity).Scale);
        Assert.Equal(1, apply.AppliedCount);
    }

    /// <summary>A teleport becomes a counter bump on the value that same tick publishes.</summary>
    /// <remarks>
    ///     Ordering is the whole test. If the counter landed on the <i>next</i> tick's value the
    ///     receiver would interpolate across the jump and be told about it afterwards, which is a
    ///     player sliding the length of the level on every respawn.
    /// </remarks>
    [Fact]
    public void ATeleportBecomesACounterBumpBeforeThePositionIsPublished() {
        using var world = new World("bridge-teleport");
        var capture = new NetworkTransformCaptureSystem();

        var entity = world.Create(
            new NetworkId(1),
            new LocalTransform { Position = Vector3.Zero, Rotation = Quaternion.Identity },
            default(NetworkTransform)
        );

        world.AdvanceVersion();
        capture.Publish(world);

        var before = world.Read<NetworkTransform>(entity).TeleportCount;

        world.AdvanceVersion();
        world.Get<LocalTransform>(entity).Position = new(500f, 0f, 0f);
        world.Add<NetworkTeleport>(entity);
        capture.Publish(world);

        var after = world.Read<NetworkTransform>(entity);

        Assert.NotEqual(before, after.TeleportCount);
        Assert.Equal(new Vector3(500f, 0f, 0f), after.Position);

        // And the tag is gone, so nothing has to remember to clear it and the next tick is ordinary.
        Assert.False(world.Has<NetworkTeleport>(entity));
        Assert.Equal(1, capture.TeleportCount);
    }

    /// <summary>The two halves round-trip, which is the thing a game actually wires up.</summary>
    [Fact]
    public void AServerAndAClientAgreeAboutWhereSomethingIs() {
        using var server = new World("bridge-round-server");
        using var client = new World("bridge-round-client");

        var capture = new NetworkTransformCaptureSystem();
        var apply = new NetworkTransformApplySystem();

        var here = server.Create(
            new NetworkId(1),
            new LocalTransform { Position = new(7f, 8f, 9f), Rotation = Quaternion.Identity },
            default(NetworkTransform)
        );

        var there = client.Create(
            new NetworkId(1),
            new LocalTransform { Position = Vector3.Zero, Rotation = Quaternion.Identity },
            default(NetworkTransform)
        );

        server.AdvanceVersion();
        capture.Publish(server);

        // Standing in for the wire, which has its own tests.
        client.AdvanceVersion();
        client.Get<NetworkTransform>(there) = server.Read<NetworkTransform>(here);
        apply.Apply(client);

        Assert.Equal(server.Read<LocalTransform>(here).Position, client.Read<LocalTransform>(there).Position);
    }
}
