// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Ui;
using Vixen.Input;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.Terrain.Tests;

/// <summary>What the mode claims while it is the active one — [docs/plan/31 § T3].</summary>
public sealed class TerrainModeTests {
    /// <summary>A shell with Select and Terrain, in the order the editor adds them.</summary>
    static (EditorShell Shell, TerrainMode Mode) Built() {
        var shell = new EditorShell(1280f, 800f);
        var mode = new TerrainMode();

        shell.Modes.Add(new SelectMode());
        shell.Modes.Add(mode);

        // What `EditorApplication.RegisterModes` wires: `EditorShell.Context` is "which context has
        // the focus", and only the application knows that entering a mode is a claim about the
        // viewport. Without it every scoped command the mode registers is out of scope.
        shell.Modes.Changed += modes => shell.Context = modes.Context ?? "scene";

        return (shell, mode);
    }

    [Fact]
    public void The_tools_are_registered_before_anybody_has_entered_the_mode() {
        var (shell, _) = Built();
        using var _shell = shell;

        foreach (var tool in TerrainMode.Tools) {
            var id = TerrainMode.ToolCommand(tool);

            Assert.True(shell.Commands.TryGet(id, out var command));
            Assert.Equal(TerrainMode.TerrainContext, command!.Context);

            // Registered, listed, rebindable — and disabled, because there is neither a mode nor a
            // terrain for "Erosion Tool" to be about.
            Assert.False(command.CanExecute);
        }
    }

    /// <summary>The digits, claimed a second time and by the same machinery.</summary>
    /// <remarks>
    ///     ⚠ Doc 24's B2 said the mode seam was the thing that could not be retrofitted. This is the
    ///     evidence that it was worth building: a second mode takes eight of the same keys, the
    ///     bookmarks keep them everywhere else, and nothing in <c>Vixen.Editor.Ui</c> changed.
    /// </remarks>
    [Fact]
    public void Entering_the_mode_claims_the_digits_and_leaving_it_gives_them_back() {
        var (shell, _) = Built();
        using var _shell = shell;

        shell.Commands.Add("scene.bookmark-go-3", new StringId("test.bookmark", "View 3"), () => { });
        shell.Keys.SetDefault("scene.bookmark-go-3", new KeyChord(InputKey.Number3, ModifierKeys.None));

        var chord = new KeyChord(InputKey.Number3, ModifierKeys.None);

        Assert.Equal("scene.bookmark-go-3", shell.Keys.CommandFor(chord, "scene"));

        Assert.Equal(
            TerrainMode.SlotCommand(2),
            shell.Keys.CommandFor(chord, TerrainMode.TerrainContext)
        );
    }

    /// <summary>The digits are bound to slots, not to named tools, because two sets share them.</summary>
    /// <remarks>
    ///     ⚠ <b>Binding "Sculpt" and "Paint Layer" both to <c>1</c> would put two commands on one
    ///     chord in one context</b>, and the keymap resolves that to whichever was registered last —
    ///     silently, and differently depending on registration order. A slot command means what
    ///     [§ Part 2]'s sentence means, "the third tool", and the named commands keep the words an
    ///     artist searches the palette for.
    /// </remarks>
    [Fact]
    public void Every_slot_has_its_own_digit_and_none_of_them_collide() {
        var (shell, _) = Built();
        using var _shell = shell;

        var chords = Enumerable.Range(0, TerrainMode.SlotCount)
            .Select(slot => shell.Keys.ChordFor(TerrainMode.SlotCommand(slot)))
            .ToArray();

        Assert.All(chords, chord => Assert.NotEqual(KeyChord.None, chord));
        Assert.Equal(chords.Length, chords.Distinct().Count());

        Assert.Equal(new(InputKey.Number1, ModifierKeys.None), chords[0]);
        Assert.Equal(new(InputKey.Number8, ModifierKeys.None), chords[^1]);

        // And the named tools carry no chord of their own, so nothing shadows a slot.
        foreach (var tool in TerrainMode.Tools) {
            Assert.Equal(KeyChord.None, shell.Keys.ChordFor(TerrainMode.ToolCommand(tool)));
        }
    }

