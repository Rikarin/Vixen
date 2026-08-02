// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Terrain;
using Xunit;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Rendering.Terrain.Tests;

/// <summary>The generated splat material — [docs/plan/31 § D6] and § T4.</summary>
public sealed class TerrainSplatTests {
    static TerrainMap Built(params TerrainLayerDescription[] layers) {
        var terrain = new TerrainMap(
            TerrainDescription.Default with {
                TileSamples = 32, TilesX = 2, TilesZ = 2,
                MetresPerQuad = 1f, MinHeight = -100f, MaxHeight = 100f
            }
        );

        foreach (var layer in layers) {
            terrain.Weights.AddLayer(layer);
        }

        return terrain;
    }

    /// <summary>The layer count quantises, so a seventh layer does not compile a new shader.</summary>
    [Theory]
    [InlineData(0, 4)]
    [InlineData(1, 4)]
    [InlineData(4, 4)]
    [InlineData(5, 8)]
    [InlineData(8, 8)]
    [InlineData(9, 12)]
    [InlineData(13, 16)]
    [InlineData(16, 16)]
    public void The_layer_count_quantises_to_four_eight_twelve_or_sixteen(int layers, int slots) {
        Assert.Equal(slots, TerrainSplat.SlotsFor(layers));
    }

    /// <summary>A terrain with no layers still compiles a loop, because a loop of zero is not one.</summary>
    [Fact]
    public void A_terrain_with_no_layers_compiles_the_smallest_material() {
        var splat = TerrainSplat.Of(Built().Weights);

        Assert.Equal(4, splat.LayerSlots);
        Assert.False(splat.HeightBlend);
        Assert.Equal(0, splat.WeightMaps);
    }

    /// <summary>The height path is one permutation for the whole material, not one per layer.</summary>
    /// <remarks>
    ///     ⚠ Eight layers with three different modes between them is one shader. What permutes is
    ///     whether <em>any</em> layer wants the height path, so a terrain that blends only by weight
    ///     compiles the first pass out entirely.
    /// </remarks>
    [Fact]
    public void One_height_blended_layer_turns_the_height_path_on_for_the_material() {
        var weight = TerrainSplat.Of(
            Built(TerrainLayerDescription.Of("Grass"), TerrainLayerDescription.Of("Rock")).Weights
        );

        Assert.False(weight.HeightBlend);

        var height = TerrainSplat.Of(
            Built(
                TerrainLayerDescription.Of("Grass"),
                TerrainLayerDescription.Of("Rock") with {
                    Blend = TerrainLayerBlend.Height, Surface = "Textures/rock-orm"
                }
            ).Weights
        );

        Assert.True(height.HeightBlend);
        Assert.Equal(4, height.LayerSlots);
    }

    [Fact]
    public void The_permutation_keys_are_the_two_the_shader_declares() {
        var splat = TerrainSplat.Of(Built(TerrainLayerDescription.Of("Grass")).Weights);

        Assert.Equal(
            [
                ("Vixen.Shaders.Terrain.Terrain.LayerSlots", (object)4),
                ("Vixen.Shaders.Terrain.Terrain.HeightBlend", false)
            ],
            splat.Permutations()
        );
    }

    [Fact]
    public void More_layers_than_a_material_can_loop_over_is_refused_with_the_reason() {
        var terrain = Built();

        for (var index = 0; index < 17; index++) {
            terrain.Weights.AddLayer("Layer " + index);
        }

        var refusal = Assert.Throws<ArgumentException>(() => TerrainSplat.Of(terrain.Weights));

        Assert.Contains("virtual texture or two terrains", refusal.Message, StringComparison.Ordinal);
    }

    // --- What the fragment stage reads --------------------------------------

    [Fact]
    public void Each_layers_tiling_reaches_the_slot_the_loop_reads_it_from() {
        var terrain = Built(
            TerrainLayerDescription.Of("Grass") with { TilingMetres = 8f },
            TerrainLayerDescription.Of("Rock") with { TilingMetres = 2f }
        );

        var splat = TerrainSplat.Of(terrain.Weights);
        var scales = new float[splat.LayerSlots];

        Assert.Equal(splat.LayerSlots, splat.FillScales(terrain.Weights, scales));

        Assert.Equal(8f, scales[0], 4);
        Assert.Equal(2f, scales[1], 4);
    }

    /// <summary>A slot with no layer gets a positive scale, not zero.</summary>
    /// <remarks>
    ///     ⚠ The shader divides world XZ by it. The early-out should mean an empty slot is never
    ///     reached, but a divisor of zero inside a branch a compiler decided to flatten is a NaN
    ///     across the whole terrain — and one metre is a number that cannot hurt.
    /// </remarks>
    [Fact]
    public void An_empty_slot_gets_a_scale_the_shader_can_divide_by() {
        var terrain = Built(TerrainLayerDescription.Of("Grass"));
        var splat = TerrainSplat.Of(terrain.Weights);
        var scales = new float[splat.LayerSlots];

        splat.FillScales(terrain.Weights, scales);

        for (var slot = 1; slot < splat.LayerSlots; slot++) {
            Assert.True(scales[slot] > 0f, $"slot {slot} would divide by {scales[slot]}.");
        }
    }

