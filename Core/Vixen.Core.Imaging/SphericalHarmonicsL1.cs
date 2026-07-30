// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Core.Imaging;

/// <summary>An irradiance probe's whole payload: four numbers per channel.</summary>
/// <remarks>
///     <para>
///         <b>A struct where <see cref="SphericalHarmonicsL2" /> is a class, and the difference is
///         the use.</b> One of those describes a scene's sky and there is one of it; a brick of an
///         irradiance field holds sixty-four of these and a clipmap holds thousands of bricks. At
///         that count an object header per probe is most of the memory, and the array has to be
///         contiguous anyway because it is copied into a volume texture.
///     </para>
///     <para>
///         <b>Four coefficients, not nine, and it is not a compromise.</b> Diffuse lighting is a
///         cosine-weighted integral over the whole sphere and a Lambertian surface cannot see detail
///         sharper than the lobe that blurs it. The second band buys a little directional contrast
///         and costs more than twice the storage of the whole payload — so L1 is what both Unity's
///         adaptive probe volumes and Epic's volumetric lightmap ship as their default, and it is
///         what <c>docs/plan/19</c> § 3 asks for.
///     </para>
///     <para>
///         <b>The basis is <see cref="SphericalHarmonicsL2" />'s, truncated, and a test says so.</b>
///         Two derivations of the same four functions is how a probe and a skylight end up
///         disagreeing about which way <c>+Y</c> is — the same argument that made a reflection
///         probe's cube faces come out of the shadow projection rather than a table.
///     </para>
///     <para>
///         <see cref="Irradiance" /> returns irradiance <i>divided by π</i>, which is the quantity a
///         shader multiplies by albedo. Getting that factor wrong is the classic "everything is π
///         times too bright" bug, so the constant test is exact rather than approximate.
///     </para>
/// </remarks>
public struct SphericalHarmonicsL1 : IEquatable<SphericalHarmonicsL1> {
    /// <summary>How many coefficients one band plus the constant is.</summary>
    public const int Count = 4;

    /// <summary>The constant term — the average radiance over the whole sphere.</summary>
    public readonly Vector3 L00;

    /// <summary>The linear term along Y.</summary>
    public readonly Vector3 L1m1;

    /// <summary>Along Z.</summary>
    public readonly Vector3 L10;

    /// <summary>Along X.</summary>
    public readonly Vector3 L11;

    /// <summary>A probe holding four given coefficients.</summary>
    /// <param name="l00">The constant term.</param>
    /// <param name="l1m1">The linear term along Y.</param>
    /// <param name="l10">Along Z.</param>
    /// <param name="l11">Along X.</param>
    /// <remarks>
    ///     Readonly fields rather than properties, deliberately. A brick of these is copied into a
    ///     volume texture as bytes, so the layout is part of the contract — four <c>Vector3</c>s in
    ///     declaration order, and not whatever a compiler chose to do with backing fields.
    /// </remarks>
    public SphericalHarmonicsL1(Vector3 l00, Vector3 l1m1, Vector3 l10, Vector3 l11) {
        L00 = l00;
        L1m1 = l1m1;
        L10 = l10;
        L11 = l11;
    }

    /// <summary>A probe that has seen nothing.</summary>
    public static SphericalHarmonicsL1 Zero => default;

    /// <summary>The four basis functions in a direction.</summary>
    /// <param name="direction">The direction, normalised.</param>
    /// <param name="basis">Four floats to fill.</param>
    /// <exception cref="ArgumentException">There is not room for four.</exception>
    public static void Evaluate(Vector3 direction, Span<float> basis) {
        if (basis.Length < Count) {
            throw new ArgumentException($"Four basis functions need four floats, not {basis.Length}.", nameof(basis));
        }

        basis[0] = 0.282095f;
        basis[1] = 0.488603f * direction.Y;
        basis[2] = 0.488603f * direction.Z;
        basis[3] = 0.488603f * direction.X;
    }

    /// <summary>Adds one sample of radiance arriving from a direction.</summary>
    /// <param name="direction">Where it came from, normalised.</param>
    /// <param name="radiance">How much, per channel.</param>
    /// <param name="solidAngle">How much of the sphere this sample stands for.</param>
    /// <remarks>
    ///     <para>
    ///         <b>The solid angle is the caller's, and that is what makes the projection exact rather
    ///         than nearly right.</b> A projection is an integral over the sphere, so every sample
    ///         has to carry the area it represents — <c>4π/n</c> for <i>n</i> uniform directions, a
    ///         texel's own solid angle for a cube map, a cosine-weighted weight for an importance
    ///         sampler. Folding a constant in here would be assuming one of those, and it is the
    ///         assumption that makes a probe come out a few per cent dark in a way nobody traces
    ///         back.
    ///     </para>
    /// </remarks>
    /// <returns>This probe with the sample added.</returns>
    public readonly SphericalHarmonicsL1 Accumulated(Vector3 direction, Vector3 radiance, float solidAngle) {
        Span<float> basis = stackalloc float[Count];
        Evaluate(direction, basis);

        var weighted = radiance * solidAngle;

        return new(
            L00 + (weighted * basis[0]),
            L1m1 + (weighted * basis[1]),
            L10 + (weighted * basis[2]),
            L11 + (weighted * basis[3])
        );
    }

