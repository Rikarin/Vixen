// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Input;

namespace Vixen.Ui;

/// <summary>How a key combination is written down, for everything in the process that writes one.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Neither half of this was ever view state, and living on a <c>Control</c> is what made
///         it look like it was.</b> <c>KeyboardShortcut</c> is a label that draws a chord, and it
///         carried the process-wide formatter and the key-name table as statics — so a keymap
///         wanting to say what <c>Ctrl+Shift+S</c> is called had to reference the controls library to
///         ask a view class a question with no element in it. <c>Vixen.Ui</c> does not reference
///         <c>Vixen.Ui.Controls</c> and must not, which is why a keymap could not live down here.
///     </para>
///     <para>
///         <b>The formatter is process-wide on purpose.</b> A shortcut is drawn by menus, by toolbar
///         tooltips and by a command palette; an application that adapted the text at each call site
///         would have to find all three and would still miss whichever one was added next. Replacing
///         <see cref="Formatter" /> once changes every shortcut the process draws.
///     </para>
///     <para>
///         ⚠ <b><see cref="Describe" /> is deliberately not platform-adapted.</b> A Mac writes
///         <c>⌘⇧S</c> with no separators and a different modifier order, and getting that right needs
///         to know what the program is running on — which this assembly, sitting below
///         <c>Vixen.Platform</c>, does not. Knowing is the application's, and so is saying so.
///     </para>
/// </remarks>
public static class ShortcutFormat {
    /// <summary>How every shortcut in the process is written.</summary>
    /// <remarks>Defaulted to <see cref="Describe" />, which is the neutral form and not the answer.</remarks>
    public static Func<InputKey, ModifierKeys, string> Formatter { get; set; } = Describe;

    /// <summary>Writes a combination the way a menu would.</summary>
    /// <param name="key">The key.</param>
    /// <param name="modifiers">What is held with it.</param>
    /// <returns>Something like <c>Ctrl+Shift+S</c>.</returns>
    /// <remarks>
    ///     The modifier order is Ctrl, Alt, Shift, Meta, which is the order Windows, GTK and Qt all
    ///     write them in. It is not alphabetical and it is not the flag order; it is a convention, and
    ///     a menu that used a different one would look wrong beside every other application on the
    ///     machine.
    /// </remarks>
    public static string Describe(InputKey key, ModifierKeys modifiers) {
        var text = new StringBuilder();

        if (modifiers.HasFlag(ModifierKeys.Control)) {
            text.Append("Ctrl+");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt)) {
            text.Append("Alt+");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift)) {
            text.Append("Shift+");
        }

        if (modifiers.HasFlag(ModifierKeys.Meta)) {
            text.Append("Meta+");
        }

        return text.Append(Name(key)).ToString();
    }

    /// <summary>What a key is called on a menu, with no modifiers in front of it.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The legend a menu prints — <c>1</c>, <c>`</c>, <c>Escape</c>.</returns>
    /// <remarks>
    ///     ⚠ <b>Public because every alternative formatter needs exactly this and nothing else of
    ///     <see cref="Describe" />.</b> A Mac formatter writes its own modifier glyphs and then wants
    ///     the key's own name; before this it had to call <c>Describe(key, ModifierKeys.None)</c> and
    ///     rely on that being the same thing, which is true and was nowhere stated.
    ///     <para>
    ///         The enum's own name for everything except the handful whose member name is a
    ///         description rather than a legend — <c>Number1</c> is the <c>1</c> key and no menu has
    ///         ever written it that way.
    ///     </para>
    /// </remarks>
    public static string Name(InputKey key) =>
        key switch {
            >= InputKey.Number1 and <= InputKey.Number9 => ((int) (key - InputKey.Number1) + 1).ToString(),
            InputKey.Number0 => "0",
            InputKey.Space => "Space",
            InputKey.Grave => "`",
            InputKey.Minus => "-",
            InputKey.Equals => "=",
            InputKey.LeftBracket => "[",
            InputKey.RightBracket => "]",
            InputKey.Backslash => "\\",
            InputKey.Semicolon => ";",
            InputKey.Apostrophe => "'",
            InputKey.Comma => ",",
            InputKey.Period => ".",
            InputKey.Slash => "/",
            _ => key.ToString()
        };
}
