// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Terrain;
using Xunit;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Editor.Terrain.Tests;

/// <summary>The settings the terrain panel is made of — [docs/plan/31 § The terrain panel].</summary>
public sealed class TerrainPanelTests {
    // --- The create form ----------------------------------------------------

    [Fact]
    public void The_form_is_valid_out_of_the_box_and_the_terrain_it_makes_matches_it() {
        var form = new TerrainCreateSettings();

        Assert.Null(form.Validate());

        var terrain = form.Build();

        Assert.Equal(form.Description, terrain.Description);
        // ⚠ 508, not 512: four tiles of 128 samples span 127 quads each. The tile size is a
        // power of two in *samples* and the extent is in quads, and the two differ by one per tile.
        Assert.Equal(508f, terrain.Description.WidthX, 1);
    }

    /// <summary>The readout is what stops somebody accidentally asking for eight gigabytes.</summary>
    [Fact]
    public void The_derived_readout_multiplies_the_numbers_nobody_multiplies_in_their_head() {
        var form = new TerrainCreateSettings { TileSamples = 256, TilesX = 32, TilesZ = 32, MetresPerQuad = 1f };
        var facts = form.Facts;

        Assert.Equal(8160f, facts.WidthX, 0);
        Assert.Equal(8161L * 8161L, facts.Samples);
        Assert.Equal(facts.Samples * 2, facts.HeightBytes);
        Assert.Equal(facts.Samples, facts.WeightBytesPerLayer);
        Assert.Equal(1024, facts.Tiles);

        // And every row says so, in the convention doc 20's lighting panel established.
        Assert.All(facts.Rows(), row => Assert.Contains("(derived)", row.Value, StringComparison.Ordinal));
    }

