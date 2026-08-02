// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Terrain;
using Xunit;

namespace Vixen.Terrain.Tests;

/// <summary>
///     Edit layers and the composite — [docs/plan/31 § D4].
/// </summary>
/// <remarks>
///     The load-bearing test is <see cref="TheCachedCompositeAlwaysMatchesTheDefinition" />. Every
///     other one checks a rule; that one checks that the cache of the rule is not stale, which is the
///     failure mode a cached derived value has and the one nothing else would catch — the cached
///     value looks perfectly reasonable, it is just old.
/// </remarks>
public sealed class TerrainLayerTests {
    static TerrainDescription Shape(int tiles = 2) =>
        TerrainDescription.Default with {
            TileSamples = 8, TilesX = tiles, TilesZ = tiles,
            MetresPerQuad = 1f, MinHeight = -100f, MaxHeight = 100f
        };

    static Terrain Flat(int tiles = 2) => new(Shape(tiles));

    /// <summary>Walks every sample and compares the cache against the definition.</summary>
    static void AssertCompositeMatches(Terrain terrain) {
        terrain.Resolve();

        for (var z = 0; z < terrain.Description.SamplesZ; z++) {
            for (var x = 0; x < terrain.Description.SamplesX; x++) {
                Assert.Equal(terrain.CompositeAt(x, z), terrain.Composite[x, z]);
            }
        }
    }

    [Fact]
    public void ANewTerrainIsFlatAtTheHeightItWasAskedFor() {
        var terrain = new Terrain(Shape(), height: 12.5f);

        Assert.Equal(12.5f, terrain.Composite.MetresAt(3, 4), 2);
        Assert.Equal(0, terrain.DirtyTileCount);

        var (minimum, maximum) = terrain.HeightRangeOf(0, 0);
        Assert.Equal(12.5f, minimum, 2);
        Assert.Equal(12.5f, maximum, 2);
    }

    [Fact]
    public void ATerrainWithNoLayersCompositesToItsBase() {
        var terrain = Flat();
        terrain.Base[3, 3] = 40_000;
        terrain.InvalidateAll();

        AssertCompositeMatches(terrain);
        Assert.Equal(40_000, terrain.Composite[3, 3]);
    }

    [Fact]
    public void ALayersDeltaAddsToTheBaseAndTheBaseSurvives() {
        var terrain = Flat();
        var layer = terrain.AddLayer("Mountains");

        terrain.Base[3, 3] = 30_000;
        layer.SetDelta(3, 3, 5_000);
        terrain.InvalidateAll();
        terrain.Resolve();

        Assert.Equal(35_000, terrain.Composite[3, 3]);
        Assert.Equal(30_000, terrain.Base[3, 3]);
    }

    [Fact]
    public void HidingALayerRemovesItsContributionAndShowingItPutsItBack() {
        var terrain = Flat();
        var layer = terrain.AddLayer("Mountains");

        terrain.Base[3, 3] = 30_000;
        layer.SetDelta(3, 3, 5_000);
        terrain.InvalidateAll();
        terrain.Resolve();

        layer.IsVisible = false;
        terrain.InvalidateAll();
        terrain.Resolve();
        Assert.Equal(30_000, terrain.Composite[3, 3]);

        layer.IsVisible = true;
        terrain.InvalidateAll();
        terrain.Resolve();
        Assert.Equal(35_000, terrain.Composite[3, 3]);
    }

    [Fact]
    public void ANegativeHeightAlphaSubtracts() {
        // Unreal's semantic, and genuinely useful: the same sculpt inverted is a valley.
        var terrain = Flat();
        var layer = terrain.AddLayer("Mountains");

        terrain.Base[3, 3] = 30_000;
        layer.SetDelta(3, 3, 5_000);
        layer.HeightAlpha = -1f;
        terrain.InvalidateAll();
        terrain.Resolve();

        Assert.Equal(25_000, terrain.Composite[3, 3]);
    }

    [Fact]
    public void AFractionalHeightAlphaScalesTheContribution() {
        var terrain = Flat();
        var layer = terrain.AddLayer("Mountains");

        terrain.Base[3, 3] = 30_000;
        layer.SetDelta(3, 3, 5_000);
        layer.HeightAlpha = 0.5f;
        terrain.InvalidateAll();
        terrain.Resolve();

        Assert.Equal(32_500, terrain.Composite[3, 3]);
    }

    [Fact]
    public void TheCompositeClampsRatherThanWrapping() {
        var terrain = Flat();
        var layer = terrain.AddLayer("Mountains");

        terrain.Base[3, 3] = 60_000;
        layer.SetDelta(3, 3, short.MaxValue);
        terrain.InvalidateAll();
        terrain.Resolve();

        Assert.Equal(TerrainSamples.MaxHeight, terrain.Composite[3, 3]);
    }

