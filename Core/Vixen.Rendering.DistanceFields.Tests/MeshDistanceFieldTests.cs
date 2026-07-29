// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Rendering.DistanceFields.Tests;

public class MeshDistanceFieldTests {
    [Fact]
    public void TheFirstAndLastSamplesAreOnTheBoundsThemselves() {
        var field = Bake();
        var last = field.Resolution;

        Assert.Equal(field.Bounds.Minimum.X, field.PositionOf(0, 0, 0).X, 5);
        Assert.Equal(field.Bounds.Minimum.Y, field.PositionOf(0, 0, 0).Y, 5);
        Assert.Equal(field.Bounds.Minimum.Z, field.PositionOf(0, 0, 0).Z, 5);

        var corner = field.PositionOf(last.X - 1, last.Y - 1, last.Z - 1);

        Assert.Equal(field.Bounds.Maximum.X, corner.X, 5);
        Assert.Equal(field.Bounds.Maximum.Y, corner.Y, 5);
        Assert.Equal(field.Bounds.Maximum.Z, corner.Z, 5);
    }

    /// <summary>
    ///     What "samples sit on grid points" has to mean if it means anything: interpolating at a
    ///     grid point returns that grid point.
    /// </summary>
    [Fact]
    public void SamplingAtAGridPointReturnsThatSample() {
        var field = Bake();

        for (var z = 0; z < field.Resolution.Z; z++) {
            for (var y = 0; y < field.Resolution.Y; y++) {
                for (var x = 0; x < field.Resolution.X; x++) {
                    Assert.Equal(field[x, y, z], field.Sample(field.PositionOf(x, y, z)), 4);
                }
            }
        }
    }

    [Fact]
    public void SamplingHalfwayBetweenTwoSamplesIsTheirMean() {
        var field = Bake();
        var here = field.PositionOf(3, 4, 5);
        var next = field.PositionOf(4, 4, 5);
        var expected = (field[3, 4, 5] + field[4, 4, 5]) * 0.5f;

        Assert.Equal(expected, field.Sample((here + next) * 0.5f), 4);
    }

    /// <summary>
    ///     Outside the box the field is clamped rather than extrapolated, which under-reports the
    ///     distance. That is the safe direction: a step too short costs an iteration, a step too long
    ///     costs a missed surface.
    /// </summary>
    [Fact]
    public void SamplingOutsideTheBoundsClampsRatherThanExtrapolating() {
        var field = Bake();
        var far = field.Bounds.Maximum + new Vector3(100f);

        Assert.Equal(field.Sample(field.Bounds.Maximum), field.Sample(far), 4);
    }

    [Fact]
    public void TheGradientPointsAwayFromTheSurface() {
        var (vertices, indices) = Shapes.Sphere(1f, 32, 64);
        var field = MeshDistanceFieldBaker.Bake(vertices, indices, new() { Resolution = 24 });

        foreach (var direction in (Vector3[]) [
            new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), Vector3.Normalize(new(1, 1, 1))
        ]) {
            var gradient = field.SampleGradient(direction * 1.1f);

            // A distance field's gradient is the outward normal wherever it is not flat, and on a
            // sphere the outward normal is the direction itself.
            Assert.True(
                Vector3.Dot(gradient, direction) > 0.95f,
                $"the gradient at {direction * 1.1f} was {gradient}"
            );
        }
    }

    [Fact]
    public void AFieldThatIsNotOneIsRejected() {
        Assert.Throws<InvalidOperationException>(
            () => new MeshDistanceField(new(new(-1), new(1)), new(1, 4, 4), new float[16]).Validate()
        );

        Assert.Throws<InvalidOperationException>(
            () => new MeshDistanceField(new(new(-1), new(1)), new(4, 4, 4), new float[16]).Validate()
        );
    }

    [Fact]
    public void AWellFormedFieldValidates() => Bake().Validate();

    static MeshDistanceField Bake() {
        var (vertices, indices) = Shapes.Box(new(0.5f, 0.4f, 0.3f));

        return MeshDistanceFieldBaker.Bake(vertices, indices, new() { Resolution = 16 });
    }
}
