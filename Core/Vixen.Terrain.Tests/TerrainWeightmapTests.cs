// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Terrain.Tests;

/// <summary>Weightmap import and export, and the layer asset — [docs/plan/31 § T4].</summary>
public sealed class TerrainWeightmapTests {
    static TerrainDescription Shape =>
        TerrainDescription.Default with {
            TileSamples = 32, TilesX = 2, TilesZ = 2,
            MetresPerQuad = 1f, MinHeight = -100f, MaxHeight = 100f
        };

    static Terrain Built() {
        var terrain = new Terrain(Shape);

        terrain.Weights.AddLayer("Grass");
        terrain.Weights.AddLayer("Rock");

        return terrain;
    }

    [Fact]
    public void A_weightmap_round_trips_through_export_and_import() {
        var terrain = Built();

        TerrainPaint.Paint(
            terrain,
            1,
            TerrainBrush.Default with { Radius = 10f, Strength = 1f },
            new(new(30f, 30f)),
            amount: 255
        );

        var bytes = new byte[TerrainWeightmap.ByteCount(terrain.Description)];

        Assert.Equal(bytes.Length, TerrainWeightmap.Export(terrain, 1, bytes));

        var round = Built();
        TerrainWeightmap.Import(round, 1, bytes, terrain.Description.SamplesX, terrain.Description.SamplesZ);

        for (var z = 0; z < terrain.Description.SamplesZ; z++) {
            for (var x = 0; x < terrain.Description.SamplesX; x++) {
                Assert.Equal(terrain.Weights.WeightAt(1, x, z), round.Weights.WeightAt(1, x, z));
            }
        }
    }

    /// <summary>An imported mask lands on a terrain whose sum still adds up.</summary>
    /// <remarks>
    ///     ⚠ <b>A mask painted in an external tool has no idea the other layers exist.</b> Writing it
    ///     verbatim leaves every sample it touched summing to something other than 255 — a terrain
    ///     that looks fine and reports a drift the next time anything checks. The import goes through
    ///     the same redistribution painting it by hand would have.
    /// </remarks>
    [Fact]
    public void An_import_restores_the_invariant_rather_than_trusting_the_file() {
        var terrain = Built();
        var mask = new byte[terrain.Description.SampleCount];

        // A mask that means nothing about the other layers: half of it fully covered.
        for (var z = 0; z < terrain.Description.SamplesZ; z++) {
            for (var x = 0; x < terrain.Description.SamplesX / 2; x++) {
                mask[(z * terrain.Description.SamplesX) + x] = 255;
            }
        }

        TerrainWeightmap.Import(terrain, 1, mask, terrain.Description.SamplesX, terrain.Description.SamplesZ);

        Assert.Equal(255, terrain.Weights.WeightAt(1, 5, 5));
        Assert.Equal(0, terrain.Weights.WeightAt(0, 5, 5));
        Assert.Null(terrain.Weights.Verify());
    }

    /// <summary>A mask authored at a round size is resampled onto the terrain's odd one.</summary>
    /// <remarks>
    ///     A terrain of two 32-sample tiles is 63 samples across and an image editor makes 64s. The
    ///     corners are pinned, so the mask's edges land on the terrain's rather than a fraction of a
    ///     pixel short of them.
    /// </remarks>
    [Fact]
    public void A_mask_of_another_size_is_resampled_edge_to_edge() {
        var terrain = Built();
        var mask = new byte[64 * 64];

        // A ramp along X, so the corners are checkable.
        for (var z = 0; z < 64; z++) {
            for (var x = 0; x < 64; x++) {
                mask[(z * 64) + x] = (byte)(x * 255 / 63);
            }
        }

        TerrainWeightmap.Import(terrain, 1, mask, 64, 64);

        Assert.Equal(0, terrain.Weights.WeightAt(1, 0, 0));
        Assert.Equal(255, terrain.Weights.WeightAt(1, terrain.Description.SamplesX - 1, 0));
        Assert.Null(terrain.Weights.Verify());
    }