    [Fact]
    public void ThreeLayersStack() {
        var terrain = Flat();

        terrain.Base[2, 2] = 10_000;

        foreach (var name in new[] { "A", "B", "C" }) {
            terrain.AddLayer(name).SetDelta(2, 2, 1_000);
        }

        terrain.InvalidateAll();
        terrain.Resolve();

        Assert.Equal(13_000, terrain.Composite[2, 2]);
        AssertCompositeMatches(terrain);
    }

    // --- The cache ----------------------------------------------------------

    /// <summary>
    ///     The cached composite is never allowed to disagree with the definition.
    /// </summary>
    [Fact]
    public void TheCachedCompositeAlwaysMatchesTheDefinition() {
        var terrain = Flat(tiles: 3);
        var first = terrain.AddLayer("A");
        var second = terrain.AddLayer("B");

        var brush = TerrainBrush.Default with { Radius = 4f, Strength = 1f, Falloff = 0.5f };

        for (var step = 0; step < 12; step++) {
            var stamp = new BrushStamp(new(step * 1.7f, step * 1.3f));
            TerrainSculpt.Sculpt(terrain, step % 2 == 0 ? first : second, brush, stamp, 5f);
        }

        AssertCompositeMatches(terrain);

        second.HeightAlpha = -0.5f;
        terrain.InvalidateAll();
        AssertCompositeMatches(terrain);

        first.IsVisible = false;
        terrain.InvalidateAll();
        AssertCompositeMatches(terrain);
    }

    [Fact]
    public void OnlyTheTilesAStrokeTouchedAreMadeDirty() {
        var terrain = Flat(tiles: 4);
        var layer = terrain.AddLayer("A");

        Assert.Equal(0, terrain.DirtyTileCount);

        // A small brush in the corner of the terrain, well inside one tile.
        var brush = TerrainBrush.Default with { Radius = 1.5f, Strength = 1f };
        TerrainSculpt.Sculpt(terrain, layer, brush, new(new(2f, 2f)), 5f);

        Assert.Equal(1, terrain.DirtyTileCount);
        Assert.True(terrain.IsTileDirty(0, 0));
        Assert.Equal(1, terrain.Resolve());
        Assert.Equal(0, terrain.DirtyTileCount);
    }

    /// <summary>
    ///     A stroke on a tile boundary dirties both tiles, not just the one that owns the sample.
    /// </summary>
    /// <remarks>
    ///     The seam bug in its last remaining form. Sample sharing means there is one copy to write,
    ///     so the heights cannot disagree — but the two tiles' <em>caches</em> can, and a boundary
    ///     stroke that dirtied only the lower tile would leave the upper one drawing the old ground
    ///     along one row.
    /// </remarks>
    [Fact]
    public void AStrokeOnABoundaryDirtiesBothTilesThatShareIt() {
        var terrain = Flat(tiles: 3);
        var layer = terrain.AddLayer("A");

        // Sample 7 is the boundary between tile 0 and tile 1 at a metre per quad.
        var brush = TerrainBrush.Default with { Radius = 1f, Strength = 1f, Falloff = 0f };
        TerrainSculpt.Sculpt(terrain, layer, brush, new(new(7f, 2f)), 5f);

        Assert.True(terrain.IsTileDirty(0, 0));
        Assert.True(terrain.IsTileDirty(1, 0));

        AssertCompositeMatches(terrain);
    }

    // --- The stack ----------------------------------------------------------

    /// <summary>A layer is sparse: it costs what it touched, not what the terrain is.</summary>
    [Fact]
    public void ALayerCostsTheChunksItTouchedAndNoMore() {
        // A real-sized terrain — 2 tiles of 128 samples is 255 across — so that a sample at 70 is
        // in bounds and in a different chunk. A 29-sample terrain would silently drop the write.
        var big = TerrainDescription.Default with { TileSamples = 128, TilesX = 2, TilesZ = 2 };
        var layer = new TerrainEditLayer(big, "A");

        Assert.True(layer.IsEmpty);
        Assert.Equal(0, layer.Bytes);

        layer.SetDelta(1, 1, 100);
        Assert.Equal(1, layer.ChunkCount);

        // Same chunk — 64 samples across — so nothing new is allocated.
        layer.SetDelta(40, 40, 100);
        Assert.Equal(1, layer.ChunkCount);

        layer.SetDelta(70, 1, 100);
        Assert.Equal(2, layer.ChunkCount);

        layer.SetDelta(70, 200, 100);
        Assert.Equal(3, layer.ChunkCount);

        // Three chunks of 64² shorts, against a terrain of 255² samples — an eighth of the cost of
        // a layer that allocated the whole grid.
        Assert.Equal(3L * 64 * 64 * 2, layer.Bytes);
        Assert.True(layer.Bytes < big.HeightBytes / 2);
    }

    [Fact]
    public void WritingZeroIntoAnUntouchedChunkAllocatesNothing() {
        var layer = new TerrainEditLayer(Shape(4), "A");

        layer.SetDelta(100, 100, 0);
        Assert.Equal(0, layer.ChunkCount);
        Assert.Equal(0, layer.Bytes);
    }

