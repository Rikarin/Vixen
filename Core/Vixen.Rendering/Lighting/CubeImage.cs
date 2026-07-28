// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.Lighting;

/// <summary>
///     A cube map in memory, linear and floating point: what a bake reads and writes.
/// </summary>
/// <remarks>
///     <para>
///         Deliberately not a <c>Texture</c>. Everything that produces an environment — projecting it
///         into spherical harmonics, prefiltering it per roughness — is arithmetic over radiance
///         values, and doing it here rather than on the GPU means it is deterministic, testable
///         without a device, and runs where a bake belongs: in the asset pipeline, once, rather than
///         in a frame.
///     </para>
///     <para>
///         Linear radiance, not encoded colour. An environment prefiltered in sRGB is a different
///         integral from one prefiltered in linear, and it is wrong in the direction that looks
///         plausible — slightly washed out, which reads as the tone mapping.
///     </para>
/// </remarks>
public sealed class CubeImage {
    readonly Vector3[] pixels;

    /// <summary>How many texels across one face is.</summary>
    public int Size { get; }

    /// <summary>Every face's texels, in layer order, each face row-major.</summary>
    public ReadOnlySpan<Vector3> Pixels => pixels;

    /// <summary>Creates an empty cube of a given face size.</summary>
    public CubeImage(int size) {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);

        Size = size;
        pixels = new Vector3[6 * size * size];
    }

    /// <summary>One face's texels, row-major.</summary>
    public Span<Vector3> Face(CubeFace face) => pixels.AsSpan((int)face * Size * Size, Size * Size);

    /// <summary>One texel, by face and integer coordinates.</summary>
    public ref Vector3 At(CubeFace face, int x, int y) => ref pixels[(((int)face * Size) + y) * Size + x];

    /// <summary>Where the centre of a texel looks.</summary>
    public Vector3 DirectionOf(CubeFace face, int x, int y) {
        var (u, v) = Coordinates(x, y);
        return CubeMapping.Direction(face, u, v);
    }

    /// <summary>How much of the sphere a texel covers, in steradians.</summary>
    public float SolidAngleOf(int x, int y) {
        var texel = 2f / Size;
        return CubeMapping.SolidAngle((x * texel) - 1f, (y * texel) - 1f, texel);
    }

    /// <summary>The texel a direction lands on.</summary>
    /// <remarks>
    ///     Nearest rather than bilinear, and that is a decision about where the filtering belongs: a
    ///     prefilter integrates hundreds of samples over a lobe, so interpolating each one buys
    ///     smoothness the integral already has, at four times the memory traffic. What it does not
    ///     survive is undersampling, which is why <see cref="EnvironmentBaker" /> reduces the source
    ///     rather than taking fewer samples.
    /// </remarks>
    public Vector3 Sample(Vector3 direction) {
        var (face, u, v) = CubeMapping.Locate(direction);

        var x = Math.Clamp((int)(((u + 1f) * 0.5f) * Size), 0, Size - 1);
        var y = Math.Clamp((int)(((v + 1f) * 0.5f) * Size), 0, Size - 1);

        return At(face, x, y);
    }

    /// <summary>A cube of one colour, which is the case every analytic expectation is written for.</summary>
    public static CubeImage Uniform(int size, Vector3 radiance) {
        var image = new CubeImage(size);
        image.pixels.AsSpan().Fill(radiance);
        return image;
    }

    /// <summary>The centre of texel (x, y) in face coordinates, −1 to 1.</summary>
    (float U, float V) Coordinates(int x, int y) {
        var texel = 2f / Size;
        return (((x + 0.5f) * texel) - 1f, ((y + 0.5f) * texel) - 1f);
    }
}
