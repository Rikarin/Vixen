// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;

namespace Vixen.Water;

/// <summary>The arithmetic both hosts of the evaluator agree on, to the last bit.</summary>
/// <remarks>
///     <para>
///         <b>[35 § Risks](../../docs/plan/35-water.md#risks), decided in W1 rather than discovered
///         in W7.</b> The seam test between the C# evaluator and the Raven one states a tolerance of
///         <em>exact</em>, and exact float agreement between a CPU and a SPIR-V implementation is a
///         real claim that <c>sin</c> and <c>cos</c> do not support: the Vulkan specification allows
///         <c>OpSin</c> 8192 ULP of error over the useful range, and a driver is free to use a
///         hardware special-function unit whose polynomial nobody has written down.
///     </para>
///     <para>
///         So the wave sum does not call the intrinsic on either side. It calls
///         <see cref="SinCos" />, which is a stated polynomial in stated operations — and
///         <c>Raven/Library/Water/Surface.rvn</c> spells out the same one.
///     </para>
///     <para>
///         ⚠ <b>What that actually bought, measured rather than claimed.</b> Doc 35 asked for exact,
///         and <c>WaterSurfaceSeamDeviceTests</c> measures <b>2 × 10⁻⁴ m</b> — because a device is
///         free to contract a multiply and an add into one FMA even where the source does not, and
///         the Metal translation layer this repository's Vulkan device runs behind does. The residue
///         is a one-ULP difference in the <em>phase</em>, and it therefore grows linearly with it: a
///         millionth of a metre at a hundred radians, a twentieth of a millimetre at five thousand.
///         Against the intrinsic's licensed 8192 ULP — which at those phases is not a rounding
///         difference but a different wave — the polynomial is still what makes any bound sayable.
///     </para>
///     <para>
///         ⚠ <b>The range reduction is the part that has to be exact, not the polynomial.</b> A
///         polynomial evaluated on a reduced argument is a handful of fused-free multiply-adds and
///         will agree anywhere; the reduction subtracts a multiple of π, and by a phase of a hundred
///         thousand radians — twenty minutes of water time at a plausible frequency — a naive
///         subtraction has lost every digit that mattered. <see cref="Reduce" /> therefore splits π
///         into four pieces, the first three of which carry few enough significant bits that
///         multiplying them by the number of half-cycles is <em>exact</em>, and subtracts them in
///         order.
///     </para>
///     <para>
///         ⚠ <b>Nothing here may be rewritten into a fused multiply-add.</b> An FMA computes one
///         rounding where the written expression computes two, so a host that contracts and a host
///         that does not disagree in the last bit — which is the whole of what this file exists to
///         prevent. C# does not contract on its own and Raven's emitters are told not to; the
///         parenthesisation below is load-bearing and not style.
///     </para>
/// </remarks>
public static class WaterMath {
    /// <summary>The first and largest piece of π: 201 ⁄ 64, which is nine significant bits.</summary>
    /// <remarks>
    ///     ⚠ <b>Deliberately not the nearest float to π</b>, and that is the whole trick. Nine
    ///     significant bits leaves fifteen for the multiplier, so <c>halfCycles × PiA</c> is an
    ///     <em>exact</em> float for every multiple a session reaches — and an exact product is one
    ///     that cancels completely when it is subtracted. The nearest float to π uses all
    ///     twenty-four bits, so its product is rounded, and the rounding is the size of the answer.
    /// </remarks>
    public const float PiA = 3.140625f;

    /// <summary>The second piece, also exact against its multiplier.</summary>
    public const float PiB = 0.0009670257568359375f;

    /// <summary>The third.</summary>
    public const float PiC = 6.2771999e-7f;

    /// <summary>And the remainder, which carries what is left of π's tail.</summary>
    /// <remarks>
    ///     Four pieces rather than two is Cody–Waite taken to where a float needs it. Two-part
    ///     reduction is accurate to a few hundred radians and then degrades linearly; four is good to
    ///     the hundred thousand a long session reaches, which is the range the tests state.
    /// </remarks>
    public const float PiD = 1.2154201e-10f;

