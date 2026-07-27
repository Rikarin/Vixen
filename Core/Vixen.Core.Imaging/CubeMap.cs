// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Vixen.Core.Mathematics;
using Vixen.Graphics;

namespace Vixen.Core.Imaging;

/// <summary>Where a cube map's texels point, and how much sky each of them covers.</summary>
/// <remarks>
///     <para>
///         Six faces in the order KTX2 and every graphics API store them: +X, −X, +Y, −Y, +Z, −Z.
///         Within a face, <c>u</c> runs left to right and <c>v</c> runs <i>top to bottom</i>, which is
///         the part that trips people: a cube face's second axis points down, so the mapping has
///         minus signs in it that look like mistakes and are not.
///     </para>
///     <para>
///         <b>A texel's solid angle is not constant.</b> A cube is a bad sphere: the texel at the
///         centre of a face covers noticeably more sky than the one at its corner, by a factor of
///         about five at any reasonable size. Every integral over an environment — irradiance,
///         prefiltering, the total energy in a probe — is wrong by that factor if it treats texels as
///         equal, and wrong in a way that looks like a lighting bug rather than a geometry one.
///         <see cref="SolidAngleOfTexel" /> is the correction, and the test that all of them sum to
///         4π is the check that it is right.
///     </para>
/// </remarks>
public static class CubeMap {
    /// <summary>How many faces a cube map has.</summary>
    public const int Faces = 6;

    /// <summary>Which way a point on a face points.</summary>
    /// <param name="face">The face, 0 to 5, in the order +X, −X, +Y, −Y, +Z, −Z.</param>
    /// <param name="u">Across the face, −1 to 1.</param>
    /// <param name="v">Down the face, −1 to 1.</param>
    /// <returns>The direction, normalised.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such face.</exception>
    public static Vector3 DirectionOf(int face, float u, float v) => Vector3.Normalize(
        face switch {
            0 => new(1f, -v, -u),
            1 => new(-1f, -v, u),
            2 => new(u, 1f, v),
            3 => new(u, -1f, -v),
            4 => new(u, -v, 1f),
            5 => new(-u, -v, -1f),
            _ => throw new ArgumentOutOfRangeException(nameof(face), face, "A cube map has six faces.")
        }
    );

    /// <summary>Which way the centre of a texel points.</summary>
    /// <param name="face">The face.</param>
    /// <param name="x">The texel's column.</param>
    /// <param name="y">The texel's row.</param>
    /// <param name="size">How wide the face is.</param>
    /// <returns>The direction, normalised.</returns>
    public static Vector3 DirectionOfTexel(int face, int x, int y, int size) =>
        DirectionOf(face, Coordinate(x, size), Coordinate(y, size));

    /// <summary>How much of the sphere one texel covers, in steradians.</summary>
    /// <param name="x">The texel's column.</param>
    /// <param name="y">The texel's row.</param>
    /// <param name="size">How wide the face is.</param>
    /// <returns>The solid angle.</returns>
    /// <remarks>
    ///     <para>
    ///         The face is flat and the sphere is not: a texel's area on the cube is the same
    ///         everywhere, and the amount of sphere it projects onto falls off as the surface tilts
    ///         away from the face centre.
    ///     </para>
    ///     <para>
    ///         <b>This is the exact area of the texel's spherical quadrilateral, not the projected
    ///         area of its centre.</b> The obvious formula — the texel's area over
    ///         (1 + u² + v²) to the three halves — is the midpoint rule, and it is off by one and a
    ///         half per cent on a 4×4 face and does not sum to 4π at any size. The exact one
    ///         telescopes: summed over a whole face it collapses to four corner terms, and over six
    ///         faces to 4π on the nose. That turns
    ///         <c>AllTheSolidAnglesOfACubeSumToTheWholeSphere</c> from an approximation with a
    ///         tolerance into an equality, which is worth having for the one function every integral
    ///         in this file depends on.
    ///     </para>
    /// </remarks>
    public static float SolidAngleOfTexel(int x, int y, int size) {
        var step = 2f / size;
        var left = (2f * x / size) - 1f;
        var top = (2f * y / size) - 1f;

        return Corner(left, top) - Corner(left, top + step) - Corner(left + step, top)
            + Corner(left + step, top + step);

        // The area of the spherical triangle a corner subtends with the axes.
        static float Corner(float u, float v) => MathF.Atan2(u * v, MathF.Sqrt((u * u) + (v * v) + 1f));
    }

