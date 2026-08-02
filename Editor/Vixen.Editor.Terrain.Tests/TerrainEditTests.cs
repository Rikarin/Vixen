// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Terrain;
using Xunit;

namespace Vixen.Editor.Terrain.Tests;

/// <summary>The stroke lifecycle — [docs/plan/31 § D11] and § T3.</summary>
public sealed class TerrainEditTests {
    static readonly EditorContext NoContext = null!;

    // --- Which tools do what ------------------------------------------------

    [Fact]
    public void A_press_without_a_drag_is_one_stamp() {
        var (edit, terrain) = Ground.Editing();

        edit.Tools.Metres = 10f;

        Assert.True(edit.Begin(new(30f, 30f)));
        Assert.NotNull(edit.Commit(rebuildColliders: false));

        Assert.Equal(10f, Ground.HeightAt(terrain, 30, 30), 1);
    }

    [Fact]
    public void Shift_reverses_the_sculpt_tool() {
        var (edit, terrain) = Ground.Editing();

        edit.Tools.Metres = 10f;
        edit.Begin(new(30f, 30f), invert: true);
        edit.Commit(rebuildColliders: false);

        Assert.Equal(-10f, Ground.HeightAt(terrain, 30, 30), 1);
    }

    [Fact]
    public void Flatten_takes_its_target_from_where_the_stroke_started() {
        var (edit, terrain) = Ground.Editing(radius: 10f);

        // A hill, so there is something to flatten and a level to flatten it to.
        edit.Tools.Metres = 20f;
        edit.Begin(new(30f, 30f));
        edit.Commit(rebuildColliders: false);

        var summit = Ground.HeightAt(terrain, 30, 30);
        Assert.True(summit > 15f);

        // Started on the summit, so the shoulder is pulled up to it.
        edit.Tools.Tool = TerrainTool.Flatten;
        edit.Begin(new(30f, 30f));
        edit.Extend(new(36f, 30f));
        edit.Commit(rebuildColliders: false);

        Assert.Equal(summit, Ground.HeightAt(terrain, 34, 30), 0);
    }

    /// <summary>Clay builds towards a plane and stops there, instead of sharpening a spike.</summary>
    /// <remarks>
    ///     The distinction the setting exists for: plain sculpt adds a fixed amount per stamp, so
    ///     holding the brush on one spot makes a needle. Clay is a one-directional flatten to a plane
    ///     a fixed distance above where the stroke started, so the same forty stamps converge.
    /// </remarks>
    [Fact]
    public void Clay_converges_on_a_plane_where_plain_sculpt_keeps_climbing() {
        var (clay, clayGround) = Ground.Editing();
        var (plain, plainGround) = Ground.Editing();

        clay.Tools.Metres = 5f;
        clay.Tools.Clay = true;

        plain.Tools.Metres = 5f;

        // ⚠ One stroke that scrubs back and forth, not eight strokes. Clay's plane is taken at the
        // *start of the stroke*, so a second stroke starts from the mesa the first one built and
        // builds another one on top — which is the right behaviour and is not what converges.
        foreach (var edit in (TerrainEdit[])[clay, plain]) {
            edit.Begin(new(30f, 30f));

            for (var pass = 0; pass < 10; pass++) {
                edit.Extend(new(pass % 2 == 0 ? 34f : 30f, 30f));
            }

            edit.Commit(rebuildColliders: false);
        }

        Assert.Equal(Ground.Rest + 5f, Ground.HeightAt(clayGround, 30, 30), 1);
        Assert.True(
            Ground.HeightAt(plainGround, 30, 30) > 30f,
            $"plain sculpt should have accumulated; it reached {Ground.HeightAt(plainGround, 30, 30)} m."
        );
    }

