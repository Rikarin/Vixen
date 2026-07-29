// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Rendering.DistanceFields.Tests;

public class MeshDistanceFieldBakerTests {
    /// <summary>
    ///     A box mesh <i>is</i> its closed form, so every sample must agree to within float noise.
    /// </summary>
    /// <remarks>
    ///     The strongest check available, and the reason the bake takes exact distances rather than
    ///     propagating approximate ones: there is no tessellation error to hide behind. A sweep-based
    ///     field would fail this by millimetres, which is exactly the size of error that is invisible
    ///     until a tracer steps through a wall.
    /// </remarks>
    [Fact]
    public void ABoxMatchesItsClosedFormEverywhere() {
        var half = new Vector3(0.5f, 0.4f, 0.3f);
        var (vertices, indices) = Shapes.Box(half);
        var field = MeshDistanceFieldBaker.Bake(vertices, indices, new() { Resolution = 24 });

        for (var z = 0; z < field.Resolution.Z; z++) {
            for (var y = 0; y < field.Resolution.Y; y++) {
                for (var x = 0; x < field.Resolution.X; x++) {
                    var expected = Shapes.BoxDistance(field.PositionOf(x, y, z), half);

                    Assert.Equal(expected, field[x, y, z], 3);
                }
            }
        }
    }

    [Fact]
    public void ASphereMatchesItsClosedFormToItsTessellation() {
        var (vertices, indices) = Shapes.Sphere(1f, 32, 64);
        var field = MeshDistanceFieldBaker.Bake(vertices, indices, new() { Resolution = 20 });

        for (var z = 0; z < field.Resolution.Z; z++) {
            for (var y = 0; y < field.Resolution.Y; y++) {
                for (var x = 0; x < field.Resolution.X; x++) {
                    var position = field.PositionOf(x, y, z);
                    var expected = Shapes.SphereDistance(position, 1f);

                    // An inscribed polyhedron's sagitta at this tessellation is under two
                    // thousandths of the radius; the tolerance is that, not a fudge factor.
                    Assert.InRange(field[x, y, z], expected - 0.005f, expected + 0.005f);
                }
            }
        }
    }

    [Fact]
    public void TheInsideIsNegativeAndTheOutsideIsPositive() {
        var half = new Vector3(0.5f);
        var (vertices, indices) = Shapes.Box(half);
        var field = MeshDistanceFieldBaker.Bake(vertices, indices, new() { Resolution = 24 });

        var centre = field.Sample(Vector3.Zero);

        Assert.True(centre < 0, "the centre of a solid box reads as outside it");
        Assert.True(field.Sample(new(0.55f, 0, 0)) > 0, "a point beyond a face reads as inside");

        // The centre is exactly −0.5 and the field says a little less, which is not an error but the
        // documented behaviour of the representation: the box's centre is a peak of |distance|, no
        // grid point lands on it, and a trilinear interpolation of a peak reads under it. The bound
        // is one cell diagonal, and the direction of the miss is the one a tracer can survive.
        Assert.InRange(centre, -0.5f, -0.5f + field.CellSize.Length());
        Assert.Equal(-0.5f, field[field.Resolution.X / 2, field.Resolution.Y / 2, field.Resolution.Z / 2], 1);
    }

    /// <summary>The case a parity test inverts.</summary>
    [Fact]
    public void AMeshWithAMissingFaceStillBakesAsSolid() {
        var half = new Vector3(0.5f);
        var (vertices, indices) = Shapes.OpenBox(half);
        var field = MeshDistanceFieldBaker.Bake(vertices, indices, new() { Resolution = 24 });

        // Low inside the box, where the hole in the ceiling covers a small part of the sky.
        Assert.True(field.Sample(new(0, -0.3f, 0)) < 0, "a point inside an open box reads as outside it");

        // And the outside has not been dragged inwards with it.
        Assert.True(field.Sample(new(0, -0.3f, 0.9f)) > 0, "a point outside an open box reads as inside it");
    }

