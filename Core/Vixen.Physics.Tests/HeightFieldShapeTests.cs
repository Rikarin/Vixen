// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Physics.Bodies;
using Vixen.Physics.Shapes;
using Xunit;

namespace Vixen.Physics.Tests;

/// <summary>
///     The height-field collider — [docs/plan/31 § B1].
/// </summary>
/// <remarks>
///     The load-bearing test in here is <see cref="ARaycastHitsTheHeightTheSampleAsked" />. Every
///     other one checks that a bad grid is refused; that one checks the sample-to-world mapping, which
///     is the thing that is wrong by half a spacing in every implementation of this that has ever been
///     written, and which presents as a character standing slightly inside the ground rather than as
///     anything that looks like a collision bug.
/// </remarks>
public sealed class HeightFieldShapeTests {
    /// <summary>A flat grid at one height.</summary>
    static float[] Flat(int sampleCount, float height) {
        var heights = new float[sampleCount * sampleCount];
        Array.Fill(heights, height);
        return heights;
    }

    /// <summary>A grid that ramps along X, so the height at a sample is its X index.</summary>
    static float[] RampAlongX(int sampleCount) {
        var heights = new float[sampleCount * sampleCount];

        for (var z = 0; z < sampleCount; z++) {
            for (var x = 0; x < sampleCount; x++) {
                heights[(z * sampleCount) + x] = x;
            }
        }

        return heights;
    }

    [Fact]
    public void AHeightFieldRemembersItsSamplesAndItsPlacement() {
        using var shapes = new PhysicsShapes();

        var heights = RampAlongX(8);
        var id = shapes.HeightField(heights, 8, new(10f, 20f, 30f), new(2f, 0.5f, 2f));

        Assert.Equal(ShapeKind.HeightField, shapes.Describe(id).Kind);
        Assert.True(heights.AsSpan().SequenceEqual(shapes.HeightsOf(id)));

        var placement = shapes.HeightFieldOf(id);
        Assert.Equal(8, placement.SampleCount);
        Assert.Equal(7, placement.QuadCount);
        Assert.Equal(new(10f, 20f, 30f), placement.Offset);
        Assert.Equal(new(2f, 0.5f, 2f), placement.Scale);
    }

