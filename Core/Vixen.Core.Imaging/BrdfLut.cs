// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Vixen.Core.Mathematics;
using Vixen.Graphics;

namespace Vixen.Core.Imaging;

/// <summary>The other half of the split sum: what the BRDF does, with no environment in it.</summary>
/// <remarks>
///     <para>
///         The specular integral splits into the environment convolved with the GGX lobe — which
///         <see cref="EnvironmentPrefilter" /> bakes per scene — and the BRDF's own response, which
///         depends only on the viewing angle and the roughness. The second is the same function for
///         every scene ever rendered, so it is a two-channel lookup table: red scales the material's
///         F0 and green is added to it.
///     </para>
///     <para>
///         Across the table, <c>x</c> is the cosine of the viewing angle and <c>y</c> is roughness.
///         The first column is a grazing angle, where the shading is most sensitive and the
///         integration is least well behaved; nothing samples exactly zero, because at zero there is
///         no reflection to integrate.
///     </para>
///     <para>
///         <b>At roughness zero the two channels sum to exactly one.</b> The GGX lobe collapses to a
///         mirror, the geometry term becomes one, and what is left is Schlick's Fresnel split into
///         its two pieces — so red is 1 − (1 − cosθ)⁵ and green is (1 − cosθ)⁵. That is an analytic
///         answer this can be checked against rather than compared to itself, which is the whole
///         reason the test exists.
///     </para>
/// </remarks>
public static class BrdfLut {
    /// <summary>How wide and tall the table is unless the caller says otherwise.</summary>
    public const int DefaultSize = 128;

    /// <summary>How many samples each cell integrates over unless the caller says otherwise.</summary>
    public const int DefaultSamples = 1024;

    /// <summary>Integrates the table.</summary>
    /// <param name="size">How wide and tall.</param>
    /// <param name="samples">How many directions each cell integrates over.</param>
    /// <returns>The table, in <see cref="PixelFormat.Rg16Float" />.</returns>
    public static TextureData Generate(int size = DefaultSize, int samples = DefaultSamples) {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(samples, 1);

        var table = new TextureData(PixelFormat.Rg16Float, size, size, levelCount: 1);
        var pixels = table.PixelSpan();

        for (var y = 0; y < size; y++) {
            var roughness = (y + 0.5f) / size;

            for (var x = 0; x < size; x++) {
                var cosine = (x + 0.5f) / size;
                var (scale, bias) = Integrate(cosine, roughness, samples);
                var texel = ((y * size) + x) * 4;

                BinaryPrimitives.WriteUInt16LittleEndian(pixels[texel..], BitConverter.HalfToUInt16Bits((Half)scale));
                BinaryPrimitives.WriteUInt16LittleEndian(
                    pixels[(texel + 2)..],
                    BitConverter.HalfToUInt16Bits((Half)bias)
                );
            }
        }

        return table;
    }

    /// <summary>Reads one cell.</summary>
    /// <param name="table">The table.</param>
    /// <param name="x">The column.</param>
    /// <param name="y">The row.</param>
    /// <returns>The scale for F0 and the bias to add to it.</returns>
    public static (float Scale, float Bias) Read(TextureData table, int x, int y) {
        ArgumentNullException.ThrowIfNull(table);

        var texel = ((y * table.Width) + x) * 4;
        var pixels = table.Pixels;

        return (
            (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(pixels[texel..])),
            (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(pixels[(texel + 2)..]))
        );
    }

    /// <summary>Integrates one cell.</summary>
    /// <param name="cosine">The cosine of the viewing angle.</param>
    /// <param name="roughness">The roughness.</param>
    /// <param name="samples">How many directions.</param>
    /// <returns>The scale for F0 and the bias to add to it.</returns>
    public static (float Scale, float Bias) Integrate(float cosine, float roughness, int samples) {
        var view = new Vector3(MathF.Sqrt(Math.Max(0f, 1f - (cosine * cosine))), 0f, cosine);
        var normal = Vector3.UnitZ;

        var scale = 0f;
        var bias = 0f;

        for (var sample = 0; sample < samples; sample++) {
            var half = EnvironmentPrefilter.ImportanceSampleGgx(
                EnvironmentPrefilter.Hammersley(sample, samples),
                roughness,
                normal
            );

            var light = (2f * Vector3.Dot(view, half) * half) - view;
            var lightCosine = light.Z;

            if (lightCosine <= 0f) {
                continue;
            }

            var halfCosine = Math.Max(half.Z, 1e-6f);
            var viewHalf = Math.Max(Vector3.Dot(view, half), 0f);

            // Schlick's Fresnel split so that F0 comes out of the integral: the scale multiplies it
            // and the bias replaces it, which is what lets one table serve every material.
            var fresnel = MathF.Pow(1f - viewHalf, 5f);
            var visibility = Geometry(cosine, lightCosine, roughness) * viewHalf
                / (halfCosine * Math.Max(cosine, 1e-6f));

            scale += (1f - fresnel) * visibility;
            bias += fresnel * visibility;
        }

        return (scale / samples, bias / samples);
    }

    /// <summary>
    ///     Smith's geometry term with the Schlick-GGX approximation, in its image-based form: the
    ///     roughness is remapped as α²/2 rather than the (roughness + 1)²/8 a direct light uses.
    ///     Using the direct-light remapping here is a well-known way to make every rough metal in a
    ///     scene too dark.
    /// </summary>
    static float Geometry(float viewCosine, float lightCosine, float roughness) {
        var alpha = roughness * roughness;
        var k = alpha / 2f;

        return Schlick(viewCosine, k) * Schlick(lightCosine, k);

        static float Schlick(float cosine, float k) => cosine / ((cosine * (1f - k)) + k);
    }
}
