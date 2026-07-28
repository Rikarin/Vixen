// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Vfx;
using Xunit;

namespace Vixen.Vfx.Tests;

/// <summary>
///     The generator the CPU and GPU paths are supposed to agree on.
/// </summary>
/// <remarks>
///     Everything here is a property a compute shader must be able to reproduce. The distribution
///     tests are loose on purpose — a hash is not a statistician's generator and does not need to be —
///     but the exactness tests are not loose at all, because "identical output on the CPU and GPU
///     paths" is Phase 7's exit criterion and a tolerance would not satisfy it.
/// </remarks>
public sealed class VfxRandomTests {
    [Fact]
    public void TheSameQuestionAlwaysGetsTheSameAnswer() {
        for (uint particle = 0; particle < 64; particle++) {
            Assert.Equal(VfxRandom.Value(particle, 7, 3), VfxRandom.Value(particle, 7, 3));
        }
    }

    [Fact]
    public void ValuesAreInTheUnitInterval() {
        for (uint particle = 0; particle < 4_096; particle++) {
            var value = VfxRandom.Value(particle, 1, 0);

            Assert.InRange(value, 0f, 0.99999994f);
        }
    }

    [Fact]
    public void TheSaltSeparatesTwoUsesOnOneParticle() {
        // Without a salt, "random size" and "random lifetime" would be the same number and every
        // large particle would live longest — a correlation that looks like art direction.
        var agreements = 0;

        for (uint particle = 0; particle < 1_024; particle++) {
            if (VfxRandom.Value(particle, 1, 0) == VfxRandom.Value(particle, 1, 1)) {
                agreements++;
            }
        }

        Assert.Equal(0, agreements);
    }

    [Fact]
    public void TheSeedSeparatesTwoInstancesOfOneEffect() {
        var agreements = 0;

        for (uint particle = 0; particle < 1_024; particle++) {
            if (VfxRandom.Value(particle, 1, 0) == VfxRandom.Value(particle, 2, 0)) {
                agreements++;
            }
        }

        Assert.Equal(0, agreements);
    }

    [Fact]
    public void SwappingTheParticleAndTheSeedIsNotTheSameQuestion() {
        // Combining the three by adding would make particle 3 of seed 5 and particle 5 of seed 3 the
        // same draw, and a diagonal stripe of the identifier space would share every value.
        Assert.NotEqual(VfxRandom.Value(3, 5, 0), VfxRandom.Value(5, 3, 0));
    }

    [Fact]
    public void ConsecutiveParticlesDoNotWalkInStep() {
        // The failure this catches is a hash that is really a counter: values that drift smoothly
        // with the identifier look random one at a time and come out as a gradient across a burst.
        var ascending = 0;

        for (uint particle = 0; particle < 1_023; particle++) {
            if (VfxRandom.Value(particle + 1, 1, 0) > VfxRandom.Value(particle, 1, 0)) {
                ascending++;
            }
        }

        Assert.InRange(ascending, 400, 620);
    }

    [Fact]
    public void ValuesSpreadOverTheWholeInterval() {
        Span<int> buckets = stackalloc int[10];

        for (uint particle = 0; particle < 100_000; particle++) {
            buckets[(int)(VfxRandom.Value(particle, 99, 0) * 10)]++;
        }

        foreach (var count in buckets) {
            Assert.InRange(count, 9_000, 11_000);
        }
    }

    [Fact]
    public void DirectionsAreUnitLength() {
        for (uint particle = 0; particle < 2_048; particle++) {
            Assert.Equal(1f, VfxRandom.Direction(particle, 1, 0).Length(), 0.0001f);
        }
    }

    [Fact]
    public void DirectionsDoNotPileUpAtThePoles() {
        // Picking two angles uniformly and converting spherically clusters points at the poles. Over
        // a sphere sampled properly, the fraction with |y| above a half is exactly a half.
        var polar = 0;

        for (uint particle = 0; particle < 20_000; particle++) {
            if (MathF.Abs(VfxRandom.Direction(particle, 1, 0).Y) > 0.5f) {
                polar++;
            }
        }

        Assert.InRange(polar, 9_600, 10_400);
    }

    [Fact]
    public void AHashIsExactlyTheseBits() {
        // The values a shader has to reproduce. If the mixing function is ever changed, this fails —
        // which is the point: it is a compatibility break for every stored replay and golden image,
        // not an implementation detail.
        Assert.Equal(0x01fce552u, VfxRandom.Hash(0));
        Assert.Equal(0x9f505634u, VfxRandom.Hash(1));
        Assert.Equal(0xa4f7896cu, VfxRandom.Hash(0xffffffff));
    }

    [Fact]
    public void ZeroIsNotAFixedPoint() {
        // A mixer built only from xor-shifts and multiplies maps zero to zero at every step, so
        // particle zero of seed zero would draw zero for ever — the first particle of an effect would
        // be the one that looked wrong. The offset before mixing is what removes it.
        Assert.NotEqual(0u, VfxRandom.Hash(0));
        Assert.NotEqual(0u, VfxRandom.Hash(0, 0, 0));
        Assert.NotEqual(0f, VfxRandom.Value(0, 0, 0));
    }
}
