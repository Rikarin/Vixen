// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;

namespace Vixen.Rendering.IrradianceFields;

/// <summary>Everything one point in a field knows about the light arriving there.</summary>
/// <remarks>
///     <para>
///         <b>Six floats per channel and two scalars, which is the whole payload of dynamic global
///         illumination.</b> <c>docs/plan/19</c> § 3 fixes it deliberately: L1 spherical harmonics for
///         the indirect diffuse, a validity scalar so a probe that ended up inside a wall can be
///         ignored rather than believed, and a shadowing scalar for the directional light so static
///         shadow data has something to be replaced by.
///     </para>
///     <para>
///         <b>Validity is the leak fix, and it is carried here rather than derived at sample time
///         because only the filler can know it.</b> A probe that traced its rays and found itself
///         surrounded by backfaces is inside geometry; nothing at the sampling end can tell that from
///         a probe in a dark room. Doc 19 carries leaks as risk G3 — the defect users actually report
///         — and every part of the remedy starts from this number.
///     </para>
///     <para>
///         A record struct, so a brick is sixty-four of these laid out end to end and equality comes
///         for free. Unlike <see cref="SphericalHarmonicsL1" /> the field layout here is <i>not</i> the
///         contract with a shader: the upload splits a brick into separate volume textures per
///         coefficient, because that is what a hardware trilinear fetch wants to read.
///     </para>
/// </remarks>
/// <param name="Radiance">
///     What arrives, projected onto four basis functions per channel. Radiance rather than
///     irradiance — a surface's normal is not known until something asks, and
///     <see cref="SphericalHarmonicsL1.Irradiance" /> is what asks.
/// </param>
/// <param name="Validity">
///     How much of this probe is worth believing, from zero to one. Zero for a probe buried in
///     geometry, one for a probe in open air.
/// </param>
/// <param name="SunShadow">
///     How much of the directional light reaches here, from zero to one. What replaces baked static
///     shadow data — see doc 19 § 4.
/// </param>
public readonly record struct IrradianceProbe(
    SphericalHarmonicsL1 Radiance,
    float Validity,
    float SunShadow
) {
    /// <summary>A probe that has seen nothing and should not be believed.</summary>
    /// <remarks>
    ///     Validity zero rather than one, which is the safe direction: an unfilled probe reads as
    ///     "no answer here" and dilation replaces it, where the other default would spread black
    ///     lighting through a scene and look exactly like a correct one that happens to be dark.
    /// </remarks>
    public static IrradianceProbe Empty => new(SphericalHarmonicsL1.Zero, 0f, 0f);

    /// <summary>A probe in open air holding a given projection.</summary>
    /// <param name="radiance">What arrives.</param>
    /// <returns>The probe, fully valid and fully lit by the sun.</returns>
    public static IrradianceProbe Lit(SphericalHarmonicsL1 radiance) => new(radiance, 1f, 1f);

    /// <summary>The diffuse lighting this probe gives a surface facing a direction, divided by π.</summary>
    /// <param name="normal">The surface normal, normalised.</param>
    /// <returns>The irradiance over π — what a shader multiplies by albedo.</returns>
    public Vector3 Irradiance(Vector3 normal) => Radiance.Irradiance(normal);

    /// <summary>One probe blended toward another.</summary>
    /// <param name="from">Where to start.</param>
    /// <param name="to">Where to end.</param>
    /// <param name="amount">How far, 0 to 1.</param>
    /// <returns>The blend.</returns>
    /// <remarks>
    ///     Component-wise, including validity — which is what makes an interpolation across the edge
    ///     of a valid region fade out rather than stop.
    /// </remarks>
    public static IrradianceProbe Lerp(IrradianceProbe from, IrradianceProbe to, float amount) =>
        new(
            SphericalHarmonicsL1.Lerp(from.Radiance, to.Radiance, amount),
            from.Validity + ((to.Validity - from.Validity) * amount),
            from.SunShadow + ((to.SunShadow - from.SunShadow) * amount)
        );

}
