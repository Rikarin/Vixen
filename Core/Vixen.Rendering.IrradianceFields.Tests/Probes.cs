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
    /// <summary>A fully valid probe carrying one number.</summary>
    /// <param name="value">The number.</param>
    /// <returns>The probe.</returns>
    public static IrradianceProbe Of(float value) =>
        IrradianceProbe.Lit(new(new Vector3(value), Vector3.Zero, Vector3.Zero, Vector3.Zero));

    /// <summary>The number a probe carries.</summary>
    /// <param name="probe">The probe.</param>
    /// <returns>The number.</returns>
    public static float Value(this IrradianceProbe probe) => probe.Radiance.L00.X;

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
