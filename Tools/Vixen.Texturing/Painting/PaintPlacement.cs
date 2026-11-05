// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Terrain;

namespace Vixen.Texturing.Painting;

/// <summary>Says which texels a stamp lands on, besides or instead of the disc around its centre.</summary>
public interface IPaintPlacement {
    /// <summary>Whether a stamp's centre is somewhere other than the atlas, so only this says what it covers.</summary>
    bool ReplacesStamp { get; }

    /// <summary>Finds what one stamp covers.</summary>
    /// <param name="stamp">The stamp.</param>
    /// <param name="brush">The brush.</param>
    /// <param name="kernel">The stamp's kernel.</param>
    /// <param name="into">Where the texels go.</param>
    void Place(PaintStamp stamp, in PaintBrush brush, in TerrainBrush kernel, PaintStampTexels into);
}

/// <summary>The texels of one stamp, each once, and the rectangles they fall in.</summary>
/// <remarks>
///     ⚠ <b>The strongest weight and not the sum.</b> A texel beside a shared edge is reached from both
///     triangles, and one stamp must lay one weight there or a mesh's edges paint darker than its faces.
/// </remarks>
public sealed class PaintStampTexels(int width, int height) {
    readonly PaintTexelMap<int> slots = new(width, height);
    readonly List<(int Texel, float Weight)> texels = [];
    readonly List<(int Piece, PaintRect Rect)> pieces = [];

    /// <summary>Each texel and its weight.</summary>
    public IReadOnlyList<(int Texel, float Weight)> Texels => texels;

    /// <summary>One rectangle for each piece of the atlas the stamp reached.</summary>
    public IReadOnlyList<(int Piece, PaintRect Rect)> Pieces => pieces;

    /// <summary>What the stroke has reached at each texel, which says where a stamp can add nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>This is most of a brush.</b> Stamps a sixth of a radius apart cross each texel a dozen times and the
    ///     first one fills the middle of the disc, so without this a stroke weighs the same texels over again
    ///     to find they are full.
    /// </remarks>
    public PaintTexelMap<float>? Reached { get; init; }

    /// <summary>The reach past which a texel has no more to take.</summary>
    public float FullReach { get; init; } = float.PositiveInfinity;

    /// <summary>Whether a stamp can add nothing to a texel.</summary>
    /// <param name="column">Its column.</param>
    /// <param name="row">Its row.</param>
    /// <returns>Whether it is full.</returns>
    public bool IsFull(int column, int row) =>
        Reached is { } held && held.TryGet(column, row, out var reach) && reach >= FullReach;

    /// <summary>Forgets the last stamp.</summary>
    public void Clear() {
        slots.Clear();
        texels.Clear();
        pieces.Clear();
    }

    /// <summary>Adds a texel.</summary>
    /// <param name="index">Its index, row-major.</param>
    /// <param name="weight">Its weight.</param>
    public void Add(int index, float weight) {
        if (!slots.TryGet(index, out var slot)) {
            slots.Set(index, texels.Count);
            texels.Add((index, weight));
        } else if (weight > texels[slot].Weight) {
            texels[slot] = (index, weight);
        }
    }

    /// <summary>Grows the rectangle of one piece.</summary>
    /// <param name="piece">Which piece, such as a chart.</param>
    /// <param name="touched">What it now also covers.</param>
    public void Touch(int piece, PaintRect touched) {
        for (var at = 0; at < pieces.Count; at++) {
            if (pieces[at].Piece == piece) {
                pieces[at] = (piece, pieces[at].Rect.Union(touched));

                return;
            }
        }

        pieces.Add((piece, touched));
    }
}
