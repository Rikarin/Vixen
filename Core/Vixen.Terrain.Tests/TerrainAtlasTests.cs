// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Terrain.Tests;

/// <summary>
///     The atlas of per-tile blocks — [docs/plan/31 § T2]'s owed split, as a layout rather than as a
///     texture per tile.
/// </summary>
public sealed class TerrainAtlasTests {
    static TerrainDescription Described(int tiles = 4, int tileSamples = 32) =>
        new() {
            TileSamples = tileSamples,
            TilesX = tiles,
            TilesZ = tiles,
            MetresPerQuad = 1f,
            MinHeight = -100f,
            MaxHeight = 100f
        };

    [Fact]
    public void ABlockPerTile() {
        var atlas = new TerrainAtlas(Described());

        Assert.Equal(4 * 32, atlas.Width);
        Assert.Equal(4 * 32, atlas.Height);
        Assert.Equal(16, atlas.TileCount);
    }

    /// <summary>The atlas is bigger than the packed grid, and the difference is the duplication.</summary>
    /// <remarks>
    ///     ⚠ <b>The blocks do not share their boundary samples — they duplicate them.</b> That costs
    ///     <c>(TileSamples / TileQuads)²</c> and buys a block whose size is a power of two starting at
    ///     a multiple of one, which is what makes the mip chain legal.
    /// </remarks>
    [Fact]
    public void TheDuplicationIsWhatItCosts() {
        var description = Described();
        var atlas = new TerrainAtlas(description);

        // The packed grid shares its boundaries: 4 × 31 + 1.
        Assert.Equal(125, description.SamplesX);
        Assert.Equal(128, atlas.Width);

        // Under two per cent, which is what a 32-sample tile costs. A 128-sample tile costs 1.6%.
        Assert.True(atlas.Width * atlas.Height < description.SamplesX * description.SamplesZ * 1.06);
    }

    /// <summary>Every block is a power of two, starting at a multiple of one.</summary>
    /// <remarks>
    ///     ⚠ <b>Which is exactly what makes a 2×2 reduction never cross a boundary.</b> Reducing the
    ///     packed grid instead would mix two tiles' texels at every level — [§ D2]'s seam arriving
    ///     through the mip chain.
    /// </remarks>
    [Fact]
    public void EveryBlockIsAlignedToItsOwnSize() {
        var atlas = new TerrainAtlas(Described(tiles: 3, tileSamples: 64));

        for (var level = 0; level < atlas.LevelCount; level++) {
            var size = atlas.BlockSizeAt(level);

            for (var z = 0; z < atlas.TilesZ; z++) {
                for (var x = 0; x < atlas.TilesX; x++) {
                    var block = atlas.BlockOf(x, z, level);

                    Assert.Equal(size, block.Width);
                    Assert.Equal(size, block.Height);
                    Assert.Equal(0, block.X % size);
                    Assert.Equal(0, block.Z % size);
                }
            }
        }
    }

    /// <summary>The chain is a tile's, not the whole atlas's.</summary>
    /// <remarks>
    ///     ⚠ <b>An atlas of 32 tiles of 128 texels is 4096 wide and would allow thirteen levels.</b>
    ///     Only eight keep a block at a texel or more; past that a level mixes tiles, which is what
    ///     the layout exists to prevent.
    /// </remarks>
    [Fact]
    public void TheChainStopsWhereABlockDoes() {
        var atlas = new TerrainAtlas(Described(tiles: 32, tileSamples: 128));

        Assert.Equal(4096, atlas.Width);
        Assert.Equal(TerrainMips.LevelCount(128), atlas.LevelCount);
        Assert.True(atlas.LevelCount < 13);
        Assert.True(atlas.BlockSizeAt(atlas.LevelCount - 1) >= 1);
    }

    /// <summary>Two tiles never share a texel, at any level.</summary>
    [Fact]
    public void NoTwoBlocksOverlap() {
        var atlas = new TerrainAtlas(Described(tiles: 3, tileSamples: 16));

        for (var level = 0; level < atlas.LevelCount; level++) {
            var seen = new HashSet<(int, int)>();

            for (var z = 0; z < atlas.TilesZ; z++) {
                for (var x = 0; x < atlas.TilesX; x++) {
                    var block = atlas.BlockOf(x, z, level);

                    for (var by = 0; by < block.Height; by++) {
                        for (var bx = 0; bx < block.Width; bx++) {
                            Assert.True(
                                seen.Add((block.X + bx, block.Z + by)),
                                $"level {level} has two tiles on texel {block.X + bx}, {block.Z + by}."
                            );
                        }
                    }
                }
            }
        }
    }