    /// <summary>Two π.</summary>
    public const float Tau = 6.28318548f;

    /// <summary>The sine and cosine of an angle, by the polynomial both hosts implement.</summary>
    /// <param name="radians">The angle.</param>
    /// <param name="sin">Its sine.</param>
    /// <param name="cos">Its cosine.</param>
    /// <remarks>
    ///     <para>
    ///         Accurate to about 2 × 10⁻⁷ over the reduced range, which is the float's own resolution
    ///         there — the point is not that it is more accurate than the intrinsic (it is not) but
    ///         that it is <em>the same</em> on both sides of the seam.
    ///     </para>
    ///     <para>
    ///         Both at once because a Gerstner term needs both and the range reduction is the
    ///         expensive half. Asking for them separately would reduce twice, which is slower and —
    ///         more to the point — gives two chances for the two hosts to disagree instead of one.
    ///     </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SinCos(float radians, out float sin, out float cos) {
        // Reduce to [-π, π], recording whether an odd multiple of π was taken out — which negates
        // both results, because sin and cos are both π-antiperiodic.
        var reduced = Reduce(radians, out var flipped);

        var square = reduced * reduced;

        // Minimax-style odd polynomial for sine over [-π, π]. Written in Horner form and
        // parenthesised, because the order of the roundings is part of the contract.
        var s = reduced * (1f + (square * (-0.16666667f + (square * (0.008333333f
            + (square * (-0.00019841270f + (square * (2.7557314e-6f + (square * -2.5052108e-8f))))))))));

        // And the even one for cosine.
        var c = 1f + (square * (-0.5f + (square * (0.041666668f + (square * (-0.0013888889f
            + (square * (2.4801587e-5f + (square * -2.7557319e-7f)))))))));

