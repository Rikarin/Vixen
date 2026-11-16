// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>Where each texel of an atlas lies in its UV island's bounding square.</summary>
/// <remarks>
///     <para>
///         <b>An island's square is the box round the texels its charts claimed, grown along its shorter side.</b> A
///         texel reads its centre's place across the square in <c>X</c> and down it in <c>Y</c>, from nought at the
///         square's top left corner to one at its far side, and the square's side as a share of the atlas's longer side
///         in <c>Z</c>. Over the longer side alone, so a square drawn in the frame is square on the atlas too.
///     </para>
///     <para>
///         ⚠ <b>Measured in texels, the way the atlas is laid out</b>, with the top row first as the file is: down is
///         down the picture, whatever way the coordinates' V runs.
///     </para>
/// </remarks>
static class IslandBounds {
    /// <summary>The frame of every texel, from the islands after the gutter grew them.</summary>
    /// <param name="islands">The island per texel, <c>-1</c> for none, row-major, gutter included.</param>
    /// <param name="covered">Which texels a chart claimed, which alone say how big an island is.</param>
    /// <param name="columns">The atlas's width in texels.</param>
    /// <param name="rows">Its height in texels.</param>
    /// <returns>Zero off every island; on one, its place in the island's square, held within it.</returns>
    public static Vector3[] Of(int[] islands, bool[] covered, int columns, int rows) {
        var boxes = Boxes(islands, covered, columns);
        var longest = (float)Math.Max(columns, rows);
        var frames = new Vector3[islands.Length];

        for (var index = 0; index < frames.Length; index++) {
            if (islands[index] < 0 || !boxes.TryGetValue(islands[index], out var box)) {
                continue;
            }

            var side = Math.Max(box.Right - box.Left, box.Bottom - box.Top) + 1f;
            var across = (index % columns - box.Left + 0.5f) / side;
            var down = (index / columns - box.Top + 0.5f) / side;

            frames[index] = new(Math.Clamp(across, 0f, 1f), Math.Clamp(down, 0f, 1f), side / longest);
        }

        return frames;
    }

    /// <summary>The first and last column and row each island's claimed texels reach.</summary>
    static Dictionary<int, Box> Boxes(int[] islands, bool[] covered, int columns) {
        Dictionary<int, Box> boxes = [];

        for (var index = 0; index < islands.Length; index++) {
            var island = islands[index];

            if (island < 0 || !covered[index]) {
                continue;
            }

            var (x, y) = (index % columns, index / columns);

            boxes[island] = boxes.TryGetValue(island, out var box)
                ? new(Math.Min(box.Left, x), Math.Min(box.Top, y), Math.Max(box.Right, x), Math.Max(box.Bottom, y))
                : new(x, y, x, y);
        }

        return boxes;
    }

    /// <summary>The texels an island's charts reach, inclusive.</summary>
    readonly record struct Box(int Left, int Top, int Right, int Bottom);
}
