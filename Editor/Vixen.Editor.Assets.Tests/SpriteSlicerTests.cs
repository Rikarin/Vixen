// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Editor.Assets.Textures;
using Vixen.Graphics;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     The three ways a sprite sheet is cut, against images built here rather than against a picture
///     of a panel.
/// </summary>
/// <remarks>
///     Every one of these is a pure function of pixels and options, which is the whole reason the
///     slicing lives in <c>Vixen.Editor.Assets</c> and not in the view: what a sprite editor is, once
///     the buttons are taken away, is this.
/// </remarks>
public class SpriteSlicerTests {
    /// <summary>A transparent image of a given size.</summary>
    static TextureData Blank(int width, int height) => new(PixelFormat.Rgba8UNorm, width, height, levelCount: 1);

    /// <summary>Paints an opaque rectangle into an image.</summary>
    static TextureData Fill(TextureData texture, int x, int y, int width, int height, byte alpha = 255) {
        var pixels = texture.LevelSpan(0);

        for (var row = y; row < y + height; row++) {
            for (var column = x; column < x + width; column++) {
                var index = ((row * texture.Width) + column) * 4;

                pixels[index] = 255;
                pixels[index + 1] = 255;
                pixels[index + 2] = 255;
                pixels[index + 3] = alpha;
            }
        }

        return texture;
    }

    [Fact]
    public void AGridBySizeCutsEveryWholeCell() {
        var texture = Fill(Blank(128, 64), 0, 0, 128, 64);

        var sprites = SpriteSlicer.Slice(texture, new(SliceMethod.GridBySize) { CellSize = new(32, 32) });

        Assert.Equal(8, sprites.Count);
        Assert.Equal("sprite_0", sprites[0].Name);
        Assert.Equal(new Rectangle(0f, 0f, 32f, 32f), sprites[0].Region);

        // Row-major from the top-left, which is the order a texture is laid out in and therefore the
        // order an animation's frames are drawn in.
        Assert.Equal(new Rectangle(32f, 32f, 32f, 32f), sprites[5].Region);
    }

    [Fact]
    public void APartialCellAtTheEdgeIsNotTaken() {
        // A hundred texels of a thirty-two-texel cell is three cells and four texels left over, and
        // the four are a mistake in the artwork or in the numbers rather than a frame.
        var texture = Fill(Blank(100, 32), 0, 0, 100, 32);

        Assert.Equal(3, SpriteSlicer.Slice(texture, new(SliceMethod.GridBySize) { CellSize = new(32, 32) }).Count);
    }

    [Fact]
    public void AGridSkipsCellsWithNothingInThem() {
        // Eleven frames on a four-by-three sheet should be eleven sprites, not eleven and a blank —
        // which is the case that makes "keep empty" off the right default.
        var texture = Blank(128, 96);

        for (var index = 0; index < 11; index++) {
            Fill(texture, index % 4 * 32, index / 4 * 32, 32, 32);
        }

        var options = new SpriteSliceOptions(SliceMethod.GridBySize) { CellSize = new(32, 32) };

        Assert.Equal(11, SpriteSlicer.Slice(texture, options).Count);
        Assert.Equal(12, SpriteSlicer.Slice(texture, options with { KeepEmpty = true }).Count);
    }

    [Fact]
    public void KeepingAnEmptyCellKeepsItAtFullSize() {
        // ⚠ Trim and keep-empty together, which is the pair that had a bug in it: a trim answers a
        // blank cell with nothing, so trimming one anyway turns every empty somebody asked to keep
        // into a zero-size sprite they cannot select. A kept blank keeps its whole cell.
        var texture = Fill(Blank(64, 32), 0, 0, 32, 32);

        var sprites = SpriteSlicer.Slice(
            texture,
            new(SliceMethod.GridBySize) { CellSize = new(32, 32), KeepEmpty = true, Trim = true }
        );

        Assert.Equal(2, sprites.Count);
        Assert.Equal(new Rectangle(32f, 0f, 32f, 32f), sprites[1].Region);
    }

    [Fact]
    public void AGridByCountFitsTheCellsToTheTexture() {
        var texture = Fill(Blank(128, 64), 0, 0, 128, 64);

        var sprites = SpriteSlicer.Slice(texture, new(SliceMethod.GridByCount) { CellCount = new(4, 2) });

        Assert.Equal(8, sprites.Count);
        Assert.Equal(new Rectangle(0f, 0f, 32f, 32f), sprites[0].Region);
        Assert.Equal(new Rectangle(96f, 32f, 32f, 32f), sprites[7].Region);
    }