        sin = flipped ? -s : s;
        cos = flipped ? -c : c;
    }

    /// <summary>The sine of an angle, by <see cref="SinCos" />.</summary>
    /// <param name="radians">The angle.</param>
    /// <returns>Its sine.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sin(float radians) {
        SinCos(radians, out var sin, out _);
        return sin;
    }

    /// <summary>The cosine of an angle, by <see cref="SinCos" />.</summary>
    /// <param name="radians">The angle.</param>
    /// <returns>Its cosine.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Cos(float radians) {
        SinCos(radians, out _, out var cos);
        return cos;
    }

    /// <summary>Brings an angle into [−π, π], saying whether the sign has to flip.</summary>
    /// <param name="radians">The angle.</param>
    /// <param name="flipped">Whether an odd multiple of π was removed.</param>
    /// <returns>The reduced angle.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><see cref="MathF.Round(float)" /> and not a cast.</b> A cast truncates toward
    ///         zero, which is a different function for negative arguments — so a body at a negative
    ///         coordinate would reduce differently from its mirror image, and the sea would be
    ///         visibly asymmetric about the world origin. Rounding is also what makes the reduced
    ///         range symmetric, which the polynomial's coefficients assume.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Four subtractions in this order, not one subtraction of a four-part sum.</b>
    ///         Adding the pieces together first rounds them back into a single float and throws away
    ///         everything the split was for.
    ///     </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Reduce(float radians, out bool flipped) {
        var halfCycles = MathF.Round(radians * (1f / MathF.PI));

        // Each product is exact against its piece, so each subtraction cancels completely and what is
        // left is the next piece's business.
        var reduced = radians - (halfCycles * PiA);
        reduced -= halfCycles * PiB;
        reduced -= halfCycles * PiC;
        reduced -= halfCycles * PiD;

        // An odd number of πs removed negates both sine and cosine. Computed by halving rather than
        // by a bit test, because `halfCycles` is a float and may be larger than an int.
        flipped = MathF.Abs(halfCycles - (2f * MathF.Round(halfCycles * 0.5f))) > 0.5f;

        return reduced;
    }

    /// <summary>A hash of a seed and an index, for a spectrum that is the same everywhere.</summary>
    /// <param name="seed">The spectrum's seed.</param>
    /// <param name="index">Which wave.</param>
    /// <returns>The hash.</returns>
    /// <remarks>
    ///     <para>
    ///         A hash rather than a sequence, so wave N depends only on N — which is what lets a
    ///         spectrum gain a wave without every other wave moving, and what makes a 16-wave sea and
    ///         the first 16 of a 32-wave one the same sixteen waves.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not <c>System.Random</c>, and the reason is a stated exit criterion.</b> W1 asks
    ///         for a spectrum that is identical from its seed on three operating systems, and the
    ///         framework's own documentation declines to promise that a seeded sequence is stable
    ///         across versions. Integer arithmetic is stable everywhere there is a 32-bit word. The
    ///         mixer is deliberately <c>FoliageScatter.Hash</c>'s, constant for constant, because it
    ///         is the one a Raven module already mirrors.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The two are combined by multiply-and-add rather than by XOR, and that one
    ///         difference is load-bearing.</b> The mixer is a bijection with a fixed point at zero, so
    ///         <c>seed ^ index</c> hands it zero whenever the two are equal — and every spectrum would
    ///         share one wave with every other along that diagonal, at exactly the same wavelength,
    ///         phase and direction. In a scatter that is one shrub; in a spectrum it is two seas that
    ///         are meant to be different agreeing about their longest swell.
    ///     </para>
    /// </remarks>
    public static uint Hash(uint seed, int index) {
        var hash = (seed * 0x9E3779B1u) + (uint)index;

        hash ^= hash >> 16;
        hash *= 0x7FEB352Du;
        hash ^= hash >> 15;
        hash *= 0x846CA68Bu;
        hash ^= hash >> 16;

        return hash;
    }

    /// <summary>One of a hash's several independent 0…1 draws.</summary>
    /// <param name="hash">The wave's hash.</param>
    /// <param name="stream">Which of its independent draws, from 1 up.</param>
    /// <returns>A number in 0…1.</returns>
    /// <remarks>
    ///     ⚠ Re-hashed per stream rather than sliced out of the bits, for the reason the foliage
    ///     scatter gives: slicing correlates the low bits of two draws, and here that would mean
    ///     every long wave sharing a phase with its own direction — a swell that reads as a
    ///     repeating corduroy rather than as a sea.
    /// </remarks>
    public static float Unit(uint hash, int stream) {
        var mixed = Hash(hash ^ (uint)(stream * 0x27D4EB2), stream);

        // 24 bits into the mantissa: every value a float can hold in [0, 1), evenly spaced. Dividing
        // by uint.MaxValue instead rounds two adjacent hashes to one float at the top of the range,
        // which is invisible in a scatter and is a wave with a duplicated phase here.
        return (mixed >> 8) * (1f / 16777216f);
    }

    /// <summary>Clamps to 0…1.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The clamped value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Saturate(float value) => value < 0f ? 0f : value > 1f ? 1f : value;

    /// <summary>A smooth 0…1 ramp between two edges, with zero derivative at both.</summary>
    /// <param name="edge0">Where it leaves zero.</param>
    /// <param name="edge1">Where it reaches one.</param>
    /// <param name="value">The value.</param>
    /// <returns>The ramp.</returns>
    /// <remarks>
    ///     ⚠ <b>Degenerate edges answer a step rather than a NaN.</b> A shoreline whose ramp width an
    ///     author left at zero is a hard edge, which is a look; a NaN propagates into the surface
    ///     height and from there into a rigid body's position, which is a boat that vanishes.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SmoothStep(float edge0, float edge1, float value) {
        if (edge1 <= edge0) {
            return value < edge0 ? 0f : 1f;
        }

        var t = Saturate((value - edge0) / (edge1 - edge0));

        return t * t * (3f - (2f * t));
    }
}
