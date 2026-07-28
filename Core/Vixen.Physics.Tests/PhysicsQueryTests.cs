// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Physics.Bodies;
using Vixen.Physics.Queries;
using Xunit;

namespace Vixen.Physics.Tests;

public sealed class PhysicsQueryTests {
    [Fact]
    public void ARayFindsTheNearestBodyAndReportsWhereItTouched() {
        using var world = new PhysicsWorld();

        var near = world.CreateBody(BodyDescription.Static(world.Shapes.Box(0.5f), new(0f, 0f, -2f)));
        world.CreateBody(BodyDescription.Static(world.Shapes.Box(0.5f), new(0f, 0f, -6f)));

        Assert.True(world.Raycast(Vector3.Zero, Vector3.Forward, 20f, out var hit));

        Assert.Equal(near, hit.Body);
        Assert.Equal(-1.5f, hit.Position.Z, 3);
        Assert.Equal(1.5f, hit.Distance, 3);

        // Pointing back along the ray, out of the box it hit.
        Assert.Equal(1f, hit.Normal.Z, 3);
    }

    [Fact]
    public void ARayThatReachesNothingMissesEvenWhenSomethingIsFurtherAlong() {
        using var world = new PhysicsWorld();

        world.CreateBody(BodyDescription.Static(world.Shapes.Box(0.5f), new(0f, 0f, -10f)));

        Assert.False(world.Raycast(Vector3.Zero, Vector3.Forward, 5f, out _));
        Assert.True(world.Raycast(Vector3.Zero, Vector3.Forward, 20f, out _));
    }

    [Fact]
    public void RaycastAllReturnsEverythingNearestFirst() {
        using var world = new PhysicsWorld();

        world.CreateBody(BodyDescription.Static(world.Shapes.Box(0.5f), new(0f, 0f, -6f)));
        world.CreateBody(BodyDescription.Static(world.Shapes.Box(0.5f), new(0f, 0f, -2f)));
        world.CreateBody(BodyDescription.Static(world.Shapes.Box(0.5f), new(0f, 0f, -4f)));

        var hits = world.RaycastAll(Vector3.Zero, Vector3.Forward, 20f);

        Assert.Equal(3, hits.Length);
        Assert.True(hits[0].Distance < hits[1].Distance);
        Assert.True(hits[1].Distance < hits[2].Distance);
    }

    [Fact]
    public void ARayCanBeToldToIgnoreTheBodyThatFiredIt() {
        using var world = new PhysicsWorld();

        var shooter = world.CreateBody(BodyDescription.Static(world.Shapes.Box(0.5f), new(0f, 0f, -1f)));
        var target = world.CreateBody(BodyDescription.Static(world.Shapes.Box(0.5f), new(0f, 0f, -5f)));

        Assert.True(world.Raycast(Vector3.Zero, Vector3.Forward, 20f, out var blocked));
        Assert.Equal(shooter, blocked.Body);

        Assert.True(world.Raycast(Vector3.Zero, Vector3.Forward, 20f, out var clear, QueryFilter.Excluding(shooter)));
        Assert.Equal(target, clear.Body);
    }

    [Fact]
    public void ARayIgnoresSensorsUnlessAskedForThem() {
        using var world = new PhysicsWorld();

        var sensor = world.CreateBody(BodyDescription.Trigger(world.Shapes.Box(0.5f), new(0f, 0f, -2f)));
        var solid = world.CreateBody(BodyDescription.Static(world.Shapes.Box(0.5f), new(0f, 0f, -5f)));

        Assert.True(world.Raycast(Vector3.Zero, Vector3.Forward, 20f, out var solidHit));
        Assert.Equal(solid, solidHit.Body);

        Assert.True(
            world.Raycast(Vector3.Zero, Vector3.Forward, 20f, out var sensorHit, QueryFilter.WithSensors())
        );

        Assert.Equal(sensor, sensorHit.Body);
    }