    [Fact]
    public void TheBackfaceThresholdIsWhatDecidesAnOpenShell() {
        // One quad, two units across, facing +Z. A point 0.3 behind it sees backface over about
        // thirty-seven per cent of the sphere — the solid angle the quad subtends from there — so
        // the threshold is not a tuning knob but the literal question "how much of the sky has to be
        // ceiling before this counts as indoors".
        Vector3[] vertices = [new(-1, -1, 0), new(1, -1, 0), new(1, 1, 0), new(-1, 1, 0)];
        int[] indices = [0, 1, 2, 0, 2, 3];

        var behind = new Vector3(0, 0, -0.3f);

        var byDefault = MeshDistanceFieldBaker.Bake(
            vertices,
            indices,
            new() { Resolution = 12, SignRayCount = 64 }
        );

        var lenient = MeshDistanceFieldBaker.Bake(
            vertices,
            indices,
            new() { Resolution = 12, SignRayCount = 64, BackfaceThreshold = 0.25f }
        );

        Assert.True(byDefault.Sample(behind) > 0, "a lone quad is solid at a half");
        Assert.True(lenient.Sample(behind) < 0, "a lone quad is hollow at a quarter");
    }

    [Fact]
    public void TwoBakesOfOneMeshAreByteIdentical() {
        var (vertices, indices) = Shapes.Sphere(1f, 16, 24);
        var first = MeshDistanceFieldBaker.Bake(vertices, indices, new() { Resolution = 16 });
        var second = MeshDistanceFieldBaker.Bake(vertices, indices, new() { Resolution = 16 });

        Assert.Equal(first.Distances, second.Distances);
    }

    /// <summary>
    ///     Samples do not read each other, so how the work is split cannot change what any of them
    ///     computes. Asserted rather than claimed.
    /// </summary>
    [Fact]
    public void AParallelBakeIsIdenticalToASerialOne() {
        var (vertices, indices) = Shapes.Sphere(1f, 16, 24);
        var parallel = MeshDistanceFieldBaker.Bake(vertices, indices, new() { Resolution = 16 });
        var serial = MeshDistanceFieldBaker.Bake(
            vertices,
            indices,
            new() { Resolution = 16, Parallel = false }
        );

        Assert.Equal(parallel.Distances, serial.Distances);
    }

    [Fact]
    public void ResolutionFollowsTheBoundsRatherThanBeingCubic() {
        var (vertices, indices) = Shapes.Box(new(2f, 0.25f, 0.25f));
        var field = MeshDistanceFieldBaker.Bake(vertices, indices, new() { Resolution = 32 });

        Assert.Equal(32, field.Resolution.X);
        Assert.True(field.Resolution.Y < field.Resolution.X);
        Assert.Equal(field.Resolution.Y, field.Resolution.Z);

        // Near-cubic is the point of doing it at all: no axis's cell is twice another's.
        var cell = field.CellSize;
        var longest = MathF.Max(cell.X, MathF.Max(cell.Y, cell.Z));
        var shortest = MathF.Min(cell.X, MathF.Min(cell.Y, cell.Z));

        Assert.True(longest / shortest < 2f, $"cells are {cell}, which is not near-cubic");
    }

    [Fact]
    public void AFlatMeshStillGetsAVolumeToBeSampledIn() {
        // A ground plane has no extent along Y at all, and a fraction of nothing is nothing.
        Vector3[] vertices = [new(-1, 0, -1), new(1, 0, -1), new(1, 0, 1), new(-1, 0, 1)];
        int[] indices = [0, 2, 1, 0, 3, 2];

        var field = MeshDistanceFieldBaker.Bake(vertices, indices, new() { Resolution = 8 });

        Assert.False(field.Bounds.IsEmpty);
        Assert.True(field.Bounds.Size.Y > 0);
        field.Validate();
    }

    [Fact]
    public void BakingNothingIsRejected() =>
        Assert.Throws<ArgumentException>(() => MeshDistanceFieldBaker.Bake([], [], new()));

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void ASettingOutOfRangeIsRejected(int resolution) {
        var (vertices, indices) = Shapes.Box(new(0.5f));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => MeshDistanceFieldBaker.Bake(vertices, indices, new() { Resolution = resolution })
        );
    }
}
