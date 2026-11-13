// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Uv;

/// <summary>One unit square of a layout, counted as UDIM counts them: across, then up.</summary>
/// <param name="U">How many squares across, from 0.</param>
/// <param name="V">How many squares up, from 0.</param>
public readonly record struct UvTile(int U, int V) {
    /// <summary>The tile's UDIM number, <c>1001 + U + 10V</c>, or null for a square UDIM has no number for.</summary>
    public int? Udim => U is >= 0 and < 10 && V >= 0 ? 1001 + U + 10 * V : null;

    /// <summary>
    ///     A coordinate in this tile moved by whole squares onto the first, as a texture of this tile alone reads it:
    ///     u less the squares across, and v, which a reader runs down, plus the rows up.
    /// </summary>
    /// <param name="coordinate">The coordinate, as a reader hands it.</param>
    /// <returns>The same place in tile 1001.</returns>
    public Vector2 Home(Vector2 coordinate) => new(coordinate.X - U, coordinate.Y + V);

    /// <summary>The UDIM number, or the two counts where there is none.</summary>
    /// <returns>The text.</returns>
    public override string ToString() =>
        Udim is { } udim
            ? udim.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"({U}, {V})");
}

/// <summary>Which unit squares a layout's triangles sit in.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A triangle is in the square its middle is in, not every square a corner touches.</b> A packer may
///         put a corner a hair over an edge, and a layout that tiles a texture on purpose runs triangles far past
///         one. Reading corners would call the first a second tile; the middle reads it as the tile it was meant
///         for, and still sees the second.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The coordinates are as a reader hands them, with v running down the image, and the rows count up
///             as UDIM's do.
///         </b>
///         A reader flips v so that 0 is a picture's top row, which puts the file's second row
///         of tiles at v from −1 to 0 here. Counting rows by the flipped v would number tile 1011 as a negative
///         row and tile 1001 as row 0 only by luck.
///     </para>
/// </remarks>
public static class UvTiles {
    /// <summary>The square a triangle sits in.</summary>
    /// <param name="a">Its first corner's coordinate.</param>
    /// <param name="b">Its second's.</param>
    /// <param name="c">Its third's.</param>
    /// <returns>The square its middle is in.</returns>
    public static UvTile Of(Vector2 a, Vector2 b, Vector2 c) {
        var middle = (a + b + c) / 3f;

        return new((int)MathF.Floor(middle.X), (int)MathF.Floor(1f - middle.Y));
    }

    /// <summary>The one square every triangle of a layout sits in, when there is one.</summary>
    /// <param name="corners">Three coordinates per triangle.</param>
    /// <returns>The square, or null for no triangles or for triangles in more than one square.</returns>
    /// <exception cref="ArgumentException">The coordinates are not whole triangles.</exception>
    public static UvTile? Only(ReadOnlySpan<Vector2> corners) =>
        Reached(corners) is [var only] ? only : null;

    /// <summary>Every square a list of triangles reaches, row by row from the bottom and across each row.</summary>
    /// <param name="corners">Three coordinates per triangle.</param>
    /// <returns>The squares, each once. Empty for no triangles.</returns>
    /// <exception cref="ArgumentException">The coordinates are not whole triangles.</exception>
    public static IReadOnlyList<UvTile> Reached(ReadOnlySpan<Vector2> corners) {
        if (corners.Length % 3 != 0) {
            throw new ArgumentException($"{corners.Length} coordinates are not whole triangles.", nameof(corners));
        }

        var reached = new HashSet<UvTile>();

        for (var at = 0; at < corners.Length; at += 3) {
            reached.Add(Of(corners[at], corners[at + 1], corners[at + 2]));
        }

        return [..reached.OrderBy(static tile => tile.V).ThenBy(static tile => tile.U)];
    }
}
