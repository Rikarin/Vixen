// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Terrain;

/// <summary>Where one sample of the terrain lands in the atlas, in texels.</summary>
/// <param name="TileX">Which tile, along X.</param>
/// <param name="TileZ">And along Z.</param>
/// <param name="X">Where in the atlas, along X.</param>
/// <param name="Z">And along Z.</param>
public readonly record struct TerrainAtlasTexel(int TileX, int TileZ, int X, int Z);

/// <summary>
///     The terrain's heights and weights as an atlas of per-tile blocks, which is what makes the
///     upload per tile and the mip chain possible.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § T2]'s owed per-tile split, and it is a split of the <em>layout</em>
///         rather than of the texture.</b> The plan wants the tile to be the unit of everything
///         including load ([§ D2]); the draw wants one texture, because a CDLOD patch straddles a
///         tile boundary except by luck and a texture per tile makes every straddling patch either two
///         draws or a shader sampling two textures. An atlas is both: one texture to bind, and a block
///         per tile to upload, evict and mip independently.
///     </para>
///     <para>
///         ⚠ <b>The blocks do not share their boundary samples — they duplicate them.</b> The packed
///         heightfield is <c>TilesX × TileQuads + 1</c> samples wide because adjacent tiles share the
///         sample between them; the atlas gives each tile all <c>TileSamples</c> of its own, so tile
///         <i>k</i>'s last column and tile <i>k+1</i>'s first hold the same number. That costs
///         <c>(TileSamples / TileQuads)²</c> — 1.6% at 128 — and buys the thing the whole layout is
///         for: a block whose size is a power of two, starting at a multiple of one.
///     </para>
///     <para>
///         ⚠ <b>Which is what makes the mip chain legal.</b> A 2×2 reduction of the atlas never
///         crosses a block boundary, because every block is <c>TileSamples</c> texels and starts at a
///         multiple of it. Reducing the <em>packed</em> grid instead would mix two tiles' texels at
///         every level — [§ D2]'s seam arriving through the mip chain, which is exactly the failure
///         <see cref="TerrainMips" />'s remarks refuse.
///     </para>
///     <para>
///         ⚠ <b>And the duplication is what keeps filtering continuous.</b> A bilinear tap just inside
///         tile <i>k</i>'s last texel blends two of that tile's samples; a tap just past it lands in
///         tile <i>k+1</i>'s first two, whose first is the same number. There is no seam, and there is
///         no half-texel stretch, because the sample-to-texel map has the same scale inside every
///         block.
///     </para>
///     <para>
///         ⚠ <b>A sample on a boundary belongs to the <em>upper</em> tile, and the choice has to be
///         the same everywhere.</b> <see cref="TerrainDescription.TileOf" /> already made it —
///         <c>x / TileQuads</c> sends sample 127 of a 128-sample tiling to tile 1, not tile 0 — and
///         this follows it rather than making it again. Two answers to "which tile is sample 127 in"
///         is a terrain that reads one block and was written into another.
///     </para>
///     <para>
///         The lower tile still <em>holds</em> that sample: its block is <c>TileSamples</c> wide and
///         its last column is the boundary. That is the duplication, and it is why a tap that crosses
///         the seam blends a value with itself rather than with a neighbour's.
///     </para>
/// </remarks>
public readonly record struct TerrainAtlas {
    /// <summary>Describes the atlas a terrain's tiles pack into.</summary>
    /// <param name="description">The terrain.</param>
    /// <exception cref="ArgumentException">The description is not one an atlas can be built for.</exception>
    public TerrainAtlas(in TerrainDescription description) {
        if (description.Validate() is { } refusal) {
            throw new ArgumentException(refusal, nameof(description));
        }

        TileSamples = description.TileSamples;
        TileQuads = description.TileQuads;
        TilesX = description.TilesX;
        TilesZ = description.TilesZ;
    }

    /// <summary>How many samples a tile is, on a side. A power of two.</summary>
    public int TileSamples { get; }

    /// <summary>How many quads that is — one fewer, because the tiles share their boundary.</summary>
    public int TileQuads { get; }

    /// <summary>How many tiles along X.</summary>
    public int TilesX { get; }

    /// <summary>And along Z.</summary>
    public int TilesZ { get; }

    /// <summary>How many tiles there are.</summary>
    public int TileCount => TilesX * TilesZ;

    /// <summary>How many texels the atlas is, along X.</summary>
    public int Width => TilesX * TileSamples;

    /// <summary>And along Z.</summary>
    public int Height => TilesZ * TileSamples;

    /// <summary>How many mip levels it has, level 0 included.</summary>
    /// <remarks>
    ///     The tile's chain rather than the atlas's own, and it is shorter: an atlas of 32 tiles of
    ///     128 texels is 4096 wide and would allow thirteen levels, of which only eight keep a block
    ///     at one texel or more. Past that a level would be mixing tiles, which is what the layout
    ///     exists to prevent.
    /// </remarks>
    public int LevelCount => TerrainMips.LevelCount(TileSamples);

    /// <summary>How many texels a block is at a level.</summary>
    /// <param name="level">Which level, 0 being the full resolution.</param>
    /// <returns>The count.</returns>
    public int BlockSizeAt(int level) => TerrainMips.SamplesAt(TileSamples, level);

    /// <summary>How many texels the atlas is at a level, along X.</summary>
    /// <param name="level">Which level.</param>
    /// <returns>The count.</returns>
    public int WidthAt(int level) => TilesX * BlockSizeAt(level);

    /// <summary>And along Z.</summary>
    /// <param name="level">Which level.</param>
    /// <returns>The count.</returns>
    public int HeightAt(int level) => TilesZ * BlockSizeAt(level);

    /// <summary>Where a tile's block starts at a level, in texels.</summary>
    /// <param name="tileX">Which tile, along X.</param>
    /// <param name="tileZ">And along Z.</param>
    /// <param name="level">Which level.</param>
    /// <returns>The low corner and the block's size.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such tile or level.</exception>
    public TerrainRect BlockOf(int tileX, int tileZ, int level = 0) {
        ArgumentOutOfRangeException.ThrowIfNegative(tileX);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(tileX, TilesX);
        ArgumentOutOfRangeException.ThrowIfNegative(tileZ);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(tileZ, TilesZ);
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(level, LevelCount);

        var size = BlockSizeAt(level);

        return new(tileX * size, tileZ * size, size, size);
    }

    /// <summary>Where a sample of the packed grid lands in the atlas at level 0.</summary>
    /// <param name="x">The sample's X.</param>
    /// <param name="z">Its Z.</param>
    /// <returns>Which tile it is in, and where in the atlas.</returns>
    /// <remarks>
    ///     ⚠ <b>The tile is <see cref="TerrainDescription.TileOf" />'s, not a second derivation.</b>
    ///     A boundary sample is in two tiles and the two have to agree about which one owns it, or a
    ///     stroke writes one block and the draw reads the other. It owns the <em>upper</em> one, which
    ///     is what <c>x / TileQuads</c> answers.
    /// </remarks>
    public TerrainAtlasTexel Locate(int x, int z) {
        var tileX = Math.Clamp(x / TileQuads, 0, TilesX - 1);
        var tileZ = Math.Clamp(z / TileQuads, 0, TilesZ - 1);

        var localX = x - (tileX * TileQuads);
        var localZ = z - (tileZ * TileQuads);

        return new(tileX, tileZ, (tileX * TileSamples) + localX, (tileZ * TileSamples) + localZ);
    }

    /// <summary>The texture coordinate a sample coordinate reads, at level 0.</summary>
    /// <param name="sample">Where, in the packed grid's own continuous coordinates.</param>
    /// <returns>The coordinate, 0…1 over the whole atlas.</returns>
    /// <remarks>
    ///     ⚠ <b>The transliteration of <c>Terrain.rvn</c>'s <c>AtlasUv</c>, and the reason it exists
    ///     here is that a wrong one is invisible.</b> An atlas coordinate that is off by one block
    ///     draws a terrain made of the wrong tiles — which reads as a corrupt heightmap rather than as
    ///     an arithmetic error, and a device is not needed to catch it.
    /// </remarks>
    public Vector2 UvOf(Vector2 sample) {
        var tileX = Math.Clamp((int)MathF.Floor(sample.X / TileQuads), 0, TilesX - 1);
        var tileZ = Math.Clamp((int)MathF.Floor(sample.Y / TileQuads), 0, TilesZ - 1);

        var localX = sample.X - (tileX * TileQuads);
        var localZ = sample.Y - (tileZ * TileQuads);

        return new(
            ((tileX * TileSamples) + localX + 0.5f) / Width,
            ((tileZ * TileSamples) + localZ + 0.5f) / Height
        );
    }
}
