// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>The mode seam of doc 20's A1, driven without an application around it.</summary>
public class ModeTests {
    /// <summary>A mode that records what happened to it and claims whatever it is told to.</summary>
    sealed class Probe(string id, string? context = null, string? panel = null) : IEditorMode {
        public string Id => id;
        public StringId Title { get; } = new("test.mode." + id, id);
        public PathBuilder? Icon => null;
        public string? Context => context;
        public string? Panel => panel;

        public IReadOnlyList<ToolbarEntry> Toolbar { get; init; } = [];

        public int Registered { get; private set; }
        public int Unregistered { get; private set; }
        public int Entered { get; private set; }
        public int Left { get; private set; }

        /// <summary>Whether <see cref="Pointer" /> and <see cref="Key" /> take what they are given.</summary>
        public bool Claims { get; set; }

        public void Register(EditorShell shell) => Registered++;
        public void Unregister(EditorShell shell) => Unregistered++;
        public void Activated() => Entered++;
        public void Deactivated() => Left++;

        public bool Pointer(PointerEvent args) => Claims;
        public bool Key(KeyEvent args) => Claims;
    }

    [Fact]
    public void A_shell_with_no_modes_has_no_mode_bar() {
        using var shell = new EditorShell(1280f, 800f);

        Assert.Empty(shell.Modes.Modes);
        Assert.Null(shell.Modes.Active);
        Assert.Null(shell.Modes.Context);

        // Doc 20 ships the seam with one mode so that "nothing depends on the mode set being final".
        // A shell with none of them is the sample, the test and the thumbnail renderer, and it has to
        // look exactly as it did before modes existed.
        Assert.Empty(shell.Modes.Bar());
    }

    [Fact]
    public void The_first_mode_registered_is_the_one_the_editor_starts_in() {
        using var shell = new EditorShell(1280f, 800f);

        var select = new Probe("select");
        var second = new Probe("blockout", "blockout");

        shell.Modes.Add(select);
        shell.Modes.Add(second);

        Assert.Same(select, shell.Modes.Active);
        Assert.Equal(1, select.Entered);
        Assert.Equal(0, second.Entered);

        // Both registered, whether or not they were ever entered: a mode's verbs belong in the
        // keybinding editor before somebody has entered the mode once.
        Assert.Equal(1, select.Registered);
        Assert.Equal(1, second.Registered);
    }

    [Fact]
    public void Every_mode_gets_a_button_and_the_active_one_is_the_checked_one() {
        using var shell = new EditorShell(1280f, 800f);

        shell.Modes.Add(new Probe("select"));
        shell.Modes.Add(new Probe("blockout", "blockout"));

        Assert.True(shell.Commands.TryGet(EditorModes.ModeCommand("select"), out var select));
        Assert.True(shell.Commands.TryGet(EditorModes.ModeCommand("blockout"), out var blockout));

        Assert.True(select!.IsChecked);
        Assert.False(blockout!.IsChecked);

        shell.Commands.Execute(blockout.Id);

        Assert.False(select.IsChecked);
        Assert.True(blockout.IsChecked);
    }

    [Fact]
    public void Re_entering_the_mode_you_are_in_does_nothing() {
        using var shell = new EditorShell(1280f, 800f);

        var select = new Probe("select");
        shell.Modes.Add(select);

        // The mode bar's buttons are ordinary commands and a command runs whenever it is clicked, so
        // pressing the button of the mode you are already in must not put a tool through Deactivated
        // and back — which is how a half-finished gesture gets dropped by a click that did nothing.
        Assert.True(shell.Modes.Activate("select"));
        Assert.Equal(1, select.Entered);
        Assert.Equal(0, select.Left);
    }

    [Fact]
    public void Switching_modes_leaves_one_and_enters_the_other() {
        using var shell = new EditorShell(1280f, 800f);

        var select = new Probe("select");
        var blockout = new Probe("blockout", "blockout");

        shell.Modes.Add(select);
        shell.Modes.Add(blockout);

        Assert.True(shell.Modes.Activate("blockout"));

        Assert.Equal(1, select.Left);
        Assert.Equal(1, blockout.Entered);
        Assert.Equal("blockout", shell.Modes.Context);
    }

