// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>Ctrl on a PC and Command on a Mac, from one keymap.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A Mac user pressing Ctrl+S expects a terminal control code, not a save.</b> Every
///         application on that machine binds ⌘, and the editor bound Ctrl — so on macOS every
///         shortcut in it was both wrong and in the way.
///     </para>
///     <para>
///         ⚠ <b>The keymap keeps one portable spelling and the two ends adapt.</b> The file, the menu
///         model and the conflict check all say <c>Ctrl+S</c>, so a keymap written on a Mac still
///         loads on Linux; an arriving key event is swapped into that vocabulary and a stored chord
///         is swapped out of it before it is drawn.
///     </para>
///     <para>
///         ⚠ <b>Nothing here sets <see cref="KeyChord.Primary" />.</b> It is process-wide and xunit
///         runs classes in parallel, so a test that set it would change what every test beside it
///         means and the failure would land somewhere else. The swap and the formatting are checked
///         as the pure functions they are; the dispatcher is checked by pressing whatever
///         <c>ForPlatform</c> says this machine's user would press, which is true on all three.
///     </para>
/// </remarks>
public class PlatformKeyTests : IDisposable {
    readonly UiDocument document = new(800f, 600f);
    readonly CommandRegistry commands = new();
    readonly KeyMap keys = new();
    readonly CommandDispatcher dispatcher;

    public PlatformKeyTests() {
        ControlTheme.Install(document);
        dispatcher = new CommandDispatcher(commands, keys);
    }

    public void Dispose() {
        document.Dispose();
        GC.SuppressFinalize(this);
    }

    static StringId Title(string text) => new("test." + text, text);

    [Fact]
    public void The_machines_own_modifier_runs_what_the_keymap_spells_with_control() {
        var saved = 0;

        commands.Add("file.save", Title("Save"), () => saved++);
        keys.SetDefault("file.save", new KeyChord(InputKey.S, ModifierKeys.Control));

        // What a user of *this* machine presses: ⌘S on a Mac, Ctrl+S everywhere else.
        Press(new KeyChord(InputKey.S, ModifierKeys.Control).ForPlatform());

        Assert.Equal(1, saved);
    }

    [Fact]
    public void The_other_modifier_is_not_the_applications_one() {
        var saved = 0;

        commands.Add("file.save", Title("Save"), () => saved++);
        keys.SetDefault("file.save", new KeyChord(InputKey.S, ModifierKeys.Control));

        // ⚠ On a Mac this is a real Ctrl+S, which means something else entirely there and must not
        // save. On a PC the swap is the identity, so this *is* the shortcut — which is why the
        // assertion is written against the swap rather than against a literal.
        var other = new KeyChord(InputKey.S, KeyChord.Primary).ForPlatform();

        Press(other);
        Assert.Equal(KeyChord.Primary == ModifierKeys.Control ? 1 : 0, saved);
    }

    [Fact]
    public void The_other_modifiers_ride_along_untouched() {
        var ran = 0;

        commands.Add("file.save-as", Title("Save As"), () => ran++);
        keys.SetDefault("file.save-as", new KeyChord(InputKey.S, ModifierKeys.Control | ModifierKeys.Shift));

        Press(new KeyChord(InputKey.S, ModifierKeys.Control | ModifierKeys.Shift).ForPlatform());

        Assert.Equal(1, ran);
    }

    [Fact]
    public void The_swap_exchanges_the_two_and_is_its_own_inverse() {
        var chord = new KeyChord(InputKey.S, ModifierKeys.Control | ModifierKeys.Alt);
        var swapped = chord.ForPlatform(ModifierKeys.Meta);

        // ⚠ Both ways. Mapping ⌘ onto Ctrl and leaving Ctrl alone would make a Mac answer to both.
        Assert.Equal(ModifierKeys.Meta | ModifierKeys.Alt, swapped.Modifiers);
        Assert.Equal(ModifierKeys.Control | ModifierKeys.Alt, new KeyChord(InputKey.S, ModifierKeys.Meta | ModifierKeys.Alt).ForPlatform(ModifierKeys.Meta).Modifiers);

        Assert.Equal(chord, swapped.ForPlatform(ModifierKeys.Meta));
    }

    [Fact]
    public void Where_the_two_are_the_same_modifier_nothing_moves() {
        var chord = new KeyChord(InputKey.S, ModifierKeys.Control);
        Assert.Equal(chord, chord.ForPlatform(ModifierKeys.Control));
    }

    [Fact]
    public void A_mac_writes_a_shortcut_as_glyphs_in_its_own_order() {
        // Glyphs, in the platform's fixed order, with no separators — what its own menus have
        // written since 1984 and what a user reads without stopping.
        Assert.Equal("⇧⌘S", KeyChord.MacFormat(InputKey.S, ModifierKeys.Meta | ModifierKeys.Shift));
        Assert.Equal("⌃⌥⇧⌘S", KeyChord.MacFormat(InputKey.S, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Meta));

        // The key's own name still comes from the control set, which is where the list of exceptions
        // lives — `Number1` is the `1` key.
        Assert.Equal("⌘1", KeyChord.MacFormat(InputKey.Number1, ModifierKeys.Meta));
    }

    /// <summary>
    ///     ⚠ The editor borrows a font from the machine rather than shipping one, and on macOS what
    ///     it finds is Arial — which has none of ⌘ ⇧ ⌥ ⌃. An unmapped codepoint does not draw as a
    ///     box or as nothing: it resolves to whatever glyph zero happens to be, and the menu bar read
    ///     "L+S" for Save. A shortcut nobody can read is worse than one written the long way.
    /// </summary>
    [Fact]
    public void A_face_that_cannot_draw_the_glyphs_gets_the_words() {
        Assert.Equal("Shift+Cmd+S", KeyChord.MacWords(InputKey.S, ModifierKeys.Meta | ModifierKeys.Shift));

        // The platform's names, not the event's: a Mac user reading "Meta+S" has to translate, and
        // the whole point of adapting is that they should not have to.
        Assert.Equal("Ctrl+Opt+Shift+Cmd+S", KeyChord.MacWords(
            InputKey.S,
            ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Meta
        ));
    }

    [Fact]
    public void The_keymap_file_stays_portable() {
        keys.SetDefault("file.save", new KeyChord(InputKey.S, ModifierKeys.Control));
        keys.Bind("file.save", new KeyChord(InputKey.K, ModifierKeys.Control), replace: true);

        // ⚠ `Ctrl+K` on every platform. The swap happens at the keyboard and at the label; what is
        // stored — and what somebody may check into a repository their colleagues open on Linux —
        // is the one portable spelling.
        Assert.Contains("Ctrl+K", keys.Save(), StringComparison.Ordinal);
        Assert.DoesNotContain("Meta+K", keys.Save(), StringComparison.Ordinal);
    }

    void Press(KeyChord chord) =>
        dispatcher.Pressed(
            document,
            new KeyEvent { Key = chord.Key, Modifiers = chord.Modifiers, Action = KeyAction.Pressed }
        );
}
