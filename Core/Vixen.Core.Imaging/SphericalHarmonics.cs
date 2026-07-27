// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Core.Imaging;

/// <summary>An environment's diffuse lighting, in nine numbers per channel.</summary>
/// <remarks>
///     <para>
///         Diffuse lighting from an environment is a cosine-weighted integral over the whole sphere,
///         and it is very smooth: a Lambertian surface cannot see any detail sharper than the cosine
///         lobe that blurs it. Ramamoorthi and Hanrahan's result is that nine spherical-harmonic
///         coefficients — the first three bands — reproduce that integral to within about one per
///         cent for <i>any</i> environment. Nine RGB numbers, against a prefiltered irradiance cube
///         map's tens of thousands of texels.
///     </para>
///     <para>
///         That is why the split-sum approximation stores diffuse as SH and only specular as a cube:
///         it makes a light probe small enough to put one in every room, and cheap enough to update
///         one per frame.
///     </para>
///     <para>
///         <see cref="Irradiance" /> returns irradiance <i>divided by π</i>, which is the quantity a
///         shader multiplies by albedo. Getting that factor wrong is the classic "everything is π
///         times too bright" bug, so the constant test is exact rather than approximate: a uniform
///         environment of radiance L must come back as exactly L.
///     </para>
/// </remarks>
public sealed class SphericalHarmonicsL2 {
    /// <summary>How many coefficients three bands is.</summary>
    public const int Count = 9;

    readonly Vector3[] coefficients;

    /// <summary>The nine coefficients, one RGB triple each.</summary>
    public ReadOnlySpan<Vector3> Coefficients => coefficients;

    /// <summary>Starts from nine given coefficients.</summary>
    /// <param name="coefficients">The nine.</param>
    /// <exception cref="ArgumentException">There are not nine.</exception>
    public SphericalHarmonicsL2(ReadOnlySpan<Vector3> coefficients) {
        if (coefficients.Length != Count) {
            throw new ArgumentException($"Three bands is {Count} coefficients, not {coefficients.Length}.");
        }

        this.coefficients = coefficients.ToArray();
    }

    /// <summary>Starts from nothing: a black environment.</summary>
    public SphericalHarmonicsL2() => coefficients = new Vector3[Count];

    /// <summary>Projects a cube map onto the first three bands.</summary>
    /// <param name="cube">The environment, six square faces in a float format.</param>
    /// <param name="level">Which mip level to read.</param>
    /// <returns>The projection.</returns>
    /// <exception cref="ArgumentException">It is not a square float cube map.</exception>
    /// <remarks>
    ///     Every texel is weighted by its own solid angle, because a cube map's texels do not cover
    ///     equal amounts of sky — see <see cref="CubeMap.SolidAngleOfTexel" />. Skipping that weight
    ///     over-counts the corners of every face by a factor of five and produces a probe that is
    ///     subtly wrong in a way no amount of staring at the numbers reveals.
    /// </remarks>
    public static SphericalHarmonicsL2 Project(TextureData cube, int level = 0) {
        CubeMap.Require(cube, nameof(cube));

        var size = cube.Levels[level].Width;
        var coefficients = new Vector3[Count];
        Span<float> basis = stackalloc float[Count];

        for (var face = 0; face < CubeMap.Faces; face++) {
            for (var y = 0; y < size; y++) {
                for (var x = 0; x < size; x++) {
                    var radiance = CubeMap.ReadTexel(cube, level, face, x, y);
                    var solidAngle = CubeMap.SolidAngleOfTexel(x, y, size);
                    Evaluate(CubeMap.DirectionOfTexel(face, x, y, size), basis);

                    for (var index = 0; index < Count; index++) {
                        coefficients[index] += radiance * (basis[index] * solidAngle);
                    }
                }
            }
        }

        return new(coefficients);
    }

    /// <summary>The nine basis functions in a direction.</summary>
    /// <param name="direction">The direction, normalised.</param>
    /// <param name="basis">Nine floats to fill.</param>
    public static void Evaluate(Vector3 direction, Span<float> basis) {
        var (x, y, z) = (direction.X, direction.Y, direction.Z);

        basis[0] = 0.282095f;
        basis[1] = 0.488603f * y;
        basis[2] = 0.488603f * z;
        basis[3] = 0.488603f * x;
        basis[4] = 1.092548f * x * y;
        basis[5] = 1.092548f * y * z;
        basis[6] = 0.315392f * ((3f * z * z) - 1f);
        basis[7] = 1.092548f * x * z;
        basis[8] = 0.546274f * ((x * x) - (y * y));
    }

    /// <summary>
    ///     The diffuse lighting arriving at a surface facing a direction, divided by π — which is
    ///     what a shader multiplies by albedo.
    /// </summary>
    /// <param name="normal">The surface normal, normalised.</param>
    /// <returns>The irradiance over π.</returns>
    /// <remarks>
    ///     The three band constants — π, 2π/3 and π/4 — are the cosine lobe's own projection onto
    ///     the same basis. They are what turns a projection of <i>radiance</i> into an integral of
    ///     <i>irradiance</i>, and leaving them out gives a probe that looks plausible and is flat.
    /// </remarks>
    public Vector3 Irradiance(Vector3 normal) {
        Span<float> basis = stackalloc float[Count];
        Evaluate(normal, basis);

        var total = Vector3.Zero;

        for (var index = 0; index < Count; index++) {
            total += coefficients[index] * (basis[index] * BandFactors[index]);
        }

        return total;
    }

    /// <summary>
    ///     The cosine lobe's own coefficients, already divided by π. Band zero is π/π, band one is
    ///     (2π/3)/π and band two is (π/4)/π.
    /// </summary>
    static readonly float[] BandFactors = [
        1f,
        2f / 3f, 2f / 3f, 2f / 3f,
        0.25f, 0.25f, 0.25f, 0.25f, 0.25f
    ];
}
