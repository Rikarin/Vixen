// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.Lighting;

/// <summary>
///     Turns an environment into the two things a frame reads: a prefiltered mip chain and nine
///     coefficients.
/// </summary>
/// <remarks>
///     <para>
///         The producing half of the split-sum approximation, which the engine had no answer for at
///         all — the shader library has had <c>Ibl.SpecularLod</c> and <c>Ibl.IrradianceSh9</c> since
///         it was written, and nothing anywhere produced a chain for the first to index or
///         coefficients for the second to evaluate.
///     </para>
///     <para>
///         On the CPU, and offline. A bake is a per-environment cost, not a per-frame one, and doing
///         it here means it is deterministic, testable against closed forms without a device, and
///         belongs to the asset pipeline where the result can be stored beside the source.
///     </para>
/// </remarks>
public static class EnvironmentBaker {
    /// <summary>
    ///     The roughness a mip level of the prefiltered chain holds.
    /// </summary>
    /// <remarks>
    ///     <strong>The inverse of <c>Ibl.SpecularLod</c>, and the contract between them.</strong> The
    ///     shader picks a level as <c>roughness × (mipCount − 1)</c>; this fills the level that
    ///     choice lands on. If the two disagree, a material's reflection is sharper or blurrier than
    ///     its roughness says — which reads as a material that was authored wrong, everywhere at
    ///     once, and never as a mismatched mapping.
    /// </remarks>
    public static float RoughnessOf(int mip, int mipCount) {
        ArgumentOutOfRangeException.ThrowIfNegative(mip);
        ArgumentOutOfRangeException.ThrowIfLessThan(mipCount, 1);

        return mipCount <= 1 ? 0f : Math.Clamp((float)mip / (mipCount - 1), 0f, 1f);
    }

    /// <summary>
    ///     Prefilters an environment against the GGX lobe, one mip per roughness.
    /// </summary>
    /// <param name="source">The environment, as radiance.</param>
    /// <param name="mipCount">How many levels the chain has.</param>
    /// <param name="samples">How many importance samples each texel integrates.</param>
    /// <remarks>
    ///     <para>
    ///         Karis's approximation, and it is worth naming what it approximates away: the split-sum
    ///         assumes the view direction is the normal, so the lobe is isotropic and one chain serves
    ///         every viewing angle. The cost is that grazing reflections are rounder than they should
    ///         be — the streak along a wet road is the case it cannot reproduce.
    ///     </para>
    ///     <para>
    ///         Level zero is the source, not an integral over it: at roughness zero the GGX lobe is a
    ///         delta and sampling it hundreds of times returns the same texel with a slightly
    ///         different answer each run. Copying is both faster and exact.
    ///     </para>
    ///     <para>
    ///         Weighted by <c>NdotL</c> rather than uniformly, which is the difference between this
    ///         and a blur. It is what keeps a bright horizon from bleeding into a surface facing away
    ///         from it.
    ///     </para>
    /// </remarks>
    public static CubeImage[] Prefilter(CubeImage source, int mipCount, int samples = 64) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(mipCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(samples, 1);

        var chain = new CubeImage[mipCount];
        chain[0] = Copy(source);

        for (var mip = 1; mip < mipCount; mip++) {
            var size = Math.Max(1, source.Size >> mip);
            var level = new CubeImage(size);
            var alpha = Square(RoughnessOf(mip, mipCount));

            foreach (var face in CubeMapping.Faces) {
                for (var y = 0; y < size; y++) {
                    for (var x = 0; x < size; x++) {
                        level.At(face, x, y) = Integrate(source, level.DirectionOf(face, x, y), alpha, samples);
                    }
                }
            }

            chain[mip] = level;
        }

        return chain;
    }

    /// <summary>One texel of one level: the environment convolved with a GGX lobe about it.</summary>
    static Vector3 Integrate(CubeImage source, Vector3 normal, float alpha, int samples) {
        var total = Vector3.Zero;
        var weight = 0f;

        for (var index = 0; index < samples; index++) {
            var half = ImportanceSampleGgx(Hammersley(index, samples), alpha, normal);

            // The reflection of the normal about the sampled half vector, which is the light
            // direction that half vector would reflect toward the viewer — with view equal to
            // normal, as the split-sum assumes.
            var light = (2f * Vector3.Dot(normal, half) * half) - normal;
            var cosine = Vector3.Dot(normal, light);

            if (cosine <= 0f) {
                continue;
            }

            total += source.Sample(light) * cosine;
            weight += cosine;
        }

        return weight > 0f ? total / weight : source.Sample(normal);
    }

    /// <summary>A GGX-distributed half vector about <paramref name="normal" />.</summary>
    /// <remarks>
    ///     The same distribution as <c>Sampling.ImportanceSampleGgx</c>, evaluated here rather than
    ///     shared: nothing on the GPU prefilters, so this is not a second implementation of a live
    ///     path — it is the only one, and the shader's copy is for the passes that sample a lobe at
    ///     run time.
    /// </remarks>
    static Vector3 ImportanceSampleGgx(Vector2 random, float alpha, Vector3 normal) {
        var phi = 2f * MathF.PI * random.X;

        // The inverse CDF of the GGX normal distribution. At alpha zero this collapses to the
        // normal, which is what a mirror is.
        var cosTheta = MathF.Sqrt((1f - random.Y) / (1f + (((alpha * alpha) - 1f) * random.Y)));
        var sinTheta = MathF.Sqrt(Math.Max(1f - (cosTheta * cosTheta), 0f));

        var tangentSpace = new Vector3(sinTheta * MathF.Cos(phi), sinTheta * MathF.Sin(phi), cosTheta);

        // An arbitrary basis about the normal. Which one does not matter — the distribution is
        // rotationally symmetric about it — only that it is orthonormal and never degenerate, which
        // is what choosing the up vector by the normal's smallest component buys.
        var up = MathF.Abs(normal.Z) < 0.999f ? new Vector3(0f, 0f, 1f) : new Vector3(1f, 0f, 0f);
        var tangent = Vector3.Normalize(Vector3.Cross(up, normal));
        var bitangent = Vector3.Cross(normal, tangent);

        return Vector3.Normalize(
            (tangent * tangentSpace.X) + (bitangent * tangentSpace.Y) + (normal * tangentSpace.Z)
        );
    }

    /// <summary>The Hammersley point set: a radical inverse against the index.</summary>
    static Vector2 Hammersley(int index, int count) => new((float)index / count, RadicalInverse((uint)index));

    /// <summary>Van der Corput's radical inverse in base two, by bit reversal.</summary>
    static float RadicalInverse(uint bits) {
        bits = (bits << 16) | (bits >> 16);
        bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
        bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
        bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
        bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
        return bits * 2.3283064365386963e-10f;
    }

    static CubeImage Copy(CubeImage source) {
        var copy = new CubeImage(source.Size);

        foreach (var face in CubeMapping.Faces) {
            source.Face(face).CopyTo(copy.Face(face));
        }

        return copy;
    }

    static float Square(float value) => value * value;
}
