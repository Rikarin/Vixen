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
///         <see cref="MenuItem.ShowShortcut" /> draws "⌘S" and nothing dispatches it. The keymap
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

        // What the sample does: the row shows the keymap's chord in this machine's spelling.
        var item = document.Root.Add<MenuItem>();
        var shown = keys.ChordFor(DocumentCommands.Save).ForPlatform();
        item.ShowShortcut(shown.Key, shown.Modifiers);

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

    static KeyEvent Press(KeyChord chord, bool repeat = false) =>
        new() { Key = chord.Key, Action = KeyAction.Pressed, Modifiers = chord.Modifiers, IsRepeat = repeat };
}