    [Fact]
    public void Removing_the_active_mode_falls_back_rather_than_leaving_none() {
        using var shell = new EditorShell(1280f, 800f);

        var select = new Probe("select");
        var blockout = new Probe("blockout", "blockout");

        shell.Modes.Add(select);
        shell.Modes.Add(blockout);
        shell.Modes.Activate("blockout");

        // This is a plugin being unloaded while the user is in its mode. A viewport whose input means
        // a mode that is no longer loaded is not a state any gesture knows how to be in.
        Assert.True(shell.Modes.Remove("blockout"));

        Assert.Same(select, shell.Modes.Active);
        Assert.Equal(1, blockout.Left);
        Assert.Equal(1, blockout.Unregistered);
        Assert.Null(shell.Modes.Context);

        // And the button goes with it, or the mode bar keeps a segment that enters nothing.
        Assert.False(shell.Commands.TryGet(EditorModes.ModeCommand("blockout"), out _));
    }

    [Fact]
    public void A_mode_that_names_a_panel_opens_it_and_closes_it_again() {
        using var shell = new EditorShell(1280f, 800f);

        shell.RegisterPanel("tools", new StringId("test.panel.tools", "Tools"), _ => { });
        shell.RegisterLayout("Default", new StringId("test.layout", "Default"), () => LayoutPresets.Standard([], [], []));
        shell.Workspace.Reset();

        shell.Modes.Add(new Probe("select"));
        shell.Modes.Add(new Probe("sculpt", panel: "tools"));

        Assert.False(shell.Workspace.IsOpen("tools"));

        shell.Modes.Activate("sculpt");
        Assert.True(shell.Workspace.IsOpen("tools"));

        shell.Modes.Activate("select");
        Assert.False(shell.Workspace.IsOpen("tools"));
    }

    [Fact]
    public void The_mode_bar_is_the_buttons_then_the_active_modes_own_strip() {
        using var shell = new EditorShell(1280f, 800f);

        shell.Commands.Add("sculpt.brush", new StringId("test.brush", "Brush"), () => { });

        shell.Modes.Add(new Probe("select"));
        shell.Modes.Add(new Probe("sculpt") { Toolbar = [new ToolbarButton("sculpt.brush")] });

        // Select has no toolbar, so the strip is the mode buttons and nothing else — not a separator
        // with an empty section after it.
        Assert.Collection(shell.Modes.Bar(), entry => Assert.IsType<ToolbarGroup>(entry));

        shell.Modes.Activate("sculpt");

        Assert.Collection(
            shell.Modes.Bar(),
            entry => Assert.IsType<ToolbarGroup>(entry),
            entry => Assert.IsType<ToolbarSeparator>(entry),
            entry => Assert.Equal("sculpt.brush", Assert.IsType<ToolbarButton>(entry).CommandId)
        );

        Assert.Equal(["sculpt.brush"], shell.ModeBar.Items.Where(id => id is not null));
    }

    [Fact]
    public void A_mode_claims_a_chord_another_command_already_has_without_taking_it() {
        using var shell = new EditorShell(1280f, 800f);

        shell.Modes.Add(new Probe("select"));
        shell.Modes.Add(new Probe("blockout", "blockout"));

        // The bookmark: doc 20's B2 gives 1..9 to view-bookmark recall, with no context at all.
        shell.Commands.Add("scene.bookmark-go-1", new StringId("test.bookmark", "View 1"), () => { });
        shell.Keys.SetDefault("scene.bookmark-go-1", new KeyChord(InputKey.Number1, ModifierKeys.None));

        // The mode's: doc 24's B2 gives 1 to vertex mode, in the blockout context.
        shell.Commands.Add(
            new EditorCommand("blockout.element.vertex", new StringId("test.vertex", "Vertex"), () => { }) {
                Context = "blockout"
            }
        );

        // ⚠ Bound rather than refused, which is the whole claim. Two commands, one key, two contexts.
        Assert.Equal(
            BindResult.Bound,
            shell.Keys.SetDefault("blockout.element.vertex", new KeyChord(InputKey.Number1, ModifierKeys.None))
        );

        var chord = new KeyChord(InputKey.Number1, ModifierKeys.None);

        Assert.Equal("scene.bookmark-go-1", shell.Keys.CommandFor(chord, "scene"));
        Assert.Equal("blockout.element.vertex", shell.Keys.CommandFor(chord, "blockout"));
        Assert.Equal("scene.bookmark-go-1", shell.Keys.CommandFor(chord));
    }

    [Fact]
    public void Registering_a_mode_twice_is_refused() {
        using var shell = new EditorShell(1280f, 800f);

        shell.Modes.Add(new Probe("select"));
        Assert.Throws<ArgumentException>(() => shell.Modes.Add(new Probe("select")));
    }
}
