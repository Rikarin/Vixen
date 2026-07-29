// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;

namespace Vixen.Rendering.IrradianceFields.Tests;

/// <summary>Probes carrying one number, so a test can say where that number went.</summary>
/// <remarks>
///     Interpolation is component-wise and every component interpolates the same way, so a test that
///     wants to know <i>whether the right texels were read</i> does not also need nine of them. The
///     number rides in the constant term, which is the one <see cref="SphericalHarmonicsL1.Irradiance" />
///     returns unchanged for every normal.
/// </remarks>
static class Probes {
    /// <summary>The constant basis function — what an environment of one everywhere projects to.</summary>
    /// <remarks>
    ///     Dividing by it here and multiplying by it back is what makes <see cref="Of" /> and
    ///     <see cref="Value" /> inverses <i>and</i> makes <see cref="Value" /> agree with
    ///     <see cref="IrradianceProbe.Irradiance" /> — so a test can say "this probe carries four" and
    ///     mean the four a shader would multiply by albedo, rather than the coefficient behind it.
    ///     Reading the coefficient raw is how a test ends up off by 2√π and nobody can see where.
    /// </remarks>
    const float Constant = 0.282095f;

    /// <summary>A fully valid probe lighting every surface with one number.</summary>
    /// <param name="value">The number.</param>
    /// <returns>The probe.</returns>
    public static IrradianceProbe Of(float value) =>
        IrradianceProbe.Lit(new(new Vector3(value / Constant), Vector3.Zero, Vector3.Zero, Vector3.Zero));

    /// <summary>The number a probe lights everything with.</summary>
    /// <param name="probe">The probe.</param>
    /// <returns>The number, which is what <see cref="IrradianceProbe.Irradiance" /> answers.</returns>
    public static float Value(this IrradianceProbe probe) => probe.Irradiance(new(0, 1, 0)).X;

    /// <summary>A function that is linear in world space, and therefore reproduced exactly.</summary>
    /// <param name="position">Where.</param>
    /// <returns>Its value.</returns>
    /// <remarks>
    ///     Trilinear interpolation reproduces a trilinear function exactly, and a linear one is
    ///     trilinear. That is what makes this a test of the <i>addressing</i> rather than of the
    ///     interpolation: any error left is a probe read from the wrong place.
    /// </remarks>
    public static float Ramp(Vector3 position) =>
        (2f * position.X) + (3f * position.Y) - position.Z + 5f;
}
