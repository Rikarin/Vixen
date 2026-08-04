// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Water;
using Xunit;

namespace Vixen.Water.Tests;

/// <summary>
///     The arithmetic the seam's stated tolerance rests on — [docs/plan/35 § Risks].
/// </summary>
/// <remarks>
///     Exact float agreement between a C# evaluator and a SPIR-V one is a real claim, and
///     <c>sin</c>/<c>cos</c> do not support it: Vulkan allows <c>OpSin</c> 8192 ULP over the useful
///     range and a driver may use whatever its special-function unit implements. So the wave sum goes
///     through a stated polynomial instead, and what is asserted here is that the polynomial is
///     accurate enough to be worth using and that its range reduction does not fall over — because a
///     reduction that is one π out is a sign flip, not a rounding error.
/// </remarks>
public sealed class WaterMathTests {
    /// <summary>The polynomial tracks the intrinsic across the whole reduced range.</summary>
    /// <remarks>
    ///     ⚠ Accuracy is not the point — being <em>the same on both hosts</em> is. But a polynomial
    ///     that had drifted would make the water visibly wrong rather than merely different, and this
    ///     is what says it has not.
    /// </remarks>
    [Fact]
    public void ThePolynomialAgreesWithTheIntrinsicAcrossOneCycle() {
        for (var step = -720; step <= 720; step++) {
            var angle = step * (MathF.PI / 360f);

            WaterMath.SinCos(angle, out var sin, out var cos);

            Assert.Equal(MathF.Sin(angle), sin, 1e-6f);
            Assert.Equal(MathF.Cos(angle), cos, 1e-6f);
        }
    }

