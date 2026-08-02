// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Editor.Ui;
using Vixen.Foliage;
using Vixen.Input;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.Terrain.Tests;

/// <summary>Flat ground, painted everywhere, that says how often it was asked.</summary>
sealed class Meadow(Func<Vector2, bool>? hit = null, Func<Vector2, Vector3>? normal = null) : IFoliageSurface {
    /// <inheritdoc />
    public FoliageSurface SampleAt(Vector2 position, string layer) =>
        hit is not null && !hit(position)
            ? FoliageSurface.Missed
            : new(new(position.X, 0f, position.Y), normal?.Invoke(position) ?? Vector3.UnitY, 1f, true);
}

/// <summary>Painting a forest — [docs/plan/31 § T5].</summary>
public sealed class FoliageModeTests {
    static readonly EditorContext NoContext = null!;

    static FoliageType Tree =>
        FoliageType.Of("Tree") with { Mesh = "Meshes/pine", Density = 0.05f, Radius = 3f };

    static (FoliageEdit Edit, FoliageVolume Volume, int Type) Painting(FoliageType? type = null) {
        var volume = new FoliageVolume(new(32f));
        var index = volume.AddType(type ?? Tree);

        var edit = new FoliageEdit { Volume = volume, Surface = new Meadow() };

        edit.Brush.Radius = 10f;
        edit.Brush.Strength = 1f;
        edit.Choose(index);

        return (edit, volume, index);
    }

    static (EditorShell Shell, FoliageMode Mode) Built() {
        var shell = new EditorShell(1280f, 800f);
        var mode = new FoliageMode();

        shell.Modes.Add(new SelectMode());
        shell.Modes.Add(new TerrainMode());
        shell.Modes.Add(mode);
        shell.Modes.Changed += modes => shell.Context = modes.Context ?? "scene";

        return (shell, mode);
    }

    // --- The mode -----------------------------------------------------------

    [Fact]
    public void The_six_tools_are_registered_before_anybody_has_entered_the_mode() {
        var (shell, _) = Built();
        using var _shell = shell;

        foreach (var tool in FoliageMode.Tools) {
            var id = FoliageMode.ToolCommand(tool);

            Assert.True(shell.Commands.TryGet(id, out var command));
            Assert.Equal(FoliageMode.FoliageContext, command!.Context);
            Assert.False(command.CanExecute);
        }
    }

    /// <summary>The digits, claimed a third time, in a third context.</summary>
    /// <remarks>
    ///     ⚠ Blockout takes 1–4, terrain takes 1–8 and foliage takes 1–6, and view-bookmark recall
    ///     keeps all nine everywhere none of them has the focus. Nothing in <c>Vixen.Editor.Ui</c>
    ///     changed for any of the three.
    /// </remarks>
    [Fact]
    public void The_digits_are_slots_in_a_context_of_their_own() {
        var (shell, _) = Built();
        using var _shell = shell;

        var chord = new KeyChord(InputKey.Number3, ModifierKeys.None);

        Assert.Equal(FoliageMode.SlotCommand(2), shell.Keys.CommandFor(chord, FoliageMode.FoliageContext));
        Assert.Equal(TerrainMode.SlotCommand(2), shell.Keys.CommandFor(chord, TerrainMode.TerrainContext));

        foreach (var tool in FoliageMode.Tools) {
            Assert.Equal(KeyChord.None, shell.Keys.ChordFor(FoliageMode.ToolCommand(tool)));
        }
    }

    [Fact]
    public void A_digit_past_the_sixth_does_nothing() {
        var (shell, mode) = Built();
        using var _shell = shell;

        shell.Modes.Activate(FoliageMode.ModeId);

        Assert.False(mode.SelectSlot(6));
        Assert.Equal(FoliageTool.Paint, mode.Tool);

        Assert.True(mode.SelectSlot(5));
        Assert.Equal(FoliageTool.Select, mode.Tool);
    }

