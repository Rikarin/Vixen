// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Terrain;
using Xunit;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Editor.Terrain.Tests;

/// <summary>The edit-layer stack panel's verbs — [docs/plan/31 § The terrain panel].</summary>
public sealed class TerrainLayerCommandTests {
    static readonly EditorContext NoContext = null!;

    static (TerrainMap Terrain, TerrainEditLayer Base) Built() {
        var terrain = new TerrainMap(Ground.Shape);
        return (terrain, terrain.AddLayer("Base"));
    }

    static void Raise(TerrainMap terrain, TerrainEditLayer layer, int x, int z, float metres) {
        layer.SetDelta(x, z, (short)(metres / terrain.Description.MetresPerStep));
        terrain.InvalidateAll();
        terrain.Resolve();
    }

    [Fact]
    public void Adding_a_layer_puts_it_on_top_and_undoing_takes_it_off() {
        var (terrain, _) = Built();
        var (command, layer) = TerrainLayerCommands.Add(terrain, "Sculpt");

        command.Do(NoContext);

        Assert.Equal(2, terrain.Layers.Count);
        Assert.Same(layer, terrain.Layers[^1]);

        command.Undo(NoContext);
        Assert.Single(terrain.Layers);

        command.Do(NoContext);
        Assert.Same(layer, terrain.Layers[^1]);
    }

    /// <summary>Undoing a removal puts back the ground, not just the name.</summary>
    /// <remarks>
    ///     ⚠ <b>The reason <see cref="TerrainMap.InsertLayer" /> exists.</b> An undo built on
    ///     <c>AddLayer</c> would put back a layer with the right name and none of its deltas — which
    ///     passes any test that only counts layers, and loses an hour of sculpting.
    /// </remarks>
    [Fact]
    public void Undoing_a_removal_puts_back_the_layers_ground_and_its_place_in_the_stack() {
        var (terrain, _) = Built();

        var middle = terrain.AddLayer("Middle");
        terrain.AddLayer("Top");

        Raise(terrain, middle, 20, 20, 25f);

        var command = TerrainLayerCommands.Remove(terrain, middle);

        command.Do(NoContext);

        Assert.Equal(2, terrain.Layers.Count);
        Assert.Equal(0f, terrain.Composite.MetresAt(20, 20), 2);

        command.Undo(NoContext);

        Assert.Equal(3, terrain.Layers.Count);
        Assert.Same(middle, terrain.Layers[1]);
        Assert.Equal(25f, terrain.Composite.MetresAt(20, 20), 1);
    }

    [Fact]
    public void Duplicating_a_layer_copies_its_ground_and_shares_nothing_with_it() {
        var (terrain, layer) = Built();

        Raise(terrain, layer, 20, 20, 10f);

        var (command, copy) = TerrainLayerCommands.Duplicate(terrain, layer);
        command.Do(NoContext);

        Assert.Equal(2, terrain.Layers.Count);
        Assert.Equal(20f, terrain.Composite.MetresAt(20, 20), 1);

        // Two layers, and writing one does not move the other: the copy is set to thirty and the
        // original still holds the ten it was cloned with.
        var was = layer.DeltaAt(20, 20);

        Raise(terrain, copy, 20, 20, 30f);

        Assert.Equal(was, layer.DeltaAt(20, 20));
        Assert.Equal(40f, terrain.Composite.MetresAt(20, 20), 1);
    }

    /// <summary>Reordering undoes to the order it was in, and not to a different one.</summary>
    /// <remarks>
    ///     ⚠ <b>A remove-then-insert is its own inverse only when the arguments are swapped.</b> The
    ///     two spellings agree for adjacent layers and disagree for everything else, which is how a
    ///     reorder undo passes its first test and fails on a stack of four.
    /// </remarks>
    [Fact]
    public void Reordering_undoes_to_the_order_it_was_in() {
        var (terrain, _) = Built();

        terrain.AddLayer("B");
        terrain.AddLayer("C");
        terrain.AddLayer("D");

        var before = terrain.Layers.Select(layer => layer.Name).ToArray();
        var command = TerrainLayerCommands.Move(terrain, 3, 0);

        command.Do(NoContext);
        Assert.Equal(["D", "Base", "B", "C"], terrain.Layers.Select(layer => layer.Name));

        command.Undo(NoContext);
        Assert.Equal(before, terrain.Layers.Select(layer => layer.Name));
    }

    [Fact]
    public void Clearing_a_layer_empties_it_and_keeps_the_object_in_the_stack() {
        var (terrain, layer) = Built();

        Raise(terrain, layer, 20, 20, 15f);

        var command = TerrainLayerCommands.Clear(terrain, layer);
        command.Do(NoContext);

        Assert.True(layer.IsEmpty);

        // ⚠ The same object: the panel's selection, the mode and a stroke in flight all hold it by
        // reference, so a clear implemented as remove-and-add would leave them pointing at nothing.
        Assert.Same(layer, terrain.Layers[0]);

        command.Undo(NoContext);

        Assert.Same(layer, terrain.Layers[0]);
        Assert.Equal(15f, terrain.Composite.MetresAt(20, 20), 1);
    }

