// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
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

/// <summary>A placement that knows where a stamp's centre lands in the atlas, which a source moving with it reads.</summary>
internal interface IPaintAnchor {
    /// <summary>Where a position the stroke was given lands in the atlas.</summary>
    /// <param name="centre">The stamp's centre, in the stroke's own units.</param>
    /// <param name="texel">Where that is in the atlas, in texels.</param>
    /// <returns>Whether it lands on the mesh at all.</returns>
    bool TryAtlas(Vector2 centre, out Vector2 texel);
}

/// <summary>The texels of one stamp, each once, and the rectangles they fall in.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The strongest weight and not the sum.</b> A texel beside a shared edge is reached from both
///         triangles, and one stamp must lay one weight there or a mesh's edges paint darker than its faces.
///     </para>
///     <para>
///         Each texel also says where it lands on the stroke's picture: through a camera, where it projects; in the
///         atlas, its own centre. A projected picture or a stencil is read there.
///     </para>
/// </remarks>
public sealed class PaintStampTexels(int width, int height) {
    readonly PaintTexelMap<int> slots = new(width, height);
    readonly List<(int Texel, float Weight)> texels = [];
    readonly List<Vector2> pictures = [];
    readonly List<(int Piece, PaintRect Rect)> pieces = [];

    /// <summary>Each texel and its weight.</summary>
    public IReadOnlyList<(int Texel, float Weight)> Texels => texels;

    /// <summary>Where each of <see cref="Texels" /> lands on the stroke's picture, in the same order.</summary>
    public IReadOnlyList<Vector2> Pictures => pictures;

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
        pictures.Clear();
        pieces.Clear();
    }

    /// <summary>Adds a texel, which lands on the picture at its own centre, as a stamp in the atlas does.</summary>
    /// <param name="index">Its index, row-major.</param>
    /// <param name="weight">Its weight.</param>
    public void Add(int index, float weight) => Add(index, weight, new(index % width + 0.5f, index / width + 0.5f));

    /// <summary>Adds a texel and where it lands on the stroke's picture.</summary>
    /// <param name="index">Its index, row-major.</param>
    /// <param name="weight">Its weight.</param>
    /// <param name="picture">Where it lands on the picture.</param>
    public void Add(int index, float weight, Vector2 picture) {
        if (!slots.TryGet(index, out var slot)) {
            slots.Set(index, texels.Count);
            texels.Add((index, weight));
            pictures.Add(picture);
        } else if (weight > texels[slot].Weight) {
            texels[slot] = (index, weight);
            pictures[slot] = picture;
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