    /// <summary>
    ///     ⚠ And it still tracks after a thousand radians, which is where a one-part reduction fails.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A wave's phase is <c>k·x + ω·t</c>, and both terms grow without bound — twenty minutes
    ///         of water time at a plausible frequency is several thousand radians. A reduction that
    ///         loses a bit of the argument per doubling puts a wave visibly out of step with itself by
    ///         the end of a session, which is a bug that only reproduces after somebody has been
    ///         playing for a while.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The stated bound is a thousandth, and it grows with the argument.</b> Two-part
    ///         Cody–Waite carries about 48 bits of π, so a phase of a hundred thousand radians spends
    ///         seventeen of them and the answer is worth what is left. That is a real limit and it is
    ///         written down here rather than discovered: what matters is that both hosts lose the same
    ///         bits, not that neither does.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheReductionSurvivesALargeArgument() {
        foreach (var angle in new[] { 1_000f, -1_000f, 12_345.6f, -98_765.4f }) {
            WaterMath.SinCos(angle, out var sin, out var cos);

            Assert.Equal(MathF.Sin(angle), sin, 1e-3f);
            Assert.Equal(MathF.Cos(angle), cos, 1e-3f);
        }
    }

    /// <summary>The reduced argument lands inside the range the polynomial was fitted on.</summary>
    [Fact]
    public void TheReductionLandsInsideTheFittedRange() {
        Gen.Float[-10_000f, 10_000f]
            .Sample(
                angle => {
                    var reduced = WaterMath.Reduce(angle, out _);

                    Assert.InRange(reduced, -MathF.PI - 1e-3f, MathF.PI + 1e-3f);
                },
                iter: 10_000
            );
    }

    /// <summary>
    ///     ⚠ It is symmetric about zero, which a truncating reduction would not be.
    /// </summary>
    /// <remarks>
    ///     A cast truncates toward zero and rounding does not, so a body at a negative coordinate
    ///     would reduce differently from its mirror image — and the sea would be visibly asymmetric
    ///     about the world origin, which is a thing nobody looks for and everybody eventually sees.
    /// </remarks>
    [Fact]
    public void SineIsOddAndCosineIsEvenAfterReduction() {
        Gen.Float[-2_000f, 2_000f]
            .Sample(
                angle => {
                    WaterMath.SinCos(angle, out var sin, out var cos);
                    WaterMath.SinCos(-angle, out var mirrored, out var mirroredCos);

                    // Exact, not close: the reduction of −x is the negation of the reduction of x by
                    // construction, so every operation downstream sees the same magnitudes.
                    Assert.Equal(-sin, mirrored);
                    Assert.Equal(cos, mirroredCos);
                },
                iter: 10_000
            );
    }

    /// <summary>The hash is integer arithmetic, so it is the same wherever there is a 32-bit word.</summary>
    /// <remarks>
    ///     Baked constants rather than a comparison against a reference implementation, because the
    ///     property being asserted is cross-host stability and the only way to state that in one
    ///     process is to write down what the answer has to be.
    /// </remarks>
    [Fact]
    public void TheHashIsTheSameEverywhere() {
        Assert.Equal(0x6D523710u, WaterMath.Hash(1u, 0));
        Assert.Equal(0x2C4486D5u, WaterMath.Hash(1u, 1));
        Assert.Equal(0x1C08C326u, WaterMath.Hash(5u, 5));
    }

    /// <summary>
    ///     ⚠ Two spectra do not share a wave where their seed happens to equal its index.
    /// </summary>
    /// <remarks>
    ///     The mixer is a bijection with a fixed point at zero, so combining the seed and the index by
    ///     XOR — which is what the foliage scatter does, harmlessly — would hand it zero along that
    ///     whole diagonal. In a scatter that is one shrub in the same place; here it is two seas that
    ///     are meant to be different agreeing about their longest swell, which is exactly the kind of
    ///     thing somebody notices as "these two lakes look identical" and cannot explain.
    /// </remarks>
    [Fact]
    public void TheSeedAndTheIndexDoNotCollideOnTheDiagonal() {
        for (var value = 1u; value < 64u; value++) {
            Assert.NotEqual(WaterMath.Hash(0u, 0), WaterMath.Hash(value, (int)value));
        }
    }

    /// <summary>Its draws are in range and its streams are independent.</summary>
    /// <remarks>
    ///     ⚠ Independence matters concretely: a wave whose phase is correlated with its own direction
    ///     gives a swell that reads as a repeating corduroy rather than as a sea, and that is what
    ///     slicing one hash into fields would produce.
    /// </remarks>
    [Fact]
    public void EachStreamIsItsOwnDraw() {
        var first = new List<float>();
        var second = new List<float>();

        for (var index = 0; index < 512; index++) {
            var hash = WaterMath.Hash(7u, index);

            var a = WaterMath.Unit(hash, 1);
            var b = WaterMath.Unit(hash, 2);

            Assert.InRange(a, 0f, 1f);
            Assert.InRange(b, 0f, 1f);

            first.Add(a);
            second.Add(b);
        }

        // Uncorrelated to a loose bound: what is being caught is a slicing bug, which correlates the
        // two almost perfectly, not a statistical subtlety.
        Assert.InRange(MathF.Abs(Correlation(first, second)), 0f, 0.15f);
    }

    /// <summary>A degenerate smooth step is a hard edge rather than a NaN.</summary>
    /// <remarks>
    ///     ⚠ A NaN here propagates into the surface height and from there into a rigid body's
    ///     position, which is a boat that vanishes. An author who left a ramp width at zero should get
    ///     a canal wall.
    /// </remarks>
    [Fact]
    public void AZeroWidthSmoothStepIsAStep() {
        Assert.Equal(0f, WaterMath.SmoothStep(3f, 3f, 2.9f));
        Assert.Equal(1f, WaterMath.SmoothStep(3f, 3f, 3.1f));

        // Inverted edges are the same step, at the lower of the two — an author who typed them the
        // wrong way round gets a hard shoreline rather than an inverted one.
        Assert.Equal(0f, WaterMath.SmoothStep(3f, 1f, 2f));
        Assert.Equal(1f, WaterMath.SmoothStep(3f, 1f, 4f));

        Assert.False(float.IsNaN(WaterMath.SmoothStep(0f, 0f, 0f)));
    }

    static float Correlation(List<float> a, List<float> b) {
        var meanA = a.Sum() / a.Count;
        var meanB = b.Sum() / b.Count;
        float covariance = 0f, varianceA = 0f, varianceB = 0f;

        for (var index = 0; index < a.Count; index++) {
            var da = a[index] - meanA;
            var db = b[index] - meanB;

            covariance += da * db;
            varianceA += da * da;
            varianceB += db * db;
        }

        return covariance / MathF.Sqrt(varianceA * varianceB);
    }
}