    /// <summary>Collapsing puts back both layers, because it destroyed both.</summary>
    [Fact]
    public void Collapsing_undoes_to_two_layers_with_what_each_of_them_held() {
        var (terrain, lower) = Built();

        var upper = terrain.AddLayer("Upper");

        Raise(terrain, lower, 20, 20, 10f);
        Raise(terrain, upper, 20, 20, 5f);

        var command = TerrainLayerCommands.Collapse(terrain, 1);
        command.Do(NoContext);

        Assert.Single(terrain.Layers);
        Assert.Equal(15f, terrain.Composite.MetresAt(20, 20), 1);

        command.Undo(NoContext);

        Assert.Equal(2, terrain.Layers.Count);
        Assert.Equal(15f, terrain.Composite.MetresAt(20, 20), 1);

        // And the separation is back: hiding the upper one leaves the lower one's ten metres.
        upper.IsVisible = false;
        terrain.InvalidateAll();
        terrain.Resolve();

        Assert.Equal(10f, terrain.Composite.MetresAt(20, 20), 1);
    }

    [Fact]
    public void Collapsing_scales_by_the_upper_layers_alpha_and_redoes_the_same_way() {
        var (terrain, lower) = Built();

        var upper = terrain.AddLayer("Upper");

        Raise(terrain, lower, 20, 20, 10f);
        Raise(terrain, upper, 20, 20, 8f);
        upper.HeightAlpha = 0.5f;

        terrain.InvalidateAll();
        terrain.Resolve();

        var command = TerrainLayerCommands.Collapse(terrain, 1);

        command.Do(NoContext);
        Assert.Equal(14f, terrain.Composite.MetresAt(20, 20), 1);

        command.Undo(NoContext);
        command.Do(NoContext);

        Assert.Equal(14f, terrain.Composite.MetresAt(20, 20), 1);
    }

    // --- The row's own toggles ----------------------------------------------

    [Fact]
    public void Hiding_a_layer_takes_it_out_of_the_composite_and_undoing_puts_it_back() {
        var (terrain, layer) = Built();

        Raise(terrain, layer, 20, 20, 12f);

        var command = TerrainLayerCommands.SetVisible(terrain, layer, false);

        command.Do(NoContext);
        Assert.Equal(0f, terrain.Composite.MetresAt(20, 20), 2);

        command.Undo(NoContext);
        Assert.Equal(12f, terrain.Composite.MetresAt(20, 20), 1);
    }

    /// <summary>A slider drag is one undo entry and a toggle is not.</summary>
    /// <remarks>
    ///     ⚠ <b>The merged command has to undo to the value before the <em>drag</em> started</b>, not
    ///     to the value one frame ago — the receiver of <c>TryMergeWith</c> is the new command
    ///     swallowing the old one, which is the half of the contract that is easy to get backwards.
    /// </remarks>
    [Fact]
    public void An_alpha_drag_merges_into_one_entry_that_undoes_to_where_it_started() {
        var (terrain, layer) = Built();

        Raise(terrain, layer, 20, 20, 20f);

        IEditorCommand? entry = null;

        foreach (var alpha in (float[])[0.8f, 0.6f, 0.4f, 0.25f]) {
            var next = TerrainLayerCommands.SetHeightAlpha(terrain, layer, alpha);
            next.Do(NoContext);

            if (entry is null || !next.TryMergeWith(entry, out entry)) {
                entry = next;
            }
        }

        Assert.Equal(5f, terrain.Composite.MetresAt(20, 20), 1);

        entry!.Undo(NoContext);

        Assert.Equal(20f, terrain.Composite.MetresAt(20, 20), 1);
        Assert.Equal(1f, layer.HeightAlpha, 3);
    }

    [Fact]
    public void An_alpha_of_one_layer_does_not_merge_with_another_layers() {
        var (terrain, layer) = Built();

        var other = terrain.AddLayer("Other");
        var first = TerrainLayerCommands.SetHeightAlpha(terrain, layer, 0.5f);
        var second = TerrainLayerCommands.SetHeightAlpha(terrain, other, 0.5f);

        Assert.False(second.TryMergeWith(first, out _));
    }

    [Fact]
    public void A_visibility_toggle_never_merges() {
        var (terrain, layer) = Built();

        var first = TerrainLayerCommands.SetVisible(terrain, layer, false);
        var second = TerrainLayerCommands.SetVisible(terrain, layer, true);

        Assert.False(second.TryMergeWith(first, out _));
    }

    [Fact]
    public void Renaming_a_layer_does_not_recomposite_because_a_name_changes_no_ground() {
        var (terrain, layer) = Built();

        Raise(terrain, layer, 20, 20, 9f);

        var command = TerrainLayerCommands.Rename(terrain, layer, "Valley");
        command.Do(NoContext);

        Assert.Equal("Valley", layer.Name);
        Assert.Equal(0, terrain.DirtyTileCount);

        command.Undo(NoContext);
        Assert.Equal("Base", layer.Name);
    }
}
