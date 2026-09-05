// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>Writing a chord down, with no element and no document anywhere near it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>That this file compiles is most of the assertion.</b> The modifier order and the
///         key-name table were statics on <c>Vixen.Ui.Controls.KeyboardShortcut</c>, which is a
///         <c>Control</c> — so until they moved, no test in this assembly could format a chord at
///         all, because <c>Vixen.Ui</c> does not reference the control library and must not. Nothing
///         below constructs an element, a document or a font.
///     </para>
///     <para>
///         ⚠ <b>Read rather than replaced.</b> <see cref="Shortcuts.Formatter" /> is process-wide by
///         design, so a test that set it would change what every test running beside it means and
///         the failure would land somewhere else entirely — the same trap <c>KeyChord.Primary</c>
///         records. What is checked is the default and the pure function it defaults to.
///     </para>
/// </remarks>
public class ShortcutsTests {
    /// <summary>The modifier order is the one every other application on the machine writes.</summary>
    /// <remarks>
    ///     Ctrl, Alt, Shift, Meta — not alphabetical and not the flag order. The flags are given out
    ///     of order here on purpose: a menu that wrote them in the order they were passed would look
    ///     wrong beside everything else on the desktop, and reading them back in flag order would
    ///     pass a test that handed them over already sorted.
    /// </remarks>
    [Fact]
    public void A_chord_is_written_in_the_order_menus_write_it() {
        var modifiers = ModifierKeys.Meta | ModifierKeys.Shift | ModifierKeys.Alt | ModifierKeys.Control;

        Assert.Equal("Ctrl+Alt+Shift+Meta+S", Shortcuts.Describe(InputKey.S, modifiers));
        Assert.Equal("Ctrl+Shift+S", Shortcuts.Describe(InputKey.S, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.Equal("S", Shortcuts.Describe(InputKey.S, ModifierKeys.None));
    }

    /// <summary>The keys whose member name is a description rather than a legend.</summary>
    /// <remarks>
    ///     ⚠ The list is the whole reason this is one table rather than a <c>ToString()</c> at each
    ///     call site: <c>Number1</c> is the <c>1</c> key and no menu has ever written it that way,
    ///     and a platform-adapted formatter needs exactly this half — glyphs for the modifiers, the
    ///     ordinary legend for the key.
    /// </remarks>
    [Fact]
    public void A_key_whose_member_name_is_a_description_is_written_by_its_legend() {
        Assert.Equal("1", Shortcuts.Name(InputKey.Number1));
        Assert.Equal("9", Shortcuts.Name(InputKey.Number9));
        Assert.Equal("0", Shortcuts.Name(InputKey.Number0));
        Assert.Equal("`", Shortcuts.Name(InputKey.Grave));
        Assert.Equal("/", Shortcuts.Name(InputKey.Slash));

        // And everything else is the enum's own name, which is what makes the list short.
        Assert.Equal("F5", Shortcuts.Name(InputKey.F5));
        Assert.Equal("Space", Shortcuts.Name(InputKey.Space));
    }

    /// <summary>The process-wide formatter starts as the unadapted default.</summary>
    /// <remarks>
    ///     ⚠ Deliberately not platform-adapted: <c>Vixen.Ui</c> sits below <c>Vixen.Platform</c> and
    ///     does not know what it is running on, so a Mac writing <c>⌘S</c> is something the
    ///     application says — <c>KeyChord.UsePlatformFormat</c> — rather than something this decides.
    ///     A default that guessed would be wrong on the machine that could not check.
    /// </remarks>
    [Fact]
    public void The_default_formatter_is_the_unadapted_one() {
        Assert.Equal(
            Shortcuts.Describe(InputKey.S, ModifierKeys.Meta),
            Shortcuts.Formatter(InputKey.S, ModifierKeys.Meta)
        );

        // "Meta", because that is what the modifier is called in an event. Turning it into "Cmd" is
        // the adaptation, and the adaptation is the application's.
        Assert.Equal("Meta+S", Shortcuts.Describe(InputKey.S, ModifierKeys.Meta));
    }
}
