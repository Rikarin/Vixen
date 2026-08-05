// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.Water;
using Xunit;

namespace Tests;

/// <summary>
///     How the screen divides into water tiles — [docs/plan/35 § D8]'s classification, arithmetic half.
/// </summary>
/// <remarks>
///     <para>
///         <b>Three things address a tile and all three have to agree.</b> The host sizes the buffer
///         and picks the instance count from <see cref="WaterTiles" />, <c>WaterTiles.rvn</c> writes one
///         word per tile through <c>WaterTile.Index</c>, and <c>Water.rvn</c>'s vertex stage reads it
///         back through <c>WaterTile.Unindex</c>. A disagreement between them is not a crash: it is a
///         draw that shades the wrong rectangle, which looks like water in the wrong place and is
///         attributed to the surface pass.
///     </para>
///     <para>
///         ⚠ <b>The covering property is the one worth having.</b> Every pixel of the target belongs to
///         exactly one tile — no gaps, no overlaps — which is what makes "the tiled pass and the
///         untiled one are the same picture" a statement about the arithmetic rather than about a
///         particular frame. <c>WaterTileImageTests</c> is the same claim on a device.
///     </para>
/// </remarks>
public sealed class WaterTileTests {
    /// <summary>A target that is not a whole number of tiles still gets covered.</summary>
    /// <remarks>
    ///     ⚠ <b>Rounded up, and the last tile of a row hangs over the edge.</b> Rounded down, the right
    ///     and bottom edges of every frame whose size is not a multiple of eight would be a strip the
    ///     water pass never ran over — seven pixels at most, at the edge, which is exactly the kind of
    ///     thing nobody sees until a screenshot is taken at an odd resolution.
    /// </remarks>
    [Theory]
    [InlineData(128, 128, 16, 16)]
    [InlineData(1920, 1080, 240, 135)]
    [InlineData(1, 1, 1, 1)]
    [InlineData(9, 17, 2, 3)]
    [InlineData(0, 0, 0, 0)]
    public void The_tiling_covers_the_target(int width, int height, int tilesX, int tilesY) {
        var count = WaterTiles.CountFor(new(width, height));

        Assert.Equal(new Int2(tilesX, tilesY), count);
        Assert.True(count.X * WaterTiles.Size >= width, "the tiling stops short of the target's right edge");
        Assert.True(count.Y * WaterTiles.Size >= height, "the tiling stops short of the target's bottom edge");
    }

    /// <summary>⚠ Every pixel of the target is in exactly one tile — no gap and no overlap.</summary>
    /// <remarks>
    ///     <b>Both halves matter and they fail differently.</b> A gap is a rectangle of water the pass
    ///     never ran over, which is the frame behind it showing through in a block — visible, and
    ///     attributed to the classification. An overlap is a tile shaded twice, which is invisible in
    ///     the picture because the fragment stage is a pure function of the UV, and shows up only as
    ///     the pass costing more than it saved.
    /// </remarks>
    [Fact]
    public void Every_pixel_belongs_to_exactly_one_tile() {
        var target = new Int2(37, 23);
        var count = WaterTiles.CountFor(target);
        var claims = new int[target.X * target.Y];

        for (var index = 0; index < WaterTiles.Total(count); index++) {
            var tile = WaterTiles.Unindex(index, count);

            // The rectangle Water.rvn's vertex stage maps this instance onto: the tile's origin, one
            // tile square, clipped at the target's edge exactly as the rasterizer clips it.
            for (var y = tile.Y * WaterTiles.Size; y < Math.Min((tile.Y + 1) * WaterTiles.Size, target.Y); y++) {
                for (var x = tile.X * WaterTiles.Size; x < Math.Min((tile.X + 1) * WaterTiles.Size, target.X); x++) {
                    claims[(y * target.X) + x]++;
                }
            }
        }

        Assert.All(claims, claimed => Assert.Equal(1, claimed));
    }

    /// <summary>An index names the tile it came from, over a whole grid.</summary>
    [Fact]
    public void Indexing_a_tile_and_unindexing_it_are_inverses() {
        var count = WaterTiles.CountFor(new(100, 60));

        for (var y = 0; y < count.Y; y++) {
            for (var x = 0; x < count.X; x++) {
                var index = WaterTiles.Index(new(x, y), count);

                Assert.InRange(index, 0, WaterTiles.Total(count) - 1);
                Assert.Equal(new Int2(x, y), WaterTiles.Unindex(index, count));
            }
        }
    }

    /// <summary>
    ///     ⚠ The flag buffer is never zero bytes, because the binding exists when the tiling does not.
    /// </summary>
    /// <remarks>
    ///     A descriptor set is written wholly or not at all, so the <em>untiled</em> variant binds the
    ///     tile buffer as well — see <c>Water.rvn</c>'s <c>waterTiles</c>. A buffer of no bytes is one
    ///     no backend will create, so a frame with tiling off would fail to allocate a resource nothing
    ///     reads.
    /// </remarks>
    [Fact]
    public void The_flag_buffer_has_a_word_even_with_no_tiles_at_all() {
        Assert.Equal(sizeof(uint), WaterTiles.Bytes(default));
        Assert.Equal(16 * 16 * sizeof(uint), WaterTiles.Bytes(WaterTiles.CountFor(new(128, 128))));
    }
}
