// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>The registry, the keymap and what turns a keystroke into one of them.</summary>
public class CommandTests {
    static StringId Title(string text) => new("test." + text, text);

    [Fact]
    public void Registering_an_id_twice_throws() {
        var registry = new CommandRegistry();
        registry.Add("file.save", Title("Save"), () => { });

        // A silent replace would let one command take over another's id by naming it; a silent
        // ignore would leave the second quietly dead. Both are found weeks later.
        Assert.Throws<ArgumentException>(() => registry.Add("file.save", Title("Save As"), () => { }));
    }

    [Fact]
    public void A_disabled_command_does_not_run_however_it_is_reached() {
        var ran = 0;
        var enabled = false;

        var registry = new CommandRegistry();
        registry.Add(new EditorCommand("edit.undo", Title("Undo"), () => ran++) { Enablement = () => enabled });

        Assert.False(registry.CanExecute("edit.undo"));
        Assert.False(registry.Execute("edit.undo"));
        Assert.Equal(0, ran);

        enabled = true;

        Assert.True(registry.Execute("edit.undo"));
        Assert.Equal(1, ran);
    }

    [Fact]
    public void An_unknown_id_is_false_rather_than_a_throw() {
        var registry = new CommandRegistry();

        // A menu model outlives the plugin whose commands it names, exactly as a saved layout
        // outlives its panels.
        Assert.False(registry.CanExecute("plugin.gone"));
        Assert.False(registry.Execute("plugin.gone"));
    }

