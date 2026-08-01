// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Blockout;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Vixen.Input;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 24's B2, end to end: the mode bar, and which command a digit means in the viewport.</summary>
public class EditorModeTests {
    [Fact]
    public void The_editor_ships_two_modes_and_starts_in_Select() {
        using var fixture = EditorSession.Start();

        Assert.Equal(
            [SelectMode.ModeId, BlockoutMode.ModeId],
            fixture.Shell.Modes.Modes.Select(mode => mode.Id)
        );

        Assert.Equal(SelectMode.ModeId, fixture.Shell.Modes.Active?.Id);

        // ⚠ Null while Select is active, which is what makes the shipped editor's viewport behave
        // exactly as it did before modes existed.
        Assert.Null(fixture.Shell.Modes.Context);
    }

    [Fact]
    public void The_mode_bar_carries_a_button_per_mode_and_the_active_modes_tools() {
        using var fixture = EditorSession.Start();

        Assert.Collection(fixture.Shell.Modes.Bar(), entry => Assert.IsType<ToolbarGroup>(entry));

        // And it is on screen rather than only in the model: one segmented group holding the two
        // mode buttons, in the chrome between the menu bar and the toolbar.
        fixture.Frames(1);
        Assert.NotEmpty(fixture.Shell.ModeBar.Strip.Children);

        fixture.Run(EditorModes.ModeCommand(BlockoutMode.ModeId));

        // Blockout's own strip joins it: the four element modes as one segmented control.
        Assert.Equal(3, fixture.Shell.Modes.Bar().Count);

        fixture.Frames(1);
        Assert.Equal(3, fixture.Shell.ModeBar.Strip.Children.Count);
    }

    [Fact]
    public void Entering_blockout_takes_the_digits_from_the_view_bookmarks_and_leaving_gives_them_back() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");
        fixture.Frames(2);

        var chord = new KeyChord(InputKey.Number2, ModifierKeys.None);
        var keys = fixture.Shell.Keys;

        // Doc 20's B2 has `1..9` recalling a view bookmark, globally. Doc 24's B2 has `2` meaning
        // vertex mode. Both are right, and neither had to move: the mode is what arbitrates.
        Assert.Equal("scene.bookmark-go-2", keys.CommandFor(chord));

        fixture.Run(EditorModes.ModeCommand(BlockoutMode.ModeId));

        Assert.Equal(BlockoutMode.BlockoutContext, fixture.Shell.Context);

        Assert.Equal(
            BlockoutMode.ElementCommand(BlockoutElement.Vertex),
            keys.CommandFor(chord, fixture.Shell.Context)
        );

        fixture.Run(EditorModes.ModeCommand(SelectMode.ModeId));

        Assert.Equal("scene", fixture.Shell.Context);
        Assert.Equal("scene.bookmark-go-2", keys.CommandFor(chord, fixture.Shell.Context));
    }

    [Fact]
    public void The_element_verbs_are_listed_and_rebindable_before_anybody_enters_the_mode() {
        using var fixture = EditorSession.Start();

        var id = BlockoutMode.ElementCommand(BlockoutElement.Face);

        // In the registry, in the palette, in the keybinding editor — and disabled, which is doc 20's
        // "a verb that is not reachable right now is visibly not reachable".
        Assert.True(fixture.Shell.Commands.TryGet(id, out _));
        Assert.False(fixture.CanRun(id));
        Assert.True(fixture.Shell.Keys.ChordFor(id).IsBound);

        fixture.Run(EditorModes.ModeCommand(BlockoutMode.ModeId));
        Assert.True(fixture.CanRun(id));
    }

    [Fact]
    public void A_press_in_the_pane_reports_the_active_modes_context() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");
        fixture.Run(EditorModes.ModeCommand(BlockoutMode.ModeId));
        fixture.Frames(2);

        // Clicking the outliner is still the outliner, which is the half a mode must not break: a
        // mode says what a click in the *viewport* means and has no opinion about a tree row.
        var outliner = fixture.Shell.Workspace.Open("hierarchy")!;
        fixture.Frames(1);

        fixture.Click(outliner);
        Assert.Equal("scene", fixture.Shell.Context);

        fixture.Click(fixture.Viewport!.Control);
        Assert.Equal(BlockoutMode.BlockoutContext, fixture.Shell.Context);
    }
}
