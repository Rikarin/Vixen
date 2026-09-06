// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>A chord parsed, adapted and written down with no editor assembly in the graph.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Which assembly this file is in is half of what it asserts.</b> <c>KeyChord</c> spent
///         four sessions in <c>Editor/Vixen.Editor.Ui/Commands/</c>, so a non-editor application
///         could draw <c>⌘S</c> beside a menu item — <c>MenuItem.ShowShortcut</c> — and had no type
///         to store the chord it was drawing. <c>Vixen.Ui.Tests</c> references <c>Vixen.Ui</c> and
///         nothing under <c>Editor/</c>, which is why these lines compiling is the layering claim.
///     </para>
///     <para>
///         The behaviour below is the part a sabotage can redden. The involution and the
///         bare-modifier filter are the two rules whose absence is silent: one makes a Mac answer to
///         both ⌘S and a real Ctrl+S, the other makes every Ctrl-prefixed shortcut reachable by
///         pressing Ctrl on its own.
///     </para>
/// </remarks>
public class KeyChordTests {
    [Fact]
    public void A_chord_round_trips_through_the_text_a_keymap_file_holds() {
        var chord = new KeyChord(InputKey.S, ModifierKeys.Control | ModifierKeys.Shift);

        Assert.Equal("Ctrl+Shift+S", chord.Save());
        Assert.True(KeyChord.TryParse(chord.Save(), out var read));
        Assert.Equal(chord, read);
    }

    [Fact]
    public void An_unparseable_line_loses_that_binding_and_not_the_file() {
        Assert.False(KeyChord.TryParse("Hyper+S", out var chord));
        Assert.Equal(KeyChord.None, chord);
        Assert.False(chord.IsBound);
    }

    // ⚠ Both directions, which is the rule that cannot be checked by mapping one. Ctrl+S stored
    // becomes ⌘S pressed, AND ⌘S stored becomes Ctrl+S pressed — a swap that only went one way
    // would leave a Mac answering to a real Ctrl+S, which on that machine means something else.
    [Fact]
    public void The_platform_swap_exchanges_control_and_the_primary_modifier_both_ways() {
        var stored = new KeyChord(InputKey.S, ModifierKeys.Control | ModifierKeys.Shift);
        var pressed = stored.ForPlatform(ModifierKeys.Meta);

        Assert.Equal(new KeyChord(InputKey.S, ModifierKeys.Meta | ModifierKeys.Shift), pressed);
        Assert.Equal(stored, pressed.ForPlatform(ModifierKeys.Meta));
        Assert.Equal(new KeyChord(InputKey.S, ModifierKeys.Control), new KeyChord(InputKey.S, ModifierKeys.Meta).ForPlatform(ModifierKeys.Meta));
    }

    [Fact]
    public void The_swap_is_the_identity_where_control_is_already_the_primary_modifier() {
        var chord = new KeyChord(InputKey.S, ModifierKeys.Control);

        Assert.Equal(chord, chord.ForPlatform(ModifierKeys.Control));
    }

    // ⚠ Holding Ctrl raises a KeyEvent whose key IS Ctrl and whose modifiers already include it, so
    // without the filter every Ctrl-prefixed shortcut would be matched by a binding of Ctrl+Ctrl
    // that nobody wrote — a chord that can never be typed and therefore never tested.
    [Fact]
    public void A_modifier_pressed_on_its_own_is_not_a_chord() {
        var held = new KeyEvent { Key = InputKey.LeftControl, Modifiers = ModifierKeys.Control };

        Assert.Equal(KeyChord.None, KeyChord.Of(held));
        Assert.Equal(
            new KeyChord(InputKey.S, ModifierKeys.Control),
            KeyChord.Of(new KeyEvent { Key = InputKey.S, Modifiers = ModifierKeys.Control })
        );
    }

    // ⚠ The two Mac forms are asserted as pure functions and the process-wide `Formatter` is not
    // touched: `ShortcutFormatTests.The_default_formatter_is_the_unadapted_one` reads that static,
    // and xunit runs the two classes in parallel — a test here that installed a formatter would
    // redden a test over there, on a machine and at a moment neither file names.
    [Fact]
    public void The_platform_forms_write_the_modifiers_in_the_platforms_order() {
        var all = ModifierKeys.Meta | ModifierKeys.Shift | ModifierKeys.Alt | ModifierKeys.Control;

        Assert.Equal("⌃⌥⇧⌘S", KeyChord.MacFormat(InputKey.S, all));
        Assert.Equal("Ctrl+Opt+Shift+Cmd+S", KeyChord.MacWords(InputKey.S, all));

        // Both end at the key-name table rather than at the enum member, which is the whole reason
        // `ShortcutFormat.Name` is public.
        Assert.Equal("⌘1", KeyChord.MacFormat(InputKey.Number1, ModifierKeys.Meta));
    }

    [Fact]
    public void An_unbound_command_describes_as_nothing_rather_than_as_a_key_called_Unknown() {
        Assert.Equal(string.Empty, KeyChord.None.Describe());
        Assert.Equal(string.Empty, KeyChord.None.Save());
    }
}
