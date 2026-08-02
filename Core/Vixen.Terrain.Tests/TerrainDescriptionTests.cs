// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Terrain;
using Xunit;

namespace Vixen.Terrain.Tests;

/// <summary>The terrain's shape and its derived numbers — [docs/plan/31 § D2].</summary>
public sealed class TerrainDescriptionTests {
    static TerrainDescription Shape(int tileSamples = 8, int tilesX = 2, int tilesZ = 3) =>
        TerrainDescription.Default with { TileSamples = tileSamples, TilesX = tilesX, TilesZ = tilesZ };

    [Fact]
    public void TilesShareTheirBoundaryRowSoTheGridIsQuadsPlusOne() {
        // The arithmetic that makes docs/plan/31 § D2's warning structural. Two tiles of 8 samples
        // are 15 samples across, not 16: the seventh column of one is the zeroth of the next.
        var shape = Shape(tileSamples: 8, tilesX: 2, tilesZ: 3);

        Assert.Equal(7, shape.TileQuads);
        Assert.Equal(15, shape.SamplesX);
        Assert.Equal(22, shape.SamplesZ);
        Assert.Equal(15 * 22, shape.SampleCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(127)]
    [InlineData(129)]
    [InlineData(2048)]
    public void ATileSampleCountJoltCannotAccelerateIsRefused(int tileSamples) {
        // 129 is the round-sounding "128 quads", and it is the number somebody reaches for.
        var reason = Shape(tileSamples).Validate();

        Assert.NotNull(reason);
        Assert.Contains("power of two", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(128)]
    [InlineData(1024)]
    public void APowerOfTwoTileIsAccepted(int tileSamples) => Assert.True(Shape(tileSamples).IsValid);

    [Fact]
    public void ADegenerateShapeIsRefusedWithAReasonADialogCanShow() {
        Assert.NotNull((Shape() with { TilesX = 0 }).Validate());
        Assert.NotNull((Shape() with { MetresPerQuad = 0f }).Validate());
        Assert.NotNull((Shape() with { MetresPerQuad = float.NaN }).Validate());
        Assert.NotNull((Shape() with { MinHeight = 10f, MaxHeight = 10f }).Validate());
        Assert.NotNull((Shape() with { MinHeight = 10f, MaxHeight = 0f }).Validate());
    }

    [Fact]
    public void TheDerivedNumbersAreWhatACreateDialogShows() {
        var shape = TerrainDescription.Default with {
            TileSamples = 128, TilesX = 4, TilesZ = 4, MetresPerQuad = 2f,
            MinHeight = -20f, MaxHeight = 20f
        };

        Assert.Equal(509, shape.SamplesX);
        Assert.Equal(1016f, shape.WidthX, 3);
        Assert.Equal(40f, shape.HeightRange, 3);
        Assert.Equal(509L * 509 * 2, shape.HeightBytes);
        Assert.Equal(16, shape.TileCount);
    }

    /// <summary>
    ///     An authored height range is what buys the precision, and the number is worth stating.
    /// </summary>
    /// <remarks>
    ///     Unreal spends the same sixteen bits over a fixed 512 m window whatever the terrain is. A
    ///     40 m landscape here gets 0.6 mm per step where that window gives 7.8 mm — thirteen times
    ///     better, for the same bytes. This is the claim [§ Improvements] makes, measured.
    /// </remarks>
    [Fact]
    public void ANarrowHeightRangeBuysPrecision() {
        var narrow = TerrainDescription.Default with { MinHeight = -20f, MaxHeight = 20f };
        var unrealShaped = TerrainDescription.Default with { MinHeight = -256f, MaxHeight = 256f };

        Assert.Equal(0.61f, narrow.MetresPerStep * 1000f, 1);
        Assert.Equal(7.81f, unrealShaped.MetresPerStep * 1000f, 1);
        Assert.True(unrealShaped.MetresPerStep / narrow.MetresPerStep > 12f);
    }

    [Fact]
    public void AHeightRoundTripsThroughItsStoredForm() {
        var shape = TerrainDescription.Default with { MinHeight = -100f, MaxHeight = 100f };

        foreach (var metres in new[] { -100f, -37.5f, 0f, 0.001f, 62.25f, 100f }) {
            var stored = shape.StoreHeight(metres);
            Assert.Equal(metres, shape.HeightOf(stored), 2);
        }
    }

    [Fact]
    public void StoringAHeightRoundsSoRepeatedTripsDoNotSink() {
        // Truncation would lose up to a step every time a tool read a height and wrote it back, so a
        // flatten would creep downwards instead of converging.
        var shape = TerrainDescription.Default;
        var stored = shape.StoreHeight(12.3456f);

        for (var pass = 0; pass < 100; pass++) {
            stored = shape.StoreHeight(shape.HeightOf(stored));
        }

        Assert.Equal(12.3456f, shape.HeightOf(stored), 2);
    }

    [Fact]
    public void AHeightOutsideTheRangeIsClampedAndNaNStoresAsTheFloor() {
        var shape = TerrainDescription.Default with { MinHeight = 0f, MaxHeight = 10f };

        Assert.Equal(0, shape.StoreHeight(-50f));
        Assert.Equal(TerrainSamples.MaxHeight, shape.StoreHeight(50f));
        Assert.Equal(0, shape.StoreHeight(float.NaN));
    }

    [Fact]
    public void ASampleOnABoundaryIsTheUpperTilesZeroAndTheLowersLastRow() {
        var shape = Shape(tileSamples: 8, tilesX: 3, tilesZ: 1);

        // Sample 7 is tile 0's last and tile 1's first. TileOf has to pick one, because a caller
        // walking tiles must visit each sample exactly once, and it picks the upper — which makes
        // ownership the half-open range [T·quads, (T+1)·quads).
        Assert.Equal((1, 0, 0, 0), shape.TileOf(7, 0));
        Assert.Equal((0, 0, 6, 0), shape.TileOf(6, 0));
        Assert.Equal((1, 0, 1, 0), shape.TileOf(8, 0));

        // The last sample of the terrain has no upper tile and is clamped back into the last one.
        Assert.Equal((2, 0, 7, 0), shape.TileOf(shape.SamplesX - 1, 0));

        // Every sample is owned exactly once.
        var owners = new Dictionary<(int, int), int>();

        for (var x = 0; x < shape.SamplesX; x++) {
            var (tileX, _, _, _) = shape.TileOf(x, 0);
            owners[(tileX, x)] = owners.GetValueOrDefault((tileX, x)) + 1;
        }

        Assert.Equal(shape.SamplesX, owners.Count);

        // And the tile's own rectangle is the other question: it includes the boundary from both
        // sides, because that is what a tile has to draw and collide with.
        Assert.True(shape.SamplesOf(0, 0).Contains(7, 0));
        Assert.True(shape.SamplesOf(1, 0).Contains(7, 0));
    }

    [Fact]
    public void EveryTilesRectangleIsTheTileSize() {
        var shape = Shape(tileSamples: 8, tilesX: 3, tilesZ: 2);

        for (var z = 0; z < shape.TilesZ; z++) {
            for (var x = 0; x < shape.TilesX; x++) {
                var rect = shape.SamplesOf(x, z);

                Assert.Equal(8, rect.Width);
                Assert.Equal(8, rect.Height);
                Assert.Equal(x * 7, rect.X);
                Assert.Equal(z * 7, rect.Z);
            }
        }
    }
}

/// <summary>Rectangles of samples.</summary>
public sealed class TerrainRectTests {
    [Fact]
    public void AnEmptyRectangleIsTheIdentityOfUnion() {
        // Otherwise the first union of a stroke that has touched nothing drags the record back to
        // sample zero and the undo entry covers the whole terrain.
        var rect = new TerrainRect(10, 20, 5, 5);

        Assert.Equal(rect, TerrainRect.Empty.Union(rect));
        Assert.Equal(rect, rect.Union(TerrainRect.Empty));
        Assert.True(TerrainRect.Empty.Union(TerrainRect.Empty).IsEmpty);
    }

    [Fact]
    public void UnionCoversBothAndClipKeepsOnlyTheOverlap() {
        var left = new TerrainRect(0, 0, 10, 10);
        var right = new TerrainRect(5, 5, 10, 10);

        Assert.Equal(new(0, 0, 15, 15), left.Union(right));
        Assert.Equal(new(5, 5, 5, 5), left.Clip(right));
        Assert.True(left.Clip(new(100, 100, 5, 5)).IsEmpty);
    }

    [Fact]
    public void GrowingReachesTheNeighboursAKernelReads() {
        var rect = new TerrainRect(10, 10, 4, 4);
        var grown = rect.Grow(1);

        Assert.Equal(new(9, 9, 6, 6), grown);
        Assert.True(TerrainRect.Empty.Grow(4).IsEmpty);
    }

    [Fact]
    public void ContainsIsHalfOpenAtTheFarEdge() {
        var rect = new TerrainRect(2, 3, 4, 5);

        Assert.True(rect.Contains(2, 3));
        Assert.True(rect.Contains(5, 7));
        Assert.False(rect.Contains(6, 7));
        Assert.False(rect.Contains(5, 8));
        Assert.Equal(20, rect.Count);
    }
}