    /// <summary>A sample on a boundary belongs to the upper tile — the description's own choice.</summary>
    /// <remarks>
    ///     ⚠ <b>Two answers to "which tile is sample 31 in" is a terrain that reads one block and was
    ///     written into another</b>, which draws as one tile of the world showing a neighbour's
    ///     heights.
    /// </remarks>
    [Fact]
    public void ABoundarySampleBelongsToTheSameTileTheDescriptionSays() {
        var description = Described(tiles: 4, tileSamples: 32);
        var atlas = new TerrainAtlas(description);

        for (var sample = 0; sample < description.SamplesX; sample++) {
            var (tileX, _, localX, _) = description.TileOf(sample, 0);
            var located = atlas.Locate(sample, 0);

            Assert.Equal(tileX, located.TileX);
            Assert.Equal((tileX * atlas.TileSamples) + localX, located.X);
        }
    }

    /// <summary>Every sample of the packed grid lands somewhere in the atlas, and inside its block.</summary>
    [Fact]
    public void EverySampleLandsInsideItsOwnBlock() {
        var description = Described(tiles: 3, tileSamples: 16);
        var atlas = new TerrainAtlas(description);

        for (var z = 0; z < description.SamplesZ; z++) {
            for (var x = 0; x < description.SamplesX; x++) {
                var texel = atlas.Locate(x, z);
                var block = atlas.BlockOf(texel.TileX, texel.TileZ);

                Assert.InRange(texel.X, block.X, block.X + block.Width - 1);
                Assert.InRange(texel.Z, block.Z, block.Z + block.Height - 1);
            }
        }
    }

    /// <summary>The uv of an integer sample is the centre of the texel it lands in.</summary>
    /// <remarks>
    ///     ⚠ <b>An atlas coordinate that is off by half a texel filters two samples where it should
    ///     read one</b>, which softens the whole terrain by an amount nobody can attribute — and it
    ///     is exactly the arithmetic <c>Terrain.rvn</c>'s <c>AtlasUv</c> has to agree with.
    /// </remarks>
    [Fact]
    public void TheUvOfASampleIsItsTexelCentre() {
        var description = Described(tiles: 3, tileSamples: 16);
        var atlas = new TerrainAtlas(description);

        for (var sample = 0; sample < description.SamplesX; sample++) {
            var texel = atlas.Locate(sample, 0);
            var uv = atlas.UvOf(new(sample, 0f));

            Assert.Equal((texel.X + 0.5f) / atlas.Width, uv.X, 5);
            Assert.Equal((texel.Z + 0.5f) / atlas.Height, uv.Y, 5);
        }
    }

    /// <summary>And the map is continuous across a block boundary, in both directions.</summary>
    /// <remarks>
    ///     ⚠ <b>This is what the duplication is for.</b> A tap just inside tile <i>k</i>'s last texel
    ///     blends two of that tile's samples; a tap just past it lands in tile <i>k+1</i>'s first two,
    ///     whose first holds the same number. If the scale differed on the two sides the seam would
    ///     stretch by half a texel and read as a crack.
    /// </remarks>
    [Fact]
    public void TheScaleIsTheSameInsideEveryBlock() {
        var atlas = new TerrainAtlas(Described(tiles: 4, tileSamples: 32));
        var quads = atlas.TileQuads;

        // One sample apart, well inside tile 0 and well inside tile 2.
        var near = atlas.UvOf(new(4f, 0f)) - atlas.UvOf(new(3f, 0f));
        var far = atlas.UvOf(new((2f * quads) + 4f, 0f)) - atlas.UvOf(new((2f * quads) + 3f, 0f));

        Assert.Equal(near.X, far.X, 6);

        // ⚠ And the boundary sample is in *both* blocks, which is the duplication. `Locate` sends it
        // to the upper tile, where it is the first texel; the lower tile's block still holds it as its
        // last, because the block is TileSamples wide and the upload writes all of it. So a tap that
        // crosses the seam blends a value with itself.
        var below = atlas.Locate(quads - 1, 0);
        var boundary = atlas.Locate(quads, 0);

        Assert.Equal(0, below.TileX);
        Assert.Equal(1, boundary.TileX);

        // The lower block's last texel and the upper block's first are one apart in the atlas, and
        // they are the same world sample.
        var lower = atlas.BlockOf(0, 0);

        Assert.Equal(lower.X + lower.Width - 1, below.X + 1);
        Assert.Equal(lower.X + lower.Width, boundary.X);
    }

    [Fact]
    public void ADescriptionThatCannotBeBuiltIsRefused() {
        Assert.Throws<ArgumentException>(() => new TerrainAtlas(new() { TileSamples = 7, TilesX = 1, TilesZ = 1 }));
    }
}
