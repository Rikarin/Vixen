// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Ui;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Water;
using Xunit;

namespace Vixen.Editor.Water.Tests;

/// <summary>
///     The one mode, its three verbs and the gesture — [docs/plan/35 § W9].
/// </summary>
/// <remarks>
///     <para>
///         [§ Part 4](../../docs/plan/35-water.md#part-4--testing)'s gesture row: "synthetic pointer
///         input against the real tools, asserting the spline and the profile rather than the pixels".
///         Everything here drives <see cref="WaterEdit" /> and <see cref="WaterMode" /> directly,
///         because both are device-free and world-free on purpose.
///     </para>
///     <para>
///         ⚠ <b>The registration test is the one doc 31's "built and not yet reachable" failure asks
///         for</b>, and it is the reason <c>Register</c> and <c>Activated</c> are separate moments: a
///         mode whose commands appear only once somebody has entered it is a mode absent from the
///         palette and unbindable until then.
///     </para>
/// </remarks>
public sealed class WaterModeTests {
    /// <summary>A shell with Select and Water, in the order an editor adds them.</summary>
    static (EditorShell Shell, WaterMode Mode) Built() {
        var shell = new EditorShell(1280f, 800f);
        var mode = new WaterMode();

        shell.Modes.Add(new SelectMode());
        shell.Modes.Add(mode);

        // What `EditorApplication.RegisterModes` wires — see TerrainModeTests for why the mode
        // cannot do it itself.
        shell.Modes.Changed += modes => shell.Context = modes.Context ?? "scene";

        return (shell, mode);
    }

    // --- Registration -------------------------------------------------------

    [Fact]
    public void The_tools_are_registered_before_anybody_has_entered_the_mode() {
        var (shell, _) = Built();
        using var _shell = shell;

        foreach (var tool in WaterMode.Tools) {
            var id = WaterMode.ToolCommand(tool);

            Assert.True(shell.Commands.TryGet(id, out var command));
            Assert.Equal(WaterMode.WaterContext, command!.Context);

            // Registered, listed, rebindable — and disabled, because nobody is in the mode.
            Assert.False(command.CanExecute);
        }

        foreach (var id in WaterMode.Commands) {
            Assert.True(shell.Commands.TryGet(id, out _), $"{id} was not registered.");
        }
    }

    [Fact]
    public void Leaving_the_mode_takes_every_command_back_out() {
        var (shell, mode) = Built();
        using var _shell = shell;

        mode.Unregister(shell);

        foreach (var tool in WaterMode.Tools) {
            Assert.False(shell.Commands.TryGet(WaterMode.ToolCommand(tool), out _));
        }

        foreach (var id in WaterMode.Commands) {
            Assert.False(shell.Commands.TryGet(id, out _), $"{id} was left behind.");
        }

        for (var slot = 0; slot < WaterMode.SlotCount; slot++) {
            Assert.False(shell.Commands.TryGet(WaterMode.SlotCommand(slot), out _));
        }
    }

    /// <summary>The digits are the tools while the mode is active, and nobody else's after.</summary>
    [Fact]
    public void Entering_the_mode_claims_the_digits_and_leaving_it_gives_them_back() {
        var (shell, mode) = Built();
        using var _shell = shell;

        shell.Commands.Add("scene.bookmark-go-2", new StringId("test.bookmark", "View 2"), () => { });
        shell.Keys.SetDefault("scene.bookmark-go-2", new KeyChord(InputKey.Number2, ModifierKeys.None));

        var chord = new KeyChord(InputKey.Number2, ModifierKeys.None);

        Assert.Equal("scene.bookmark-go-2", shell.Keys.CommandFor(chord, shell.Context));

        shell.Modes.Activate(WaterMode.ModeId);
        Assert.Equal(WaterMode.SlotCommand(1), shell.Keys.CommandFor(chord, shell.Context));

        shell.Modes.Activate("select");
        Assert.Equal("scene.bookmark-go-2", shell.Keys.CommandFor(chord, shell.Context));

        Assert.NotNull(mode);
    }

    /// <summary>Selecting a tool through a slot is selecting it, and past the last one does nothing.</summary>
    [Fact]
    public void A_slot_selects_its_tool_and_a_fourth_does_nothing() {
        var (shell, mode) = Built();
        using var _shell = shell;

        Assert.True(mode.SelectSlot(1));
        Assert.Equal(WaterTool.Profile, mode.Tool);

        Assert.False(mode.SelectSlot(3));
        Assert.Equal(WaterTool.Profile, mode.Tool);
    }

