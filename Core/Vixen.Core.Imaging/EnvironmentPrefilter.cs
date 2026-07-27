// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Core.Imaging;

/// <summary>Turns an environment map into the specular half of the split-sum approximation.</summary>
/// <remarks>
///     <para>
///         The split-sum approximation says that specular reflection of an environment can be
///         separated into two integrals that are each cheap: the environment convolved with the GGX
///         lobe, which depends on roughness and direction, and the BRDF's own response, which depends
///         on roughness and viewing angle and not on the environment at all. The first goes here, one
///         mip level per roughness; the second goes in <see cref="BrdfLut" /> and is the same texture
///         for every scene ever rendered.
///     </para>
///     <para>
///         <b>Level zero is the environment itself.</b> Roughness zero is a mirror, and a mirror
///         reflects what is there — so it is copied rather than integrated, which is both faster and
///         exact where importance sampling would only be close.
///     </para>
///     <para>
///         <b>This is the reference form, and it is slow.</b> Doc 03 asks for a CPU and a compute
///         version of this because reflection probes update at run time; what is here is the CPU one,
///         written to be read and checked rather than to be fast. It samples the source with nearest
///         filtering inside one face, so a high-roughness level wants a high sample count to stay
///         smooth — the saving grace being that high-roughness levels are the small ones.
///     </para>
/// </remarks>
public static class EnvironmentPrefilter {
    /// <summary>How many samples a level takes unless the caller says otherwise.</summary>
    public const int DefaultSamples = 128;

    /// <summary>Convolves an environment with the GGX lobe, one mip level per roughness.</summary>
    /// <param name="cube">The environment: six square faces in a float format.</param>
    /// <param name="levelCount">How many roughness steps, or zero for a full chain down to 1×1.</param>
    /// <param name="samples">How many directions each texel integrates over.</param>
    /// <returns>A new cube map whose level <c>i</c> is roughness <c>i / (levelCount − 1)</c>.</returns>
    /// <exception cref="ArgumentException">The source is not a square float cube map.</exception>
    public static TextureData Specular(TextureData cube, int levelCount = 0, int samples = DefaultSamples) {
        CubeMap.Require(cube, nameof(cube));
        ArgumentOutOfRangeException.ThrowIfNegative(levelCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(samples, 1);

        var prefiltered = new TextureData(cube.Format, cube.Width, cube.Height, levelCount, faceCount: CubeMap.Faces);

        for (var face = 0; face < CubeMap.Faces; face++) {
            var size = prefiltered.Levels[0].Width;

            for (var y = 0; y < size; y++) {
                for (var x = 0; x < size; x++) {
                    CubeMap.WriteTexel(prefiltered, 0, face, x, y, CubeMap.ReadTexel(cube, 0, face, x, y));
                }
            }
        }

        for (var level = 1; level < prefiltered.LevelCount; level++) {
            // A single level would be a mirror and nothing else, so there is no step to divide by.
            var roughness = prefiltered.LevelCount == 1 ? 0f : (float)level / (prefiltered.LevelCount - 1);
            var size = prefiltered.Levels[level].Width;

            for (var face = 0; face < CubeMap.Faces; face++) {
                for (var y = 0; y < size; y++) {
                    for (var x = 0; x < size; x++) {
                        var normal = CubeMap.DirectionOfTexel(face, x, y, size);
                        CubeMap.WriteTexel(prefiltered, level, face, x, y, Convolve(cube, normal, roughness, samples));
                    }
                }
            }
        }

        return prefiltered;
    }

    /// <summary>What roughness a prefiltered level stands for.</summary>
    /// <param name="level">The level.</param>
    /// <param name="levelCount">How many levels the chain has.</param>
    /// <returns>The roughness, zero to one.</returns>
    public static float RoughnessOf(int level, int levelCount) =>
        levelCount <= 1 ? 0f : (float)level / (levelCount - 1);

    /// <summary>
    ///     The GGX lobe around one direction, integrated against the environment. The normal, the
    ///     view and the reflection are all assumed to be the same direction — the approximation that
    ///     makes a prefiltered cube possible at all, and the reason a grazing reflection off a rough
    ///     surface is stretched in reality and round here.
    /// </summary>
    static Vector3 Convolve(TextureData cube, Vector3 normal, float roughness, int samples) {
        var total = Vector3.Zero;
        var weight = 0f;

        for (var sample = 0; sample < samples; sample++) {
            var half = ImportanceSampleGgx(Hammersley(sample, samples), roughness, normal);
            var light = (2f * Vector3.Dot(normal, half) * half) - normal;
            var cosine = Vector3.Dot(normal, light);

            if (cosine <= 0f) {
                continue;
            }

            total += CubeMap.Sample(cube, 0, light) * cosine;
            weight += cosine;
        }

        return weight > 0f ? total / weight : CubeMap.Sample(cube, 0, normal);
    }

    /// <summary>
    ///     The Hammersley sequence: the sample's index over the count, and its index with the bits
    ///     reversed. Two numbers that fill the unit square far more evenly than a random pair, which
    ///     is what lets a hundred and twenty-eight samples stand in for thousands.
    /// </summary>
    internal static (float X, float Y) Hammersley(int index, int count) {
        var bits = (uint)index;
        bits = (bits << 16) | (bits >> 16);
        bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
        bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
        bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
        bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);

        return ((float)index / count, bits * 2.3283064365386963e-10f);
    }

    /// <summary>
    ///     Picks a half-vector from the GGX distribution around a normal, so that samples land where
    ///     the lobe actually is. Sampling uniformly and weighting afterwards gives the same answer
    ///     and needs orders of magnitude more samples to stop being noise.
    /// </summary>
    internal static Vector3 ImportanceSampleGgx((float X, float Y) random, float roughness, Vector3 normal) {
        var alpha = roughness * roughness;
        var phi = 2f * MathF.PI * random.X;

        var cosineTheta = MathF.Sqrt((1f - random.Y) / (1f + (((alpha * alpha) - 1f) * random.Y)));
        var sineTheta = MathF.Sqrt(Math.Max(0f, 1f - (cosineTheta * cosineTheta)));

        var local = new Vector3(sineTheta * MathF.Cos(phi), sineTheta * MathF.Sin(phi), cosineTheta);

        // Any frame will do as long as it is orthonormal; the lobe is rotationally symmetric about
        // the normal, so which way "across" points cannot matter.
        var up = MathF.Abs(normal.Z) < 0.999f ? Vector3.UnitZ : Vector3.UnitX;
        var across = Vector3.Normalize(Vector3.Cross(up, normal));
        var along = Vector3.Cross(normal, across);

        return Vector3.Normalize((across * local.X) + (along * local.Y) + (normal * local.Z));
    }
}
