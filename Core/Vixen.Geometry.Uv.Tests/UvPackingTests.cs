// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>What the packer promises about every set of islands, including the ones nobody meant to send.</summary>
/// <remarks>
///     docs/plan/42 § Part 5's U4. The standalone packer is the entry point the request named, so its
///     contract is the one that has to hold under the inputs a real pipeline produces: an island with
///     no area, an island bigger than the atlas, a list with nothing in it, a margin of zero.
/// </remarks>
public class UvPackingTests {
    [Fact]
    public void AnEmptyListPacksToNothing() {
        var placements = UvUnwrap.Pack([], new() { Resolution = 512 }, out var report);

        Assert.Empty(placements);
        Assert.Equal(0, report.ChartCount);
        Assert.Equal(0f, report.PackingEfficiency);
        Assert.Equal(0f, report.EffectiveEfficiency);
        Assert.Single(report.Stages);
        Assert.Equal(UvStage.Pack, report.Stages[0].Stage);
    }

    [Fact]
    public void EveryIslandGetsExactlyOnePlacement() {
        var islands = IslandCorpus.Trellis(120);
        var placements = UvUnwrap.Pack(islands, new() { Resolution = 512 });

        Assert.Equal(islands.Length, placements.Count);

        for (var index = 0; index < placements.Count; index++) {
            Assert.Equal(index, placements[index].Island);
            Assert.InRange(placements[index].Rotation, 0, 3);
            Assert.True(placements[index].Scale > 0f, $"Island {index} was placed at scale {placements[index].Scale}.");
        }
    }

    [Fact]
    public void NothingOverlapsAndNothingLeavesTheAtlas() {
        var islands = IslandCorpus.Trellis(120);
        var settings = new PackSettings { Resolution = 512, Margin = 4 };
        var placements = UvUnwrap.Pack(islands, settings);
        var map = PackedAtlas.Rasterize(islands, placements, 512, Int2.Zero, out var overlaps);

        Assert.Equal(0, overlaps);
        Assert.True(
            PackedAtlas.MinimumBorder(map, 512) >= settings.Margin,
            $"An island came within {PackedAtlas.MinimumBorder(map, 512)} texels of the atlas edge, "
            + $"where the margin is {settings.Margin}. That is the off-by-one at the boundary."
        );
    }

    /// <summary>⚠ The factor-of-two error, pinned. Two adjacent islands share one margin, not two.</summary>
    /// <remarks>
    ///     docs/plan/42 § D8. Each island padding itself by a full margin puts <c>2 × Margin</c> texels
    ///     between neighbours, which wastes a quarter of a 2K atlas at four texels and looks completely
    ///     fine; each padding itself by half leaves <c>Margin</c> between neighbours and <i>Margin/2</i>
    ///     against the atlas edge, which bleeds off the edge and also looks fine. The measurement is the
    ///     only thing that separates the three.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    public void TheGapBetweenTwoIslandsIsTheMarginAndNotTwiceIt(int margin) {
        // A texel density of 16 over a unit square is exactly a 16-texel square, so every number
        // below is an integer and a measured gap is a statement about the packer alone.
        UvIsland[] islands = [IslandCorpus.Square(1f), IslandCorpus.Square(1f)];
        var resolution = 64;
        var placements = UvUnwrap.Pack(
            islands,
            new() { Resolution = resolution, Margin = margin, TexelDensity = 16f }
        );

        var map = PackedAtlas.Rasterize(islands, placements, resolution, Int2.Zero, out var overlaps);

