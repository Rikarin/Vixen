// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>The keystroke half of <see cref="CommandRoute" />, which used not to exist.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The same verb reached two different handlers depending on how it was invoked.</b>
///         <see cref="CommandChainTests" /> proves the menu leg: a control that registered
///         <c>edit.copy</c> at the focus outranks the editor's global command when something calls
///         <see cref="CommandRoute.Execute" />. The chord leg did not go through the route at all —
///         <see cref="CommandDispatcher" /> turned a chord into an id and then looked that id up in
///         <see cref="CommandRegistry" />, a flat table with no walk in it — so pressing the shortcut
///         printed beside that menu item ran the editor's command instead.
///     </para>
///     <para>
///         ⚠ <b>Nothing in the editor could see the difference, because both legs did something.</b>
///         Clicking Edit ▸ Copy copied the field; pressing ⌘C copied the selected entity. Neither is
///         an error, neither logs, and the only symptom is that a user who learns the shortcut gets
///         a different program from the user who reads the menu.
///     </para>
///     <para>
///         The chords here are pressed as <see cref="KeyChord.Primary" /> and stored as
///         <c>Ctrl</c>, which is the portable spelling the keymap file uses — see
///         <see cref="KeyChord.ForPlatform()" />. Written the other way round the suite would pass on
///         Linux and fail on a Mac.
///     </para>
/// </remarks>
public class ChordRouteTests {
    static StringId Title(string text) => new("test." + text, text);

    [Fact]
    public void A_chord_reaches_the_focused_element_and_not_only_the_menu_item() {
        using var shell = new EditorShell(1280f, 800f);

        var ran = "";
        shell.Commands.Add("edit.copy", Title("Copy"), () => ran = "application");
        shell.Keys.SetDefault("edit.copy", new KeyChord(InputKey.C, ModifierKeys.Control));

        var view = shell.Document.Root.Add("div");
        view.Focusable = true;
        view.AddCommandHandler("edit.copy", () => ran = "view");

        shell.Document.Focus(view);

        var args = Press(shell, InputKey.C);
        Assert.Equal("view", ran);
        Assert.True(args.Handled);

        // And the editor's is what is left when the focus goes away — the same fall-through the menu
        // leg has, reached by the same walk rather than by a second rule written here.
        ran = "";
        shell.Document.Focus(null);

        Assert.True(Press(shell, InputKey.C).Handled);
        Assert.Equal("application", ran);
    }

    [Fact]
    public void An_element_that_refuses_does_not_fall_through_to_the_editor_s_command() {
        using var shell = new EditorShell(1280f, 800f);

        var ran = "";
        var refused = 0;

        shell.Commands.Add("edit.copy", Title("Copy"), () => ran = "application");
        shell.Keys.SetDefault("edit.copy", new KeyChord(InputKey.C, ModifierKeys.Control));
        shell.Dispatcher.Refused += _ => refused++;

        var view = shell.Document.Root.Add("div");
        view.Focusable = true;
        view.AddCommandHandler("edit.copy", () => ran = "view", () => false);

        shell.Document.Focus(view);

        // ⚠ The rule the whole route is for: the nearest responder that *answers* wins, and its
        // predicate is the only one asked. A fall-through here would make an empty text box's ⌘C
        // copy the selected entity instead — worse than nothing happening, because the user cannot
        // see that the verb changed its object.
        var args = Press(shell, InputKey.C);

        Assert.Equal("", ran);
        Assert.True(args.Handled);
        Assert.Equal(1, refused);
    }

    [Fact]
    public void A_control_can_answer_a_chord_for_a_verb_the_editor_never_registered() {
        using var shell = new EditorShell(1280f, 800f);

        var ran = 0;
        shell.Keys.SetDefault("edit.select-all", new KeyChord(InputKey.A, ModifierKeys.Control));

        var view = shell.Document.Root.Add("div");
        view.Focusable = true;
        view.AddCommandHandler("edit.select-all", () => ran++);

        shell.Document.Focus(view);

        // ⚠ There is no `edit.select-all` in the registry. The old path failed the `TryGet` on the
        // line after the chord resolved, so a verb that only ever lives on controls — which is what
        // Select All is — had no chord at all outside the editor's own command table.
        Assert.True(Press(shell, InputKey.A).Handled);
        Assert.Equal(1, ran);
    }

    [Fact]
    public void An_unbound_chord_is_still_nobody_s_and_the_walk_is_not_run() {
        using var shell = new EditorShell(1280f, 800f);

        var ran = 0;
        var view = shell.Document.Root.Add("div");
        view.Focusable = true;
        view.AddCommandHandler("edit.copy", () => ran++);

        shell.Document.Focus(view);

        // Nothing in the keymap binds this, so there is no id to walk for. The element's handler is
        // reachable by a menu item and by `CommandRoute.Execute`, and a keystroke it was never given
        // must leave it alone — otherwise every registered verb would become a hidden shortcut.
        Assert.False(Press(shell, InputKey.C).Handled);
        Assert.Equal(0, ran);
    }

    [Fact]
    public void An_unmodified_chord_is_not_taken_from_a_code_editor_either() {
        using var shell = new EditorShell(1280f, 800f);

        var ran = 0;
        shell.Commands.Add("scene.frame", Title("Frame Selected"), () => ran++);
        shell.Keys.SetDefault("scene.frame", new KeyChord(InputKey.F, ModifierKeys.None));

        var code = shell.Document.Root.Add<CodeEditor>();
        shell.Document.Focus(code);

        // ⚠ The guard used to read `element is TextField`, and a `CodeEditor` is not one — so a bare
        // `F` typed into the code editor ran the viewport's frame-selection. It only ever looked
        // harmless because the control usually handled the key before the root's handler saw it,
        // which is not a rule, it is an ordering.
        var args = new KeyEvent { Key = InputKey.F, Action = KeyAction.Pressed };
        shell.Dispatcher.Pressed(shell.Document, args);

        Assert.Equal(0, ran);
        Assert.False(args.Handled);
    }

    [Fact]
    public void A_modified_chord_is_still_taken_from_a_code_editor() {
        using var shell = new EditorShell(1280f, 800f);

        var ran = 0;
        shell.Commands.Add("file.save", Title("Save"), () => ran++);
        shell.Keys.SetDefault("file.save", new KeyChord(InputKey.S, ModifierKeys.Control));

        var code = shell.Document.Root.Add<CodeEditor>();
        shell.Document.Focus(code);

        // The other half, and the half that makes the guard narrow rather than a blanket refusal:
        // ⌘S must save while the caret is in the editor, because it is not a letter anybody is
        // typing. Without this the widening would have made every shortcut dead in a text control.
        Assert.True(Press(shell, InputKey.S).Handled);
        Assert.Equal(1, ran);
    }

    static KeyEvent Press(EditorShell shell, InputKey key) {
        var args = new KeyEvent { Key = key, Modifiers = KeyChord.Primary, Action = KeyAction.Pressed };
        shell.Dispatcher.Pressed(shell.Document, args);

        return args;
    }
}