    /// <summary>Which face and where on it a direction lands.</summary>
    /// <param name="direction">The direction. Need not be normalised.</param>
    /// <returns>The face and the coordinates on it, each −1 to 1.</returns>
    /// <exception cref="ArgumentException">The direction is zero and points at no face.</exception>
    public static (int Face, float U, float V) FaceOf(Vector3 direction) {
        var absoluteX = MathF.Abs(direction.X);
        var absoluteY = MathF.Abs(direction.Y);
        var absoluteZ = MathF.Abs(direction.Z);

        if (absoluteX >= absoluteY && absoluteX >= absoluteZ) {
            if (absoluteX <= 0f) {
                throw new ArgumentException("A zero direction points at no face.", nameof(direction));
            }

            return direction.X > 0f
                ? (0, -direction.Z / absoluteX, -direction.Y / absoluteX)
                : (1, direction.Z / absoluteX, -direction.Y / absoluteX);
        }

        if (absoluteY >= absoluteZ) {
            return direction.Y > 0f
                ? (2, direction.X / absoluteY, direction.Z / absoluteY)
                : (3, direction.X / absoluteY, -direction.Z / absoluteY);
        }

        return direction.Z > 0f
            ? (4, direction.X / absoluteZ, -direction.Y / absoluteZ)
            : (5, -direction.X / absoluteZ, -direction.Y / absoluteZ);
    }

    /// <summary>Reads the texel a direction lands on.</summary>
    /// <param name="cube">The cube map.</param>
    /// <param name="level">Which mip level.</param>
    /// <param name="direction">The direction.</param>
    /// <returns>The radiance there.</returns>
    /// <remarks>
    ///     Nearest, and within one face. Filtering across a face boundary means reaching into another
    ///     face's texels with a different orientation, which is worth doing in the compute form and
    ///     is not what a reference implementation is for. The seam it leaves is one texel wide and
    ///     shrinks with the face size.
    /// </remarks>
    public static Vector3 Sample(TextureData cube, int level, Vector3 direction) {
        ArgumentNullException.ThrowIfNull(cube);

        var (face, u, v) = FaceOf(direction);
        var described = cube.Levels[level];
        var x = Math.Clamp((int)((u + 1f) * 0.5f * described.Width), 0, described.Width - 1);
        var y = Math.Clamp((int)((v + 1f) * 0.5f * described.Height), 0, described.Height - 1);

        return ReadTexel(cube, level, face, x, y);
    }