    [Fact]
    public void ARayOnlySeesTheLayersItIsGiven() {
        var layers = PhysicsLayers.Define().Add("A").Add("B").Build();
        using var world = new PhysicsWorld(new() { Layers = layers });

        var onA = world.CreateBody(
            BodyDescription.Static(world.Shapes.Box(0.5f), new(0f, 0f, -2f)) with { Layer = new(0) }
        );

        var onB = world.CreateBody(
            BodyDescription.Static(world.Shapes.Box(0.5f), new(0f, 0f, -5f)) with { Layer = new(1) }
        );

        Assert.True(
            world.Raycast(Vector3.Zero, Vector3.Forward, 20f, out var first, QueryFilter.On(new PhysicsLayer(0).AsMask))
        );

        Assert.Equal(onA, first.Body);

        Assert.True(
            world.Raycast(Vector3.Zero, Vector3.Forward, 20f, out var second, QueryFilter.On(new PhysicsLayer(1).AsMask))
        );

        Assert.Equal(onB, second.Body);

        Assert.False(
            world.Raycast(Vector3.Zero, Vector3.Forward, 20f, out _, QueryFilter.On(PhysicsLayerMask.None))
        );
    }

    /// <summary>
    ///     The reason <c>QueryFilter</c> stores its mask and its ignored body inverted: an omitted
    ///     filter has to mean "no filtering", and stored plainly it would mean "hit nothing, and skip
    ///     body zero".
    /// </summary>
    [Fact]
    public void AnOmittedFilterSeesEverything() {
        Assert.Equal(PhysicsLayerMask.All, default(QueryFilter).Layers);
        Assert.Equal(BodyHandle.None, default(QueryFilter).IgnoreBody);
        Assert.Equal(QueryFilter.Default, default);

        using var world = new PhysicsWorld();
        world.CreateBody(BodyDescription.Static(world.Shapes.Box(0.5f), new(0f, 0f, -2f)));

        Assert.True(world.Raycast(Vector3.Zero, Vector3.Forward, 20f, out _));
    }

    [Fact]
    public void AnOverlapFindsEveryBodyInsideTheShapeAndNothingOutside() {
        using var world = new PhysicsWorld();

        var inside = world.CreateBody(BodyDescription.Static(world.Shapes.Box(0.25f), new(1f, 0f, 0f)));
        var alsoInside = world.CreateBody(BodyDescription.Static(world.Shapes.Box(0.25f), new(-1f, 0f, 0f)));
        world.CreateBody(BodyDescription.Static(world.Shapes.Box(0.25f), new(10f, 0f, 0f)));

        var found = world.OverlapSphere(Vector3.Zero, 2f);

        Assert.Equal(2, found.Length);
        var bodies = new HashSet<BodyHandle>();

        foreach (var overlap in found) {
            bodies.Add(overlap.Body);
        }

        Assert.Contains(inside, bodies);
        Assert.Contains(alsoInside, bodies);
    }

    [Fact]
    public void AShapeCastStopsAtTheFirstThingInItsWay() {
        using var world = new PhysicsWorld();

        var wall = world.CreateBody(
            BodyDescription.Static(world.Shapes.Box(new Vector3(5f, 5f, 0.5f)), new(0f, 0f, -5f))
        );

        var sphere = world.Shapes.Sphere(0.5f);

        Assert.True(world.ShapeCast(sphere, Vector3.Zero, Quaternion.Identity, new(0f, 0f, -20f), out var hit));

        Assert.Equal(wall, hit.Body);

        // The sphere's surface meets the wall's front face at z = −4, a fifth of the way along a
        // twenty-metre sweep.
        Assert.Equal(0.2f, hit.Fraction, 2);
    }

    [Fact]
    public void APointIsInsideABodyOrItIsNot() {
        using var world = new PhysicsWorld();

        world.CreateBody(BodyDescription.Static(world.Shapes.Box(1f), Vector3.Zero));

        Assert.True(world.CheckPoint(new(0.5f, 0.5f, 0.5f)));
        Assert.False(world.CheckPoint(new(5f, 0f, 0f)));
    }
}