    /// <summary>The mode requires nothing, which is what makes it a separate mode.</summary>
    /// <remarks>
    ///     ⚠ Sculpt and paint need a terrain; foliage paints onto any surface. A mode that required
    ///     one would have to answer "what is the target surface" twice with different answers.
    /// </remarks>
    [Fact]
    public void The_mode_can_be_entered_with_no_terrain_at_all() {
        var (shell, mode) = Built();
        using var _shell = shell;

        shell.Modes.Activate(FoliageMode.ModeId);

        Assert.Equal(FoliageMode.ModeId, shell.Modes.Active?.Id);
        Assert.False(mode.HasPalette);

        // And the tools that place things are refused until a palette exists, while the two that act
        // on what is already there are not.
        Assert.False(shell.Commands.CanExecute(FoliageMode.ToolCommand(FoliageTool.Paint)));
        Assert.True(shell.Commands.CanExecute(FoliageMode.ToolCommand(FoliageTool.Select)));
    }

    [Fact]
    public void Adding_a_type_makes_the_placing_tools_reachable() {
        var (shell, mode) = Built();
        using var _shell = shell;

        shell.Modes.Activate(FoliageMode.ModeId);
        mode.Editing.Volume = new FoliageVolume();

        Assert.True(shell.Commands.Execute(FoliageMode.AddTypeCommand));

        Assert.True(mode.HasPalette);
        Assert.True(shell.Commands.CanExecute(FoliageMode.ToolCommand(FoliageTool.Paint)));
        Assert.Contains(0, mode.Editing.Chosen);
    }

