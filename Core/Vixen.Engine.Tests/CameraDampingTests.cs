// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Engine.Cameras;
using Xunit;

namespace Vixen.Engine.Tests;

public sealed class CameraDampingTests {
    [Fact]
    public void ADampingTimeRemovesNinetyNinePercentOfTheError() {
        var result = CameraDamping.Approach(0f, 100f, 0.5f, 0.5f);

        Assert.Equal(99f, result, 3);
    }

    [Fact]
    public void ZeroDampingArrives() => Assert.Equal(100f, CameraDamping.Approach(0f, 100f, 0f, 1f / 60f));

    [Fact]
    public void AZeroLengthStepMovesNothing() =>
        Assert.Equal(0f, CameraDamping.Approach(0f, 100f, 0.5f, 0f));

    /// <summary>
    ///     The property the whole scheme exists for: the same elapsed time leaves the same residual,
    ///     whether it arrived as one step or as many. A fixed lerp factor fails this by a factor of
    ///     five between 30 and 144 Hz.
    /// </summary>
    [Fact]
    public void DampingIsIndependentOfTheFrameRate() {
        var coarse = CameraDamping.Approach(0f, 100f, 0.4f, 0.1f);
        var fine = 0f;

        for (var step = 0; step < 10; step++) {
            fine = CameraDamping.Approach(fine, 100f, 0.4f, 0.01f);
        }

        Assert.Equal(coarse, fine, 3);
    }

    /// <summary>
    ///     And the same for rotation, which is less obvious: a slerp travels the geodesic at constant
    ///     angular speed, so the residual <em>angle</em> composes exactly the way a residual distance
    ///     does. An <c>Nlerp</c> in the same place would not.
    /// </summary>
    [Fact]
    public void RotationalDampingIsIndependentOfTheFrameRate() {
        var target = Quaternion.FromAxisAngle(Vector3.UnitY, MathUtil.PiOverTwo);
        var coarse = CameraDamping.Approach(Quaternion.Identity, target, 0.4f, 0.1f);
        var fine = Quaternion.Identity;

        for (var step = 0; step < 10; step++) {
            fine = CameraDamping.Approach(fine, target, 0.4f, 0.01f);
        }

        Assert.True(Quaternion.NearEqual(coarse, fine, 1e-4f), $"{coarse} against {fine}");
    }

    [Fact]
    public void EachAxisIsDampedOnItsOwnClock() {
        var result = CameraDamping.Approach(
            Vector3.Zero,
            new(100f, 100f, 100f),
            new(0.5f, 0f, 1e9f),
            0.5f
        );

        Assert.Equal(99f, result.X, 3);
        Assert.Equal(100f, result.Y, 3);
        Assert.Equal(0f, result.Z, 3);
    }

    [Fact]
    public void DecayIsApproachWrittenAsAResidual() {
        var decayed = CameraDamping.Decay(new(10f, 0f, 0f), new(0.5f, 0.5f, 0.5f), 0.5f);

        Assert.Equal(0.1f, decayed.X, 3);
    }
}
