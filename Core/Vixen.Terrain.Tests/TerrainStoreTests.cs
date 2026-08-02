// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Terrain.Tests;

/// <summary>The <c>.vxterrain</c> format — [docs/plan/31 § T3]'s owed asset.</summary>
public sealed class TerrainStoreTests {
    static Terrain Built(int tiles = 2, int tileSamples = 32) =>
        new(
            new() {
                TileSamples = tileSamples,
                TilesX = tiles,
                TilesZ = tiles,
                MetresPerQuad = 1f,
                MinHeight = -100f,
                MaxHeight = 100f
            }
        );

    static Terrain Sculpted() {
        var terrain = Built();
        var layer = terrain.AddLayer("Sculpt");

        TerrainSculpt.Sculpt(terrain, layer, TerrainBrush.Default with { Radius = 6f, Strength = 1f }, new(new(20f, 20f)), 25f);
        terrain.Resolve();

        return terrain;
    }

    [Fact]
    public void TheDescriptionSurvives() {
        var terrain = Built(tiles: 3, tileSamples: 64);
        var read = TerrainStore.Read(TerrainStore.Write(terrain));

        Assert.Equal(terrain.Description, read.Description);
    }

    /// <summary>The composite comes back, which means the layers did.</summary>
    /// <remarks>
    ///     ⚠ <b>The composite is a cache and is deliberately not written.</b> The layer stack is the
    ///     definition; writing both would be writing a number twice and guaranteeing they disagree the
    ///     first time somebody edits the file. Reading recomposites, which is the same code the editor
    ///     runs — so this asserts the layers survived rather than that a cached array did.
    /// </remarks>
    [Fact]
    public void ASculptedTerrainComesBackTheSame() {
        var terrain = Sculpted();
        var read = TerrainStore.Read(TerrainStore.Write(terrain));

        read.Resolve();

        Assert.Equal(terrain.Composite.Span.ToArray(), read.Composite.Span.ToArray());
        Assert.Equal(terrain.Layers.Count, read.Layers.Count);
        Assert.Equal("Sculpt", read.Layers[0].Name);
    }

    /// <summary>A layer's state comes with it — its name, its kind, its alpha and its flags.</summary>
    [Fact]
    public void ALayersStateSurvives() {
        var terrain = Built();
        var layer = terrain.AddLayer("Roads", TerrainLayerKind.Splines);

        layer.HeightAlpha = 0.35f;
        layer.IsVisible = false;
        layer.IsLocked = true;

        var read = TerrainStore.Read(TerrainStore.Write(terrain));
        var restored = read.Layers[0];

        Assert.Equal("Roads", restored.Name);
        Assert.Equal(TerrainLayerKind.Splines, restored.Kind);
        Assert.Equal(0.35f, restored.HeightAlpha, 5);
        Assert.False(restored.IsVisible);
        Assert.True(restored.IsLocked);
    }

    /// <summary>Only the chunks a layer touched are written.</summary>
    /// <remarks>
    ///     ⚠ <b>An edit layer over a 4 km² terrain that somebody sculpted one hill into is sixteen
    ///     million zeroes and a hundred thousand numbers.</b> Storing the zeroes is what makes a stack
    ///     of layers unaffordable, so a layer's cost is the size of the edit rather than of the world.
    /// </remarks>
    [Fact]
    public void AnUntouchedLayerCostsAlmostNothing() {
        var bare = Built();
        var withLayer = Built();

        withLayer.AddLayer("Empty");

        var difference = TerrainStore.Write(withLayer).Length - TerrainStore.Write(bare).Length;

        // A name, a kind, an alpha, two flags and a chunk count — tens of bytes, not a heightfield.
        Assert.InRange(difference, 1, 128);
    }

    /// <summary>Paint layers and their weights survive.</summary>
    [Fact]
    public void PaintLayersSurvive() {
        var terrain = Built();

        terrain.Weights.AddLayer(
            TerrainLayerDescription.Of("Grass") with { Albedo = "T/grass", TilingMetres = 6f }
        );

        terrain.Weights.AddLayer(TerrainLayerDescription.Of("Rock"));
        terrain.Weights.SetWeight(1, 4, 4, 200);

        var read = TerrainStore.Read(TerrainStore.Write(terrain));

        Assert.Equal(2, read.Weights.LayerCount);
        Assert.Equal("T/grass", read.Weights.LayerOf(0).Albedo);
        Assert.Equal(6f, read.Weights.LayerOf(0).TilingMetres, 5);
        Assert.Equal(200, read.Weights.WeightAt(1, 4, 4));
    }

    /// <summary>Holes survive, and they are stored as coordinates rather than as a mask.</summary>
    /// <remarks>
    ///     ⚠ <b>A terrain with three holes in it is the normal case</b>, and a bitmask over sixteen
    ///     million samples is two megabytes to say "three".
    /// </remarks>
    [Fact]
    public void HolesSurviveAndCostWhatTheyAre() {
        var terrain = Built();

        terrain.Holes.SetHole(5, 5, true);
        terrain.Holes.SetHole(6, 5, true);

        var written = TerrainStore.Write(terrain);
        var read = TerrainStore.Read(written);

        Assert.True(read.Holes.IsHole(5, 5));
        Assert.True(read.Holes.IsHole(6, 5));
        Assert.False(read.Holes.IsHole(7, 5));
        Assert.Equal(2, read.Holes.HoleCount);

        // Two holes cost two coordinate pairs, not a mask over the world.
        Assert.InRange(written.Length - TerrainStore.Write(Built()).Length, 1, 64);
    }

    /// <summary>A version this build does not write is refused rather than misread.</summary>
    /// <remarks>
    ///     ⚠ <b>A heightfield read with the wrong field order is not a parse error</b> — it is a
    ///     terrain that loads and looks like static, and the person seeing it has no reason to suspect
    ///     the format.
    /// </remarks>
    [Fact]
    public void AFutureVersionIsRefused() {
        var written = TerrainStore.Write(Built());

        written[TerrainStore.Magic.Length] = 99;

        Assert.Contains(
            "version",
            Assert.Throws<ArgumentException>(() => TerrainStore.Read(written)).Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void SomethingThatIsNotATerrainIsRefused() {
        Assert.Throws<ArgumentException>(() => TerrainStore.Read("nope"u8));
    }

    /// <summary>The base byte count is what a create form shows, and it is the format's own number.</summary>
    [Fact]
    public void TheBaseByteCountIsWhatItWrites() {
        var terrain = Built();
        var written = TerrainStore.Write(terrain);

        // The bare file is the header and the heightfield, plus the empty layer, weight and hole
        // counts — three integers.
        Assert.Equal(TerrainStore.BaseByteCount(terrain.Description) + (3 * sizeof(int)), written.Length);
    }
}