    [Fact]
    public void A_chord_round_trips_through_text() {
        var chord = new KeyChord(InputKey.S, ModifierKeys.Control | ModifierKeys.Shift);

        Assert.Equal("Ctrl+Shift+S", chord.Save());
        Assert.True(KeyChord.TryParse(chord.Save(), out var parsed));
        Assert.Equal(chord, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Hyper+S")]
    [InlineData("Ctrl+NotAKey")]
    public void A_chord_that_will_not_parse_is_dropped_rather_than_thrown(string text) {
        Assert.False(KeyChord.TryParse(text, out var chord));
        Assert.False(chord.IsBound);
    }

    [Fact]
    public void A_modifier_pressed_alone_is_not_a_chord() {
        var args = new KeyEvent {
            Key = InputKey.LeftControl,
            Action = KeyAction.Pressed,
            Modifiers = ModifierKeys.Control
        };

        // Otherwise every Ctrl shortcut would be reachable by pressing Ctrl, matched against a
        // `Ctrl+Ctrl` binding nobody wrote and nobody could type.
        Assert.False(KeyChord.Of(args).IsBound);
    }

    [Fact]
    public void An_occupied_chord_is_refused_and_says_who_has_it() {
        var keys = new KeyMap();
        var chord = new KeyChord(InputKey.S, ModifierKeys.Control);

        Assert.Equal(BindResult.Bound, keys.SetDefault("file.save", chord));
        Assert.Equal(BindResult.Conflict, keys.Bind("file.save-all", chord));

        Assert.Equal("file.save", keys.CommandFor(chord));
        Assert.False(keys.ChordFor("file.save-all").IsBound);

        Assert.Equal(BindResult.Replaced, keys.Bind("file.save-all", chord, replace: true));
        Assert.Equal("file.save-all", keys.CommandFor(chord));
        Assert.False(keys.ChordFor("file.save").IsBound);
    }

    [Fact]
    public void Only_the_users_overrides_are_saved() {
        var keys = new KeyMap();
        keys.SetDefault("file.save", new KeyChord(InputKey.S, ModifierKeys.Control));
        keys.SetDefault("edit.undo", new KeyChord(InputKey.Z, ModifierKeys.Control));

        keys.Bind("edit.undo", new KeyChord(InputKey.U, ModifierKeys.Control));

        var text = KeyMapYaml.Write(keys);

        Assert.Contains("edit.undo", text, StringComparison.Ordinal);

        // The point of saving only the overrides: a default that moves in a later version reaches
        // everybody who had not deliberately rebound it.
        Assert.DoesNotContain("file.save", text, StringComparison.Ordinal);

        var reloaded = new KeyMap();
        reloaded.SetDefault("file.save", new KeyChord(InputKey.S, ModifierKeys.Control));
        reloaded.SetDefault("edit.undo", new KeyChord(InputKey.Z, ModifierKeys.Control));
        KeyMapYaml.Read(reloaded, text);

        Assert.Equal(new KeyChord(InputKey.U, ModifierKeys.Control), reloaded.ChordFor("edit.undo"));
        Assert.Equal(new KeyChord(InputKey.S, ModifierKeys.Control), reloaded.ChordFor("file.save"));
        Assert.True(reloaded.IsCustomised("edit.undo"));
        Assert.False(reloaded.IsCustomised("file.save"));
    }

    [Fact]
    public void A_command_deliberately_unbound_stays_unbound_across_a_save() {
        var keys = new KeyMap();
        keys.SetDefault("file.save", new KeyChord(InputKey.S, ModifierKeys.Control));
        keys.Bind("file.save", KeyChord.None);

        var reloaded = new KeyMap();
        reloaded.SetDefault("file.save", new KeyChord(InputKey.S, ModifierKeys.Control));
        KeyMapYaml.Read(reloaded, KeyMapYaml.Write(keys));

        // Written as an empty chord rather than omitted: omitting it would mean "use the default",
        // and the user said the opposite.
        Assert.False(reloaded.ChordFor("file.save").IsBound);
    }

    [Fact]
    public void Reset_puts_every_binding_back() {
        var keys = new KeyMap();
        keys.SetDefault("file.save", new KeyChord(InputKey.S, ModifierKeys.Control));
        keys.Bind("file.save", new KeyChord(InputKey.W, ModifierKeys.Control));

        keys.Reset();

        Assert.Equal(new KeyChord(InputKey.S, ModifierKeys.Control), keys.ChordFor("file.save"));
        Assert.Null(keys.CommandFor(new KeyChord(InputKey.W, ModifierKeys.Control)));
    }

    [Fact]
    public void A_bound_chord_runs_its_command() {
        using var document = new UiDocument(400f, 300f);

        var ran = 0;
        var registry = new CommandRegistry();
        registry.Add("edit.undo", Title("Undo"), () => ran++);

        var keys = new KeyMap();
        keys.SetDefault("edit.undo", new KeyChord(InputKey.Z, ModifierKeys.Control));

        var dispatcher = new CommandDispatcher(registry, keys);
        dispatcher.Attach(document);

        document.Dispatch(Pressed(InputKey.Z));
        Assert.Equal(1, ran);

        // Auto-repeat is not a fresh press: holding the undo chord must undo once.
        document.Dispatch(Pressed(InputKey.Z, repeat: true));
        Assert.Equal(1, ran);
    }

    [Fact]
    public void An_unmodified_chord_is_not_taken_from_a_text_field() {
        using var document = new UiDocument(400f, 300f);
        ControlTheme.Install(document);

        var field = document.Root.Add<TextBox>();

        var framed = 0;
        var registry = new CommandRegistry();
        registry.Add("view.frame", Title("Frame Selected"), () => framed++);

        var keys = new KeyMap();
        keys.SetDefault("view.frame", new KeyChord(InputKey.F, ModifierKeys.None));

        var dispatcher = new CommandDispatcher(registry, keys);
        dispatcher.Attach(document);

        document.Dispatch(Key(InputKey.F, ModifierKeys.None));
        Assert.Equal(1, framed);

        document.Focus(field);
        document.Dispatch(Key(InputKey.F, ModifierKeys.None));

        // Otherwise naming an object would move the camera, and the object would end up called
        // "Cubeaaa".
        Assert.Equal(1, framed);
    }

    [Fact]
    public void A_disabled_command_reached_by_its_chord_is_reported_rather_than_ignored() {
        using var document = new UiDocument(400f, 300f);

        var registry = new CommandRegistry();
        registry.Add(new EditorCommand("edit.undo", Title("Undo"), () => { }) { Enablement = () => false });

        var keys = new KeyMap();
        keys.SetDefault("edit.undo", new KeyChord(InputKey.Z, ModifierKeys.Control));

        var dispatcher = new CommandDispatcher(registry, keys);
        var refused = 0;

        dispatcher.Refused += _ => refused++;
        dispatcher.Attach(document);

        document.Dispatch(Pressed(InputKey.Z));

        // "The shortcut does nothing" and "the shortcut is not bound" have different fixes, so the
        // two must not look the same.
        Assert.Equal(1, refused);
    }

    static KeyEvent Key(InputKey key, ModifierKeys modifiers, bool repeat = false) =>
        new() { Key = key, Action = KeyAction.Pressed, Modifiers = modifiers, IsRepeat = repeat };

    /// <summary>A press of a chord the keymap spells with Control, as this machine's user makes it.</summary>
    /// <remarks>
    ///     ⚠ <b>Not a literal <c>ModifierKeys.Control</c>.</b> The keymap stores one portable
    ///     spelling and the keyboard adapts — ⌘ on a Mac — so a test that pressed Control by hand
    ///     would pass on a PC and assert the opposite of the intended behaviour on a Mac. See
    ///     <c>KeyChord.ForPlatform</c>.
    /// </remarks>
    static KeyEvent Pressed(InputKey key, bool repeat = false) {
        var chord = new KeyChord(key, ModifierKeys.Control).ForPlatform();
        return Key(chord.Key, chord.Modifiers, repeat);
    }
}
