// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.Lighting;

/// <summary>
///     An order-2 spherical-harmonic projection: nine coefficients per channel.
/// </summary>
/// <remarks>
///     <para>
///         The field names are the shader's — <c>Raven/Library/Shading/Ibl.rvn</c> declares
///         <c>ShCoefficients</c> with exactly these nine, in this order — because the two are one
///         layout with a GPU in the middle. A field renamed on one side and not the other puts the
///         <c>z</c> lobe where the <c>y</c> lobe should be, which tilts the ambient light off axis and
///         looks like a bad probe rather than a mismatched struct.
///     </para>
///     <para>
///         Nine numbers is enough for diffuse and nowhere near enough for specular, which is the
///         whole division of labour in image-based lighting: irradiance is so smooth that order-2
///         reproduces it to within a percent, and reflections are not, so they stay a cubemap.
///     </para>
/// </remarks>
public struct ShCoefficients {
    /// <summary>The constant term — the environment's average radiance, scaled.</summary>
    public Vector3 L00;

    /// <summary>The linear term along Y.</summary>
    public Vector3 L1m1;

    /// <summary>The linear term along Z.</summary>
    public Vector3 L10;

    /// <summary>The linear term along X.</summary>
    public Vector3 L11;

    /// <summary>The quadratic XY term.</summary>
    public Vector3 L2m2;

    /// <summary>The quadratic YZ term.</summary>
    public Vector3 L2m1;

    /// <summary>The quadratic term along Z.</summary>
    public Vector3 L20;

    /// <summary>The quadratic XZ term.</summary>
    public Vector3 L21;

    /// <summary>The quadratic X²−Y² term.</summary>
    public Vector3 L22;

    /// <summary>Adds two projections, which is what blending two probes is.</summary>
    public static ShCoefficients operator +(ShCoefficients left, ShCoefficients right) =>
        new() {
            L00 = left.L00 + right.L00,
            L1m1 = left.L1m1 + right.L1m1,
            L10 = left.L10 + right.L10,
            L11 = left.L11 + right.L11,
            L2m2 = left.L2m2 + right.L2m2,
            L2m1 = left.L2m1 + right.L2m1,
            L20 = left.L20 + right.L20,
            L21 = left.L21 + right.L21,
            L22 = left.L22 + right.L22
        };

    /// <summary>Scales a projection, which is what weighting a probe is.</summary>
    /// <remarks>
    ///     Linear, and that is the property probe interpolation rests on: a weighted sum of
    ///     projections is the projection of the weighted sum, so blending four probes costs nine
    ///     multiply-adds rather than four evaluations.
    /// </remarks>
    public static ShCoefficients operator *(ShCoefficients coefficients, float scale) =>
        new() {
            L00 = coefficients.L00 * scale,
            L1m1 = coefficients.L1m1 * scale,
            L10 = coefficients.L10 * scale,
            L11 = coefficients.L11 * scale,
            L2m2 = coefficients.L2m2 * scale,
            L2m1 = coefficients.L2m1 * scale,
            L20 = coefficients.L20 * scale,
            L21 = coefficients.L21 * scale,
            L22 = coefficients.L22 * scale
        };

    /// <summary>Scales a projection.</summary>
    public static ShCoefficients operator *(float scale, ShCoefficients coefficients) => coefficients * scale;
}

/// <summary>
///     Projects an environment into spherical harmonics, and evaluates what it does to a surface.
/// </summary>
/// <remarks>
///     <para>
///         The diffuse half of the split-sum approximation, done where it belongs: on the CPU, once,
///         over an environment that is not going to change. What reaches a frame is nine
///         <c>float3</c>s.
///     </para>
///     <para>
///         <strong><see cref="Evaluate" /> mirrors <c>Ibl.IrradianceSh9</c> and must keep doing
///         so.</strong> It is here because a probe has to be checkable without a GPU — the analytic
///         expectations below are what say the projection is right at all — and a second
///         implementation of anything is a thing that drifts, so it is written from the same five
///         constants in the same order.
///     </para>
/// </remarks>
public static class SphericalHarmonics {
    // The order-2 basis, evaluated per direction. The axis each band is assigned to is the shader's:
    // l1m1 takes y, l10 takes z, l11 takes x. Any other assignment is a rotation of the environment
    // that nothing reports.
    const float Y0 = 0.282095f;
    const float Y1 = 0.488603f;
    const float Y2 = 1.092548f;
    const float Y20 = 0.315392f;
    const float Y22 = 0.546274f;