    /// <summary>The same digit means a different tool in each category.</summary>
    [Fact]
    public void A_digit_selects_within_whichever_category_is_showing() {
        var (shell, mode) = Built();
        using var _shell = shell;

        shell.Modes.Activate(TerrainMode.ModeId);

        var terrain = Ground.Terrain();
        terrain.Weights.AddLayer("Grass");

        mode.Editing.Terrain = terrain;

        Assert.True(shell.Commands.Execute(TerrainMode.SlotCommand(2)));
        Assert.Equal(TerrainTool.Flatten, mode.Tool);

        Assert.True(shell.Commands.Execute(TerrainMode.CategoryCommand(TerrainCategory.Paint)));
        Assert.True(shell.Commands.Execute(TerrainMode.SlotCommand(2)));

        Assert.Equal(TerrainPaintTool.Flatten, mode.PaintTool);

        // ⚠ And a digit past the current category's count does nothing rather than wrapping round to
        // the first tool, which is the version of this that silently paints with the wrong one.
        Assert.False(shell.Commands.CanExecute(TerrainMode.SlotCommand(7)));
        Assert.Equal(TerrainPaintTool.Flatten, mode.PaintTool);
    }

    [Fact]
    public void A_tool_command_selects_its_tool_while_the_mode_is_active_and_a_terrain_is_there() {
        var (shell, mode) = Built();
        using var _shell = shell;

        shell.Modes.Activate(TerrainMode.ModeId);

        // ⚠ Still refused, because there is no terrain. A mode entered over an empty scene shows the
        // create panel; a strip whose buttons work over nothing is one that reads as broken.
        Assert.False(shell.Commands.Execute(TerrainMode.ToolCommand(TerrainTool.Erosion)));
        Assert.Equal(TerrainTool.Sculpt, mode.Tool);

        mode.Editing.Terrain = Ground.Terrain();

        Assert.True(shell.Commands.Execute(TerrainMode.ToolCommand(TerrainTool.Erosion)));
        Assert.Equal(TerrainTool.Erosion, mode.Tool);
    }

    [Fact]
    public void The_size_and_strength_keys_are_the_ones_the_design_names() {
        var (shell, mode) = Built();
        using var _shell = shell;

        Assert.Equal(
            new KeyChord(InputKey.RightBracket, ModifierKeys.None),
            shell.Keys.ChordFor(TerrainMode.GrowBrushCommand)
        );

        Assert.Equal(
            new KeyChord(InputKey.LeftBracket, ModifierKeys.None),
            shell.Keys.ChordFor(TerrainMode.ShrinkBrushCommand)
        );

        Assert.Equal(new KeyChord(InputKey.Equals, ModifierKeys.None), shell.Keys.ChordFor(TerrainMode.HarderCommand));
        Assert.Equal(new KeyChord(InputKey.Minus, ModifierKeys.None), shell.Keys.ChordFor(TerrainMode.SofterCommand));

        shell.Modes.Activate(TerrainMode.ModeId);
        mode.Editing.Terrain = Ground.Terrain();

        var radius = mode.Editing.Brush.Radius;

        Assert.True(shell.Commands.Execute(TerrainMode.GrowBrushCommand));
        Assert.True(mode.Editing.Brush.Radius > radius);

        Assert.True(shell.Commands.Execute(TerrainMode.ShrinkBrushCommand));
        Assert.Equal(radius, mode.Editing.Brush.Radius, 4);
    }

    [Fact]
    public void The_mode_bar_carries_the_categories_and_then_the_current_ones_tools() {
        var (shell, mode) = Built();
        using var _shell = shell;

        Assert.Collection(shell.Modes.Bar(), entry => Assert.IsType<ToolbarGroup>(entry));

        shell.Modes.Activate(TerrainMode.ModeId);

        var bar = shell.Modes.Bar();

        // The mode picker, a rule, the category picker, a rule, and the tools of the category.
        Assert.Equal(5, bar.Count);
        Assert.IsType<ToolbarSeparator>(bar[1]);
        Assert.IsType<ToolbarSeparator>(bar[3]);

        Assert.Equal(
            [.. TerrainMode.Categories.Select(TerrainMode.CategoryCommand)],
            Assert.IsType<ToolbarGroup>(bar[2]).CommandIds
        );

        Assert.Equal(
            [.. TerrainMode.Tools.Select(TerrainMode.ToolCommand)],
            Assert.IsType<ToolbarGroup>(bar[4]).CommandIds
        );

        // ⚠ The second strip changes under the first, which is the whole of what a category is.
        mode.Category = TerrainCategory.Paint;

        Assert.Equal(
            [.. TerrainMode.PaintTools.Select(TerrainMode.PaintToolCommand)],
            Assert.IsType<ToolbarGroup>(shell.Modes.Bar()[4]).CommandIds
        );
    }

