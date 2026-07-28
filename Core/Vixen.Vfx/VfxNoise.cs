// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Vixen.Core.Mathematics;

namespace Vixen.Vfx;

/// <summary>
///     A noise field a compute shader can reproduce, and the curl of one.
/// </summary>
/// <remarks>
///     <para>
///         <b>Value noise over a lattice, not gradient noise.</b> Perlin and simplex both need a
///         table of gradients, and a table is the one thing the GPU side would have to be given
///         rather than compute — a uniform buffer, an upload, and a way for the two to disagree
///         about its contents. Value noise needs nothing but the hash that is already here: the
///         corners of the unit cell hash to numbers, and the point between them is an interpolation.
///         It has visible axis alignment at low frequencies, which is why turbulence sums octaves.
///     </para>
///     <para>
///         <b>Curl, because divergence-free is what makes it read as fluid.</b> Sampling noise
///         straight into a velocity gives particles that pile up wherever the field points inward
///         and thin out where it points out — a field with sources and sinks, which no fluid has.
///         The curl of any vector field has zero divergence identically, so taking one costs three
///         extra samples per axis and buys smoke that swirls instead of smoke that clumps.
///     </para>
///     <para>
///         <b>Every step is exact or nearly so on both sides.</b> The lattice hash is the integer
///         one from <see cref="VfxRandom" />, so the corner values agree bit for bit; the
///         interpolation and the finite differences are ordinary float arithmetic, which agrees to
///         the last bit or two. That is the same honest claim the rest of the module makes.
///     </para>
/// </remarks>
public static class VfxNoise {
    /// <summary>How far apart the samples of the finite difference are.</summary>
    /// <remarks>
    ///     <para>
    ///         A curl is a derivative and this is the step it is taken over. Too small and the
    ///         difference of two nearly equal floats loses its significant bits; too large and the
    ///         field is smoothed into something that no longer swirls at the scale it was asked for.
    ///         Ten centimetres of lattice space sits between the two for the frequencies an effect
    ///         actually uses.
    ///     </para>
    ///     <para>
    ///         A constant rather than a parameter, because it has to be the same number on both
    ///         backends and a caller who could change it would be a caller who could make them
    ///         disagree.
    ///     </para>
    /// </remarks>
    public const float Epsilon = 0.1f;

    /// <summary>A value in [0, 1) that varies smoothly with position.</summary>
    /// <param name="point">Where to sample.</param>
    /// <param name="seed">Which field. Two seeds give unrelated fields.</param>
    /// <returns>The value.</returns>
    public static float Value(Vector3 point, uint seed) {
        var cellX = (int)MathF.Floor(point.X);
        var cellY = (int)MathF.Floor(point.Y);
        var cellZ = (int)MathF.Floor(point.Z);

        // Smoothstep rather than the raw fraction. Linear interpolation between lattice values has a
        // discontinuous derivative at every cell boundary, which shows up as a grid of creases in the
        // velocity field — visible in the motion long before it is visible in the noise itself.
        var fx = Smooth(point.X - cellX);
        var fy = Smooth(point.Y - cellY);
        var fz = Smooth(point.Z - cellZ);

        var x0y0 = Mix(Corner(cellX, cellY, cellZ, seed), Corner(cellX, cellY, cellZ + 1, seed), fz);
        var x0y1 = Mix(Corner(cellX, cellY + 1, cellZ, seed), Corner(cellX, cellY + 1, cellZ + 1, seed), fz);
        var x1y0 = Mix(Corner(cellX + 1, cellY, cellZ, seed), Corner(cellX + 1, cellY, cellZ + 1, seed), fz);
        var x1y1 = Mix(Corner(cellX + 1, cellY + 1, cellZ, seed), Corner(cellX + 1, cellY + 1, cellZ + 1, seed), fz);

        return Mix(Mix(x0y0, x0y1, fy), Mix(x1y0, x1y1, fy), fx);
    }