    [Fact]
    public void Smoothing_a_spike_takes_the_top_off_it() {
        var (edit, terrain) = Ground.Editing(radius: 3f);

        // ⚠ Falloff all the way in, so the thing built is a spike. At the default half-falloff the
        // inner half of the brush is a *plateau* — every sample of the 3×3 neighbourhood at the peak
        // is the same height, the average equals the centre, and a correct smooth changes nothing.
        edit.Brush.Falloff = 1f;
        edit.Tools.Metres = 30f;
        edit.Begin(new(30f, 30f));
        edit.Commit(rebuildColliders: false);

        var before = Ground.HeightAt(terrain, 30, 30);

        edit.Tools.Tool = TerrainTool.Smooth;
        edit.Tools.SmoothPasses = 4;
        edit.Brush.Radius = 8f;
        edit.Begin(new(30f, 30f));
        edit.Commit(rebuildColliders: false);

        Assert.True(Ground.HeightAt(terrain, 30, 30) < before);
    }

    [Fact]
    public void Erosion_slides_material_off_a_slope_that_is_too_steep() {
        var (edit, terrain) = Ground.Editing(radius: 4f);

        // A cone rather than a mesa — a plateau has no slope to exceed the talus angle.
        edit.Brush.Falloff = 1f;
        edit.Tools.Metres = 40f;
        edit.Begin(new(30f, 30f));
        edit.Commit(rebuildColliders: false);

        var summit = Ground.HeightAt(terrain, 30, 30);

        edit.Tools.Tool = TerrainTool.Erosion;
        edit.Tools.Talus = 0.1f;
        edit.Tools.Iterations = 8;
        edit.Brush.Radius = 10f;
        edit.Begin(new(30f, 30f));
        edit.Commit(rebuildColliders: false);

        Assert.True(
            Ground.HeightAt(terrain, 30, 30) < summit,
            "the summit should have lost material to the slope below it."
        );
    }

    /// <summary>The ramp previews as the second point moves, and leaves only the last one.</summary>
    /// <remarks>
    ///     ⚠ <b>The property a two-point tool gets wrong.</b> A ramp is one shape between two points
    ///     rather than a sequence of stamps that accumulate, so dragging past a point and coming back
    ///     must leave the ground the way the final pair of points describes — not the union of every
    ///     ramp drawn on the way there.
    /// </remarks>
    [Fact]
    public void A_ramp_previews_and_leaves_only_the_last_one_it_drew() {
        var (edit, terrain) = Ground.Editing(radius: 8f);

        // ⚠ Something to ramp *from*. A ramp interpolates the ground at its two ends, so two points
        // at the same height on flat ground describe the ground that is already there and write
        // nothing — which is correct and is not a test of anything.
        edit.Brush.Falloff = 1f;
        edit.Tools.Metres = 20f;
        edit.Begin(new(10f, 30f));
        edit.Commit(rebuildColliders: false);

        edit.Tools.Tool = TerrainTool.Ramp;
        edit.Tools.RampWidth = 6f;

        edit.Begin(new(10f, 30f));
        edit.Extend(new(50f, 30f));

        // Somewhere the long drag covered and the short one does not.
        Assert.False(Ground.IsUntouched(terrain, 45, 30), "the long ramp should have reached sample 45.");

        edit.Extend(new(20f, 30f));
        edit.Commit(rebuildColliders: false);

        Assert.True(Ground.IsUntouched(terrain, 45, 30), "the preview of the long ramp was left behind.");
    }

    [Fact]
    public void Holes_are_punched_and_are_not_an_edit_layer() {
        var (edit, terrain) = Ground.Editing(radius: 5f);

        edit.Tools.Tool = TerrainTool.Holes;
        edit.Begin(new(30f, 30f));

        var command = edit.Commit(rebuildColliders: false);

        Assert.IsType<TerrainHoleCommand>(command);
        Assert.True(terrain.Holes.IsHole(30, 30));
        Assert.True(terrain.Layers[0].IsEmpty, "a hole is a bit on the terrain, not a delta on a layer.");

        command!.Undo(NoContext);
        Assert.False(terrain.Holes.IsHole(30, 30));

        command.Do(NoContext);
        Assert.True(terrain.Holes.IsHole(30, 30));
    }

    // --- One drag, one entry ------------------------------------------------

