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
public sealed class PaintStampTexels {
    readonly Dictionary<int, float> weights = [];
    readonly Dictionary<int, PaintRect> pieces = [];

    /// <summary>Each texel and its weight.</summary>
    public IReadOnlyDictionary<int, float> Weights => weights;

    /// <summary>One rectangle for each piece of the atlas the stamp reached.</summary>
    public IReadOnlyCollection<PaintRect> Pieces => pieces.Values;

    /// <summary>Forgets the last stamp.</summary>
    public void Clear() {
        weights.Clear();
        pieces.Clear();
    }

    /// <summary>Adds a texel.</summary>
    /// <param name="index">Its index, row-major.</param>
    /// <param name="weight">Its weight.</param>
    public void Add(int index, float weight) {
        if (!weights.TryGetValue(index, out var held) || weight > held) {
            weights[index] = weight;
        }
    }

    /// <summary>Grows the rectangle of one piece.</summary>
    /// <param name="piece">Which piece, such as a chart.</param>
    /// <param name="touched">What it now also covers.</param>
    public void Touch(int piece, PaintRect touched) =>
        pieces[piece] = pieces.TryGetValue(piece, out var held) ? held.Union(touched) : touched;
}
