// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Terrain;
using Xunit;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Editor.Terrain.Tests;

/// <summary>Painting a terrain's layers — [docs/plan/31 § T4].</summary>
public sealed class TerrainPaintModeTests {
    static readonly EditorContext NoContext = null!;

    static (TerrainEdit Edit, TerrainMap Terrain) Painting(params string[] layers) {
        var terrain = Ground.Terrain();

        foreach (var name in layers.Length > 0 ? layers : ["Grass", "Rock"]) {
            terrain.Weights.AddLayer(name);
        }

        var edit = new TerrainEdit { Terrain = terrain };

        edit.Brush.Radius = 6f;
        edit.Brush.Strength = 1f;
        edit.Tools.Category = TerrainCategory.Paint;
        edit.Target = 1;

        return (edit, terrain);
    }

    // --- The four tools -----------------------------------------------------

    [Fact]
    public void A_paint_stroke_raises_the_target_layer_and_lowers_the_rest() {
        var (edit, terrain) = Painting();

        edit.Tools.Coverage = 255;

        Assert.True(edit.Begin(new(30f, 30f)));
        Assert.NotNull(edit.Commit(rebuildColliders: false));

        Assert.True(terrain.Weights.WeightAt(1, 30, 30) > 200);
        Assert.True(terrain.Weights.WeightAt(0, 30, 30) < 55);
        Assert.Null(terrain.Weights.Verify());
    }

    [Fact]
    public void Shift_removes_the_target_layer_and_gives_the_weight_back() {
        var (edit, terrain) = Painting();

        edit.Tools.Coverage = 255;
        edit.Begin(new(30f, 30f));
        edit.Commit(rebuildColliders: false);

        edit.Begin(new(30f, 30f), invert: true);
        edit.Commit(rebuildColliders: false);

        Assert.Equal(0, terrain.Weights.WeightAt(1, 30, 30));
        Assert.Equal(255, terrain.Weights.WeightAt(0, 30, 30));
    }

    [Fact]
    public void The_paint_flatten_tool_converges_on_the_coverage_asked_for() {
        var (edit, terrain) = Painting();

        edit.Tools.PaintTool = TerrainPaintTool.Flatten;
        edit.Tools.TargetCoverage = 0.5f;
        edit.Brush.Falloff = 0f;

        for (var pass = 0; pass < 6; pass++) {
            edit.Begin(new(30f, 30f));
            edit.Commit(rebuildColliders: false);
        }

        Assert.InRange(terrain.Weights.WeightAt(1, 30, 30), 125, 130);
    }

    [Fact]
    public void Every_paint_tool_leaves_the_sum_holding() {
        foreach (var tool in TerrainMode.PaintTools) {
            var (edit, terrain) = Painting("Grass", "Rock", "Sand");

            edit.Tools.PaintTool = tool;
            edit.Begin(new(20f, 20f));
            edit.Extend(new(30f, 26f));
            edit.Commit(rebuildColliders: false);

            Assert.Null(terrain.Weights.Verify());
        }
    }

    // --- One drag, one entry ------------------------------------------------

    /// <summary>A paint undo restores every layer, not just the one that was painted.</summary>
    [Fact]
    public void A_paint_stroke_is_one_entry_that_puts_every_layer_back() {
        var (edit, terrain) = Painting("Grass", "Rock", "Sand");
        var before = Snapshot(terrain);

        edit.Tools.Coverage = 200;
        edit.Begin(new(20f, 20f));

        for (var x = 22; x <= 40; x += 2) {
            edit.Extend(new(x, 20f));
        }

        var command = edit.Commit(rebuildColliders: false);

        Assert.IsType<TerrainPaintCommand>(command);
        Assert.NotEqual(before, Snapshot(terrain));

        var after = Snapshot(terrain);

        command!.Undo(NoContext);
        Assert.Equal(before, Snapshot(terrain));
        Assert.Null(terrain.Weights.Verify());

        command.Do(NoContext);
        Assert.Equal(after, Snapshot(terrain));
    }

    [Fact]
    public void Two_paint_strokes_never_merge() {
        var (edit, _) = Painting();

        edit.Begin(new(20f, 20f));
        var first = edit.Commit(rebuildColliders: false)!;

        edit.Begin(new(40f, 40f));
        var second = edit.Commit(rebuildColliders: false)!;

        Assert.False(second.TryMergeWith(first, out _));
    }

    [Fact]
    public void Cancelling_a_paint_stroke_puts_the_weights_back() {
        var (edit, terrain) = Painting();

        edit.Tools.Coverage = 255;
        edit.Begin(new(30f, 30f));

        Assert.True(terrain.Weights.WeightAt(1, 30, 30) > 0);

        edit.Cancel();

        Assert.Equal(0, terrain.Weights.WeightAt(1, 30, 30));
        Assert.Null(terrain.Weights.Verify());
    }