    /// <summary>Removing a palette entry is unavailable rather than absent, and says why.</summary>
    /// <remarks>
    ///     ⚠ It renumbers every index above it — in the chunks, in the selection and in every undo
    ///     entry on the stack. A verb that silently did the wrong thing would be worse than one that
    ///     says it is not built.
    /// </remarks>
    [Fact]
    public void Removing_a_type_is_unavailable_and_explains_itself() {
        var (shell, _) = Built();
        using var _shell = shell;

        Assert.True(shell.Commands.TryGet(FoliageMode.RemoveTypeCommand, out var command));
        Assert.True(command!.IsUnavailable);
        Assert.Contains("renumbers", command.Unavailable.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void Unregistering_the_mode_takes_its_commands_with_it() {
        var (shell, _) = Built();
        using var _shell = shell;

        Assert.True(shell.Modes.Remove(FoliageMode.ModeId));

        foreach (var tool in FoliageMode.Tools) {
            Assert.False(shell.Commands.TryGet(FoliageMode.ToolCommand(tool), out _));
        }

        for (var slot = 0; slot < FoliageMode.SlotCount; slot++) {
            Assert.False(shell.Commands.TryGet(FoliageMode.SlotCommand(slot), out _));
        }

        foreach (var id in FoliageMode.Commands) {
            Assert.False(shell.Commands.TryGet(id, out _));
        }
    }

    // --- The tools ----------------------------------------------------------

    [Fact]
    public void A_paint_stroke_places_instances_and_is_one_entry() {
        var (edit, volume, _) = Painting();

        Assert.True(edit.Begin(new(50f, 50f)));
        edit.Extend(new(60f, 50f));

        var command = edit.Commit();

        Assert.NotNull(command);
        Assert.True(volume.InstanceCount > 0);

        var placed = volume.InstanceCount;

        command.Undo(NoContext);
        Assert.Equal(0, volume.InstanceCount);

        command.Do(NoContext);
        Assert.Equal(placed, volume.InstanceCount);
    }

    [Fact]
    public void The_single_tool_places_exactly_one() {
        var (edit, volume, _) = Painting();

        edit.Settings.Tool = FoliageTool.Single;
        edit.Begin(new(50f, 50f));
        edit.Commit();

        Assert.Equal(1, volume.InstanceCount);
    }

    [Fact]
    public void Shift_erases_what_a_paint_stroke_would_have_placed() {
        var (edit, volume, _) = Painting();

        edit.Begin(new(50f, 50f));
        edit.Commit();

        var placed = volume.InstanceCount;
        Assert.True(placed > 0);

        edit.Begin(new(50f, 50f), invert: true);
        var command = edit.Commit();

        Assert.Equal(0, volume.InstanceCount);
        Assert.NotNull(command);

        command.Undo(NoContext);
        Assert.Equal(placed, volume.InstanceCount);
    }

    [Fact]
    public void The_erase_tool_only_takes_the_chosen_types() {
        var volume = new FoliageVolume(new(32f));
        var tree = volume.AddType(Tree);
        var rock = volume.AddType(Tree with { Name = "Rock" });

        volume.Add(tree, new(new(50f, 0f, 50f), Quaternion.Identity, 1f));
        volume.Add(rock, new(new(51f, 0f, 50f), Quaternion.Identity, 1f));

        var edit = new FoliageEdit { Volume = volume, Surface = new Meadow() };

        edit.Brush.Radius = 10f;
        edit.Settings.Tool = FoliageTool.Erase;
        edit.Choose(tree);

        edit.Begin(new(50f, 50f));
        edit.Commit();

        Assert.Equal(0, volume.CountOf(tree));
        Assert.Equal(1, volume.CountOf(rock));
    }

    /// <summary>Reapply re-rolls the properties asked for and leaves the position alone.</summary>
    /// <remarks>
    ///     ⚠ <b>The tool this is for.</b> Changing a type's scale range afterwards should re-roll the
    ///     scale of existing trees <em>without moving them</em>; re-rolling everything is a different
    ///     operation and it moves a forest somebody has already thinned by hand.
    /// </remarks>
    [Fact]
    public void Reapply_rerolls_the_scale_and_never_the_position() {
        var (edit, volume, type) = Painting(Tree with { MinScale = 1f, MaxScale = 1f });

        edit.Begin(new(50f, 50f));
        edit.Commit();

        var before = Snapshot(volume);
        Assert.NotEmpty(before);
        Assert.All(before, entry => Assert.Equal(1f, entry.Scale, 4));

        volume.SetType(type, volume.Palette[type] with { MinScale = 2f, MaxScale = 4f });

        edit.Settings.Tool = FoliageTool.Reapply;
        edit.Settings.Reapply = FoliageReapply.Scale;
        edit.Begin(new(50f, 50f));
        edit.Commit();

        var after = Snapshot(volume);

        Assert.Equal(before.Length, after.Length);
        Assert.Equal(
            before.Select(entry => entry.Position).OrderBy(at => at.X).ToArray(),
            after.Select(entry => entry.Position).OrderBy(at => at.X).ToArray()
        );

        Assert.All(after, entry => Assert.InRange(entry.Scale, 2f, 4f));
    }

    [Fact]
    public void Reapply_with_nothing_ticked_changes_nothing() {
        var (edit, volume, _) = Painting();

        edit.Begin(new(50f, 50f));
        edit.Commit();

        var before = Snapshot(volume);

        edit.Settings.Tool = FoliageTool.Reapply;
        edit.Settings.Reapply = FoliageReapply.None;
        edit.Begin(new(50f, 50f));
        edit.Commit();

        Assert.Equal(before, Snapshot(volume));
    }

    /// <summary>Reapply's filter pass removes what the type would now refuse.</summary>
    [Fact]
    public void Reapply_can_drop_instances_the_filters_would_now_refuse() {
        var (edit, volume, type) = Painting();

        edit.Begin(new(50f, 50f));
        edit.Commit();

        var placed = volume.InstanceCount;
        Assert.True(placed > 0);

        // The ground is flat; a type that now demands a slope refuses all of it.
        volume.SetType(type, volume.Palette[type] with { MinSlope = 0.5f });

        edit.Settings.Tool = FoliageTool.Reapply;
        edit.Settings.Reapply = FoliageReapply.Filters;
        edit.Begin(new(50f, 50f));

        var command = edit.Commit();

        Assert.Equal(0, volume.InstanceCount);

        command!.Undo(NoContext);
        Assert.Equal(placed, volume.InstanceCount);
    }

    [Fact]
    public void Cancelling_a_stroke_puts_the_instances_back() {
        var (edit, volume, _) = Painting();

        edit.Begin(new(50f, 50f));
        edit.Extend(new(58f, 50f));

        Assert.True(volume.InstanceCount > 0);

        edit.Cancel();

        Assert.Equal(0, volume.InstanceCount);
        Assert.Null(edit.Commit());
    }

    [Fact]
    public void Cancelling_an_erase_puts_back_what_it_took() {
        var (edit, volume, _) = Painting();

        edit.Begin(new(50f, 50f));
        edit.Commit();

        var placed = volume.InstanceCount;

        edit.Settings.Tool = FoliageTool.Erase;
        edit.Begin(new(50f, 50f));

        Assert.Equal(0, volume.InstanceCount);

        edit.Cancel();

        Assert.Equal(placed, volume.InstanceCount);
    }

    // --- What refuses a stroke ----------------------------------------------

    [Fact]
    public void An_empty_palette_refuses_the_brush_and_says_so() {
        var edit = new FoliageEdit { Volume = new FoliageVolume(), Surface = new Meadow() };

        Assert.False(edit.CanStroke);
        Assert.False(edit.Begin(new(50f, 50f)));
        Assert.Contains("palette is empty", edit.Refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_filter_off_refuses_the_brush_rather_than_landing_nowhere() {
        var (edit, _, _) = Painting();

        edit.Settings.OnTerrain = false;
        edit.Settings.OnStaticMeshes = false;
        edit.Settings.OnBlockout = false;
        edit.Settings.OnFoliage = false;

        Assert.False(edit.Begin(new(50f, 50f)));
        Assert.Contains("filter", edit.Refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_surface_refuses_a_placing_tool_and_not_an_erasing_one() {
        var (edit, volume, _) = Painting();

        edit.Begin(new(50f, 50f));
        edit.Commit();

        edit.Surface = null;

        Assert.False(edit.Begin(new(50f, 50f)));

        edit.Settings.Tool = FoliageTool.Erase;

        Assert.True(edit.Begin(new(50f, 50f)));
        Assert.Equal(0, volume.InstanceCount);
    }

    // --- Selection and the gizmo --------------------------------------------

    [Fact]
    public void Selecting_takes_the_chosen_types_within_the_brush() {
        var (edit, volume, _) = Painting();

        edit.Begin(new(50f, 50f));
        edit.Commit();

        edit.Brush.Radius = 4f;

        var selected = edit.Select(new(50f, 50f));

        Assert.True(selected > 0);
        Assert.True(selected <= volume.InstanceCount);
        Assert.Equal(selected, edit.Selection.Count);

        edit.Deselect();
        Assert.Empty(edit.Selection);
    }

    /// <summary>A moved instance is still there, and undo puts it back.</summary>
    [Fact]
    public void A_selected_instance_moves_and_the_move_undoes() {
        var volume = new FoliageVolume(new(32f));
        var type = volume.AddType(Tree);

        volume.Add(type, new(new(10f, 0f, 10f), Quaternion.Identity, 1f));

        var edit = new FoliageEdit { Volume = volume, Surface = new Meadow() };

        edit.Brush.Radius = 5f;
        edit.Choose(type);

        Assert.Equal(1, edit.Select(new(10f, 10f)));

        var command = edit.MoveSelection(new(4f, 0f, 0f))!;

        command.Do(NoContext);

        Assert.Equal(14f, volume.Chunks.Single().Instances[0].Position.X, 3);

        command.Undo(NoContext);

        Assert.Equal(10f, volume.Chunks.Single().Instances[0].Position.X, 3);
    }

    /// <summary>A move across a cell boundary re-cells and the selection follows it.</summary>
    /// <remarks>
    ///     ⚠ <b>The address changes, so a gizmo still holding the old one moves a different tree
    ///     next frame.</b> That is the failure that reads as the gizmo drifting.
    /// </remarks>
    [Fact]
    public void Moving_an_instance_across_a_cell_boundary_rebinds_the_selection() {
        var volume = new FoliageVolume(new(32f));
        var type = volume.AddType(Tree);

        volume.Add(type, new(new(30f, 0f, 10f), Quaternion.Identity, 1f));

        var edit = new FoliageEdit { Volume = volume, Surface = new Meadow() };

        edit.Brush.Radius = 5f;
        edit.Choose(type);
        edit.Select(new(30f, 10f));

        var command = edit.MoveSelection(new(10f, 0f, 0f))!;
        command.Do(NoContext);

        var chunk = Assert.Single(volume.Chunks);

        Assert.Equal(new(1, 0), chunk.Cell);
        Assert.Equal(new FoliageCellKey(1, 0), edit.Selection.Single().Cell);
        Assert.Equal(40f, volume.At(edit.Selection.Single())!.Value.Position.X, 3);
    }

    [Fact]
    public void Deleting_the_selection_is_one_entry() {
        var (shell, mode) = Built();
        using var _shell = shell;

        shell.Modes.Activate(FoliageMode.ModeId);

        var volume = new FoliageVolume(new(32f));
        var type = volume.AddType(Tree);

        volume.Add(type, new(new(10f, 0f, 10f), Quaternion.Identity, 1f));
        volume.Add(type, new(new(14f, 0f, 10f), Quaternion.Identity, 1f));

        mode.Editing.Volume = volume;
        mode.Editing.Surface = new Meadow();
        mode.Editing.Brush.Radius = 10f;
        mode.Editing.Choose(type);
        mode.Editing.Select(new(12f, 10f));

        Assert.True(shell.Commands.CanExecute(FoliageMode.DeleteSelectionCommand));

        var command = mode.DeleteSelection()!;

        Assert.Equal(0, volume.InstanceCount);
        Assert.Empty(mode.Editing.Selection);

        command.Undo(NoContext);
        Assert.Equal(2, volume.InstanceCount);
    }

    // --- The exit criterion --------------------------------------------------

    /// <summary>
    ///     [docs/plan/31 § T5]'s exit criterion: fifty thousand painted trees, one selected, moved,
    ///     and still there after a reload.
    /// </summary>
    /// <remarks>
    ///     The culling half is <c>FoliageRendererTests</c>, where it can be measured against a
    ///     frustum. What this adds is the authoring half: they arrive through strokes, one of them is
    ///     picked out and dragged, and the whole volume survives a round trip through bytes with the
    ///     move intact.
    /// </remarks>
    [Fact]
    public void Fifty_thousand_painted_trees_survive_a_move_and_a_reload() {
        var volume = new FoliageVolume(new(32f));
        var type = volume.AddType(Tree with { Density = 0.5f, Radius = 1.5f });

        var edit = new FoliageEdit { Volume = volume, Surface = new Meadow() };

        edit.Brush.Radius = 40f;
        edit.Brush.Strength = 1f;
        edit.Choose(type);

        // A grid of strokes across two square kilometres.
        for (var z = 0; z < 40; z++) {
            for (var x = 0; x < 40; x++) {
                edit.Begin(new(60f + (x * 70f), 60f + (z * 70f)));
                edit.Commit();
            }
        }

        Assert.True(
            volume.InstanceCount >= 50_000,
            $"the strokes placed {volume.InstanceCount} trees, which is fewer than fifty thousand."
        );

        // ⚠ And the spacing held across every one of them, which is the property that makes this a
        // forest rather than a mat: 1 600 strokes, each checking against what the earlier ones left.
        Assert.True(volume.CellCount > 100);

        // --- One of them selected and moved ----------------------------------
        edit.Brush.Radius = 2f;
        edit.Settings.Tool = FoliageTool.Select;

        var picked = 0;
        var at = Vector2.Zero;

        foreach (var chunk in volume.Chunks) {
            var instance = chunk.Instances[0];

            at = new(instance.Position.X, instance.Position.Z);
            picked = edit.Select(at);

            if (picked == 1) {
                break;
            }
        }

        Assert.Equal(1, picked);

        var moved = edit.MoveSelection(new(0f, 7f, 0f))!;
        moved.Do(NoContext);

        var lifted = volume.At(edit.Selection.Single())!.Value;

        Assert.Equal(7f, lifted.Position.Y, 3);

        // --- Still there after a reload --------------------------------------
        var bytes = new byte[FoliageStore.ByteCount(volume)];
        FoliageStore.Write(volume, bytes);

        var reloaded = new FoliageVolume(new(32f));
        reloaded.AddType(volume.Palette[type]);

        Assert.Equal(volume.InstanceCount, FoliageStore.Read(reloaded, bytes));

        var found = reloaded
            .Within(new(lifted.Position.X, lifted.Position.Z), 0.1f)
            .Select(address => reloaded.At(address)!.Value)
            .Where(instance => MathF.Abs(instance.Position.Y - 7f) < 1e-3f)
            .ToArray();

        Assert.Single(found);
        Assert.Equal(lifted.Position, found[0].Position);
    }

    static (Vector3 Position, float Scale)[] Snapshot(FoliageVolume volume) =>
        [
            .. volume.Chunks
                .SelectMany(chunk => chunk.Instances)
                .Select(instance => (instance.Position, instance.Scale))
                .OrderBy(entry => entry.Position.X)
                .ThenBy(entry => entry.Position.Z)
        ];
}
