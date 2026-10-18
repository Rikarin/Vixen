// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>That an editing chord nothing took is sent along the command route, and the menu's verb runs.</summary>
/// <remarks>
///     ⚠ <b>Every assertion is through <see cref="UiDocument.Dispatch(KeyEvent)" /></b>, the call a
///     platform host makes, and never through <see cref="CommandRoute.Execute" />: the route always
///     worked from a menu item. What was missing was a keystroke reaching it — a chord that climbed
///     out of the focus arrived at the root and stopped there, because only a hand-built
///     <c>CommandDispatcher</c> ever turned one into an id (#642, #650).
/// </remarks>
public class EditingChordRouteTests {
    static UiDocument Document(EditingKeymap keymap = EditingKeymap.Windows) =>
        new(200f, 200f) { RoutesEditingChords = true, EditingKeymap = keymap };

    static UiElement View(UiElement parent) {
        var element = parent.Add("div");
        element.Focusable = true;

        return element;
    }

    static KeyEvent Chord(InputKey key, ModifierKeys modifiers, bool repeat = false) =>
        new() { Key = key, Modifiers = modifiers, Action = KeyAction.Pressed, IsRepeat = repeat };

    [Fact]
    public void A_copy_chord_runs_the_focused_view_s_copy() {
        using var document = Document();
        var list = View(document.Root);
        var copied = 0;

        list.AddCommandHandler("edit.copy", () => copied++);
        document.Focus(list);

        var args = Chord(InputKey.C, ModifierKeys.Control);
        document.Dispatch(args);

        Assert.Equal(1, copied);
        Assert.True(args.Handled);
    }

    [Fact]
    public void With_nothing_focused_the_chord_falls_through_to_the_window_s_responder() {
        using var document = Document();
        var copied = 0;

        var window = new CommandResponder();
        window.Add("edit.copy", () => copied++);
        document.CommandResponder = window;

        document.Dispatch(Chord(InputKey.C, ModifierKeys.Control));

        Assert.Null(document.Focused);
        Assert.Equal(1, copied);
    }

    [Fact]
    public void The_nearest_responder_s_refusal_is_final_for_the_chord_as_for_the_menu() {
        using var document = Document();
        var field = View(document.Root);
        var nearest = 0;
        var window = 0;

        field.AddCommandHandler("edit.copy", () => nearest++, () => false);

        var responder = new CommandResponder();
        responder.Add("edit.copy", () => window++);
        document.CommandResponder = responder;

        document.Focus(field);

        var args = Chord(InputKey.C, ModifierKeys.Control);
        document.Dispatch(args);

        Assert.Equal(0, nearest);
        Assert.Equal(0, window);

        // Handled all the same: the chord was Copy's, and a refusal is not "not mine".
        Assert.True(args.Handled);
    }

    [Fact]
    public void Undo_outside_a_text_field_reaches_the_root_s_manager() {
        using var document = Document();
        var button = View(document.Root);
        var manager = new UndoManager();
        var value = 1;

        document.UndoManager = manager;
        UndoCommands.Install(document.Root);
        manager.Register("Set", () => value = 1, () => value = 2);
        value = 2;

        document.Focus(button);
        document.Dispatch(Chord(InputKey.Z, ModifierKeys.Control));

        Assert.Equal(1, value);

        document.Dispatch(Chord(InputKey.Y, ModifierKeys.Control));

        Assert.Equal(2, value);
    }

    [Fact]
    public void A_chord_nothing_answers_is_left_for_whoever_else_listens() {
        using var document = Document();
        var view = View(document.Root);
        document.Focus(view);

        var args = Chord(InputKey.V, ModifierKeys.Control);
        document.Dispatch(args);

        Assert.False(args.Handled);
    }

    [Fact]
    public void A_control_that_took_the_key_itself_is_not_also_given_the_command() {
        using var document = Document();
        var field = View(document.Root);
        var command = 0;

        field.AddCommandHandler("edit.select-all", () => command++);
        field.AddHandler<KeyEvent>((_, args) => args.Handled = true);
        document.Focus(field);

        document.Dispatch(Chord(InputKey.A, ModifierKeys.Control));

        Assert.Equal(0, command);
    }

    [Fact]
    public void Only_the_exact_chord_and_only_the_platform_s_table() {
        using var document = Document(EditingKeymap.MacOs);
        var view = View(document.Root);
        var selected = 0;
        var copied = 0;

        view.AddCommandHandler("edit.select-all", () => selected++);
        view.AddCommandHandler("edit.copy", () => copied++);
        document.Focus(view);

        // ⌃A is the start of the line on a Mac, never Select All.
        document.Dispatch(Chord(InputKey.A, ModifierKeys.Control));

        // ⌘⇧C is not Copy, though the text table would let Shift fall away for it.
        document.Dispatch(Chord(InputKey.C, ModifierKeys.Meta | ModifierKeys.Shift));

        // An auto-repeat is ignored, as the keymap's dispatcher ignores one.
        document.Dispatch(Chord(InputKey.C, ModifierKeys.Meta, repeat: true));

        Assert.Equal(0, selected);
        Assert.Equal(0, copied);

        document.Dispatch(Chord(InputKey.A, ModifierKeys.Meta));
        document.Dispatch(Chord(InputKey.C, ModifierKeys.Meta));

        Assert.Equal(1, selected);
        Assert.Equal(1, copied);
    }

    [Fact]
    public void A_document_that_did_not_ask_for_it_routes_nothing() {
        using var document = new UiDocument(200f, 200f) { EditingKeymap = EditingKeymap.Windows };
        var view = View(document.Root);
        var copied = 0;

        view.AddCommandHandler("edit.copy", () => copied++);
        document.Focus(view);

        var args = Chord(InputKey.C, ModifierKeys.Control);
        document.Dispatch(args);

        // ⚠ The editor's case: its keymap is the only reading of a chord, and the user may have
        // unbound this one.
        Assert.False(document.RoutesEditingChords);
        Assert.Equal(0, copied);
        Assert.False(args.Handled);
    }
}
