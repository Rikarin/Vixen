// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Rendering.DistanceFields.Tests;

public class DistanceFieldInstanceTests {
    static MeshDistanceField UnitSphere() {
        var (vertices, indices) = Shapes.Sphere(1f, 32, 64);

        return MeshDistanceFieldBaker.Bake(vertices, indices, new() { Resolution = 24 });
    }

    [Fact]
    public void MovingAFieldMovesWhatItMeasures() {
        var instance = DistanceFieldInstance.At(UnitSphere(), new(10, 0, 0));

        Assert.True(instance.Sample(new(10, 0, 0)) < 0, "the centre of a moved sphere is outside it");
        Assert.Equal(0f, instance.Sample(new(11, 0, 0)), 1);
        Assert.True(instance.Sample(Vector3.Zero) > 0, "the old position is still inside");
    }

    /// <summary>
    ///     A rotated distance is the same distance, which is exactly why a field survives rotation
    ///     and does not survive a non-uniform scale.
    /// </summary>
    [Fact]
    public void TurningAFieldDoesNotChangeADistance() {
        var field = UnitSphere();
        var upright = DistanceFieldInstance.At(field, Vector3.Zero);

        var turned = new DistanceFieldInstance(
            field,
            Vector3.Zero,
            Quaternion.FromAxisAngle(Vector3.Normalize(new(1, 2, 3)), 0.7f),
            1f
        );

        // A tolerance rather than a decimal count, and the tolerance is the field's own
        // reconstruction error: the turned instance reads the same field at a different local point,
        // so the two disagree by however much a trilinear interpolation disagrees with itself
        // between grid points. The claim is about the distance, not about the sampling.
        foreach (var probe in (Vector3[]) [new(1.3f, 0, 0), new(0, 0.5f, 0), new(-0.9f, 0.4f, 0.2f)]) {
            Assert.Equal(upright.Sample(probe), turned.Sample(probe), field.CellSize.Length());
        }
    }

    [Fact]
    public void ScalingAFieldScalesEveryDistanceWithIt() {
        var field = UnitSphere();
        var unit = DistanceFieldInstance.At(field, Vector3.Zero);
        var tripled = new DistanceFieldInstance(field, Vector3.Zero, Quaternion.Identity, 3f);

        // The surface is where it should be...
        Assert.Equal(0f, tripled.Sample(new(3f, 0, 0)), 1);

        // ...and so is everything else: a point three times as far out reads three times the
        // distance. Dropping the multiply on the way out is the classic error and it is invisible
        // at a scale of one.
        foreach (var probe in (Vector3[]) [new(1.3f, 0, 0), new(0, 0.5f, 0), new(-0.9f, 0.4f, 0.2f)]) {
            Assert.Equal(unit.Sample(probe) * 3f, tripled.Sample(probe * 3f), 0.001f);
        }
    }

    [Fact]
    public void WorldBoundsFollowThePlacement() {
        var field = UnitSphere();
        var moved = new DistanceFieldInstance(field, new(5, 0, 0), Quaternion.Identity, 2f);
        var bounds = moved.WorldBounds;

        Assert.Equal(5f, bounds.Center.X, 3);
        Assert.Equal(field.Bounds.Size.X * 2f, bounds.Size.X, 3);
        Assert.True(bounds.Contains(new Vector3(5, 0, 0)));
    }

    [Fact]
    public void ARotatedBoxBoundIsTheAxisAlignedOneAroundIt() {
        var field = UnitSphere();

        var turned = new DistanceFieldInstance(
            field,
            Vector3.Zero,
            Quaternion.FromAxisAngle(Vector3.UnitY, MathF.PI / 4f),
            1f
        );

        // Turning a cube by 45° about Y widens its axis-aligned bound by √2 across the turned axes
        // and leaves the third alone. The slack is real and is why the type documents it.
        Assert.Equal(field.Bounds.Size.X * MathF.Sqrt(2f), turned.WorldBounds.Size.X, 3);
        Assert.Equal(field.Bounds.Size.Y, turned.WorldBounds.Size.Y, 3);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void AScaleThatIsNotAScaleIsRejected(float scale) {
        var instance = new DistanceFieldInstance(UnitSphere(), Vector3.Zero, Quaternion.Identity, scale);

        Assert.Throws<InvalidOperationException>(instance.Validate);
    }

    [Fact]
    public void AnInstanceOfNothingIsRejected() =>
        Assert.Throws<InvalidOperationException>(
            new DistanceFieldInstance(null!, Vector3.Zero, Quaternion.Identity, 1f).Validate
        );
}