    /// <summary>A paint stroke changes no height, so it rebuilds no collider.</summary>
    /// <remarks>
    ///     ⚠ <b>Not an omission.</b> The shape is the shape it was; what changes is which
    ///     <em>material</em> each quad is, and that is read from the weights when it is asked rather
    ///     than baked into the shape. A rebuild would be a Jolt height field built to hold the same
    ///     heights it already has.
    /// </remarks>
    [Fact]
    public void Painting_rebuilds_no_colliders_because_no_height_moved() {
        var (edit, _) = Painting();
        var colliders = new RecordingColliders();

        edit.Colliders = colliders;
        edit.Begin(new(30f, 30f));
        edit.Commit();

        Assert.Empty(colliders.Rebuilt);
    }

    [Fact]
    public void A_paint_stroke_still_tells_the_renderer_which_samples_moved() {
        var (edit, _) = Painting();
        var reported = new List<TerrainRect>();

        edit.Changed += reported.Add;
        edit.Begin(new(30f, 30f));
        edit.Extend(new(40f, 30f));
        edit.Commit(rebuildColliders: false);

        Assert.Equal(2, reported.Count);
        Assert.All(reported, rect => Assert.False(rect.IsEmpty));
    }

    // --- What refuses a paint stroke ----------------------------------------