    /// <summary>Leaving the mode drops a half-laid curve.</summary>
    /// <remarks>
    ///     ⚠ A gesture that survived a mode switch would be finished by the next click in a mode that
    ///     has no idea a curve was in flight.
    /// </remarks>
    [Fact]
    public void Deactivating_drops_the_draw() {
        var (shell, mode) = Built();
        using var _shell = shell;

        mode.Editing.Add(new(0f, 0f, 0f));
        mode.Editing.Add(new(10f, 0f, 0f));

        Assert.Equal(2, mode.Editing.Points.Count);

        mode.Deactivated();

        Assert.Empty(mode.Editing.Points);
        Assert.False(mode.Editing.IsDrawing);
    }

    /// <summary>And so does changing tool, because a draw belongs to the tool that started it.</summary>
    [Fact]
    public void Changing_tool_drops_the_draw() {
        var (shell, mode) = Built();
        using var _shell = shell;

        mode.Editing.Add(new(0f, 0f, 0f));
        mode.Tool = WaterTool.Profile;

        Assert.Empty(mode.Editing.Points);
    }

    // --- The gesture --------------------------------------------------------

    /// <summary>A lake is three points and a closed curve.</summary>
    [Fact]
    public void Drawing_a_lake_makes_a_closed_curve() {
        var (shell, mode) = Built();
        using var _shell = shell;

        Spline? laid = null;
        WaterBodyKind? kind = null;

        mode.Drawn += (spline, drawnKind) => {
            laid = spline;
            kind = drawnKind;
        };

        mode.Editing.Kind = WaterBodyKind.Lake;

        mode.Editing.Add(new(0f, 4f, 0f));
        mode.Editing.Add(new(20f, 4f, 0f));
        mode.Editing.Add(new(20f, 4f, 20f));
        mode.Editing.Add(new(0f, 4f, 20f));

        Assert.NotNull(mode.Finish());

        Assert.NotNull(laid);
        Assert.True(laid!.IsClosed, "a lake's curve has to close, or it has no inside.");
        Assert.Equal(4, laid.Points.Length);
        Assert.Equal(WaterBodyKind.Lake, kind);

        // And the gesture is over, so the next click starts a new one rather than extending this.
        Assert.Empty(mode.Editing.Points);
    }

    /// <summary>A river is an open curve, and two points are enough.</summary>
    [Fact]
    public void Drawing_a_river_makes_an_open_curve() {
        var (shell, mode) = Built();
        using var _shell = shell;

        Spline? laid = null;

        mode.Drawn += (spline, _) => laid = spline;
        mode.Editing.Kind = WaterBodyKind.River;

        mode.Editing.Add(new(0f, 8f, 0f));
        mode.Editing.Add(new(40f, 6f, 10f));

        Assert.NotNull(mode.Finish());
        Assert.NotNull(laid);
        Assert.False(laid!.IsClosed);

        // ⚠ The heights are the ground's, which is the whole difference between a river and a bent
        // lake: a body whose surface is one height for every point cannot run downhill.
        Assert.Equal(8f, laid.Points[0].Position.Y, 4);
        Assert.Equal(6f, laid.Points[1].Position.Y, 4);
    }

    /// <summary>Two points do not make a lake, and Finish says so by doing nothing.</summary>
    /// <remarks>
    ///     ⚠ Refused at the gesture rather than at <see cref="WaterBody" />'s constructor, which is
    ///     where an author would meet it as an exception dialog.
    /// </remarks>
    [Fact]
    public void A_lake_with_two_points_cannot_be_finished() {
        var (shell, mode) = Built();
        using var _shell = shell;

        mode.Editing.Kind = WaterBodyKind.Lake;
        mode.Editing.Add(new(0f, 0f, 0f));
        mode.Editing.Add(new(10f, 0f, 0f));

        Assert.False(mode.Editing.CanCommit);
        Assert.Null(mode.Finish());

        // And it is still in flight, so the author can carry on clicking.
        Assert.Equal(2, mode.Editing.Points.Count);
    }

    /// <summary>Escape drops the draw and is taken; anything else falls through.</summary>
    [Fact]
    public void Escape_cancels_a_draw_in_flight() {
        var (shell, mode) = Built();
        using var _shell = shell;

        mode.Editing.Add(new(0f, 0f, 0f));

        Assert.True(mode.Key(null!, new KeyEvent { Key = InputKey.Escape }));
        Assert.Empty(mode.Editing.Points);

        // With nothing in flight it is somebody else's key.
        Assert.False(mode.Key(null!, new KeyEvent { Key = InputKey.Escape }));
    }
}