        Assert.Equal(0, overlaps);
        Assert.Equal(2 * 16 * 16, PackedAtlas.Covered(map));
        Assert.Equal(margin, PackedAtlas.MinimumGap(map, resolution, margin + 4));
        Assert.Equal(margin, PackedAtlas.MinimumBorder(map, resolution));
    }

    [Fact]
    public void AZeroMarginPutsIslandsFlushAgainstEachOtherAndTheEdge() {
        UvIsland[] islands = [IslandCorpus.Square(1f), IslandCorpus.Square(1f)];
        var placements = UvUnwrap.Pack(
            islands,
            new() { Resolution = 64, Margin = 0, TexelDensity = 16f }
        );

        var map = PackedAtlas.Rasterize(islands, placements, 64, Int2.Zero, out var overlaps);

        Assert.Equal(0, overlaps);
        Assert.Equal(0, PackedAtlas.MinimumGap(map, 64, 4));
        Assert.Equal(0, PackedAtlas.MinimumBorder(map, 64));
    }

    [Fact]
    public void AZeroAreaIslandIsPlacedRatherThanDropped() {
        var point = new UvIsland([Vector2.Zero, Vector2.Zero, Vector2.Zero], [0, 1, 2], Vector2.Zero, Vector2.Zero, 1f);
        UvIsland[] islands = [IslandCorpus.Square(1f), point, IslandCorpus.Square(0.5f)];

        var placements = UvUnwrap.Pack(islands, new() { Resolution = 128, Margin = 2 }, out var report);

        Assert.Equal(3, placements.Count);
        Assert.Equal(3, report.ChartCount);
        Assert.All(placements, placement => Assert.InRange(placement.Offset.X, 0f, 1f));
        Assert.All(placements, placement => Assert.InRange(placement.Offset.Y, 0f, 1f));
    }

    [Fact]
    public void AnIslandWithNoTrianglesPacksAsItsBoundingBox() {
        // The remesher's patch layout and a layout read back from a file both arrive this way.
        var bounds = new UvIsland([], [], Vector2.Zero, new(2f, 1f), 1f);
        UvIsland[] islands = [bounds, IslandCorpus.Square(1f)];

        var placements = UvUnwrap.Pack(islands, new() { Resolution = 128, Margin = 2, TexelDensity = 16f });

        Assert.Equal(2, placements.Count);
        Assert.True(placements[0].Scale > 0f);
    }

    [Fact]
    public void TheDefaultIslandIsPlacedRatherThanThrowing() {
        UvIsland[] islands = [default, IslandCorpus.Square(1f)];

        var placements = UvUnwrap.Pack(islands, new() { Resolution = 64, Margin = 1 });

        Assert.Equal(2, placements.Count);
    }

    [Fact]
    public void AnIslandLargerThanTheAtlasIsScaledDownAndSaidSo() {
        // Ten world units at 512 texels per unit wants 5120 texels of a 256-texel atlas.
        UvIsland[] islands = [IslandCorpus.Square(10f)];

        var placements = UvUnwrap.Pack(
            islands,
            new() { Resolution = 256, Margin = 4, TexelDensity = 512f, Overflow = PackOverflow.Scale },
            out var report
        );

        Assert.Single(placements);
        Assert.Contains(report.Warnings, warning => warning.Contains("texel density", StringComparison.Ordinal));
        Assert.True(
            report.TexelDensity.Maximum < 512f,
            $"The report claims {report.TexelDensity.Maximum} texels per unit after a rescale that had to happen."
        );
    }

    [Fact]
    public void AnIslandLargerThanTheAtlasIsRefusedWhenRefusalIsAskedFor() {
        UvIsland[] islands = [IslandCorpus.Square(10f)];

        var failure = Assert.Throws<InvalidOperationException>(
            () => UvUnwrap.Pack(
                islands,
                new() { Resolution = 256, Margin = 4, TexelDensity = 512f, Overflow = PackOverflow.Refuse }
            )
        );

        Assert.Contains("do not fit", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AResolutionSmallerThanTheIslandsStillProducesAnAtlas() {
        var islands = IslandCorpus.Trellis(40);

        var placements = UvUnwrap.Pack(islands, new() { Resolution = 16, Margin = 1 });
        var map = PackedAtlas.Rasterize(islands, placements, 16, Int2.Zero, out var overlaps);

        Assert.Equal(40, placements.Count);
        Assert.Equal(0, overlaps);
    }

    [Fact]
    public void AMarginThatConsumesTheAtlasIsRefusedBeforeAnythingIsPacked() {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => UvUnwrap.Pack([IslandCorpus.Square(1f)], new() { Resolution = 8, Margin = 4 })
        );

        Assert.Throws<ArgumentOutOfRangeException>(
            () => UvUnwrap.Pack([IslandCorpus.Square(1f)], new() { Resolution = 0 })
        );

        Assert.Throws<ArgumentOutOfRangeException>(
            () => UvUnwrap.Pack([IslandCorpus.Square(1f)], new() { Resolution = 64, Margin = -1 })
        );
    }

    [Fact]
    public void CoordinatesThatDoNotMatchCornersAreRefusedByName() {
        var mismatched = new UvIsland(
            [Vector2.Zero, Vector2.One, new(1f, 0f)],
            [0, 1],
            Vector2.Zero,
            Vector2.One,
            1f
        );

        var failure = Assert.Throws<ArgumentException>(() => UvUnwrap.Pack([mismatched], new() { Resolution = 64 }));

        Assert.Contains("Island 0", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>docs/plan/42 § D11: a tile boundary is a placement rule, not a second packer.</summary>
    [Fact]
    public void NoIslandStraddlesAUdimTileBoundary() {
        var islands = IslandCorpus.Trellis(200);

        var placements = UvUnwrap.Pack(
            islands,
            new() {
                Resolution = 256,
                Margin = 2,

                // Far more density than one 256² tile can hold, so the spill is the only way out.
                TexelDensity = 900f,
                Overflow = PackOverflow.NextTile
            },
            out var report
        );

        var tiles = placements.Select(placement => placement.Tile).Distinct().ToList();

        Assert.True(tiles.Count > 1, $"The corpus fitted one tile, so this proves nothing about straddling.");
        Assert.Contains(report.Warnings, warning => warning.Contains("UDIM", StringComparison.Ordinal));

        for (var index = 0; index < placements.Count; index++) {
            var placement = placements[index];
            var island = islands[index];
            var minimum = new Vector2(float.MaxValue);
            var maximum = new Vector2(float.MinValue);

            foreach (var coordinate in island.Coordinates) {
                var mapped = placement.Apply(island, coordinate) - new Vector2(placement.Tile.X, placement.Tile.Y);

                minimum = new(MathF.Min(minimum.X, mapped.X), MathF.Min(minimum.Y, mapped.Y));
                maximum = new(MathF.Max(maximum.X, mapped.X), MathF.Max(maximum.Y, mapped.Y));
            }

            Assert.True(
                minimum.X >= 0f && minimum.Y >= 0f && maximum.X <= 1f && maximum.Y <= 1f,
                $"Island {index} runs from {minimum} to {maximum} inside tile {placement.Tile}, which straddles it."
            );
        }

        foreach (var tile in tiles) {
            PackedAtlas.Rasterize(islands, placements, 256, tile, out var overlaps);

            Assert.Equal(0, overlaps);
        }
    }

    /// <summary>docs/plan/42's exit criterion 7: a quarter turn is exact, so a full turn is identity.</summary>
    [Fact]
    public void AQuarterTurnIsExactAndFourOfThemAreIdentity() {
        // Exact binary fractions, so `size - coordinate` is exact and the assertion is about the
        // transform rather than about floating point.
        var island = IslandCorpus.Square(1.5f);
        var size = island.Size;

        foreach (var coordinate in island.Coordinates) {
            var local = coordinate - island.Minimum;
            var turned = local;
            var extent = size;

            for (var turn = 0; turn < 4; turn++) {
                turned = new(extent.Y - turned.Y, turned.X);
                extent = new(extent.Y, extent.X);
            }

            Assert.Equal(local.X, turned.X);
            Assert.Equal(local.Y, turned.Y);
        }
    }

    /// <summary>docs/plan/42's exit criterion 7: repacking leaves the island's own shape untouched.</summary>
    [Fact]
    public void RepackingLeavesIslandShapesUntouched() {
        var islands = IslandCorpus.Trellis(60);
        var placements = UvUnwrap.Pack(islands, new() { Resolution = 512, Margin = 4 });
        var turned = 0;

        for (var index = 0; index < islands.Length; index++) {
            var island = islands[index];
            var placement = placements[index];

            if (placement.Rotation != 0) {
                turned++;
            }

            for (var corner = 1; corner < island.Coordinates.Count; corner++) {
                var before = island.Coordinates[corner] - island.Coordinates[0];
                var after = placement.Apply(island, island.Coordinates[corner])
                    - placement.Apply(island, island.Coordinates[0]);

                Assert.Equal(before.Length() * placement.Scale, after.Length(), 5);
            }
        }

        Assert.True(turned > 0, "Nothing was turned, so the turn is untested by this fixture.");
    }

    [Fact]
    public void TheEfficiencyPairBracketsWhatTheMarginCost() {
        var islands = IslandCorpus.Trellis(200);

        UvUnwrap.Pack(islands, new() { Resolution = 1024, Margin = 4 }, out var margined);
        UvUnwrap.Pack(islands, new() { Resolution = 1024, Margin = 0 }, out var flush);

        Assert.InRange(margined.PackingEfficiency, 0.01f, 1f);
        Assert.InRange(margined.EffectiveEfficiency, margined.PackingEfficiency, 1f);

        // ⚠ The gap between the two *is* the margin's cost, so removing the margin has to close it.
        var cost = margined.EffectiveEfficiency - margined.PackingEfficiency;
        var free = flush.EffectiveEfficiency - flush.PackingEfficiency;

        Assert.True(free < cost, $"A zero margin cost {free:0.0000} and a four-texel one cost {cost:0.0000}.");
    }

    /// <summary>The tail places every island it is handed. A bounded core is not a hidden truncation.</summary>
    [Fact]
    public void TheCoreLimitCapsTheCostAndNotTheOutput() {
        var islands = IslandCorpus.Trellis(300);

        var placements = UvUnwrap.Pack(
            islands,
            new() { Resolution = 512, Margin = 3, CoreLimit = 24 },
            out var report
        );

        var map = PackedAtlas.Rasterize(islands, placements, 512, Int2.Zero, out var overlaps);

        Assert.Equal(300, placements.Count);
        Assert.Equal(0, overlaps);
        Assert.Contains(report.Warnings, warning => warning.Contains("tail sweep", StringComparison.Ordinal));
        Assert.True(PackedAtlas.Covered(map) > 0);
    }

    [Theory]
    [InlineData(PackQuality.Rectangle)]
    [InlineData(PackQuality.Irregular)]
    [InlineData(PackQuality.SuperPatch)]
    public void EveryRungHoldsTheSameMarginRule(PackQuality quality) {
        var islands = IslandCorpus.Trellis(80);
        var settings = new PackSettings { Resolution = 512, Margin = 4, Quality = quality };
        var placements = UvUnwrap.Pack(islands, settings);
        var map = PackedAtlas.Rasterize(islands, placements, 512, Int2.Zero, out var overlaps);

        Assert.Equal(0, overlaps);
        Assert.True(
            PackedAtlas.MinimumGap(map, 512, settings.Margin) >= settings.Margin,
            $"{quality} put two islands within {PackedAtlas.MinimumGap(map, 512, settings.Margin)} texels."
        );

        Assert.True(PackedAtlas.MinimumBorder(map, 512) >= settings.Margin);
    }

    [Fact]
    public void UniformDensityHoldsAcrossEveryChart() {
        // docs/plan/42's exit criterion 5. Islands of wildly different world scale, one density.
        var islands = IslandCorpus.Trellis(150, scale: 1f)
            .Select((island, index) => island with { Scale = 1f + (index % 7) })
            .ToArray();

        UvUnwrap.Pack(islands, new() { Resolution = 1024, Margin = 4, TexelDensity = 64f }, out var report);

        Assert.Equal(64f, report.TexelDensity.Minimum, 3);
        Assert.Equal(64f, report.TexelDensity.Maximum, 3);
        Assert.Equal(0f, report.TexelDensity.Variance, 3);
    }
}