    [Fact]
    public void A_terrain_with_no_target_layers_refuses_the_brush_and_says_so() {
        var terrain = Ground.Terrain();
        var edit = new TerrainEdit { Terrain = terrain };

        edit.Tools.Category = TerrainCategory.Paint;

        Assert.False(edit.CanStroke);
        Assert.False(edit.Begin(new(30f, 30f)));
        Assert.Contains("no target layers", edit.Refusal, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A locked edit layer does not refuse a paint stroke, because it is not what is written.</summary>
    /// <remarks>
    ///     ⚠ The edit-layer stack and the target-layer list are two lists with similar names. A paint
    ///     layer has no lock and no generator; refusing a paint stroke because the *sculpt* layer is
    ///     locked would be the two getting confused in the one place it is invisible.
    /// </remarks>
    [Fact]
    public void A_locked_edit_layer_does_not_refuse_a_paint_stroke() {
        var (edit, terrain) = Painting();

        edit.Layer!.IsLocked = true;

        Assert.True(edit.Begin(new(30f, 30f)));
        Assert.NotNull(edit.Commit(rebuildColliders: false));
        Assert.True(terrain.Weights.WeightAt(1, 30, 30) > 0);
    }

    /// <summary>A target left pointing past the end is clamped rather than silently painting nothing.</summary>
    [Fact]
    public void A_target_past_the_end_is_clamped_to_a_layer_that_exists() {
        var (edit, terrain) = Painting();

        edit.Target = 99;

        Assert.Equal(terrain.Weights.LayerCount - 1, edit.Target);
        Assert.True(edit.Begin(new(30f, 30f)));
    }

    [Fact]
    public void Changing_the_category_mid_drag_abandons_the_stroke() {
        var (edit, terrain) = Painting();
        var mode = new TerrainMode();

        mode.Editing.Terrain = terrain;
        mode.Editing.Tools.Category = TerrainCategory.Paint;
        mode.Editing.Tools.Coverage = 255;
        mode.Editing.Brush.Radius = 6f;
        mode.Editing.Target = 1;

        Assert.True(mode.Editing.Begin(new(30f, 30f)));
        Assert.True(terrain.Weights.WeightAt(1, 30, 30) > 0);

        mode.Category = TerrainCategory.Sculpt;

        Assert.False(mode.Editing.IsStroking);
        Assert.Equal(0, terrain.Weights.WeightAt(1, 30, 30));
    }

    // --- The target-layer panel ---------------------------------------------

    [Fact]
    public void The_panel_lists_a_row_per_target_layer_with_its_coverage() {
        var (_, terrain) = Painting("Grass", "Rock");
        var rows = TerrainLayerSettings.Rows(terrain);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Grass", rows[0].Layer.Name);
        Assert.Equal(1f, rows[0].Coverage, 2);
        Assert.Equal(0f, rows[1].Coverage, 2);
        Assert.Contains("coverage", rows[0].Caption, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_weight_blended_row_says_it_takes_from_nobody() {
        var terrain = Ground.Terrain();

        terrain.Weights.AddLayer("Grass");
        terrain.Weights.AddLayer("Snow", TerrainBlend.NonWeight);

        var rows = TerrainLayerSettings.Rows(terrain);

        Assert.Contains("takes from nobody", rows[1].Caption, StringComparison.Ordinal);
        Assert.DoesNotContain("takes from nobody", rows[0].Caption, StringComparison.Ordinal);
    }

    [Fact]
    public void The_layer_form_round_trips_through_a_description() {
        var layer = new TerrainLayerDescription(
            "Gravel",
            Albedo: "Textures/gravel",
            Surface: "Textures/gravel-orm",
            TilingMetres: 2.5f,
            Blend: TerrainLayerBlend.Height,
            HeightContrast: 0.3f,
            PhysicsMaterial: "Materials/gravel"
        );

        var form = TerrainLayerSettings.Of(layer, TerrainBlend.NonWeight);

        Assert.Equal(layer, form.Description);
        Assert.False(form.IsWeightBlended);
        Assert.Equal(TerrainBlend.NonWeight, form.BlendBudget);
        Assert.True(form.IsHeightBlended);
        Assert.True(form.IsValid);
    }

    // --- The target-layer commands ------------------------------------------

    [Fact]
    public void Adding_a_target_layer_is_one_entry_and_undoing_it_gives_the_coverage_back() {
        var terrain = Ground.Terrain();

        terrain.Weights.AddLayer("Grass");

        var (command, index) = TerrainLayerCommands.AddTarget(terrain, "Rock");

        command.Do(NoContext);

        Assert.Equal(2, terrain.Weights.LayerCount);
        Assert.Equal(1, index);
        Assert.Null(terrain.Weights.Verify());

        command.Undo(NoContext);

        Assert.Single(terrain.Weights.Layers);
        Assert.Equal(255, terrain.Weights.WeightAt(0, 10, 10));
    }

    /// <summary>Undoing a removal puts back what the redistribution took, not just the layer.</summary>
    /// <remarks>
    ///     ⚠ <b>Removing a weight-blended layer gives its weight to the others in proportion, and
    ///     that is not invertible from the layer alone.</b> Putting the channel back would leave the
    ///     others holding what they were given, so every sample it covered would sum above the total.
    /// </remarks>
    [Fact]
    public void Undoing_a_target_removal_puts_every_layer_back_where_it_was() {
        var terrain = Ground.Terrain();

        terrain.Weights.AddLayer("Grass");
        terrain.Weights.AddLayer("Rock");
        terrain.Weights.AddLayer("Sand");

        TerrainPaint.Paint(
            terrain,
            1,
            TerrainBrush.Default with { Radius = 10f, Strength = 1f },
            new(new(30f, 30f)),
            amount: 180
        );

        var before = Snapshot(terrain);
        var command = TerrainLayerCommands.RemoveTarget(terrain, 1);

        command.Do(NoContext);

        Assert.Equal(2, terrain.Weights.LayerCount);
        Assert.Equal(["Grass", "Sand"], terrain.Weights.Names);
        Assert.Null(terrain.Weights.Verify());

        command.Undo(NoContext);

        Assert.Equal(["Grass", "Rock", "Sand"], terrain.Weights.Names);
        Assert.Equal(before, Snapshot(terrain));
        Assert.Null(terrain.Weights.Verify());
    }

    [Fact]
    public void Assigning_a_ground_to_a_target_layer_keeps_what_was_painted_with_it() {
        var terrain = Ground.Terrain();

        terrain.Weights.AddLayer("Grass");
        terrain.Weights.AddLayer("Layer 2");

        TerrainPaint.Paint(
            terrain,
            1,
            TerrainBrush.Default with { Radius = 8f, Strength = 1f },
            new(new(30f, 30f)),
            amount: 255
        );

        var painted = terrain.Weights.WeightAt(1, 30, 30);
        var command = TerrainLayerCommands.AssignTarget(
            terrain,
            1,
            TerrainLayerDescription.Of("Gravel") with { TilingMetres = 2f, PhysicsMaterial = "Materials/gravel" }
        );

        command.Do(NoContext);

        Assert.Equal("Gravel", terrain.Weights.Names[1]);
        Assert.Equal(painted, terrain.Weights.WeightAt(1, 30, 30));

        command.Undo(NoContext);

        Assert.Equal("Layer 2", terrain.Weights.Names[1]);
        Assert.Equal(painted, terrain.Weights.WeightAt(1, 30, 30));
    }

    static byte[] Snapshot(TerrainMap terrain) {
        var weights = new byte[terrain.Weights.LayerCount * terrain.Description.SampleCount];
        var at = 0;

        for (var layer = 0; layer < terrain.Weights.LayerCount; layer++) {
            for (var z = 0; z < terrain.Description.SamplesZ; z++) {
                for (var x = 0; x < terrain.Description.SamplesX; x++) {
                    weights[at++] = terrain.Weights.WeightAt(layer, x, z);
                }
            }
        }

        return weights;
    }
}
