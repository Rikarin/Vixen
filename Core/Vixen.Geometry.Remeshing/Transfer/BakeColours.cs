// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>A picture a colour bake reads from, as numbers, with no file in sight.</summary>
/// <param name="Width">Its width in pixels.</param>
/// <param name="Height">Its height in pixels.</param>
/// <param name="Pixels">Row-major from <c>v = 0</c>, the image's top, as every baked map is.</param>
/// <remarks>
///     ⚠ <b>Read between pixels and repeating</b>, as a renderer reads a texture: a coordinate outside
///     the unit square is the same picture again, which is what a tiling material's coordinates mean.
/// </remarks>
public sealed record BakeImage(int Width, int Height, IReadOnlyList<Vector4> Pixels) {
    /// <summary>The picture at a coordinate, blended from the four pixels round it.</summary>
    /// <param name="uv">The coordinate.</param>
    /// <returns>The value.</returns>
    public Vector4 At(Vector2 uv) {
        if (Width <= 0 || Height <= 0 || Pixels.Count < Width * Height) {
            return Vector4.One;
        }

        var x = Wrap(uv.X) * Width - 0.5f;
        var y = Wrap(uv.Y) * Height - 0.5f;
        var (left, top) = ((int)MathF.Floor(x), (int)MathF.Floor(y));
        var (across, down) = (x - left, y - top);

        var upper = Vector4.Lerp(Pixel(left, top), Pixel(left + 1, top), across);
        var lower = Vector4.Lerp(Pixel(left, top + 1), Pixel(left + 1, top + 1), across);

        return Vector4.Lerp(upper, lower, down);
    }

    Vector4 Pixel(int x, int y) => Pixels[Mod(y, Height) * Width + Mod(x, Width)];

    static float Wrap(float value) => value - MathF.Floor(value);

    static int Mod(int value, int size) => (value % size + size) % size;
}

/// <summary>What colour each point of the source is, for a bake that carries it onto the target.</summary>
/// <param name="Corners">A colour per source corner, or null for white: vertex colours, or a material's colour.</param>
/// <param name="Images">The pictures the source's faces read, at the source's own coordinates.</param>
/// <param name="FaceImages">
///     Which of <paramref name="Images" /> each source face reads, <c>-1</c> for none, or null for
///     the first.
/// </param>
/// <remarks>
///     <para>
///         <b>A colour is the corners' blend times the face's picture</b>, which is how a material with a
///         base colour factor and a texture, or a mesh with vertex colours, is drawn. Either half may be
///         absent and reads one.
///     </para>
///     <para>
///         ⚠ <b>This is the transfer from one layout to another as well.</b> A source that is the target
///         with its old coordinates, and a picture made for those coordinates, bakes the same picture onto
///         the target's new ones.
///     </para>
/// </remarks>
public sealed record BakeColours(
    IReadOnlyList<Vector4>? Corners,
    IReadOnlyList<BakeImage> Images,
    IReadOnlyList<int>? FaceImages = null
);
