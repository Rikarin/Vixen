// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>Writing a chord down with no document, no element and no controls library anywhere.</summary>
/// <remarks>
///     ⚠ <b>The absence is the assertion.</b> This file was impossible to write until the formatter
///     and the key-name table stopped being statics on a <c>Control</c>: <c>Vixen.Ui.Tests</c> does
///     not reference <c>Vixen.Ui.Controls</c>, so a test that could name <c>KeyboardShortcut</c> would
///     be evidence the split had not happened. That is what blocked a keymap from living below the
///     controls library, which is what #650's move needs.
/// </remarks>
public class ShortcutFormatTests {
    [Fact]
    public void A_chord_is_written_with_no_element_in_sight() {
        Assert.Equal("Ctrl+Shift+S", ShortcutFormat.Describe(InputKey.S, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.Equal("S", ShortcutFormat.Describe(InputKey.S, ModifierKeys.None));
    }

    /// <summary>Ctrl, Alt, Shift, Meta — the order Windows, GTK and Qt all write, not the flag order.</summary>
    [Fact]
    public void The_modifier_order_is_the_platforms_and_not_the_enums() {
        var all = ModifierKeys.Meta | ModifierKeys.Shift | ModifierKeys.Alt | ModifierKeys.Control;

        Assert.Equal("Ctrl+Alt+Shift+Meta+A", ShortcutFormat.Describe(InputKey.A, all));
    }

    /// <summary>The handful of keys whose enum name is a description rather than a legend.</summary>
    [Theory]
    [InlineData(InputKey.Number1, "1")]
    [InlineData(InputKey.Number9, "9")]
    [InlineData(InputKey.Number0, "0")]
    [InlineData(InputKey.Grave, "`")]
    [InlineData(InputKey.LeftBracket, "[")]
    [InlineData(InputKey.Slash, "/")]
    [InlineData(InputKey.Escape, "Escape")]
    public void The_key_name_table_prints_the_legend_and_not_the_member(InputKey key, string legend) {
        Assert.Equal(legend, ShortcutFormat.Name(key));

        // And `Name` is what `Describe` prints once the modifiers are done, which is the promise an
        // alternative formatter relies on when it writes its own glyphs and then asks for the key.
        Assert.Equal(legend, ShortcutFormat.Describe(key, ModifierKeys.None));
    }

    [Fact]
    public void Replacing_the_formatter_changes_what_the_process_writes() {
        var original = ShortcutFormat.Formatter;

        try {
            ShortcutFormat.Formatter = static (key, modifiers) =>
                (modifiers.HasFlag(ModifierKeys.Meta) ? "⌘" : "") + ShortcutFormat.Name(key);

            Assert.Equal("⌘S", ShortcutFormat.Formatter(InputKey.S, ModifierKeys.Meta));

            // ⚠ And the default is untouched by the swap: `Describe` is the neutral form and not the
            // thing being replaced, so a caller that wants the long form can still ask for it.
            Assert.Equal("Meta+S", ShortcutFormat.Describe(InputKey.S, ModifierKeys.Meta));
        } finally {
            ShortcutFormat.Formatter = original;
        }
    }
}