    /// <summary>The curl of a three-channel noise potential: a divergence-free vector field.</summary>
    /// <param name="point">Where to sample.</param>
    /// <param name="seed">Which field.</param>
    /// <returns>A vector whose divergence is zero to the accuracy of the difference.</returns>
    /// <remarks>
    ///     The three channels are three <em>fields</em> rather than three offsets into one, because
    ///     offsetting one field would correlate the components along the offset direction — a
    ///     turbulence that is subtly stripey in one axis, which reads as a rendering artefact rather
    ///     than as a choice of noise.
    /// </remarks>
    public static Vector3 Curl(Vector3 point, uint seed) {
        // ∂z of the y potential minus ∂y of the z potential, and round. Each derivative is a central
        // difference, which is second-order accurate where a forward difference is first — worth the
        // extra sample when the whole point is that the divergence comes out near zero.
        var dyOfZ = Derivative(point, seed + 2, 1);
        var dzOfY = Derivative(point, seed + 1, 2);
        var dzOfX = Derivative(point, seed, 2);
        var dxOfZ = Derivative(point, seed + 2, 0);
        var dxOfY = Derivative(point, seed + 1, 0);
        var dyOfX = Derivative(point, seed, 1);

        return new(dyOfZ - dzOfY, dzOfX - dxOfZ, dxOfY - dyOfX);
    }

    /// <summary>Several octaves of curl, each half the amplitude and twice the frequency.</summary>
    /// <param name="point">Where to sample.</param>
    /// <param name="seed">Which field.</param>
    /// <param name="octaves">How many. One is the plain curl.</param>
    /// <returns>The summed field.</returns>
    /// <remarks>
    ///     <b>Octaves are what hide the lattice.</b> One octave of value noise is visibly aligned to
    ///     its grid; three are not, because the second and third put detail where the first is flat.
    ///     Summing curls rather than curling a sum is the same field — curl is linear — and is the
    ///     order that lets each octave keep its own seed.
    /// </remarks>
    public static Vector3 Turbulence(Vector3 point, uint seed, int octaves) {
        var total = Vector3.Zero;
        var amplitude = 1f;
        var frequency = 1f;

        for (var octave = 0; octave < octaves; octave++) {
            total += Curl(point * frequency, seed + ((uint)octave * 3)) * amplitude;
            amplitude *= 0.5f;
            frequency *= 2f;
        }

        return total;
    }

    /// <summary>One partial derivative of one noise channel, as a central difference.</summary>
    static float Derivative(Vector3 point, uint seed, int axis) {
        var step = Vector3.Zero;

        switch (axis) {
            case 0: {
                step = new(Epsilon, 0f, 0f);

                break;
            }

            case 1: {
                step = new(0f, Epsilon, 0f);

                break;
            }

            default: {
                step = new(0f, 0f, Epsilon);

                break;
            }
        }

        return (Value(point + step, seed) - Value(point - step, seed)) / (2f * Epsilon);
    }

    /// <summary>The lattice value at an integer corner.</summary>
    /// <remarks>
    ///     The three coordinates are hashed in turn rather than combined arithmetically, for the
    ///     reason <see cref="VfxRandom.Hash(uint, uint, uint)" /> gives: adding them first would make
    ///     every corner on a diagonal plane share a value, which is a visible lattice of its own.
    ///     Negative coordinates wrap into <c>uint</c> the same way on both sides, since two's
    ///     complement is what every target uses.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float Corner(int x, int y, int z, uint seed) =>
        (VfxRandom.Hash(VfxRandom.Hash(VfxRandom.Hash((uint)x) ^ (uint)y) ^ (uint)z, seed, 0) >> 8) * (1f / (1 << 24));

    /// <summary>The smoothstep interpolant, 3t² − 2t³.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float Smooth(float t) => t * t * (3f - (2f * t));

    /// <summary>Linear interpolation written the way the emitter writes it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float Mix(float a, float b, float t) => a + ((b - a) * t);
}