    [Fact]
    public void Each_layers_blend_mode_and_contrast_reach_the_slot_too() {
        var terrain = Built(
            TerrainLayerDescription.Of("Grass"),
            TerrainLayerDescription.Of("Rock") with {
                Blend = TerrainLayerBlend.Height, Surface = "Textures/rock-orm", HeightContrast = 0.2f
            },
            TerrainLayerDescription.Of("Leaves") with { Blend = TerrainLayerBlend.Alpha }
        );

        var splat = TerrainSplat.Of(terrain.Weights);
        var blends = new Vector2[splat.LayerSlots];

        splat.FillBlends(terrain.Weights, blends);

        Assert.Equal(0f, blends[0].X, 4);
        Assert.Equal(1f, blends[1].X, 4);
        Assert.Equal(0.2f, blends[1].Y, 4);
        Assert.Equal(2f, blends[2].X, 4);
    }

    [Fact]
    public void A_zero_contrast_is_lifted_off_zero_because_the_shader_divides_by_it() {
        var terrain = Built(
            TerrainLayerDescription.Of("Rock") with {
                Blend = TerrainLayerBlend.Height, Surface = "orm", HeightContrast = 0f
            }
        );

        var blends = new Vector2[16];
        TerrainSplat.Of(terrain.Weights).FillBlends(terrain.Weights, blends);

        Assert.True(blends[0].Y > 0f);
    }

    [Fact]
    public void Too_little_room_is_refused_rather_than_truncated() {
        var terrain = Built(TerrainLayerDescription.Of("Grass"));
        var splat = TerrainSplat.Of(terrain.Weights);

        Assert.Throws<ArgumentException>(() => splat.FillScales(terrain.Weights, new float[2]));
        Assert.Throws<ArgumentException>(() => splat.FillBlends(terrain.Weights, new Vector2[2]));
    }

    // --- Packing ------------------------------------------------------------

    [Fact]
    public void Four_layers_pack_into_one_textures_four_channels() {
        var terrain = Built(
            TerrainLayerDescription.Of("A"),
            TerrainLayerDescription.Of("B"),
            TerrainLayerDescription.Of("C"),
            TerrainLayerDescription.Of("D")
        );

        TerrainPaint.Paint(
            terrain,
            2,
            TerrainBrush.Default with { Radius = 6f, Strength = 1f, Falloff = 0f },
            new(new(10f, 10f)),
            amount: 255
        );

        var rect = new TerrainRect(8, 8, 5, 5);
        var texels = new byte[rect.Count * 4];

        Assert.Equal(texels.Length, TerrainSplat.Pack(terrain.Weights, 0, rect, texels));

        // Sample (10, 10) is the third row and column of the rectangle; layer 2 is the blue channel.
        var at = (((10 - 8) * 5) + (10 - 8)) * 4;

        Assert.Equal(terrain.Weights.WeightAt(0, 10, 10), texels[at]);
        Assert.Equal(terrain.Weights.WeightAt(2, 10, 10), texels[at + 2]);
        Assert.Equal(255, texels[at + 2]);
    }

    /// <summary>A channel with no layer is written zero rather than left alone.</summary>
    /// <remarks>
    ///     ⚠ The staging buffer is reused between uploads, so a terrain that loses its fourth layer
    ///     would keep drawing it out of whatever the last pack left in the alpha channel — a ground
    ///     that disappears from the panel and stays on the terrain.
    /// </remarks>
    [Fact]
    public void A_channel_with_no_layer_is_cleared_rather_than_left_holding_the_last_pack() {
        var terrain = Built(TerrainLayerDescription.Of("A"), TerrainLayerDescription.Of("B"));
        var rect = new TerrainRect(0, 0, 4, 4);
        var texels = new byte[rect.Count * 4];

        Array.Fill(texels, (byte)0xAB);
        TerrainSplat.Pack(terrain.Weights, 0, rect, texels);

        for (var texel = 0; texel < rect.Count; texel++) {
            Assert.Equal(0, texels[(texel * 4) + 2]);
            Assert.Equal(0, texels[(texel * 4) + 3]);
        }
    }

    [Fact]
    public void A_weightmap_past_the_layers_packs_as_nothing() {
        var terrain = Built(TerrainLayerDescription.Of("A"));
        var rect = new TerrainRect(0, 0, 4, 4);
        var texels = new byte[rect.Count * 4];

        Array.Fill(texels, (byte)0xFF);
        TerrainSplat.Pack(terrain.Weights, map: 3, rect, texels);

        Assert.All(texels, texel => Assert.Equal(0, texel));
    }

    [Fact]
    public void Too_little_room_to_pack_is_refused() {
        var terrain = Built(TerrainLayerDescription.Of("A"));

        Assert.Throws<ArgumentException>(
            () => TerrainSplat.Pack(terrain.Weights, 0, new(0, 0, 4, 4), new byte[8])
        );
    }
}
