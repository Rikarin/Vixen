// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Ui;
using Vixen.Input;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.Blockout.Tests;

/// <summary>Doc 24's B2: the second mode, and what it claims while it is the active one.</summary>
public class BlockoutModeTests {
    /// <summary>A shell with the two modes doc 24's P0 ships, in the order the editor adds them.</summary>
    static (EditorShell Shell, BlockoutMode Mode) Built() {
        var shell = new EditorShell(1280f, 800f);
        var mode = new BlockoutMode();

        shell.Modes.Add(new SelectMode());
        shell.Modes.Add(mode);

        // ⚠ What `EditorApplication.RegisterModes` wires, reproduced here because it is the join
        // rather than the mechanism. `EditorShell.Context` is "which context has the focus", and only
        // the application knows that entering a mode is a claim about the viewport — so a test of the
        // mode on its own has to make the same claim or every scoped command it registers is out of
        // scope and refuses to run.
        shell.Modes.Changed += modes => shell.Context = modes.Context ?? "scene";

        return (shell, mode);
    }

    [Fact]
    public void The_editor_starts_in_Select_and_the_viewport_keeps_its_own_context() {
        var (shell, _) = Built();
        using var _shell = shell;

        Assert.Equal(SelectMode.ModeId, shell.Modes.Active?.Id);

        // ⚠ Null, not "select". A Select mode that claimed a context would shadow the outliner's
        // scoped verbs the moment the pointer moved into the pane, so "no mode" and "Select" have to
        // be the same thing as far as the keymap is concerned.
        Assert.Null(shell.Modes.Context);
    }

    [Fact]
    public void The_element_modes_are_registered_before_anybody_has_entered_the_mode() {
        var (shell, _) = Built();
        using var _shell = shell;

        foreach (var element in Enum.GetValues<BlockoutElement>()) {
            var id = BlockoutMode.ElementCommand(element);

            Assert.True(shell.Commands.TryGet(id, out var command));
            Assert.Equal(BlockoutMode.BlockoutContext, command!.Context);

            // Registered, listed, rebindable — and disabled, because running "Face Mode" from the
            // palette while you are in Select would set a state nothing is reading.
            Assert.False(command.CanExecute);
        }
    }

    [Fact]
    public void Entering_the_mode_claims_the_digits_and_leaving_it_gives_them_back() {
        var (shell, _) = Built();
        using var _shell = shell;

        // The other claimant, exactly as `ViewportCommands` registers it: bound to the same key, in
        // no context at all.
        shell.Commands.Add("scene.bookmark-go-2", new StringId("test.bookmark", "View 2"), () => { });
        shell.Keys.SetDefault("scene.bookmark-go-2", new KeyChord(InputKey.Number2, ModifierKeys.None));

        var chord = new KeyChord(InputKey.Number2, ModifierKeys.None);

        // Outside the mode the digit is the bookmark, which is the editor as it shipped.
        Assert.Equal("scene.bookmark-go-2", shell.Keys.CommandFor(chord, "scene"));

        // Inside it, it is vertex mode. Neither command moved and neither gave up the key.
        Assert.Equal(
            BlockoutMode.ElementCommand(BlockoutElement.Vertex),
            shell.Keys.CommandFor(chord, BlockoutMode.BlockoutContext)
        );
    }

    [Fact]
    public void An_element_command_selects_its_element_while_the_mode_is_active() {
        var (shell, mode) = Built();
        using var _shell = shell;

        shell.Modes.Activate(BlockoutMode.ModeId);

        Assert.Equal(BlockoutElement.Object, mode.Element);

        Assert.True(shell.Commands.Execute(BlockoutMode.ElementCommand(BlockoutElement.Face)));
        Assert.Equal(BlockoutElement.Face, mode.Element);

        Assert.True(shell.Commands.Execute(BlockoutMode.ElementCommand(BlockoutElement.Edge)));
        Assert.Equal(BlockoutElement.Edge, mode.Element);
    }

    [Fact]
    public void Tab_leaves_the_mesh_and_goes_back_into_the_element_mode_it_left() {
        var (shell, mode) = Built();
        using var _shell = shell;

        shell.Modes.Activate(BlockoutMode.ModeId);
        shell.Commands.Execute(BlockoutMode.ElementCommand(BlockoutElement.Edge));

        Assert.True(shell.Commands.Execute(BlockoutMode.ToggleMeshCommand));
        Assert.Equal(BlockoutElement.Object, mode.Element);

        // ⚠ Edge, not Face. Tab is "in and out of the mesh" in every tool that has it, and coming
        // back out into a different element mode from the one you went in with is the version of this
        // that makes people stop using the key.
        Assert.True(shell.Commands.Execute(BlockoutMode.ToggleMeshCommand));
        Assert.Equal(BlockoutElement.Edge, mode.Element);
    }

    [Fact]
    public void Leaving_the_mode_comes_back_out_to_Object() {
        var (shell, mode) = Built();
        using var _shell = shell;

        shell.Modes.Activate(BlockoutMode.ModeId);
        shell.Commands.Execute(BlockoutMode.ElementCommand(BlockoutElement.Vertex));

        shell.Modes.Activate(SelectMode.ModeId);
        Assert.Equal(BlockoutElement.Object, mode.Element);
    }

    [Fact]
    public void The_mode_bar_carries_the_four_element_modes_only_while_blockout_is_active() {
        var (shell, _) = Built();
        using var _shell = shell;

        Assert.Collection(shell.Modes.Bar(), entry => Assert.IsType<ToolbarGroup>(entry));

        shell.Modes.Activate(BlockoutMode.ModeId);

        var bar = shell.Modes.Bar();

        Assert.Equal(3, bar.Count);
        Assert.IsType<ToolbarSeparator>(bar[1]);

        Assert.Equal(
            [.. Enum.GetValues<BlockoutElement>().Select(BlockoutMode.ElementCommand)],
            Assert.IsType<ToolbarGroup>(bar[2]).CommandIds
        );
    }

    [Fact]
    public void The_mode_refuses_every_viewport_event_because_there_is_nothing_to_edit_yet() {
        var (shell, mode) = Built();
        using var _shell = shell;

        shell.Modes.Activate(BlockoutMode.ModeId);

        // Doc 24's P0 ships a Blockout mode "that so far only owns its keys". A press in the pane
        // still picks an entity and the gizmo still has the drag, which is what makes entering the
        // mode safe before P1 exists.
        Assert.False(mode.Pointer(new PointerEvent()));
        Assert.False(mode.Key(new KeyEvent()));
    }

    [Fact]
    public void Unregistering_the_mode_takes_its_commands_with_it() {
        var (shell, _) = Built();
        using var _shell = shell;

        Assert.True(shell.Modes.Remove(BlockoutMode.ModeId));

        foreach (var element in Enum.GetValues<BlockoutElement>()) {
            Assert.False(shell.Commands.TryGet(BlockoutMode.ElementCommand(element), out _));
        }

        Assert.False(shell.Commands.TryGet(BlockoutMode.ToggleMeshCommand, out _));
    }
}