    /// <summary>Reads one texel.</summary>
    /// <param name="cube">The cube map.</param>
    /// <param name="level">Which mip level.</param>
    /// <param name="face">Which face.</param>
    /// <param name="x">The texel's column.</param>
    /// <param name="y">The texel's row.</param>
    /// <returns>The radiance there.</returns>
    /// <exception cref="NotSupportedException">The format is not a float one.</exception>
    public static Vector3 ReadTexel(TextureData cube, int level, int face, int x, int y) {
        ArgumentNullException.ThrowIfNull(cube);

        var described = cube.Levels[level];
        var stride = (int)cube.Format.LevelSize(described.Width, described.Height);
        var image = cube.Level(level).Slice(face * stride, stride);
        var texel = (y * described.Width) + x;

        return cube.Format switch {
            PixelFormat.Rgba16Float => new(
                (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(image[(texel * 8)..])),
                (float)BitConverter.UInt16BitsToHalf(
                    BinaryPrimitives.ReadUInt16LittleEndian(image[((texel * 8) + 2)..])
                ),
                (float)BitConverter.UInt16BitsToHalf(
                    BinaryPrimitives.ReadUInt16LittleEndian(image[((texel * 8) + 4)..])
                )
            ),
            PixelFormat.Rgba32Float => new(
                BinaryPrimitives.ReadSingleLittleEndian(image[(texel * 16)..]),
                BinaryPrimitives.ReadSingleLittleEndian(image[((texel * 16) + 4)..]),
                BinaryPrimitives.ReadSingleLittleEndian(image[((texel * 16) + 8)..])
            ),
            _ => throw new NotSupportedException(
                $"{cube.Format} is not a format an environment map is read from. Radiance has no upper bound, "
                + "so it lives in Rgba16Float or Rgba32Float."
            )
        };
    }

    /// <summary>Writes one texel, leaving alpha at one.</summary>
    /// <param name="cube">The cube map.</param>
    /// <param name="level">Which mip level.</param>
    /// <param name="face">Which face.</param>
    /// <param name="x">The texel's column.</param>
    /// <param name="y">The texel's row.</param>
    /// <param name="radiance">What to write.</param>
    /// <exception cref="NotSupportedException">The format is not a float one.</exception>
    public static void WriteTexel(TextureData cube, int level, int face, int x, int y, Vector3 radiance) {
        ArgumentNullException.ThrowIfNull(cube);

        var described = cube.Levels[level];
        var stride = (int)cube.Format.LevelSize(described.Width, described.Height);
        var image = cube.LevelSpan(level).Slice(face * stride, stride);
        var texel = (y * described.Width) + x;

        switch (cube.Format) {
            case PixelFormat.Rgba16Float:
                for (var channel = 0; channel < 3; channel++) {
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        image[((texel * 8) + (channel * 2))..],
                        BitConverter.HalfToUInt16Bits((Half)radiance[channel])
                    );
                }

                BinaryPrimitives.WriteUInt16LittleEndian(image[((texel * 8) + 6)..], 0x3C00);
                return;

            case PixelFormat.Rgba32Float:
                for (var channel = 0; channel < 3; channel++) {
                    BinaryPrimitives.WriteSingleLittleEndian(
                        image[((texel * 16) + (channel * 4))..],
                        radiance[channel]
                    );
                }

                BinaryPrimitives.WriteSingleLittleEndian(image[((texel * 16) + 12)..], 1f);
                return;

            default:
                throw new NotSupportedException($"{cube.Format} is not a format an environment map is written to.");
        }
    }

    /// <summary>Checks that a texture is a square cube map in a float format, and says what is wrong if not.</summary>
    /// <param name="cube">The texture.</param>
    /// <param name="name">The parameter's name, for the exception.</param>
    /// <exception cref="ArgumentException">It is not one.</exception>
    public static void Require(TextureData cube, string name) {
        ArgumentNullException.ThrowIfNull(cube);

        if (cube.FaceCount != Faces) {
            throw new ArgumentException($"An environment map has six faces; this has {cube.FaceCount}.", name);
        }

        if (cube.Width != cube.Height) {
            throw new ArgumentException(
                $"A cube map's faces are square; these are {cube.Width}×{cube.Height}.",
                name
            );
        }

        if (cube.Format is not (PixelFormat.Rgba16Float or PixelFormat.Rgba32Float)) {
            throw new ArgumentException(
                $"{cube.Format} cannot hold radiance, which has no upper bound. Use Rgba16Float or Rgba32Float.",
                name
            );
        }
    }

    /// <summary>Where the centre of texel <paramref name="index" /> sits, from −1 to 1.</summary>
    static float Coordinate(int index, int size) => (2f * (index + 0.5f) / size) - 1f;
}