    /// <summary>Entering the mode over an empty scene is a state with something to say.</summary>
    /// <remarks>
    ///     ⚠ [§ Part 2]: "TerrainMode with no terrain selected shows the create panel rather than an
    ///     empty toolbar. Entering a mode that does nothing and says nothing is the state every one of
    ///     these tools puts a new user in." <c>Create</c> is what the panel draws, and it is valid out
    ///     of the box — so the first thing a new user meets is a button that works.
    /// </remarks>
    [Fact]
    public void A_mode_with_no_terrain_still_offers_a_terrain_to_create() {
        var (shell, mode) = Built();
        using var _shell = shell;

        shell.Modes.Activate(TerrainMode.ModeId);

        Assert.False(mode.HasTerrain);
        Assert.Null(mode.Create.Validate());
        Assert.True(shell.Commands.CanExecute(TerrainMode.CreateCommand));

        var made = 0;
        mode.Created += _ => made++;

        Assert.True(shell.Commands.Execute(TerrainMode.CreateCommand));

        Assert.Equal(1, made);
        Assert.True(mode.HasTerrain);

        // And it comes with a layer to write, because a terrain a brush refuses is the second empty
        // state in a row.
        Assert.NotNull(mode.Editing.Layer);
        Assert.True(mode.Editing.CanStroke);
    }

    [Fact]
    public void The_panel_is_named_so_that_leaving_the_mode_closes_it() {
        var (_, mode) = Built();

        Assert.Equal(TerrainMode.PanelId, mode.Panel);
    }

    [Fact]
    public void The_mode_refuses_the_pane_free_overloads_because_a_brush_needs_a_ray() {
        var (shell, mode) = Built();
        using var _shell = shell;

        shell.Modes.Activate(TerrainMode.ModeId);
        mode.Editing.Terrain = Ground.Terrain();

        Assert.False(mode.Pointer(new PointerEvent()));
        Assert.False(mode.Key(new KeyEvent()));
    }

    [Fact]
    public void Leaving_the_mode_abandons_a_stroke_rather_than_committing_it() {
        var (shell, mode) = Built();
        using var _shell = shell;

        shell.Modes.Activate(TerrainMode.ModeId);

        var terrain = Ground.Terrain();
        mode.Editing.Terrain = terrain;
        mode.Editing.Tools.Metres = 20f;
        mode.Editing.Brush.Radius = 6f;

        Assert.True(mode.Editing.Begin(new(32f, 32f)));
        Assert.True(Ground.HeightAt(terrain, 32, 32) > 1f);

        shell.Modes.Activate(SelectMode.ModeId);

        Assert.False(mode.Editing.IsStroking);
        Assert.True(Ground.IsUntouched(terrain, 32, 32));
    }

    [Fact]
    public void Changing_the_tool_mid_drag_abandons_the_stroke() {
        var (_, mode) = Built();

        var terrain = Ground.Terrain();
        mode.Editing.Terrain = terrain;
        mode.Editing.Tools.Metres = 20f;

        Assert.True(mode.Editing.Begin(new(32f, 32f)));

        mode.Tool = TerrainTool.Smooth;

        Assert.False(mode.Editing.IsStroking);
        Assert.True(Ground.IsUntouched(terrain, 32, 32));
    }

    [Fact]
    public void Unregistering_the_mode_takes_its_commands_with_it() {
        var (shell, _) = Built();
        using var _shell = shell;

        Assert.True(shell.Modes.Remove(TerrainMode.ModeId));

        foreach (var tool in TerrainMode.Tools) {
            Assert.False(shell.Commands.TryGet(TerrainMode.ToolCommand(tool), out _));
        }

        foreach (var tool in TerrainMode.PaintTools) {
            Assert.False(shell.Commands.TryGet(TerrainMode.PaintToolCommand(tool), out _));
        }

        foreach (var category in TerrainMode.Categories) {
            Assert.False(shell.Commands.TryGet(TerrainMode.CategoryCommand(category), out _));
        }

        for (var slot = 0; slot < TerrainMode.SlotCount; slot++) {
            Assert.False(shell.Commands.TryGet(TerrainMode.SlotCommand(slot), out _));
        }

        foreach (var id in TerrainMode.BrushCommands
                     .Concat(TerrainMode.LayerCommands)
                     .Concat(TerrainMode.TargetCommands)) {
            Assert.False(shell.Commands.TryGet(id, out _));
        }

        Assert.False(shell.Commands.TryGet(TerrainMode.CreateCommand, out _));
    }
}
