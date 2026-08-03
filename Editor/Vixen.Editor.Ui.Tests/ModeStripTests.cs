// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>The mode strip: how it is drawn, and the key that moves along it.</summary>
public class ModeStripTests {
    sealed class Fake(string id, string title, IconArt? art = null) : IEditorMode {
        public string Id => id;

        public StringId Title { get; } = new("mode." + id, title);

        public PathBuilder? Icon => null;

        public IconArt? Art => art;

        public string? Context => null;

        public string? Panel => null;

        public IReadOnlyList<ToolbarEntry> Toolbar => [];

        public void Register(EditorShell shell) { }

        public void Unregister(EditorShell shell) { }

        public bool Pointer(PointerEvent args) => false;

        public bool Key(KeyEvent args) => false;

        public void Activated() { }

        public void Deactivated() { }
    }

    static EditorShell Built() {
        var shell = new EditorShell(1280f, 800f);

        shell.Modes.Add(new Fake("select", "Select", ModeArt.Select));
        shell.Modes.Add(new Fake("blockout", "Blockout", ModeArt.Blockout));
        shell.Modes.Add(new Fake("terrain", "Terrain", ModeArt.Terrain));

        return shell;
    }

    [Fact]
    public void A_modes_art_reaches_the_command_the_strip_is_built_from() {
        using var shell = Built();

        var command = shell.Commands["mode.blockout"];

        Assert.NotNull(command);
        Assert.Same(ModeArt.Blockout, command.Art);
    }

    /// <summary>
    ///     ⚠ <b>The button, not the command.</b> The art reaching the command is half the claim; the
    ///     other half is <c>ToolbarPresenter</c> choosing the captioned face for it — which it does
    ///     only because <c>Art</c> is checked before <c>Icon</c>, and a reordering there would leave
    ///     four buttons that say the right thing and draw nothing.
    /// </summary>
    [Fact]
    public void The_strip_draws_a_picture_and_the_title_together() {
        using var shell = Built();

        shell.Document.Update();

        var buttons = Find<Button>(shell.Document.Root)
            .Where(button => button.LeadingIcon.Art is not null)
            .ToList();

        Assert.Equal(3, buttons.Count);
        Assert.Contains(buttons, button => button.Label == "Blockout");

        // Every one of them says its name as well as showing its picture, which is the difference
        // between this and the icon strip the toolbar draws for verbs.
        Assert.All(buttons, button => Assert.False(string.IsNullOrEmpty(button.Label)));
    }

    [Fact]
    public void Tab_moves_to_the_next_mode_and_wraps() {
        using var shell = Built();

        Assert.Equal("select", shell.Modes.Active?.Id);

        for (var expected = 0; expected < 4; expected++) {
            Press(shell, InputKey.Tab);

            var wanted = new[] { "blockout", "terrain", "select", "blockout" }[expected];

            Assert.Equal(wanted, shell.Modes.Active?.Id);
        }
    }

    /// <summary>
    ///     ⚠ <b>A shell with one mode must not bind Tab to a no-op.</b> Tab is the interface's own
    ///     focus traversal everywhere else, and a command that ran and did nothing would swallow it.
    /// </summary>
    [Fact]
    public void With_one_mode_the_cycle_command_is_disabled() {
        using var shell = new EditorShell(1280f, 800f);

        shell.Modes.Add(new Fake("select", "Select"));

        var next = shell.Commands["mode.next"];

        Assert.NotNull(next);
        Assert.False(next.Enablement?.Invoke());
    }

    static void Press(EditorShell shell, InputKey key) =>
        shell.Document.Dispatch(new KeyEvent { Key = key, Action = KeyAction.Pressed });

    static IEnumerable<T> Find<T>(UiElement element) where T : UiElement {
        if (element is T found) {
            yield return found;
        }

        foreach (var child in element.Children) {
            foreach (var nested in Find<T>(child)) {
                yield return nested;
            }
        }
    }
}
