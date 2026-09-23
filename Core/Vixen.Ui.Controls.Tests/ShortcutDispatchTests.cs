// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>
///     A chord drawn beside a menu item is dispatched to the document that hosts the verb, from an
///     application that is not the editor.
/// </summary>
/// <remarks>
///     <para>
///         <a href="https://github.com/Rikarin/Vixen/issues/650">#650</a> opens with the symptom:
///         <see cref="MenuItem.ShowShortcut(Vixen.Input.InputKey, Vixen.Ui.ModifierKeys)" /> draws
///         "⌘S" and nothing dispatches it. The keymap
///         and the dispatcher are <c>Vixen.Ui.Controls</c> types now, and <c>DocumentCommands</c>
///         is a <c>Vixen.Ui</c> one, so the whole path — a chord, a table, a route, a document — is
///         available to any application; this is the arrangement <c>Samples/02-HelloUi</c> makes,
///         asserted here because there is no test project under <c>Samples/</c>.
///     </para>
///     <para>
///         ⚠ <b>The chord is read out of the same table it is dispatched from</b>, which is the
///         point of drawing it from a keymap rather than by hand: what the row shows and what the
///         keyboard resolves cannot be two different chords.
///     </para>
/// </remarks>
public class ShortcutDispatchTests {
    sealed class Note(string name) : EditableDocument(name) {
        public int Saves { get; private set; }

        protected override bool OnSave() {
            Saves++;

            return true;
        }

        protected override bool OnRevert() => true;
    }

    /// <summary>Pressing the chord the row shows saves the document, once, and only while it is dirty.</summary>
    [Fact]
    public void The_chord_a_menu_item_shows_saves_the_document_that_hosts_the_verb() {
        using var document = new UiDocument(400f, 300f);
        ControlTheme.Install(document);

        var panel = document.Root.Add("div");
        var field = panel.Add("div");
        field.Focusable = true;

        var note = new Note("Material");
        panel.HostedDocument = note;
        DocumentCommands.Install(panel);

        var keys = new KeyMap();
        keys.SetDefault(DocumentCommands.Save, new KeyChord(InputKey.S, ModifierKeys.Control));

        // ⚠ What the sample does, and it is one call rather than three lines since #650: the row
        // shows the keymap's chord in this machine's spelling, read out of the table that answers
        // for it. The chord pressed below is taken back OFF the label, so it is literally what the
        // menu drew rather than a second computation that could agree with the drawing by accident.
        var item = document.Root.Add<MenuItem>();
        var label = item.ShowShortcut(keys, DocumentCommands.Save);

        Assert.NotNull(label);

        var shown = new KeyChord(label.Key, label.Modifiers);

        var dispatcher = new CommandDispatcher(new CommandRegistry(), keys);
        document.Root.AddHandler<KeyEvent>((_, args) => dispatcher.Pressed(document, args));

        document.Focus(field);
        document.Update();

        // Clean: the chord is the document's, so it is taken rather than typed, and nothing is saved.
        var clean = Press(shown);
        document.Dispatch(clean);
        Assert.Equal(0, note.Saves);
        Assert.True(clean.Handled);

        note.MarkDirty();
        document.Update();

        document.Dispatch(Press(shown));
        Assert.Equal(1, note.Saves);
        Assert.False(note.IsDirty.Value);

        // Held ⌘S is one save.
        document.Dispatch(Press(shown, repeat: true));
        Assert.Equal(1, note.Saves);
    }

    /// <summary>A chord the table does not hold is nobody's, and falls through to be typed.</summary>
    /// <remarks>
    ///     The other half of the assertion above: a dispatcher that handled every Control chord
    ///     would pass it while eating every shortcut a text box wanted.
    /// </remarks>
    [Fact]
    public void A_chord_the_keymap_does_not_hold_is_left_alone() {
        using var document = new UiDocument(400f, 300f);

        var panel = document.Root.Add("div");
        var note = new Note("Material");
        panel.HostedDocument = note;
        DocumentCommands.Install(panel);

        var keys = new KeyMap();
        keys.SetDefault(DocumentCommands.Save, new KeyChord(InputKey.S, ModifierKeys.Control));

        var dispatcher = new CommandDispatcher(new CommandRegistry(), keys);
        document.Root.AddHandler<KeyEvent>((_, args) => dispatcher.Pressed(document, args));
        document.Update();

        note.MarkDirty();

        var other = Press(new KeyChord(InputKey.T, ModifierKeys.Control).ForPlatform());
        document.Dispatch(other);

        Assert.False(other.Handled);
        Assert.Equal(0, note.Saves);
    }