    /// <summary>The vertical precision is on the form because the range is authored.</summary>
    /// <remarks>
    ///     ⚠ Unreal fixes the height range and nobody has to think about it. [§ D2] lets the author
    ///     set it, which buys a rolling landscape sub-millimetre precision — and makes it possible to
    ///     ask for a 20 km range and wonder later why a flatten will not settle.
    /// </remarks>
    [Fact]
    public void A_wider_height_range_costs_vertical_precision_and_the_form_says_how_much() {
        var tight = new TerrainCreateSettings { MinHeight = -20f, MaxHeight = 20f };
        var wide = new TerrainCreateSettings { MinHeight = -10_000f, MaxHeight = 10_000f };

        Assert.True(tight.Facts.MetresPerStep < 0.001f);
        Assert.True(wide.Facts.MetresPerStep > 0.2f);

        Assert.Contains("mm per step", tight.Facts.Rows()[^1].Value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(129)]
    [InlineData(100)]
    [InlineData(2)]
    public void A_tile_size_Jolt_would_refuse_is_refused_by_the_form(int samples) {
        var form = new TerrainCreateSettings { TileSamples = samples };

        Assert.False(form.IsValid);
        Assert.Throws<InvalidOperationException>(() => form.Build());
    }

    [Fact]
    public void The_three_tile_sizes_offered_are_all_one_less_than_a_power_of_two() {
        foreach (var quads in TerrainCreateSettings.TileQuadChoices) {
            var samples = quads + 1;

            Assert.Equal(0, samples & (samples - 1));
            Assert.True(new TerrainCreateSettings { TileSamples = samples }.IsValid);
        }
    }

    [Fact]
    public void A_base_height_outside_the_range_is_refused_because_it_is_not_representable() {
        var form = new TerrainCreateSettings { MinHeight = 0f, MaxHeight = 100f, BaseHeight = 150f };

        Assert.False(form.IsValid);
        Assert.Contains("outside the range", form.Validate(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_form_reads_back_from_a_terrain_that_already_exists() {
        var terrain = new TerrainMap(Ground.Shape);
        var form = TerrainCreateSettings.Of(terrain);

        Assert.Equal(terrain.Description, form.Description);
        Assert.Null(form.Consequence(terrain));
    }

    /// <summary>Applying a form that loses something says what, before rather than after.</summary>
    [Fact]
    public void The_form_says_what_applying_it_would_cost() {
        var terrain = new TerrainMap(Ground.Shape);

        var cropped = TerrainCreateSettings.Of(terrain);
        cropped.TilesX = 1;

        Assert.Contains("cropped", cropped.Consequence(terrain), StringComparison.Ordinal);

        var rescaled = TerrainCreateSettings.Of(terrain);
        rescaled.MaxHeight = 1_000f;

        Assert.Contains("rescaled to keep its metres", rescaled.Consequence(terrain), StringComparison.Ordinal);
    }

    // --- The brush section --------------------------------------------------

    [Fact]
    public void The_brush_settings_produce_the_brush_the_kernel_takes() {
        var settings = new TerrainBrushSettings {
            Radius = 12f, Strength = 0.75f, Falloff = 0.25f,
            Curve = BrushFalloffKind.Spherical, Shape = BrushShape.Alpha,
            Spacing = 0.5f, Rotation = BrushRotation.Random, Angle = 90f
        };

        var brush = settings.ToBrush();

        Assert.Equal(12f, brush.Radius, 4);
        Assert.Equal(0.75f, brush.Strength, 4);
        Assert.Equal(BrushFalloffKind.Spherical, brush.Curve);
        Assert.Equal(BrushShape.Alpha, brush.Shape);

        // ⚠ Degrees on the form, radians in the kernel, and this is the only place the two meet.
        Assert.Equal(MathF.PI / 2f, brush.Angle, 4);
    }

    /// <summary>The size keys scale the radius rather than adding to it.</summary>
    /// <remarks>
    ///     ⚠ <b>A metre is most of a small brush and nothing at all of a large one</b>, so an
    ///     additive step is useless at one end of the range and maddening at the other.
    /// </remarks>
    [Fact]
    public void The_size_keys_scale_the_radius_so_they_work_at_both_ends_of_the_range() {
        var small = new TerrainBrushSettings { Radius = 1f };
        var large = new TerrainBrushSettings { Radius = 1000f };

        small.Resize(1);
        large.Resize(1);

        Assert.Equal(
            small.Radius / 1f,
            large.Radius / 1000f,
            3
        );
    }

    [Fact]
    public void The_brush_keys_stop_at_the_ends_rather_than_running_past_them() {
        var settings = new TerrainBrushSettings { Radius = TerrainBrushSettings.MinimumRadius };

        settings.Resize(-100);
        Assert.Equal(TerrainBrushSettings.MinimumRadius, settings.Radius, 4);

        settings.Radius = TerrainBrushSettings.MaximumRadius;
        settings.Resize(100);
        Assert.Equal(TerrainBrushSettings.MaximumRadius, settings.Radius, 1);

        settings.Press(-100);
        Assert.Equal(0f, settings.Strength, 4);

        settings.Press(100);
        Assert.Equal(1f, settings.Strength, 4);
    }

    [Fact]
    public void A_zero_spacing_falls_back_rather_than_making_a_stroke_of_infinite_stamps() {
        var brush = new TerrainBrushSettings { Spacing = 0f }.ToBrush();

        Assert.True(brush.StampDistance > 0f);
    }

    // --- The tool parameters ------------------------------------------------

    [Fact]
    public void Each_tools_rows_are_shown_only_for_that_tool() {
        var settings = new TerrainToolSettings();

        foreach (var tool in TerrainMode.Tools) {
            settings.Tool = tool;

            Assert.Equal(tool == TerrainTool.Sculpt, settings.IsSculpt);
            Assert.Equal(tool == TerrainTool.Noise, settings.IsNoise);
            Assert.Equal(tool == TerrainTool.Holes, settings.IsHoles);
            Assert.Equal(tool is TerrainTool.Erosion or TerrainTool.Hydro, settings.IsIterative);
        }
    }

    /// <summary>The parameters outlive the tool selection.</summary>
    /// <remarks>
    ///     An artist who sets the talus angle, goes to smooth a ridge and comes back expects the
    ///     angle to still be there — which is why there is one settings object rather than one per
    ///     tool.
    /// </remarks>
    [Fact]
    public void Switching_tool_and_back_keeps_the_parameters() {
        var settings = new TerrainToolSettings { Tool = TerrainTool.Erosion, Talus = 3.5f };

        settings.Tool = TerrainTool.Smooth;
        settings.Tool = TerrainTool.Erosion;

        Assert.Equal(3.5f, settings.Talus, 4);
    }

    [Fact]
    public void Only_the_iterative_tools_run_more_than_one_pass_per_stamp() {
        var settings = new TerrainToolSettings { Iterations = 6, SmoothPasses = 3 };

        Assert.Equal(1, settings.PassesOf(TerrainTool.Sculpt));
        Assert.Equal(3, settings.PassesOf(TerrainTool.Smooth));
        Assert.Equal(6, settings.PassesOf(TerrainTool.Erosion));
        Assert.Equal(6, settings.PassesOf(TerrainTool.Hydro));
    }
}