    [Fact]
    public void PaddingComesOutOfTheCellsAndNotOffTheEnd() {
        // Four columns and three two-texel gaps in 134: the cells are 32 each, and the last one has
        // no padding after it. Charging padding for the final cell is the off-by-one to avoid.
        var texture = Fill(Blank(134, 32), 0, 0, 134, 32);

        var sprites = SpriteSlicer.Slice(
            texture,
            new(SliceMethod.GridByCount) { CellCount = new(4, 1), Padding = new(2, 0) }
        );

        Assert.Equal(4, sprites.Count);
        Assert.Equal(new Rectangle(0f, 0f, 32f, 32f), sprites[0].Region);
        Assert.Equal(new Rectangle(102f, 0f, 32f, 32f), sprites[3].Region);
    }

    [Fact]
    public void AnOffsetMovesWhereTheGridStarts() {
        var texture = Fill(Blank(72, 40), 0, 0, 72, 40);

        var sprites = SpriteSlicer.Slice(
            texture,
            new(SliceMethod.GridBySize) { CellSize = new(32, 32), Offset = new(8, 8) }
        );

        Assert.Equal(2, sprites.Count);
        Assert.Equal(new Rectangle(8f, 8f, 32f, 32f), sprites[0].Region);
    }

    [Fact]
    public void TrimmingShrinksACellToWhatIsDrawnInIt() {
        var texture = Fill(Blank(32, 32), 8, 4, 10, 6);

        var sprites = SpriteSlicer.Slice(
            texture,
            new(SliceMethod.GridBySize) { CellSize = new(32, 32), Trim = true }
        );

        Assert.Equal(new Rectangle(8f, 4f, 10f, 6f), Assert.Single(sprites).Region);
    }

    [Fact]
    public void TrimmingABlankRegionAnswersWithNothing() {
        // ⚠ An empty rect rather than the region unchanged: "there is nothing here" is the honest
        // answer, and returning the cell would make trim look as though it had worked.
        Assert.True(SpriteSlicer.Trim(Blank(32, 32), new(0f, 0f, 32f, 32f)).IsEmpty);
    }

    [Fact]
    public void TheAlphaThresholdDecidesWhatCountsAsDrawn() {
        var texture = Fill(Blank(32, 32), 4, 4, 8, 8, alpha: 3);

        // Zero is not a disabled threshold: a texel with alpha 3 is very nearly invisible and is
        // still drawn, so the default keeps it.
        Assert.False(SpriteSlicer.Trim(texture, new(0f, 0f, 32f, 32f)).IsEmpty);
        Assert.True(SpriteSlicer.Trim(texture, new(0f, 0f, 32f, 32f), threshold: 4).IsEmpty);
    }

    [Fact]
    public void AutomaticSlicingFindsOneRectPerIsland() {
        var texture = Blank(64, 32);

        Fill(texture, 2, 2, 10, 10);
        Fill(texture, 40, 4, 12, 8);

        var sprites = SpriteSlicer.Slice(texture, new(SliceMethod.Automatic));

        Assert.Equal(2, sprites.Count);
        Assert.Equal(new Rectangle(2f, 2f, 10f, 10f), sprites[0].Region);
        Assert.Equal(new Rectangle(40f, 4f, 12f, 8f), sprites[1].Region);
    }

    [Fact]
    public void ADiagonalRunOfTexelsIsOneIslandRatherThanMany() {
        // Eight-connected, because a diagonal line is one stroke to everybody who has ever drawn
        // one — and four-connectivity would cut it into a sprite per pixel.
        var texture = Blank(32, 32);

        for (var step = 0; step < 8; step++) {
            Fill(texture, 4 + step, 4 + step, 1, 1);
        }

        Assert.Equal(new Rectangle(4f, 4f, 8f, 8f), Assert.Single(SpriteSlicer.Slice(texture, new(SliceMethod.Automatic))).Region);
    }

    [Fact]
    public void OverlappingIslandsAreMergedIntoOneSprite() {
        // A character's detached eye sits inside the head's bounding box. Two islands whose boxes
        // overlap are one drawing, and slicing between them cuts a frame in half.
        var texture = Blank(64, 64);

        Fill(texture, 8, 8, 20, 4);
        Fill(texture, 8, 20, 20, 4);
        Fill(texture, 8, 8, 4, 16);

        var sprites = SpriteSlicer.Slice(texture, new(SliceMethod.Automatic));

        Assert.Equal(new Rectangle(8f, 8f, 20f, 16f), Assert.Single(sprites).Region);
    }