    /// <summary>
    ///     ⚠ A command the keymap does not bind draws nothing, where the hand-written form drew the
    ///     word <c>Unknown</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The defect the <see cref="KeyMap" /> overload exists for, and it was live in the
    ///         editor.</b> <c>SceneMenus</c> guarded with <c>if (keys.ChordFor(id) is { } chord)</c>
    ///         — <see cref="KeyChord" /> is a struct, so that pattern matches
    ///         <see cref="KeyChord.None" /> exactly as well as a real chord and the guard refused
    ///         nothing. <c>ShowShortcut(InputKey.Unknown, None)</c> then asked the formatter for a
    ///         name, and the last arm of <c>ShortcutFormat.Name</c> is <c>key.ToString()</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The second half asserts a size rather than a style.</b> "Draws nothing" is the
    ///         claim, and a <see langword="null" /> return beside a label still reading
    ///         <c>Ctrl+S</c> would satisfy a test about the return value and be the bug — so the
    ///         row is laid out and the label is required to occupy no width.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it goes back, which nothing asserted until it did.</b> Hiding is
    ///         <c>display: none</c> on a part that outlives the hiding, so the only thing that can
    ///         ever show it again is the <c>display: flex</c>
    ///         <see cref="MenuItem.ShowShortcut(Vixen.Input.InputKey, Vixen.Ui.ModifierKeys)" />
    ///         writes — and a suite that stopped at Clear was green with that line deleted. Clear
    ///         then re-bind is the keybinding editor's own round trip and the one path where a row
    ///         can stay blank for the rest of the session.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_command_the_keymap_does_not_bind_draws_no_chord() {
        using var document = new UiDocument(400f, 300f);
        ControlTheme.Install(document);

        var keys = new KeyMap();
        keys.SetDefault(DocumentCommands.Save, new KeyChord(InputKey.S, ModifierKeys.Control));

        var item = document.Root.Add<MenuItem>();

        // Never bound: no label is created at all, which is what "Unknown" used to be instead of.
        Assert.Null(item.ShowShortcut(keys, "file.new"));
        Assert.Null(item.Shortcut);

        Assert.NotNull(item.ShowShortcut(keys, DocumentCommands.Save));
        document.Update();

        Assert.True(item.Shortcut!.Width > 0f, "the bound chord drew nothing");

        // And a row that had a chord loses it when the command is unbound, rather than going on
        // showing a key it no longer answers to — the keybinding editor's Clear, which is
        // `Bind(id, KeyChord.None, replace: true)`.
        keys.Bind(DocumentCommands.Save, KeyChord.None, replace: true);

        Assert.Null(item.ShowShortcut(keys, DocumentCommands.Save));
        document.Update();

        Assert.Equal(0f, item.Shortcut!.Width);

        // ⚠ And back: Clear is undoable, so the label the hide left in place has to be shown again
        // by the chord-drawing overload rather than by anything the hiding remembers. Without the
        // `display: flex` that overload writes, the row stays blank for the rest of the session and
        // every assertion above still passes.
        keys.Bind(DocumentCommands.Save, new KeyChord(InputKey.S, ModifierKeys.Control), replace: true);

        Assert.NotNull(item.ShowShortcut(keys, DocumentCommands.Save));
        document.Update();

        Assert.True(item.Shortcut!.Width > 0f, "the re-bound chord stayed hidden");
    }

    static KeyEvent Press(KeyChord chord, bool repeat = false) =>
        new() { Key = chord.Key, Action = KeyAction.Pressed, Modifiers = chord.Modifiers, IsRepeat = repeat };
}
