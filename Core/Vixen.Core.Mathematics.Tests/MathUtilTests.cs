// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Core.Mathematics.Tests;

/// <summary>
///     The scalar helpers. Most of these are one line of arithmetic; what is worth pinning is the
///     behaviour at the edges, which is where the subtly-different rewrites in each subsystem would
///     otherwise disagree.
/// </summary>
public class MathUtilTests {
    [Fact]
    public void Degrees_and_radians_round_trip() {
        Assert.Equal(MathUtil.Pi, MathUtil.DegreesToRadians(180f), 5);
        Assert.Equal(180f, MathUtil.RadiansToDegrees(MathUtil.Pi), 4);
        Assert.Equal(90f, MathUtil.RadiansToDegrees(MathUtil.DegreesToRadians(90f)), 4);
    }

    [Fact]
    public void NearEqual_is_absolute_near_zero_and_relative_far_from_it() {
        // Near zero a fixed epsilon is right.
        Assert.True(MathUtil.NearEqual(0f, 1e-7f));
        Assert.False(MathUtil.NearEqual(0f, 1e-4f));

        // Out where float steps in units of thousands, the same fixed epsilon would call nothing
        // equal — including a number and itself plus one ulp. This is the case a fixed epsilon gets
        // wrong and the reason the tolerance scales.
        Assert.True(MathUtil.NearEqual(1e7f, 1e7f + 1f));
        Assert.False(MathUtil.NearEqual(1e7f, 1.1e7f));
    }

    [Fact]
    public void NearEqual_handles_the_values_that_break_naive_comparisons() {
        Assert.True(MathUtil.NearEqual(float.PositiveInfinity, float.PositiveInfinity));
        Assert.False(MathUtil.NearEqual(float.NaN, float.NaN));
        Assert.False(MathUtil.NearEqual(float.PositiveInfinity, float.NegativeInfinity));
        Assert.True(MathUtil.NearEqual(-0f, 0f));
    }

    [Fact]
    public void Clamp_and_Saturate_constrain_to_their_intervals() {
        Assert.Equal(5f, MathUtil.Clamp(10f, 0f, 5f));
        Assert.Equal(0f, MathUtil.Clamp(-10f, 0f, 5f));
        Assert.Equal(3f, MathUtil.Clamp(3f, 0f, 5f));
        Assert.Equal(1f, MathUtil.Saturate(2f));
        Assert.Equal(0f, MathUtil.Saturate(-2f));
        Assert.Equal(7, MathUtil.Clamp(9, 1, 7));
    }

    [Fact]
    public void Lerp_is_exact_at_zero_and_inverts() {
        Assert.Equal(10f, MathUtil.Lerp(10f, 20f, 0f));
        Assert.Equal(20f, MathUtil.Lerp(10f, 20f, 1f));
        Assert.Equal(15f, MathUtil.Lerp(10f, 20f, 0.5f));

        // Not clamped: extrapolation is sometimes what a caller wants.
        Assert.Equal(30f, MathUtil.Lerp(10f, 20f, 2f));

        Assert.Equal(0.5f, MathUtil.InverseLerp(10f, 20f, 15f), 5);
        Assert.Equal(0f, MathUtil.InverseLerp(10f, 10f, 15f));
    }

    [Fact]
    public void The_smooth_steps_are_flat_at_both_ends() {
        Assert.Equal(0f, MathUtil.SmoothStep(0f));
        Assert.Equal(1f, MathUtil.SmoothStep(1f));
        Assert.Equal(0.5f, MathUtil.SmoothStep(0.5f), 5);
        Assert.Equal(0f, MathUtil.SmootherStep(-1f));
        Assert.Equal(1f, MathUtil.SmootherStep(2f));
        Assert.Equal(0.5f, MathUtil.SmootherStep(0.5f), 5);

        // Smoother is flatter: closer to the ends it moves less than SmoothStep does.
        Assert.True(MathUtil.SmootherStep(0.1f) < MathUtil.SmoothStep(0.1f));
    }

    [Fact]
    public void Angles_wrap_into_the_principal_range() {
        Assert.Equal(0f, MathUtil.WrapAngle(MathUtil.TwoPi), 5);
        Assert.Equal(MathUtil.Pi, MathF.Abs(MathUtil.WrapAngle(MathUtil.Pi)), 5);
        Assert.Equal(-MathUtil.PiOverTwo, MathUtil.WrapAngle(-MathUtil.PiOverTwo), 5);
        Assert.Equal(0.5f, MathUtil.WrapAngle((MathUtil.TwoPi * 3f) + 0.5f), 4);
    }

    [Fact]
    public void DeltaAngle_takes_the_short_way_round() {
        // The case the subtraction gets wrong: 350° to 10° is +20°, not -340°.
        var from = MathUtil.DegreesToRadians(350f);
        var to = MathUtil.DegreesToRadians(10f);

        Assert.Equal(20f, MathUtil.RadiansToDegrees(MathUtil.DeltaAngle(from, to)), 3);
        Assert.Equal(-20f, MathUtil.RadiansToDegrees(MathUtil.DeltaAngle(to, from)), 3);
    }

    [Fact]
    public void Powers_of_two_are_recognised_and_rounded_up_to() {
        Assert.True(MathUtil.IsPowerOfTwo(1));
        Assert.True(MathUtil.IsPowerOfTwo(1024));
        Assert.False(MathUtil.IsPowerOfTwo(0));
        Assert.False(MathUtil.IsPowerOfTwo(-8));
        Assert.False(MathUtil.IsPowerOfTwo(1023));

        Assert.Equal(1, MathUtil.NextPowerOfTwo(0));
        Assert.Equal(1, MathUtil.NextPowerOfTwo(1));
        Assert.Equal(2, MathUtil.NextPowerOfTwo(2));
        Assert.Equal(4, MathUtil.NextPowerOfTwo(3));
        Assert.Equal(1024, MathUtil.NextPowerOfTwo(1024));
        Assert.Equal(2048, MathUtil.NextPowerOfTwo(1025));
        Assert.Throws<ArgumentOutOfRangeException>(() => MathUtil.NextPowerOfTwo(int.MaxValue));
    }

    [Fact]
    public void AlignUp_rounds_to_a_multiple() {
        Assert.Equal(256, MathUtil.AlignUp(200, 256));
        Assert.Equal(256, MathUtil.AlignUp(256, 256));
        Assert.Equal(512, MathUtil.AlignUp(257, 256));
        Assert.Equal(0, MathUtil.AlignUp(0, 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => MathUtil.AlignUp(16, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MathUtil.AlignUp(-1, 16));
    }

    [Fact]
    public void Repeat_wraps_negatives_forward_where_the_remainder_operator_reflects_them() {
        Assert.Equal(1f, MathUtil.Repeat(11f, 10f), 5);
        Assert.Equal(0f, MathUtil.Repeat(10f, 10f), 5);

        // -1 % 10 is -1; a looping animation wants 9.
        Assert.Equal(9f, MathUtil.Repeat(-1f, 10f), 5);
        Assert.Equal(0f, MathUtil.Repeat(5f, 0f));
    }

    [Fact]
    public void PingPong_bounces_within_the_interval() {
        Assert.Equal(3f, MathUtil.PingPong(3f, 10f), 5);
        Assert.Equal(10f, MathUtil.PingPong(10f, 10f), 5);
        Assert.Equal(7f, MathUtil.PingPong(13f, 10f), 5);
        Assert.Equal(0f, MathUtil.PingPong(20f, 10f), 5);
    }
}