    /// <summary>A drag of forty pointer events is one undo entry.</summary>
    [Fact]
    public void A_whole_drag_is_one_command() {
        var (edit, terrain) = Ground.Editing();

        edit.Tools.Metres = 5f;
        edit.Begin(new(10f, 30f));

        for (var x = 11; x <= 50; x++) {
            edit.Extend(new(x, 30f));
        }

        var command = edit.Commit(rebuildColliders: false);

        Assert.NotNull(command);
        Assert.True(Ground.HeightAt(terrain, 30, 30) > 1f);

        command.Undo(NoContext);

        for (var x = 10; x <= 50; x++) {
            Assert.True(Ground.IsUntouched(terrain, x, 30), $"sample {x} was not put back.");
        }
    }

    /// <summary>And two strokes are two entries, which is what "undo that" means.</summary>
    /// <remarks>
    ///     ⚠ [§ D11]: merging is off. The receiver of <see cref="IEditorCommand.TryMergeWith" /> is
    ///     the new command being asked whether it can swallow the old one, and a stroke command
    ///     always says no — so an artist who sculpts a valley and then a ridge gets the ridge back
    ///     with one undo rather than losing both.
    /// </remarks>
    [Fact]
    public void Two_strokes_never_merge_into_one() {
        var (edit, _) = Ground.Editing();

        edit.Tools.Metres = 5f;

        edit.Begin(new(20f, 20f));
        var first = edit.Commit(rebuildColliders: false)!;

        edit.Begin(new(40f, 40f));
        var second = edit.Commit(rebuildColliders: false)!;

        Assert.False(second.TryMergeWith(first, out var merged));
        Assert.Null(merged);
    }

    [Fact]
    public void A_stroke_that_touched_nothing_is_not_an_entry() {
        var (edit, _) = Ground.Editing();

        // Well off the terrain: every stamp clips to nothing.
        edit.Begin(new(-500f, -500f));

        Assert.Null(edit.Commit(rebuildColliders: false));
    }

    [Fact]
    public void Cancelling_a_stroke_puts_the_ground_back() {
        var (edit, terrain) = Ground.Editing();

        edit.Tools.Metres = 20f;
        edit.Begin(new(30f, 30f));
        edit.Extend(new(36f, 30f));

        Assert.True(Ground.HeightAt(terrain, 30, 30) > 1f);

        edit.Cancel();

        Assert.False(edit.IsStroking);
        Assert.True(Ground.IsUntouched(terrain, 30, 30));
        Assert.Null(edit.Commit(rebuildColliders: false));
    }

    /// <summary>Undo and redo of eight strokes come back to exactly where they were.</summary>
    /// <remarks>
    ///     § T3's exit criterion, in the part that is arithmetic: eight strokes undone and redone.
    ///     Comparing every sample rather than a handful, because the failure this catches — a record
    ///     taken after the kernel ran — restores <em>most</em> of the ground correctly.
    /// </remarks>
    [Fact]
    public void Eight_strokes_undo_and_redo_to_the_same_ground() {
        var (edit, terrain) = Ground.Editing();
        var commands = new List<IEditorCommand>();

        edit.Tools.Metres = 6f;

        for (var index = 0; index < 8; index++) {
            edit.Tools.Tool = index % 2 == 0 ? TerrainTool.Sculpt : TerrainTool.Smooth;
            edit.Begin(new(12f + (index * 4f), 30f));
            edit.Extend(new(16f + (index * 4f), 34f));

            commands.Add(edit.Commit(rebuildColliders: false)!);
        }

        var sculpted = Snapshot(terrain);

        for (var index = commands.Count - 1; index >= 0; index--) {
            commands[index].Undo(NoContext);
        }

        terrain.Resolve();

        Assert.All(Snapshot(terrain), height => Assert.Equal(Ground.Rest, height, 3));

        foreach (var command in commands) {
            command.Do(NoContext);
        }

        terrain.Resolve();

        Assert.Equal(sculpted, Snapshot(terrain));
    }

    // --- What the drag tells the world --------------------------------------

