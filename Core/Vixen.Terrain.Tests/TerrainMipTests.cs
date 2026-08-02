// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Terrain.Tests;

/// <summary>The height mip chain — [docs/plan/31 § T1]'s last owed item.</summary>
public sealed class TerrainMipTests {
    static TerrainDescription Description =>
        TerrainDescription.Default with { TilesX = 1, TilesZ = 1, TileSamples = 128 };

    /// <summary>A tile's levels halve its quads, not its samples.</summary>
    /// <remarks>
    ///     ⚠ <b>A tile is a power of two <em>plus one</em> samples, so a level is not half its
    ///     parent.</b> 129 → 65 → 33 keeps the boundary sample on the boundary; 129 → 64 drops the
    ///     last row, and the seam it opens is one texel wide and permanent.
    /// </remarks>
    [Theory]
    [InlineData(129, new[] { 129, 65, 33, 17, 9, 5, 3, 2 })]
    [InlineData(65, new[] { 65, 33, 17, 9, 5, 3, 2 })]
    [InlineData(5, new[] { 5, 3, 2 })]
    public void EachLevelHalvesTheQuads(int tileSamples, int[] expected) {
        Assert.Equal(expected.Length, TerrainMips.LevelCount(tileSamples));

        for (var level = 0; level < expected.Length; level++) {
            Assert.Equal(expected[level], TerrainMips.SamplesAt(tileSamples, level));
        }
    }

    [Fact]
    public void ALevelNeverShrinksBelowAQuad() {
        Assert.Equal(TerrainMips.MinimumSamples, TerrainMips.SamplesAt(129, 40));
        Assert.Equal(TerrainMips.MinimumSamples, TerrainMips.SamplesAt(2, 1));
    }

    [Fact]
    public void TheChainIsAsBigAsItsLevels() {
        var total = 0L;

        for (var level = 0; level < TerrainMips.LevelCount(129); level++) {
            var samples = TerrainMips.SamplesAt(129, level);

            total += (long)samples * samples;
        }

        Assert.Equal(total, TerrainMips.ChainSamples(129));
    }

    /// <summary>A ridge survives every level.</summary>
    /// <remarks>
    ///     ⚠ <b>The decision the whole file is about.</b> An averaged mip sinks a peak by a quarter
    ///     per level, so a distant patch draws a mountain that is not the mountain — and the error
    ///     compounds, which is why it is invisible near the camera and obvious on the horizon.
    /// </remarks>
    [Fact]
    public void APeakSurvivesToTheCoarsestLevel() {
        var terrain = new Terrain(Description, 0f);
        var layer = terrain.AddLayer("Peak");

        layer.SetDelta(40, 40, 20_000);

        terrain.InvalidateAll();
        terrain.Resolve();

        var chain = new ushort[TerrainMips.ChainSamples(Description.TileSamples)];

        TerrainMips.Build(terrain, 0, 0, chain);

        var rest = terrain.Composite[0, 0];
        var at = 0;

        for (var level = 0; level < TerrainMips.LevelCount(Description.TileSamples); level++) {
            var samples = TerrainMips.SamplesAt(Description.TileSamples, level);
            var highest = (ushort)0;

            for (var index = 0; index < samples * samples; index++) {
                highest = Math.Max(highest, chain[at + index]);
            }

            Assert.True(
                highest > rest,
                $"level {level} lost the peak: its highest sample is {highest} and the ground is {rest}."
            );

            at += samples * samples;
        }
    }

    /// <summary>And the peak's own value, not a fraction of it.</summary>
    [Fact]
    public void TheReductionIsAMaximumRatherThanAnAverage() {
        var terrain = new Terrain(Description, 0f);
        var layer = terrain.AddLayer("Peak");

        layer.SetDelta(40, 40, 20_000);

        terrain.InvalidateAll();
        terrain.Resolve();

        var peak = terrain.Composite[40, 40];
        var chain = new ushort[TerrainMips.ChainSamples(Description.TileSamples)];

        TerrainMips.Build(terrain, 0, 0, chain);

        var samples = Description.TileSamples;
        var second = samples * samples;
        var secondSize = TerrainMips.SamplesAt(samples, 1);

        Assert.Equal(peak, chain[second + ((20 * secondSize) + 20)]);
    }

    [Fact]
    public void LevelZeroIsTheCompositeItself() {
        var terrain = new Terrain(Description, 12f);

        terrain.Resolve();

        var chain = new ushort[TerrainMips.ChainSamples(Description.TileSamples)];
        var written = TerrainMips.Build(terrain, 0, 0, chain);

        Assert.Equal(TerrainMips.ChainSamples(Description.TileSamples), written);

        for (var z = 0; z < Description.TileSamples; z++) {
            for (var x = 0; x < Description.TileSamples; x++) {
                Assert.Equal(terrain.Composite[x, z], chain[(z * Description.TileSamples) + x]);
            }
        }
    }

    /// <summary>Reducing never reads past a level's own edge.</summary>
    /// <remarks>
    ///     ⚠ <b>A level of odd size has a last row whose parent is one sample rather than two</b>, and
    ///     reading past it takes the first sample of the next row — which puts the far edge of a tile
    ///     into its near one, so the heightfield wraps.
    /// </remarks>
    [Fact]
    public void TheFarEdgeNeverAppearsInTheNearOne() {
        var description = TerrainDescription.Default with { TilesX = 1, TilesZ = 1, TileSamples = 8 };
        var terrain = new Terrain(description, 0f);
        var layer = terrain.AddLayer("Edge");

        // A wall along the last column only.
        for (var z = 0; z < description.SamplesZ; z++) {
            layer.SetDelta(description.SamplesX - 1, z, 20_000);
        }

        terrain.InvalidateAll();
        terrain.Resolve();

        var rest = terrain.Composite[0, 0];
        var chain = new ushort[TerrainMips.ChainSamples(8)];

        TerrainMips.Build(terrain, 0, 0, chain);

        var at = 8 * 8;
        var size = TerrainMips.SamplesAt(8, 1);

        // The first column of level 1 covers columns 0 and 1 of level 0, neither of which is the wall.
        for (var z = 0; z < size; z++) {
            Assert.Equal(rest, chain[at + (z * size)]);
        }
    }

    [Fact]
    public void ADestinationTooSmallIsRefused() {
        var terrain = new Terrain(Description, 0f);

        var thrown = Assert.Throws<ArgumentException>(
            () => TerrainMips.Build(terrain, 0, 0, new ushort[16])
        );

        Assert.Contains("chain is", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>Each tile reduces its own copy of the shared boundary sample, so the two agree.</summary>
    [Fact]
    public void TwoTilesAgreeAboutTheirSharedBoundary() {
        var description = TerrainDescription.Default with { TilesX = 2, TilesZ = 1, TileSamples = 8 };
        var terrain = new Terrain(description, 0f);
        var layer = terrain.AddLayer("Ridge");

        for (var z = 0; z < description.SamplesZ; z++) {
            layer.SetDelta(7, z, 12_000);
        }

        terrain.InvalidateAll();
        terrain.Resolve();

        var left = new ushort[TerrainMips.ChainSamples(8)];
        var right = new ushort[TerrainMips.ChainSamples(8)];

        TerrainMips.Build(terrain, 0, 0, left);
        TerrainMips.Build(terrain, 1, 0, right);

        // Sample 7 is the left tile's last column and the right tile's first.
        for (var z = 0; z < 8; z++) {
            Assert.Equal(left[(z * 8) + 7], right[z * 8]);
        }
    }
}
