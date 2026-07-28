// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Physics.Bodies;
using Vixen.Physics.Shapes;
using Xunit;

namespace Vixen.Physics.Tests;

public sealed class PhysicsShapeTests {
    static readonly Vector3[] Tetrahedron = [
        new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f), new(0f, 0f, 1f)
    ];

    [Fact]
    public void TheSameDescriptionRegistersOnceAndSharesOneNativeShape() {
        using var shapes = new PhysicsShapes();

        var first = shapes.Box(new Vector3(1f, 2f, 3f));
        var second = shapes.Box(new Vector3(1f, 2f, 3f));
        var different = shapes.Box(new Vector3(1f, 2f, 4f));

        Assert.Equal(first, second);
        Assert.NotEqual(first, different);
        Assert.Equal(2, shapes.Count);
    }

    [Fact]
    public void NothingIsBuiltUntilABodyAsksForIt() {
        using var world = new PhysicsWorld();

        var shape = world.Shapes.Box(0.5f);
        Assert.Equal(0, world.Shapes.BuiltCount);

        world.CreateBody(BodyDescription.Static(shape, Vector3.Zero));
        Assert.Equal(1, world.Shapes.BuiltCount);

        world.CreateBody(BodyDescription.Static(shape, new(5f, 0f, 0f)));
        Assert.Equal(1, world.Shapes.BuiltCount);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void AShapeWithANonsenseMeasurementIsRefusedAtRegistration(float radius) {
        using var shapes = new PhysicsShapes();
        Assert.Throws<PhysicsShapeException>(() => shapes.Sphere(radius));
    }

    [Fact]
    public void AHullNeedsFourPointsAndAMeshNeedsWholeTriangles() {
        using var shapes = new PhysicsShapes();

        Assert.Throws<PhysicsShapeException>(() => shapes.ConvexHull(Tetrahedron.AsSpan(0, 3)));
        Assert.Throws<PhysicsShapeException>(() => shapes.Mesh(Tetrahedron, [0, 1]));
        Assert.Throws<PhysicsShapeException>(() => shapes.Mesh(Tetrahedron, [0, 1, 99]));
    }

    [Fact]
    public void AConvexHullIsSolidWhereItsPointsAre() {
        using var world = new PhysicsWorld();

        var hull = world.Shapes.ConvexHull(Tetrahedron);
        world.CreateBody(BodyDescription.Static(hull, Vector3.Zero));

        Assert.True(world.CheckPoint(new(0.1f, 0.1f, 0.1f)));
        Assert.False(world.CheckPoint(new(2f, 2f, 2f)));
    }

    [Fact]
    public void AMeshStopsAFallingBody() {
        using var world = new PhysicsWorld();

        Vector3[] vertices = [new(-10f, 0f, -10f), new(10f, 0f, -10f), new(10f, 0f, 10f), new(-10f, 0f, 10f)];

        // Wound so the faces point up. Reverse these and the ball falls straight through — a mesh is
        // one-sided, and which side is which is the winding. See PhysicsShapes.Mesh.
        var floor = world.Shapes.Mesh(vertices, [0, 2, 1, 0, 3, 2]);

        world.CreateBody(BodyDescription.Static(floor, Vector3.Zero));
        var ball = world.CreateBody(BodyDescription.Dynamic(world.Shapes.Sphere(0.5f), new(0f, 4f, 0f)));

        for (var step = 0; step < 240; step++) {
            world.Step(1f / 60f);
        }

        Assert.InRange(world.GetPosition(ball).Y, 0.4f, 0.6f);
    }

    /// <summary>
    ///     Jolt's static compound refuses fewer than two sub-shapes, and a prefab whose collider list
    ///     happens to have one entry is a perfectly ordinary thing to author.
    /// </summary>
    [Fact]
    public void ACompoundOfOneChildIsBuiltAsTheChildItself() {
        using var world = new PhysicsWorld();

        var sphere = world.Shapes.Sphere(0.5f);
        var compound = world.Shapes.Compound([CompoundChild.At(sphere)]);
        var body = world.CreateBody(BodyDescription.Static(compound, Vector3.Zero));

        Assert.True(world.CheckPoint(Vector3.Zero));
        Assert.Equal(compound, world.ShapeOf(body));
    }

    [Fact]
    public void ACompoundIsSolidAtEachChildAndHollowBetweenThem() {
        using var world = new PhysicsWorld();

        var ball = world.Shapes.Sphere(0.5f);

        var dumbbell = world.Shapes.Compound([
            new(ball, new(-2f, 0f, 0f), Quaternion.Identity),
            new(ball, new(2f, 0f, 0f), Quaternion.Identity)
        ]);

        world.CreateBody(BodyDescription.Static(dumbbell, Vector3.Zero));

        Assert.True(world.CheckPoint(new(-2f, 0f, 0f)));
        Assert.True(world.CheckPoint(new(2f, 0f, 0f)));
        Assert.False(world.CheckPoint(Vector3.Zero));
    }

    [Fact]
    public void APlaneIsSolidBelowItsSurfaceAndEmptyAbove() {
        using var world = new PhysicsWorld();

        var ground = world.Shapes.Plane(Vector3.Up);
        world.CreateBody(BodyDescription.Static(ground, Vector3.Zero));

        var ball = world.CreateBody(BodyDescription.Dynamic(world.Shapes.Sphere(0.5f), new(0f, 5f, 0f)));

        for (var step = 0; step < 240; step++) {
            world.Step(1f / 60f);
        }

        Assert.InRange(world.GetPosition(ball).Y, 0.4f, 0.6f);
    }

    [Fact]
    public void AnIdFromAnotherRegistryIsRefusedRatherThanMisread() {
        using var first = new PhysicsShapes();
        using var second = new PhysicsShapes();

        var id = first.Sphere(1f);

        Assert.Throws<PhysicsHandleException>(() => second.Describe(id));
        Assert.Throws<PhysicsHandleException>(() => first.Describe(ShapeId.None));
    }

    [Fact]
    public void RegisteringIntoADisposedRegistryIsRefused() {
        var shapes = new PhysicsShapes();
        shapes.Dispose();

        Assert.True(shapes.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => shapes.Sphere(1f));
    }
}
