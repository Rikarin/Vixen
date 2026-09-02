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

    /// <summary>A passenger is published in the vehicle's frame, and the frame is named.</summary>
    /// <remarks>
    ///     Doc 28 § Movement's whole requirement in one test: what goes on the wire for a rider is a
    ///     seat offset and the id of the seat it is an offset from, not a world position that fights
    ///     the vehicle's own.
    /// </remarks>
    [Fact]
    public void ARiderIsPublishedRelativeToTheVehicleItIsParentedTo() {
        using var world = new World("frame-publish");
        var capture = new NetworkTransformCaptureSystem();

        var vehicle = Networked(world, 1, new(100f, 0f, 0f));
        var rider = Networked(world, 2, Vector3.Zero);

        Hierarchy.SetParent(world, rider, vehicle);
        world.Get<LocalTransform>(rider).Position = new(0f, 1.5f, -0.5f);

        world.AdvanceVersion();
        capture.Publish(world);

        // The offset, not the offset plus a hundred.
        Assert.Equal(new Vector3(0f, 1.5f, -0.5f), world.Read<NetworkTransform>(rider).Position);
        Assert.Equal(1u, world.Read<NetworkParent>(rider).Value);
        Assert.Equal(1, capture.ReframedCount);

        // And the vehicle itself is in nobody's frame, so it never grows the component at all.
        Assert.False(world.Has<NetworkParent>(vehicle));
    }

    /// <summary>Getting off puts the rider back in the world, and says so.</summary>
    [Fact]
    public void DismountingPutsTheRiderBackInWorldSpace() {
        using var world = new World("frame-dismount");
        var capture = new NetworkTransformCaptureSystem();

        var vehicle = Networked(world, 1, new(100f, 0f, 0f));
        var rider = Networked(world, 2, Vector3.Zero);

        Hierarchy.SetParent(world, rider, vehicle);
        world.AdvanceVersion();
        capture.Publish(world);
        Assert.Equal(1u, world.Read<NetworkParent>(rider).Value);

        Hierarchy.SetParent(world, rider, Entity.Null);
        world.AdvanceVersion();
        capture.Publish(world);

        // Zero and not "the component is gone": replication never removes a component, so the frame
        // a rider has left has to be a value rather than an absence.
        Assert.Equal(0u, world.Read<NetworkParent>(rider).Value);
        Assert.Equal(2, capture.ReframedCount);
    }

    /// <summary>
    ///     ⚠ An entity hanging off a parent the wire cannot name is published in world space, which
    ///     is what it always should have been.
    /// </summary>
    /// <remarks>
    ///     The defect this pass fixes rather than a feature it adds. <c>LocalTransform</c> is
    ///     relative to the parent and the bridge published it verbatim, so a networked entity under a
    ///     purely local parent sent an offset that the other end read as a world position — silently,
    ///     and wrong by however far the parent was from the origin.
    /// </remarks>
    [Fact]
    public void AnEntityUnderAnUnnamedParentIsPublishedInWorldSpace() {
        using var world = new World("frame-unnameable");
        var capture = new NetworkTransformCaptureSystem();

        // A parent with no NetworkId: an art pivot, a spawn point, anything the game did not network.
        var pivot = Hierarchy.CreateTransform(world, LocalTransform.At(new(100f, 0f, 0f)));
        var child = Networked(world, 1, new(0f, 0f, 5f));

        Hierarchy.SetParent(world, child, pivot);

        world.AdvanceVersion();
        capture.Publish(world);

        Assert.Equal(new Vector3(100f, 0f, 5f), world.Read<NetworkTransform>(child).Position);
        Assert.Equal(1, capture.UnnameableFrameCount);

        // And no frame is claimed for it, because there is no id that names one.
        Assert.False(world.Has<NetworkParent>(child));
    }

    /// <summary>
    ///     ⚠ A transform quoted in a frame that has not arrived is not applied at all.
    /// </summary>
    /// <remarks>
    ///     <b>The ordering defect this feature is mostly about.</b> The rider's numbers are a seat
    ///     offset; read as world coordinates they put it a metre and a half above the world origin
    ///     until the vehicle turns up. Holding it still instead is the only answer that is never
    ///     visibly wrong, and it has to survive the transform not changing again — the value arrived,
    ///     it simply could not be used, so a change-filtered pass would look once and never again.
    /// </remarks>
    [Fact]
    public void ARiderWhoseVehicleHasNotArrivedIsHeldRatherThanPlacedAtTheOrigin() {
        using var world = new World("frame-ordering");
        var client = new ReplicationClient(new());
        var apply = new NetworkTransformApplySystem { Client = client };

        var rider = Networked(world, 2, new(-50f, 0f, -50f));
        client.Bind(new(2), rider);

        world.Get<NetworkTransform>(rider).Position = new(0f, 1.5f, -0.5f);
        world.Add(rider, new NetworkParent { Value = 1 });
        world.AdvanceVersion();

        apply.Apply(world);
        apply.Apply(world);

        // Where it was, not at the seat offset from the origin.
        Assert.Equal(new Vector3(-50f, 0f, -50f), world.Read<LocalTransform>(rider).Position);
        Assert.Equal(2, apply.UnresolvedFrameCount);
        Assert.Equal(0, apply.AppliedCount);

        // Now the vehicle arrives. Nothing about the rider's records has changed since.
        var vehicle = Networked(world, 1, new(100f, 0f, 0f));
        client.Bind(new(1), vehicle);

        apply.Apply(world);

        Assert.Equal(vehicle, Hierarchy.ParentOf(world, rider));
        Assert.Equal(new Vector3(0f, 1.5f, -0.5f), world.Read<LocalTransform>(rider).Position);
        Assert.Equal(1, apply.ReparentedCount);
        Assert.Equal(2, apply.UnresolvedFrameCount);
    }

    /// <summary>The two ends agree about where a rider is in the world, which is the point.</summary>
    [Fact]
    public void AServerAndAClientAgreeAboutWhereARiderIs() {
        using var server = new World("frame-round-server");
        using var client = new World("frame-round-client");

        var capture = new NetworkTransformCaptureSystem();
        var replication = new ReplicationClient(new());
        var apply = new NetworkTransformApplySystem { Client = replication };

        var vehicle = Networked(server, 1, new(100f, 0f, 0f));
        var rider = Networked(server, 2, Vector3.Zero);
        Hierarchy.SetParent(server, rider, vehicle);
        server.Get<LocalTransform>(rider).Position = new(0f, 1.5f, -0.5f);

        var theirVehicle = Networked(client, 1, new(100f, 0f, 0f));
        var theirRider = Networked(client, 2, Vector3.Zero);
        replication.Bind(new(1), theirVehicle);
        replication.Bind(new(2), theirRider);

        server.AdvanceVersion();
        capture.Publish(server);

        // Standing in for the wire, which has its own tests. Both records, because both are records.
        client.AdvanceVersion();
        client.Get<NetworkTransform>(theirRider) = server.Read<NetworkTransform>(rider);
        client.Add(theirRider, server.Read<NetworkParent>(rider));
        apply.Apply(client);

        Assert.Equal(theirVehicle, Hierarchy.ParentOf(client, theirRider));
        Assert.Equal(
            Hierarchy.ResolveWorldMatrix(server, rider).Translation,
            Hierarchy.ResolveWorldMatrix(client, theirRider).Translation
        );
    }

    /// <summary>An entity with no frame at all is untouched, which is every entity that ships today.</summary>
    [Fact]
    public void AnEntityWithNoFrameIsNotReparentedByAnything() {
        using var world = new World("frame-absent");
        var apply = new NetworkTransformApplySystem { Client = new(new()) };

        var pivot = Hierarchy.CreateTransform(world, LocalTransform.At(new(10f, 0f, 0f)));
        var child = Networked(world, 1, Vector3.Zero);
        Hierarchy.SetParent(world, child, pivot);

        world.AdvanceVersion();
        world.Get<NetworkTransform>(child).Position = new(1f, 2f, 3f);
        apply.Apply(world);

        Assert.Equal(pivot, Hierarchy.ParentOf(world, child));
        Assert.Equal(new Vector3(1f, 2f, 3f), world.Read<LocalTransform>(child).Position);
        Assert.Equal(0, apply.ReparentedCount);
    }

    static Entity Networked(World world, uint id, Vector3 at) {
        var entity = Hierarchy.CreateTransform(world, LocalTransform.At(at));
        world.Add(entity, new NetworkId(id));
        world.Add(entity, new NetworkTransform { Position = at, Rotation = Quaternion.Identity });

        return entity;
    }
}