    /// <summary>
    ///     The diffuse lighting arriving at a surface facing a direction, divided by π.
    /// </summary>
    /// <param name="normal">The surface normal, normalised.</param>
    /// <returns>The irradiance over π.</returns>
    /// <remarks>
    ///     The two band constants — π and 2π/3, each already divided by π — are the cosine lobe's own
    ///     projection onto this basis. They are what turns a projection of <i>radiance</i> into an
    ///     integral of <i>irradiance</i>, and leaving them out gives a probe that looks plausible and
    ///     is flat.
    /// </remarks>
    public readonly Vector3 Irradiance(Vector3 normal) {
        Span<float> basis = stackalloc float[Count];
        Evaluate(normal, basis);

        return (L00 * basis[0])
            + ((L1m1 * basis[1]) + (L10 * basis[2]) + (L11 * basis[3])) * (2f / 3f);
    }

    /// <summary>The radiance arriving from a direction, as far as four coefficients can say.</summary>
    /// <param name="direction">The direction, normalised.</param>
    /// <returns>The radiance, per channel.</returns>
    /// <remarks>
    ///     The raw basis with no cosine lobe — what a ray that terminated in a probe field reads,
    ///     where <see cref="Irradiance" /> is what a surface standing in it receives. ⚠ Unclamped,
    ///     and the L1 truncation cuts both ways: toward the dark side of a one-sided distribution
    ///     this goes negative, toward the bright side it overshoots. The clamp belongs to whoever
    ///     turns the number into light.
    /// </remarks>
    public readonly Vector3 Radiance(Vector3 direction) {
        Span<float> basis = stackalloc float[Count];
        Evaluate(direction, basis);

        return (L00 * basis[0]) + (L1m1 * basis[1]) + (L10 * basis[2]) + (L11 * basis[3]);
    }

    /// <summary>One probe blended toward another.</summary>
    /// <param name="from">Where to start.</param>
    /// <param name="to">Where to end.</param>
    /// <param name="amount">How far, 0 to 1.</param>
    /// <returns>The blend.</returns>
    /// <remarks>
    ///     Coefficient-wise, which is exact rather than approximate: the projection is linear, so a
    ///     blend of two probes' coefficients <i>is</i> the projection of the blend of what they saw.
    ///     That is the property the whole scheme rests on — it is what lets a field interpolate
    ///     between probes at all, and what lets a probe converge toward a new answer over frames
    ///     instead of jumping to it.
    /// </remarks>
    public static SphericalHarmonicsL1 Lerp(SphericalHarmonicsL1 from, SphericalHarmonicsL1 to, float amount) =>
        new(
            Vector3.Lerp(from.L00, to.L00, amount),
            Vector3.Lerp(from.L1m1, to.L1m1, amount),
            Vector3.Lerp(from.L10, to.L10, amount),
            Vector3.Lerp(from.L11, to.L11, amount)
        );

    /// <summary>This probe with every coefficient multiplied.</summary>
    /// <param name="scale">What to multiply by.</param>
    /// <returns>The scaled probe.</returns>
    public readonly SphericalHarmonicsL1 Scaled(float scale) =>
        new(L00 * scale, L1m1 * scale, L10 * scale, L11 * scale);

    /// <summary>The first four coefficients of a nine-coefficient probe.</summary>
    /// <param name="wider">The probe to narrow.</param>
    /// <returns>The narrowed probe.</returns>
    /// <exception cref="ArgumentNullException">There is no probe.</exception>
    /// <remarks>
    ///     What a skylight already projected into nine coefficients becomes when a field wants it in
    ///     four. Truncation is the right operation and not an approximation of one: the basis is
    ///     orthonormal, so dropping a band drops exactly that band's contribution and changes none of
    ///     the others.
    /// </remarks>
    public static SphericalHarmonicsL1 From(SphericalHarmonicsL2 wider) {
        ArgumentNullException.ThrowIfNull(wider);

        var coefficients = wider.Coefficients;

        return new(coefficients[0], coefficients[1], coefficients[2], coefficients[3]);
    }

    /// <inheritdoc />
    public readonly bool Equals(SphericalHarmonicsL1 other) =>
        L00 == other.L00 && L1m1 == other.L1m1 && L10 == other.L10 && L11 == other.L11;

    /// <inheritdoc />
    public override readonly bool Equals(object? obj) => obj is SphericalHarmonicsL1 other && Equals(other);

    /// <inheritdoc />
    public override readonly int GetHashCode() => HashCode.Combine(L00, L1m1, L10, L11);

    /// <summary>Whether two probes hold the same coefficients.</summary>
    /// <param name="left">One probe.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they are equal.</returns>
    public static bool operator ==(SphericalHarmonicsL1 left, SphericalHarmonicsL1 right) => left.Equals(right);

    /// <summary>Whether two probes hold different coefficients.</summary>
    /// <param name="left">One probe.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they differ.</returns>
    public static bool operator !=(SphericalHarmonicsL1 left, SphericalHarmonicsL1 right) => !left.Equals(right);
}