    /// <summary>The composite moves under the brush, not at pointer-up.</summary>
    /// <remarks>
    ///     ⚠ [§ D11]: "applied to the composite for display as it happens and committed to the layer
    ///     at pointer-up". The tiles are marked by the kernel and resolved per stamp, which is what
    ///     makes the viewport show the ground moving rather than jumping when the button comes up.
    /// </remarks>
    [Fact]
    public void The_ground_moves_during_the_drag_rather_than_at_the_end() {
        var (edit, terrain) = Ground.Editing();
        var reported = new List<TerrainRect>();

        edit.Changed += reported.Add;
        edit.Tools.Metres = 8f;

        edit.Begin(new(20f, 30f));

        Assert.True(Ground.HeightAt(terrain, 20, 30) > 1f, "the first stamp should already be visible.");
        Assert.Single(reported);

        edit.Extend(new(40f, 30f));

        Assert.True(Ground.HeightAt(terrain, 40, 30) > 1f);
        Assert.Equal(2, reported.Count);
    }

    /// <summary>A stroke rebuilds the tiles it touched, and no others.</summary>
    [Fact]
    public void Committing_rebuilds_only_the_tiles_the_stroke_touched() {
        var (edit, _) = Ground.Editing(radius: 3f);
        var colliders = new RecordingColliders();

        edit.Colliders = colliders;
        edit.Tools.Metres = 5f;

        // Well inside the low tile, so the brush cannot reach the boundary at sample 31.
        edit.Begin(new(12f, 12f));
        edit.Commit();

        Assert.Equal([(0, 0)], colliders.Tiles);
    }

    /// <summary>A stroke on a tile seam rebuilds both sides of it.</summary>
    /// <remarks>
    ///     ⚠ <b>The classic bug in this subsystem, in its collision form.</b> A boundary sample
    ///     belongs to two tiles; rebuilding only the one <c>TileOf</c> answers with leaves a strip of
    ///     collision disagreeing with the ground beside it by whatever the stroke moved — a lip the
    ///     player trips on, on a seam nothing draws. <c>TilesOf</c> is what answers with both.
    /// </remarks>
    [Fact]
    public void A_stroke_on_a_seam_rebuilds_both_tiles() {
        var (edit, _) = Ground.Editing(radius: 4f);
        var colliders = new RecordingColliders();

        edit.Colliders = colliders;
        edit.Tools.Metres = 5f;

        // Sample 31 is the shared row of the two tiles along X.
        edit.Begin(new(31f, 12f));
        edit.Commit();

        Assert.Contains((0, 0), colliders.Tiles);
        Assert.Contains((1, 0), colliders.Tiles);
    }

    /// <summary>The colliders are rebuilt once per stroke, not once per stamp.</summary>
    [Fact]
    public void A_long_drag_rebuilds_its_tiles_once() {
        var (edit, _) = Ground.Editing(radius: 2f);
        var colliders = new RecordingColliders();

        edit.Colliders = colliders;
        edit.Tools.Metres = 2f;
        edit.Begin(new(4f, 12f));

        // Stopping at 24 rather than at the tile's last quad: the recorded rectangle is grown by
        // TerrainSculpt.NeighbourMargin, so a stroke that reaches sample 28 with a two-metre brush
        // records out to the boundary at 31 and legitimately rebuilds both tiles.
        for (var x = 5; x <= 24; x++) {
            edit.Extend(new(x, 12f));
        }

        Assert.Empty(colliders.Rebuilt);

        edit.Commit();

        Assert.Equal([(0, 0)], colliders.Rebuilt);
    }

    [Fact]
    public void Undoing_a_stroke_rebuilds_what_it_had_touched() {
        var (edit, _) = Ground.Editing(radius: 3f);
        var colliders = new RecordingColliders();

        edit.Colliders = colliders;
        edit.Tools.Metres = 5f;
        edit.Begin(new(12f, 12f));

        var command = edit.Commit()!;
        colliders.Clear();

        command.Undo(NoContext);

        Assert.Equal([(0, 0)], colliders.Tiles);
    }