    [Fact]
    public void A_mask_that_does_not_match_its_size_is_refused() {
        var terrain = Built();

        Assert.Throws<ArgumentException>(() => TerrainWeightmap.Import(terrain, 0, new byte[10], 64, 64));
        Assert.Throws<ArgumentException>(() => TerrainWeightmap.Import(terrain, 0, new byte[64], 0, 64));
        Assert.Throws<ArgumentException>(() => TerrainWeightmap.Export(terrain, 0, new byte[10]));
    }

    [Fact]
    public void A_layer_that_does_not_exist_is_refused() {
        var terrain = Built();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => TerrainWeightmap.Import(terrain, 5, new byte[64 * 64], 64, 64)
        );
    }

    // --- The layer asset ----------------------------------------------------

    [Fact]
    public void A_layer_carries_its_ground_beside_its_channel() {
        var terrain = new Terrain(Shape);
        var gravel = new TerrainLayerDescription(
            "Gravel",
            Albedo: "Textures/gravel-albedo",
            Surface: "Textures/gravel-orm",
            TilingMetres: 2f,
            Blend: TerrainLayerBlend.Height,
            PhysicsMaterial: "Materials/gravel"
        );

        var index = terrain.Weights.AddLayer(gravel);

        Assert.Equal(gravel, terrain.Weights.LayerOf(index));
        Assert.Equal("Gravel", terrain.Weights.Names[index]);
        Assert.True(terrain.Weights.LayerOf(index).NeedsHeight);
    }

    /// <summary>Reassigning a layer's material keeps everything painted with it.</summary>
    /// <remarks>
    ///     ⚠ Deciding that the third layer is gravel rather than mud is a change of material, not a
    ///     change of where it is painted — and clearing the channel would lose an hour of painting to
    ///     a dropdown.
    /// </remarks>
    [Fact]
    public void Reassigning_a_layers_ground_keeps_what_was_painted_with_it() {
        var terrain = Built();

        TerrainPaint.Paint(terrain, 1, TerrainBrush.Default with { Radius = 8f }, new(new(30f, 30f)), 255);
        var painted = terrain.Weights.WeightAt(1, 30, 30);

        terrain.Weights.SetLayer(1, TerrainLayerDescription.Of("Gravel") with { TilingMetres = 3f });

        Assert.Equal("Gravel", terrain.Weights.Names[1]);
        Assert.Equal(painted, terrain.Weights.WeightAt(1, 30, 30));
    }

    [Fact]
    public void Coverage_is_what_the_panels_bar_draws() {
        var terrain = Built();

        // The first weight-blended layer starts covering everything.
        Assert.Equal(1f, terrain.Weights.CoverageOf(0), 3);
        Assert.Equal(0f, terrain.Weights.CoverageOf(1), 3);

        TerrainPaint.Flatten(
            terrain,
            1,
            TerrainBrush.Default with { Radius = 1000f, Strength = 1f, Falloff = 0f },
            new(new(30f, 30f)),
            target: 1f
        );

        Assert.Equal(1f, terrain.Weights.CoverageOf(1), 2);
        Assert.Equal(0f, terrain.Weights.CoverageOf(0), 2);
    }

    /// <summary>The ground under a foot is one layer, not a blend.</summary>
    [Fact]
    public void The_dominant_layer_is_what_a_footstep_reads() {
        var terrain = Built();

        Assert.Equal(0, terrain.Weights.DominantAt(30, 30));

        TerrainPaint.Paint(terrain, 1, TerrainBrush.Default with { Radius = 6f }, new(new(30f, 30f)), 255);

        Assert.Equal(1, terrain.Weights.DominantAt(30, 30));
    }

    // --- The ground under a foot --------------------------------------------

    /// <summary>A tile's quads carry the layer that claims them, for the collision shape.</summary>
    [Fact]
    public void A_tile_of_quads_carries_the_ground_each_one_is() {
        var terrain = Built();
        var quads = terrain.Description.TileQuads;
        var materials = new sbyte[quads * quads];

        Assert.Equal(materials.Length, terrain.Weights.FillCollisionMaterials(0, 0, materials));
        Assert.All(materials, material => Assert.Equal(0, material));

        TerrainPaint.Paint(
            terrain,
            1,
            TerrainBrush.Default with { Radius = 8f, Strength = 1f, Falloff = 0f },
            new(new(10f, 10f)),
            amount: 255
        );

        terrain.Weights.FillCollisionMaterials(0, 0, materials);

        Assert.Equal(1, materials[(10 * quads) + 10]);
        Assert.Equal(0, materials[(28 * quads) + 28]);
    }

    /// <summary>The majority of the quad's four corners, not one of them.</summary>
    /// <remarks>
    ///     ⚠ Taking one corner makes the material flip along a boundary depending on which way the
    ///     quad happens to be indexed — a strip of the wrong footstep sound one quad wide, following
    ///     the edge of every painted region.
    /// </remarks>
    [Fact]
    public void A_quad_takes_the_layer_with_the_most_weight_over_its_four_corners() {
        var terrain = Built();

        // Three corners of the quad at (10, 10) painted, one not.
        terrain.Weights.SetWeight(1, 10, 10, 255);
        terrain.Weights.SetWeight(1, 11, 10, 255);
        terrain.Weights.SetWeight(1, 10, 11, 255);

        var quads = terrain.Description.TileQuads;
        var materials = new sbyte[quads * quads];

        terrain.Weights.FillCollisionMaterials(0, 0, materials);

        Assert.Equal(1, materials[(10 * quads) + 10]);
    }

    [Fact]
    public void A_terrain_with_no_layers_says_so_rather_than_claiming_the_first_one() {
        var terrain = new Terrain(Shape);
        var quads = terrain.Description.TileQuads;
        var materials = new sbyte[quads * quads];

        terrain.Weights.FillCollisionMaterials(0, 0, materials);

        // ⚠ −1, not 0: zero is a layer index and would claim every quad is the first ground.
        Assert.All(materials, material => Assert.Equal(-1, material));
        Assert.Null(terrain.Weights.GroundAt(10, 10));
    }

    [Fact]
    public void The_ground_at_a_place_is_the_layer_asset_that_claims_it() {
        var terrain = new Terrain(Shape);

        terrain.Weights.AddLayer(TerrainLayerDescription.Of("Grass") with { PhysicsMaterial = "Materials/grass" });
        terrain.Weights.AddLayer(TerrainLayerDescription.Of("Gravel") with { PhysicsMaterial = "Materials/gravel" });

        Assert.Equal("Materials/grass", terrain.Weights.GroundAt(10, 10)!.Value.PhysicsMaterial);

        TerrainPaint.Paint(
            terrain,
            1,
            TerrainBrush.Default with { Radius = 6f, Strength = 1f, Falloff = 0f },
            new(new(10f, 10f)),
            amount: 255
        );

        Assert.Equal("Materials/gravel", terrain.Weights.GroundAt(10, 10)!.Value.PhysicsMaterial);
    }

    [Fact]
    public void Too_little_room_for_a_tiles_materials_is_refused() {
        var terrain = Built();

        Assert.Throws<ArgumentException>(() => terrain.Weights.FillCollisionMaterials(0, 0, new sbyte[4]));
    }

    [Fact]
    public void A_layer_that_cannot_be_used_says_why() {
        Assert.Null(TerrainLayerDescription.Of("Grass").Validate());

        Assert.Contains(
            "needs a name",
            new TerrainLayerDescription("").Validate(),
            StringComparison.Ordinal
        );

        Assert.Contains(
            "not a distance",
            (TerrainLayerDescription.Of("Grass") with { TilingMetres = 0f }).Validate(),
            StringComparison.Ordinal
        );

        // ⚠ A height blend with nothing to read the height from degrades silently to a weight blend,
        // which is the class of failure reported as "the height blending does not work".
        Assert.Contains(
            "no surface texture",
            (TerrainLayerDescription.Of("Gravel") with { Blend = TerrainLayerBlend.Height }).Validate(),
            StringComparison.Ordinal
        );
    }
}
