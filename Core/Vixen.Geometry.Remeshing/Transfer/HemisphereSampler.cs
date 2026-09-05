// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>A fixed cosine-weighted set of directions over a hemisphere, and the frame to put it in.</summary>
/// <remarks>
///     <para>
///         <b>Cosine-weighted rather than uniform, and that is what makes the ray count an
///         estimator rather than a budget.</b> Ambient occlusion is the cosine-weighted visibility
///         integral <c>(1/π)∫V(ω)·cos θ dω</c>. Drawing the directions from the cosine density makes
///         the estimate the plain <i>mean</i> of <c>V</c> over the samples — no per-ray weight, no
///         normalisation constant, and no chance of the two disagreeing. A uniform hemisphere with a
///         <c>cos θ</c> weight computes the same number and spends most of its rays near the horizon
///         where the weight is nearly zero.
///     </para>
///     <para>
///         ⚠ <b>Nothing here is random, and it is not an optimisation that it is not.</b> A content
///         hash rests on this bake (§ D14's byte-identity), so a clock-seeded or thread-order-seeded
///         sampler would make the same source and the same settings produce two different atlases.
///         The sample index gives a stratified pair through Hammersley — <c>(i + ½)/n</c> against the
///         base-2 radical inverse — and the only thing that varies between texels is the azimuthal
///         <see cref="Turn" />, which is a hash of the texel index and therefore a function of the
///         input alone.
///     </para>
///     <para>
///         ⚠ <b>The turn exists because a <i>shared</i> sample set is worse than a noisy one.</b>
///         Every texel firing the identical directions turns the estimator's error into a
///         low-frequency pattern that survives every mip — banding that reads as a modelling
///         artefact. Rotating each texel's set about its own normal spreads that error across
///         neighbours instead, which is the same trade a GPU sampler makes with a blue-noise offset,
///         without giving up determinism.
///     </para>
/// </remarks>
static class HemisphereSampler {
    /// <summary>One cosine-weighted direction in the frame where <c>z</c> is the normal.</summary>
    /// <param name="index">Which sample, in <c>[0, count)</c>.</param>
    /// <param name="count">How many there are in total.</param>
    /// <param name="turn">A rotation about <c>z</c>, in turns rather than radians.</param>
    /// <returns>A unit direction with <c>z ≥ 0</c>.</returns>
    /// <remarks>
    ///     Malley's method: a uniform point on the disc lifted onto the hemisphere. The radius is
    ///     <c>√u</c> because the disc's area grows as the square of it, and the height is
    ///     <c>√(1 − u)</c> because the lift keeps the point on the sphere — which together are
    ///     exactly the cosine density, with no rejection and no transcendental inverse.
    /// </remarks>
    public static Vector3 Local(int index, int count, float turn) {
        var u = (index + 0.5f) / count;
        var angle = MathF.Tau * (RadicalInverse(index) + turn);

        // ⚠ Clamped rather than trusted. `u` reaches 1 − ½/count and not 1, so the argument is
        // positive in exact arithmetic; the clamp is what keeps a rounding of the division from
        // handing MathF.Sqrt a negative and writing a NaN direction into every downstream map.
        var height = MathF.Sqrt(MathF.Max(0f, 1f - u));
        var radius = MathF.Sqrt(MathF.Min(1f, u));

        return new(radius * MathF.Cos(angle), radius * MathF.Sin(angle), height);
    }

    /// <summary>The azimuthal rotation a texel's sample set is taken at.</summary>
    /// <param name="texel">The texel's index in the atlas.</param>
    /// <returns>A rotation in <c>[0, 1)</c> turns.</returns>
    /// <remarks>
    ///     ⚠ The same integer hash <c>TransferFixtures.Wobble</c> uses, and for the same reason: a
    ///     value that looks scattered, is a pure function of an index, and needs no state to be
    ///     carried between texels — so the answer does not depend on the order the texels were
    ///     walked in.
    /// </remarks>
    public static float Turn(int texel) {
        var hash = (uint) texel * 2654435761u;

        hash ^= hash >> 15;
        hash *= 2246822519u;
        hash ^= hash >> 13;

        return (hash & 0xFFFFFF) / (float) 0x1000000;
    }

    /// <summary>An orthonormal pair spanning the plane a normal is perpendicular to.</summary>
    /// <param name="normal">The unit normal.</param>
    /// <returns>Two unit vectors, perpendicular to it and to each other.</returns>
    /// <remarks>
    ///     ⚠ <b>Frisvad's branchless construction, with Duff's sign fix.</b> The naive
    ///     "cross with whichever axis is least aligned" is exact but discontinuous, and the original
    ///     branchless form loses all its precision as <c>z</c> approaches <c>−1</c> — where its
    ///     divisor cancels. Taking the sign of <c>z</c> through <see cref="MathF.CopySign" /> moves
    ///     the cancellation to a value that cannot occur, and a zero normal falls out as the identity
    ///     axes rather than as a <c>NaN</c>.
    /// </remarks>
    public static (Vector3 Tangent, Vector3 Bitangent) Basis(Vector3 normal) {
        var sign = MathF.CopySign(1f, normal.Z);
        var a = -1f / (sign + normal.Z);
        var b = normal.X * normal.Y * a;

        return (
            new(1f + (sign * normal.X * normal.X * a), sign * b, -sign * normal.X),
            new(b, sign + (normal.Y * normal.Y * a), -normal.Y)
        );
    }

    /// <summary>The base-2 radical inverse, which is the second half of a Hammersley pair.</summary>
    static float RadicalInverse(int index) {
        var bits = (uint) index;

        bits = (bits << 16) | (bits >> 16);
        bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
        bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
        bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
        bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);

        return bits * 2.3283064365386963e-10f;
    }
}