    /// <summary>Projects a cube map's radiance into nine coefficients per channel.</summary>
    /// <remarks>
    ///     <para>
    ///         Every texel weighted by the solid angle it covers, which is not a detail: a cube's
    ///         corner texels subtend about a fifth of what its centre texels do, so an unweighted sum
    ///         is an environment pulled toward its own corners.
    ///     </para>
    ///     <para>
    ///         The result is a projection of <em>radiance</em>. Turning it into irradiance is
    ///         <see cref="Evaluate" />'s job, and the cosine-lobe convolution is folded into its
    ///         constants — which is why they do not look like the basis functions above.
    ///     </para>
    /// </remarks>
    public static ShCoefficients Project(CubeImage environment) {
        ArgumentNullException.ThrowIfNull(environment);

        var result = default(ShCoefficients);

        foreach (var face in CubeMapping.Faces) {
            for (var y = 0; y < environment.Size; y++) {
                for (var x = 0; x < environment.Size; x++) {
                    var direction = environment.DirectionOf(face, x, y);
                    var radiance = environment.At(face, x, y) * environment.SolidAngleOf(x, y);

                    Accumulate(ref result, direction, radiance);
                }
            }
        }

        return result;
    }

    /// <summary>Adds one direction's radiance to a projection.</summary>
    /// <remarks>Public because a probe baked by rendering rays rather than a cube needs it too.</remarks>
    public static void Accumulate(ref ShCoefficients coefficients, Vector3 direction, Vector3 weighted) {
        var (x, y, z) = (direction.X, direction.Y, direction.Z);

        coefficients.L00 += weighted * Y0;
        coefficients.L1m1 += weighted * (Y1 * y);
        coefficients.L10 += weighted * (Y1 * z);
        coefficients.L11 += weighted * (Y1 * x);
        coefficients.L2m2 += weighted * (Y2 * x * y);
        coefficients.L2m1 += weighted * (Y2 * y * z);
        coefficients.L20 += weighted * (Y20 * ((3f * z * z) - 1f));
        coefficients.L21 += weighted * (Y2 * x * z);
        coefficients.L22 += weighted * (Y22 * ((x * x) - (y * y)));
    }

    /// <summary>
    ///     The irradiance arriving at a surface facing <paramref name="normal" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Irradiance, not radiance: for a uniform environment of radiance <c>L</c> this returns
    ///         <c>πL</c>, and a Lambertian surface reflects <c>albedo/π</c> of it — which is what
    ///         <c>Ibl.Diffuse</c> does. The factor of π has to be somewhere, and it being in the BRDF
    ///         rather than in the probe is what lets a probe's coefficients mean one thing.
    ///     </para>
    ///     <para>
    ///         Clamped at zero because an order-2 fit of a high-contrast environment rings, and a
    ///         negative irradiance is a surface that removes light from the frame.
    ///     </para>
    /// </remarks>
    public static Vector3 Evaluate(in ShCoefficients coefficients, Vector3 normal) {
        // Ramamoorthi and Hanrahan's constants, which fold the cosine-lobe convolution into the
        // basis. The same five numbers, in the same order, as `Ibl.IrradianceSh9`.
        const float C1 = 0.429043f;
        const float C2 = 0.511664f;
        const float C3 = 0.743125f;
        const float C4 = 0.886227f;
        const float C5 = 0.247708f;

        var (x, y, z) = (normal.X, normal.Y, normal.Z);

        var result = (coefficients.L00 * C4) - (coefficients.L20 * C5);
        result += ((coefficients.L1m1 * y) + (coefficients.L10 * z) + (coefficients.L11 * x)) * (2f * C2);

        result += ((coefficients.L2m2 * (x * y))
                + (coefficients.L2m1 * (y * z))
                + (coefficients.L21 * (x * z)))
            * (2f * C1);

        result += (coefficients.L20 * (C3 * z * z)) + (coefficients.L22 * (C1 * ((x * x) - (y * y))));

        return Vector3.Max(result, Vector3.Zero);
    }
}