    [Fact]
    public void TheSamplesAreCopiedSoSculptingTheSourceDoesNotMoveTheCollider() {
        using var shapes = new PhysicsShapes();

        var heights = Flat(4, 1f);
        var id = shapes.HeightField(heights, 4, Vector3.Zero, Vector3.One);

        heights[5] = 99f;

        Assert.Equal(1f, shapes.HeightsOf(id)[5]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(129)]
    public void ASampleCountJoltCannotAccelerateIsRefused(int sampleCount) {
        using var shapes = new PhysicsShapes();

        // 129 is in here on purpose: a tile of 128 quads is 129 samples, and reaching for the round
        // number is exactly how somebody trips Jolt's constraint in a build whose asserts are gone.
        // 2 is in here because it is a power of two and still too small — one block, and the quadtree
        // over the blocks needs two.
        var heights = new float[Math.Max(sampleCount * sampleCount, 1)];

        Assert.Throws<PhysicsShapeException>(() => shapes.HeightField(heights, sampleCount, Vector3.Zero, Vector3.One));
    }

    [Fact]
    public void ASampleArrayOfTheWrongLengthIsRefused() {
        using var shapes = new PhysicsShapes();

        Assert.Throws<PhysicsShapeException>(() => shapes.HeightField(new float[15], 4, Vector3.Zero, Vector3.One));
        Assert.Throws<PhysicsShapeException>(() => shapes.HeightField(new float[17], 4, Vector3.Zero, Vector3.One));
    }

    [Theory]
    [InlineData(0f, 1f, 1f)]
    [InlineData(-1f, 1f, 1f)]
    [InlineData(1f, 0f, 1f)]
    [InlineData(1f, float.NaN, 1f)]
    [InlineData(1f, 1f, 0f)]
    [InlineData(1f, 1f, float.PositiveInfinity)]
    public void AScaleThatWouldDegenerateTheGridIsRefused(float x, float y, float z) {
        using var shapes = new PhysicsShapes();

        Assert.Throws<PhysicsShapeException>(
            () => shapes.HeightField(Flat(4, 0f), 4, Vector3.Zero, new(x, y, z))
        );
    }

    [Fact]
    public void ANonFiniteSampleIsRefusedButAHoleIsNot() {
        using var shapes = new PhysicsShapes();

        var broken = Flat(4, 0f);
        broken[3] = float.NaN;
        Assert.Throws<PhysicsShapeException>(() => shapes.HeightField(broken, 4, Vector3.Zero, Vector3.One));

        var holed = Flat(4, 0f);
        holed[3] = PhysicsShapes.NoCollisionHeight;
        var id = shapes.HeightField(holed, 4, Vector3.Zero, Vector3.One);
        Assert.Equal(PhysicsShapes.NoCollisionHeight, shapes.HeightsOf(id)[3]);
    }

    [Fact]
    public void AGridThatIsAllHolesIsRefusedWithAMessageThatSaysSo() {
        using var shapes = new PhysicsShapes();

        var heights = Flat(4, PhysicsShapes.NoCollisionHeight);
        var thrown = Assert.Throws<PhysicsShapeException>(
            () => shapes.HeightField(heights, 4, Vector3.Zero, Vector3.One)
        );

        Assert.Contains(nameof(PhysicsShapes.NoCollisionHeight), thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHeightFieldCannotBeDynamic() {
        using var world = new PhysicsWorld();

        var id = world.Shapes.HeightField(Flat(4, 0f), 4, Vector3.Zero, Vector3.One);
        Assert.False(world.Shapes.Describe(id).CanBeDynamic);

        Assert.ThrowsAny<Exception>(
            () => world.CreateBody(BodyDescription.Create() with {
                Shape = id, Motion = BodyMotion.Dynamic
            })
        );
    }

    [Fact]
    public void NothingIsBuiltUntilABodyAsksForIt() {
        using var world = new PhysicsWorld();

        var id = world.Shapes.HeightField(Flat(8, 0f), 8, Vector3.Zero, Vector3.One);
        Assert.Equal(0, world.Shapes.BuiltCount);

        world.CreateBody(BodyDescription.Static(id, Vector3.Zero));
        Assert.Equal(1, world.Shapes.BuiltCount);
    }

    [Theory]
    [InlineData(4, 2)]
    [InlineData(8, 4)]
    [InlineData(16, 8)]
    [InlineData(32, 8)]
    [InlineData(256, 8)]
    public void TheBlockSizeIsTheLargestJoltAllowsAndAlwaysLeavesTwoBlocks(int sampleCount, int expected) {
        var block = PhysicsShapes.HeightFieldBlockSize(sampleCount);

        Assert.Equal(expected, block);
        Assert.Equal(0, sampleCount % block);

        // The two constraints Jolt reports by returning nothing: at least two blocks along each axis,
        // and a block count that is a power of two.
        var blocks = sampleCount / block;
        Assert.True(blocks >= 2, $"{sampleCount} samples in blocks of {block} is {blocks} block(s).");
        Assert.Equal(0, blocks & (blocks - 1));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void EveryGridTheRegistryAcceptsIsOneJoltActuallyBuilds(int sampleCount) {
        // The pair that matters: the validation above says which grids are legal, and this says the
        // legal ones build. Without it, tightening one without the other passes both halves.
        using var world = new PhysicsWorld();

        var id = world.Shapes.HeightField(Flat(sampleCount, 1f), sampleCount, Vector3.Zero, Vector3.One);
        world.CreateBody(BodyDescription.Static(id, Vector3.Zero));

        Assert.Equal(1, world.Shapes.BuiltCount);
    }

    [Fact]
    public void ARaycastHitsTheHeightTheSampleAsked() {
        using var world = new PhysicsWorld();

        // Heights ramp with the X index and the Y scale is 3, so the surface at sample x sits at
        // 3x. Spacing is 2 in X and Z, so sample x is at world x = 2x. Casting straight down over
        // sample 3 must therefore land at (6, 9, ·) — and would land at 6 or 12 if the mapping
        // confused the height scale with the spacing, which is the mistake this is here to catch.
        var id = world.Shapes.HeightField(RampAlongX(8), 8, Vector3.Zero, new(2f, 3f, 2f));
        world.CreateBody(BodyDescription.Static(id, Vector3.Zero));

        var hit = world.Raycast(new(6f, 100f, 6f), -Vector3.UnitY, 200f, out var where);

        Assert.True(hit);

        // ⚠ Not exact, and the difference is the shape's own compression rather than the mapping.
        // Jolt stores eight bits per sample against its *block's* range: blocks are four samples
        // across here, so a block spans three height units — nine metres after the Y scale — and one
        // quantum is 9 / 255 ≈ 3.5 cm. The bound is stated rather than absorbed into a loose
        // tolerance, because a mapping error would be metres and would still pass a loose one.
        Assert.InRange(where.Position.Y, 9f - 0.05f, 9f + 0.05f);
        Assert.True(where.Normal.Y > 0f, "The ground's normal points up.");
    }

    [Fact]
    public void AFlatFieldIsExactBecauseItsBlocksHaveNoRangeToQuantise() {
        // The other half of the compression story, and the one that says the residual above is
        // quantisation and not bias: with every sample in a block equal, eight bits are eight bits
        // about nothing and the surface is where it was asked to be.
        using var world = new PhysicsWorld();

        var id = world.Shapes.HeightField(Flat(16, 4f), 16, Vector3.Zero, new(1f, 2.5f, 1f));
        world.CreateBody(BodyDescription.Static(id, Vector3.Zero));

        Assert.True(world.Raycast(new(7.5f, 100f, 7.5f), -Vector3.UnitY, 200f, out var where));
        Assert.Equal(10f, where.Position.Y, 4);
    }

    [Fact]
    public void TheOffsetMovesTheWholeGrid() {
        using var world = new PhysicsWorld();

        var id = world.Shapes.HeightField(Flat(8, 2f), 8, new(100f, 5f, 100f), Vector3.One);
        world.CreateBody(BodyDescription.Static(id, Vector3.Zero));

        Assert.True(world.Raycast(new(103f, 100f, 103f), -Vector3.UnitY, 200f, out var where));
        Assert.Equal(7f, where.Position.Y, 3);

        // And nothing is left behind at the origin.
        Assert.False(world.Raycast(new(3f, 100f, 3f), -Vector3.UnitY, 200f, out _));
    }

    [Fact]
    public void AHoleIsAHoleToARaycast() {
        using var world = new PhysicsWorld();

        var heights = Flat(8, 0f);

        // A hole sample kills the quads that touch it, so the gap around sample (4, 4) is what the
        // ray goes through. Sample (1, 1) stays solid and is the control.
        heights[(4 * 8) + 4] = PhysicsShapes.NoCollisionHeight;

        var id = world.Shapes.HeightField(heights, 8, Vector3.Zero, Vector3.One);
        world.CreateBody(BodyDescription.Static(id, Vector3.Zero));

        Assert.True(world.Raycast(new(1.5f, 100f, 1.5f), -Vector3.UnitY, 200f, out _));
        Assert.False(world.Raycast(new(4.5f, 100f, 4.5f), -Vector3.UnitY, 200f, out _));
    }

    [Fact]
    public void PositionOfAndBoundsOfAgreeWithTheDefinition() {
        var placement = new HeightFieldPlacement(4, new(1f, 2f, 3f), new(10f, 0.5f, 20f));

        Assert.Equal(new(21f, 6f, 63f), placement.PositionOf(2, 3, 8f));

        var bounds = placement.BoundsOf(-4f, 10f);
        Assert.Equal(new(1f, 0f, 3f), bounds.Minimum);
        Assert.Equal(new(31f, 7f, 63f), bounds.Maximum);
    }

    [Fact]
    public void ANegativeHeightScaleStillProducesABoundingBoxTheRightWayUp() {
        // Legal, and the reason BoundsOf reduces rather than assuming which corner is the top.
        var placement = new HeightFieldPlacement(4, Vector3.Zero, new(1f, -2f, 1f));
        var bounds = placement.BoundsOf(0f, 10f);

        Assert.Equal(-20f, bounds.Minimum.Y, 4);
        Assert.Equal(0f, bounds.Maximum.Y, 4);
    }
}
