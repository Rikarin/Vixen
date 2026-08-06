// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Uv;

/// <summary>A flattened chart, in its own coordinates, waiting to be placed in an atlas.</summary>
/// <param name="Coordinates">A coordinate per corner of <paramref name="Corners" />.</param>
/// <param name="Corners">The triangle corners this island covers, three per triangle.</param>
/// <param name="Minimum">The island's lower corner, in <paramref name="Coordinates" />' space.</param>
/// <param name="Maximum">Its upper corner.</param>
/// <param name="Scale">Coordinates per world unit, so that texel density is a fact rather than a guess.</param>
/// <remarks>
///     <para>
///         <b>This is the value that makes <c>Pack</c> a peer of a standalone packer rather than an
///         internal detail.</b> docs/plan/42 § D1: an artist who cut seams by hand in another package
///         wants those seams kept and the islands rearranged, and that is most of why a standalone
///         packer is a product at all. An island can therefore come from this library's own charter,
///         from the remesher's patch layout, or from a file — the packer does not ask.
///     </para>
///     <para>
///         ⚠ <b>Coordinates are per <i>corner</i>, not per position, and that is not an
///         implementation detail.</b> A seam is one shared position whose two sides carry different
///         coordinates; <see cref="EditMesh" />'s corner layer represents that for nothing, and a
///         per-vertex structure represents it by duplicating the vertex. Doing the arithmetic in the
///         representation where a seam is free is what keeps the split at the very end, where a
///         renderer wants it, instead of threaded through every stage.
///     </para>
///     <para>
///         ⚠ <b><see cref="Scale" /> travels with the island because the packer is allowed to move
///         it and not to resize it.</b> docs/plan/42 § D9: non-uniform texel density is invisible in
///         the atlas and glaring in the game — the classic symptom being a character's face at half
///         the resolution of their boots — so a packer that quietly rescales an island to make it fit
///         has to be able to say that it did.
///     </para>
/// </remarks>
public readonly record struct UvIsland(
    IReadOnlyList<Vector2> Coordinates,
    IReadOnlyList<int> Corners,
    Vector2 Minimum,
    Vector2 Maximum,
    float Scale
) {
    /// <summary>How many triangles the island covers.</summary>
    public int TriangleCount => Corners.Count / 3;

    /// <summary>How wide and how tall the island is.</summary>
    public Vector2 Size => Maximum - Minimum;
}

/// <summary>Where the packer put an island, as a transform rather than as rewritten coordinates.</summary>
/// <param name="Island">Which island, by index into what was handed in.</param>
/// <param name="Offset">Where the island's lower corner sits, in the atlas's unit square.</param>
/// <param name="Scale">What the island's coordinates were multiplied by.</param>
/// <param name="Rotation">Quarter turns applied, counter-clockwise, before the offset.</param>
/// <param name="Tile">Which UDIM tile, as integer offsets. <c>(0, 0)</c> for a single-atlas pack.</param>
/// <remarks>
///     ⚠ <b>A transform rather than coordinates, so that a caller can pack twice and compare.</b> A
///     packer that rewrote the coordinates in place would make "what did this setting cost me" a
///     question you answer by re-running the whole unwrap. It also keeps the island's own shape
///     exactly as it arrived, which is docs/plan/42's exit criterion 7 — repacking an artist's layout
///     must leave the island shapes untouched.
/// </remarks>
public readonly record struct UvPlacement(
    int Island,
    Vector2 Offset,
    float Scale,
    int Rotation,
    Int2 Tile
) {
    /// <summary>Where one of the island's coordinates ends up.</summary>
    /// <param name="island">The island this placement is for.</param>
    /// <param name="coordinate">One of its coordinates.</param>
    /// <returns>The atlas coordinate, in the unit square offset by <see cref="Tile" />.</returns>
    /// <remarks>
    ///     <para>
    ///         The transform written out, so that a caller and the packer cannot disagree about what
    ///         <see cref="Rotation" /> meant. Subtract the island's lower corner, turn, scale, offset.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A quarter turn is a subtraction and a swap, and that is the point.</b> There is no
    ///         resampling and no interpolation anywhere in it, so repacking an artist's layout returns
    ///         the island's own shape rather than a shape very close to it — docs/plan/42's exit
    ///         criterion 7.
    ///     </para>
    /// </remarks>
    public Vector2 Apply(in UvIsland island, Vector2 coordinate) {
        var local = coordinate - island.Minimum;
        var size = island.Size;

        var turned = (Rotation & 3) switch {
            1 => new Vector2(size.Y - local.Y, local.X),
            2 => new Vector2(size.X - local.X, size.Y - local.Y),
            3 => new Vector2(local.Y, size.X - local.X),
            _ => local
        };

        return Offset + (turned * Scale) + new Vector2(Tile.X, Tile.Y);
    }
}