    [Fact]
    public void AMergeRunsToAFixedPointRatherThanOnce() {
        // Three L-shaped strokes stepping down the sheet. No two of them share a texel or even
        // touch diagonally, but each one's bounding box overlaps the next — and the first's does not
        // reach the third. Merging has to run to a fixed point to join all three, because the pair it
        // merges first makes a larger box that only then overlaps what is left.
        var texture = Blank(64, 64);

        foreach (var corner in (int[]) [0, 15, 30]) {
            Fill(texture, corner, corner, 21, 1);
            Fill(texture, corner, corner, 1, 21);
        }

        var sprites = SpriteSlicer.Slice(texture, new(SliceMethod.Automatic));

        Assert.Equal(new Rectangle(0f, 0f, 51f, 51f), Assert.Single(sprites).Region);
    }

    [Fact]
    public void SpecksBelowTheMinimumAreNotSprites() {
        var texture = Blank(64, 32);

        Fill(texture, 2, 2, 16, 16);
        Fill(texture, 40, 20, 1, 1);

        Assert.Single(SpriteSlicer.Slice(texture, new(SliceMethod.Automatic)));
        Assert.Equal(2, SpriteSlicer.Slice(texture, new(SliceMethod.Automatic) { MinimumSize = 1 }).Count);
    }

    [Fact]
    public void IslandsComeOutInReadingOrderEvenWhenARowIsNotAligned() {
        // ⚠ The banding rule. Frames on one row of a hand-drawn sheet are rarely aligned to the
        // texel, and ordering by the top edge alone interleaves two rows wherever one frame reaches
        // a pixel higher than its neighbour — which shuffles the numbering an animation depends on.
        var texture = Blank(64, 64);

        Fill(texture, 4, 5, 8, 8);
        Fill(texture, 20, 4, 8, 8);
        Fill(texture, 36, 6, 8, 8);
        Fill(texture, 4, 40, 8, 8);

        var sprites = SpriteSlicer.Slice(texture, new(SliceMethod.Automatic));

        Assert.Equal(4, sprites.Count);
        Assert.Equal(4f, sprites[0].Region.Left);
        Assert.Equal(20f, sprites[1].Region.Left);
        Assert.Equal(36f, sprites[2].Region.Left);
        Assert.Equal(40f, sprites[3].Region.Top);
    }

    [Fact]
    public void ATextureWithNoAlphaToReadSlicesIntoNothing() {
        // A compressed source arrives in blocks. Rather than half-decode one, automatic slicing says
        // it cannot — and the panel disables the button rather than showing an empty result.
        var compressed = new TextureData(PixelFormat.Bc7RgbaUNorm, 64, 64, levelCount: 1);

        Assert.False(SpriteSlicer.CanReadAlpha(compressed));
        Assert.Empty(SpriteSlicer.Slice(compressed, new(SliceMethod.Automatic)));
    }

    [Fact]
    public void TheOptionsCarryThePivotAndTheBorderOntoEveryRect() {
        var texture = Fill(Blank(64, 32), 0, 0, 64, 32);

        var sprites = SpriteSlicer.Slice(
            texture,
            new(SliceMethod.GridBySize) {
                CellSize = new(32, 32),
                Pivot = new(0f, 0f),
                Border = NineSlice.Uniform(4f),
                NamePrefix = "tile"
            }
        );

        Assert.Equal("tile_1", sprites[1].Name);
        Assert.Equal(new Vector2(0f, 0f), sprites[0].Pivot);
        Assert.Equal(NineSlice.Uniform(4f), sprites[0].Border);
    }

    [Fact]
    public void ARectRoundsOutwardsSoNoColumnOfArtworkIsLost() {
        // ⚠ Outwards, never to nearest: a rect a person dragged is a float and a texel is not, and
        // a sprite one texel short shows a seam against the cell beside it.
        var rect = SpriteRect.From("hand", new(3.4f, 7.9f, 10.2f, 4.1f));

        Assert.Equal(3, rect.X);
        Assert.Equal(7, rect.Y);
        Assert.Equal(11, rect.Width);
        Assert.Equal(5, rect.Height);
    }

    [Fact]
    public void ARectTurnsIntoTheSpriteTheRuntimeDraws() {
        var rect = SpriteRect.From("hero", new(32f, 16f, 64f, 32f), new(0f, 0f), NineSlice.Uniform(8f));
        var sprite = rect.ToSprite(new(128, 64), 32f);

        Assert.Equal("hero", sprite.Name);
        Assert.Equal(new Rectangle(0.25f, 0.25f, 0.5f, 0.5f), sprite.Uv);
        Assert.Equal(new Vector2(2f, 1f), sprite.Size);
        Assert.Equal(0.25f, sprite.UnitBorder.Left, 5);
    }
}