    [Fact]
    public void AddingADeltaSaturatesRatherThanWrapping() {
        // A brush held down accumulates without bound; wrapping turns the top of a mountain into a
        // pit the moment it crosses 32 767.
        var layer = new TerrainEditLayer(Shape(), "A");

        layer.SetDelta(1, 1, 32_000);
        layer.AddDelta(1, 1, 5_000);
        Assert.Equal(short.MaxValue, layer.DeltaAt(1, 1));

        layer.SetDelta(2, 2, -32_000);
        layer.AddDelta(2, 2, -5_000);
        Assert.Equal(short.MinValue, layer.DeltaAt(2, 2));
    }

    [Fact]
    public void CollapsingAddsTheUpperLayerIntoTheLowerAtItsAlpha() {
        var terrain = Flat();
        var lower = terrain.AddLayer("Lower");
        var upper = terrain.AddLayer("Upper");

        lower.SetDelta(3, 3, 1_000);
        upper.SetDelta(3, 3, 2_000);
        upper.HeightAlpha = 0.5f;

        terrain.InvalidateAll();
        terrain.Resolve();
        var before = terrain.Composite[3, 3];

        terrain.Collapse(1);

        Assert.Single(terrain.Layers);
        Assert.Equal(2_000, lower.DeltaAt(3, 3));

        terrain.Resolve();
        Assert.Equal(before, terrain.Composite[3, 3]);
    }

    [Fact]
    public void CollapsingTheBottomLayerIsRefused() {
        var terrain = Flat();
        terrain.AddLayer("Only");

        Assert.Throws<ArgumentOutOfRangeException>(() => terrain.Collapse(0));
    }

    [Fact]
    public void ErasingALayersRectangleLeavesTheLayersBelowAlone() {
        var terrain = Flat();
        var lower = terrain.AddLayer("Lower");
        var upper = terrain.AddLayer("Upper");

        lower.SetDelta(3, 3, 1_000);
        upper.SetDelta(3, 3, 2_000);

        upper.Clear(new(0, 0, 8, 8));
        terrain.InvalidateAll();
        terrain.Resolve();

        Assert.Equal(0, upper.DeltaAt(3, 3));
        Assert.Equal(1_000, lower.DeltaAt(3, 3));
    }

    [Fact]
    public void ReorderingInvalidatesEverythingBecauseTheClampIsNotCommutative() {
        var terrain = Flat();
        var first = terrain.AddLayer("A");
        var second = terrain.AddLayer("B");

        terrain.Resolve();
        Assert.Equal(0, terrain.DirtyTileCount);

        terrain.MoveLayer(0, 1);

        Assert.Equal(terrain.Description.TileCount, terrain.DirtyTileCount);
        Assert.Equal("B", terrain.Layers[0].Name);
        Assert.Equal("A", terrain.Layers[1].Name);
        Assert.Same(second, terrain.Layers[0]);
        Assert.Same(first, terrain.Layers[1]);
    }

    // --- Reserved layers ----------------------------------------------------

    [Fact]
    public void AReservedLayerRefusesTheBrushAndSaysWhichGeneratorOwnsIt() {
        var terrain = Flat();
        var splines = terrain.AddLayer("Splines", TerrainLayerKind.Splines);

        Assert.False(splines.AcceptsBrush);

        var thrown = Assert.Throws<ArgumentException>(() => new TerrainStroke(terrain, splines));
        Assert.Contains("Splines", thrown.Message, StringComparison.Ordinal);

        // And the kernel is a no-op rather than a throw, so a tool that got there anyway does nothing.
        var rect = TerrainSculpt.Sculpt(terrain, splines, TerrainBrush.Default, new(new(2f, 2f)), 5f);
        Assert.True(rect.IsEmpty);
        Assert.True(splines.IsEmpty);
    }

    [Fact]
    public void ALockedLayerRefusesTheBrushToo() {
        var terrain = Flat();
        var layer = terrain.AddLayer("A");
        layer.IsLocked = true;

        Assert.False(layer.AcceptsBrush);

        var thrown = Assert.Throws<ArgumentException>(() => new TerrainStroke(terrain, layer));
        Assert.Contains("locked", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARemovedLayerStopsContributing() {
        var terrain = Flat();
        var layer = terrain.AddLayer("A");

        layer.SetDelta(3, 3, 5_000);
        terrain.InvalidateAll();
        terrain.Resolve();
        Assert.Equal(5_000, terrain.Composite[3, 3] - terrain.Base[3, 3]);

        Assert.True(terrain.RemoveLayer(layer));
        terrain.Resolve();

        Assert.Equal(terrain.Base[3, 3], terrain.Composite[3, 3]);
        Assert.False(terrain.RemoveLayer(layer));
    }
}