    // --- What refuses a stroke ----------------------------------------------

    [Fact]
    public void A_locked_layer_refuses_the_brush_and_says_so() {
        var (edit, terrain) = Ground.Editing();

        edit.Layer!.IsLocked = true;

        Assert.False(edit.CanStroke);
        Assert.False(edit.Begin(new(30f, 30f)));
        Assert.Contains("locked", edit.Refusal, StringComparison.OrdinalIgnoreCase);
        Assert.True(Ground.IsUntouched(terrain, 30, 30));
    }

    /// <summary>A reserved layer names the generator that owns it.</summary>
    /// <remarks>
    ///     ⚠ A layer regenerated wholesale by a spline would discard a hand edit the next time it
    ///     ran — silently, and an hour later. Naming the generator is what turns "the brush does not
    ///     work" into "the Splines tool owns that layer".
    /// </remarks>
    [Fact]
    public void A_generated_layer_refuses_the_brush_and_names_its_generator() {
        var terrain = new Vixen.Terrain.Terrain(Ground.Shape);
        var edit = new TerrainEdit { Terrain = terrain };

        edit.Layer = terrain.AddLayer("Splines", TerrainLayerKind.Splines);

        Assert.False(edit.Begin(new(30f, 30f)));
        Assert.Contains("Splines", edit.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_terrain_with_no_layer_refuses_the_brush() {
        var terrain = new Vixen.Terrain.Terrain(Ground.Shape);
        var edit = new TerrainEdit { Terrain = terrain };

        Assert.Null(edit.Layer);
        Assert.False(edit.Begin(new(30f, 30f)));
        Assert.Contains("edit layer", edit.Refusal, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Holes need no edit layer, because they are not written to one.</summary>
    [Fact]
    public void The_hole_tool_works_on_a_terrain_with_no_layer() {
        var terrain = new Vixen.Terrain.Terrain(Ground.Shape);
        var edit = new TerrainEdit { Terrain = terrain };

        edit.Tools.Tool = TerrainTool.Holes;
        edit.Brush.Radius = 4f;

        Assert.True(edit.Begin(new(30f, 30f)));
        Assert.NotNull(edit.Commit(rebuildColliders: false));
        Assert.True(terrain.Holes.IsHole(30, 30));
    }

    /// <summary>Changing the terrain mid-drag drops the drag rather than carrying it across.</summary>
    [Fact]
    public void Changing_the_terrain_abandons_the_stroke() {
        var (edit, terrain) = Ground.Editing();

        edit.Tools.Metres = 20f;
        edit.Begin(new(30f, 30f));

        edit.Terrain = Ground.Terrain();

        Assert.False(edit.IsStroking);
        Assert.True(Ground.IsUntouched(terrain, 30, 30));
    }

    /// <summary>The brush a stroke started with is the brush it finishes with.</summary>
    /// <remarks>
    ///     ⚠ The panel can move while a drag is in flight — a pen's barrel wheel, a key, another
    ///     window — and a stroke whose radius changed halfway has an undo record sized to a footprint
    ///     that no longer matches what it wrote.
    /// </remarks>
    [Fact]
    public void The_brush_is_snapshotted_when_the_stroke_starts() {
        var (edit, terrain) = Ground.Editing(radius: 3f);

        edit.Tools.Metres = 10f;
        edit.Begin(new(30f, 30f));

        edit.Brush.Radius = 20f;
        edit.Extend(new(31f, 30f));
        edit.Commit(rebuildColliders: false);

        // Fifteen metres out is inside the widened brush and outside the one the stroke holds.
        Assert.True(Ground.IsUntouched(terrain, 45, 30));
    }

    static float[] Snapshot(Vixen.Terrain.Terrain terrain) {
        var heights = new float[terrain.Description.SampleCount];
        var at = 0;

        for (var z = 0; z < terrain.Description.SamplesZ; z++) {
            for (var x = 0; x < terrain.Description.SamplesX; x++) {
                heights[at++] = terrain.Composite.MetresAt(x, z);
            }
        }

        return heights;
    }
}
