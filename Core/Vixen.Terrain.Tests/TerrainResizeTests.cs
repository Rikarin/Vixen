// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Terrain.Tests;

/// <summary>Resizing a terrain without losing what is on it — [docs/plan/31 § The terrain panel].</summary>
public sealed class TerrainResizeTests {
    static TerrainDescription Shape =>
        TerrainDescription.Default with {
            TileSamples = 32, TilesX = 2, TilesZ = 2,
            MetresPerQuad = 1f, MinHeight = -100f, MaxHeight = 100f
        };

    static Terrain Built(out TerrainEditLayer layer) {
        var terrain = new Terrain(Shape);

        layer = terrain.AddLayer("Sculpt");
        layer.SetDelta(10, 10, (short)(20f / terrain.Description.MetresPerStep));

        terrain.InvalidateAll();
        terrain.Resolve();

        return terrain;
    }

    [Fact]
    public void Adding_tiles_keeps_the_ground_that_was_there() {
        var terrain = Built(out _);
        var grown = TerrainResize.WithTiles(terrain, 4, 4);

        Assert.Equal(4, grown.Description.TilesX);
        Assert.Equal(20f, grown.Composite.MetresAt(10, 10), 1);

        // And the new ground is the fill, which for a terrain of the default range is its floor.
        Assert.Equal(0f, grown.Composite.MetresAt(100, 100), 1);
    }

    [Fact]
    public void The_layer_stack_comes_across_with_its_deltas_and_its_flags() {
        var terrain = Built(out var layer);

        layer.HeightAlpha = 0.5f;
        layer.IsLocked = true;

        var grown = TerrainResize.WithTiles(terrain, 3, 3);

        Assert.Single(grown.Layers);
        Assert.Equal("Sculpt", grown.Layers[0].Name);
        Assert.Equal(0.5f, grown.Layers[0].HeightAlpha, 4);
        Assert.True(grown.Layers[0].IsLocked);
        Assert.Equal(layer.DeltaAt(10, 10), grown.Layers[0].DeltaAt(10, 10));

        // A new object, so editing one does not move the other.
        Assert.NotSame(layer, grown.Layers[0]);
    }

    [Fact]
    public void Cropping_keeps_what_is_inside_and_discards_the_rest() {
        var terrain = new Terrain(Shape);
        var layer = terrain.AddLayer("Sculpt");

        layer.SetDelta(10, 10, 5000);
        layer.SetDelta(50, 50, 5000);
        terrain.InvalidateAll();

        var cropped = TerrainResize.WithTiles(terrain, 1, 1);

        Assert.Equal(32, cropped.Description.SamplesX);
        Assert.Equal(5000, cropped.Layers[0].DeltaAt(10, 10));
    }

    /// <summary>Changing the height range keeps the metres and spends the precision.</summary>
    /// <remarks>
    ///     ⚠ <b>[§ D2]: "the range is a property of the asset and changing it rescales, with the
    ///     dialog saying so".</b> Carrying the stored numbers across instead would silently move every
    ///     hill — a mountain at 40 m in a ±100 m range becomes 400 m in a ±1000 m one, which looks
    ///     exactly like a bug in the importer.
    /// </remarks>
    [Fact]
    public void Widening_the_height_range_keeps_every_height_in_metres() {
        var terrain = Built(out _);
        var wider = TerrainResize.To(terrain, terrain.Description with { MinHeight = -1000f, MaxHeight = 1000f });

        Assert.Equal(20f, wider.Composite.MetresAt(10, 10), 0);
        Assert.Equal(0f, wider.Composite.MetresAt(0, 0), 0);

        // And the precision is what was paid for it.
        Assert.True(wider.Description.MetresPerStep > terrain.Description.MetresPerStep * 9f);
    }

    /// <summary>A delta is a difference, so it scales by the ratio rather than through StoreHeight.</summary>
    /// <remarks>
    ///     ⚠ <b>Putting a delta through the absolute conversion adds the old minimum and subtracts the
    ///     new one</b>, which for a terrain whose floor moved turns every edit layer into a uniform
    ///     offset of the whole terrain — visible as the base showing through everywhere the layer was
    ///     empty.
    /// </remarks>
    [Fact]
    public void A_layers_deltas_survive_a_range_change_as_the_metres_they_were() {
        var terrain = new Terrain(Shape);
        var layer = terrain.AddLayer("Sculpt");

        layer.SetDelta(10, 10, (short)(20f / terrain.Description.MetresPerStep));
        terrain.InvalidateAll();
        terrain.Resolve();

        // The floor moves as well as the range, which is what catches the absolute conversion.
        var moved = TerrainResize.To(terrain, terrain.Description with { MinHeight = -400f, MaxHeight = 400f });

        Assert.Equal(20f, moved.Composite.MetresAt(10, 10), 0);

        // Everything the layer never touched is still the base, rather than offset by the change.
        Assert.Equal(0f, moved.Composite.MetresAt(20, 20), 0);
    }

    [Fact]
    public void The_paint_channels_and_the_holes_come_across_too() {
        var terrain = new Terrain(Shape);

        terrain.Weights.AddLayer("Grass");
        terrain.Weights.AddLayer("Rock");
        terrain.Weights.Paint(1, 10, 10, 100);
        terrain.Holes.SetHole(12, 12, true);

        var grown = TerrainResize.WithTiles(terrain, 3, 3);

        Assert.Equal(["Grass", "Rock"], grown.Weights.Names);
        Assert.Equal(terrain.Weights.WeightAt(1, 10, 10), grown.Weights.WeightAt(1, 10, 10));
        Assert.True(grown.Holes.IsHole(12, 12));

        // ⚠ And the invariant still holds, including over the ground that was added: a sample nobody
        // has painted has to sum to the total like every other one.
        Assert.Null(grown.Weights.Verify());
    }

    [Fact]
    public void A_shape_a_terrain_cannot_have_is_refused_rather_than_built() {
        var terrain = Built(out _);

        Assert.Throws<ArgumentException>(
            () => TerrainResize.To(terrain, terrain.Description with { TileSamples = 33 })
        );
    }

    [Fact]
    public void The_source_terrain_is_left_alone() {
        var terrain = Built(out var layer);
        var grown = TerrainResize.WithTiles(terrain, 4, 4);

        grown.Layers[0].SetDelta(10, 10, 0);

        Assert.Equal(2, terrain.Description.TilesX);
        Assert.NotEqual(0, layer.DeltaAt(10, 10));
    }
}
