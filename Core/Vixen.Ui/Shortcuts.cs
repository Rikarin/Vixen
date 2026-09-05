// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Input;

namespace Vixen.Ui;

/// <summary>How a key combination is written down.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Formatting a chord is not view state, and it lived on a <c>Control</c> anyway.</b>
///         <c>Vixen.Ui.Controls.KeyboardShortcut</c> is an element that draws one, and it carried
///         both the process-wide formatter and the key-name table as statics — so anything that
///         wanted to write <c>Ctrl+Shift+S</c> into a string had to reference the control library
///         and, through it, an element tree it had no use for. Neither member needs an element,
///         a document or a font.
///     </para>
///     <para>
///         ⚠ <b>What that cost was a layering answer nobody could reach.</b>
///         <c>Vixen.Editor.Ui</c>'s <c>KeyChord</c> is a <c>readonly record struct</c> over an
///         <see cref="InputKey" /> and a <see cref="ModifierKeys" /> and is otherwise the most
///         obviously movable type in that assembly — and it could not move into <c>Vixen.Ui</c>,
///         because four of its lines went through those two statics and <c>Vixen.Ui</c> does not
///         reference <c>Vixen.Ui.Controls</c> and must not.
///     </para>
///     <para>
///         <b>An addition and not a removal.</b> <c>KeyboardShortcut.Formatter</c> and
///         <c>KeyboardShortcut.Describe</c> are still there and still mean what they meant; they
///         forward here, so there is one formatter in the process rather than two that can
///         disagree, and an application that already replaced the one on the control keeps working
///         without knowing this exists.
///     </para>
/// </remarks>
public static class Shortcuts {
    /// <summary>How every shortcut in the process is written.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The hook a Mac head needs, and the reason it is process-wide rather than at each
    ///         call site.</b> A shortcut is drawn by menus, by toolbar tooltips and by the command
    ///         palette; an application that adapted the text itself would have to find all three and
    ///         would still miss whichever one was added next. Replacing this once changes every
    ///         shortcut the process writes, whether it is drawn by a control or put in a string.
    ///     </para>
    ///     <para>
    ///         Defaulted to <see cref="Describe" />, which is deliberately not platform-adapted:
    ///         <c>Vixen.Ui</c> sits below <c>Vixen.Platform</c> and does not know what it is running
    ///         on. Knowing is the application's, and so is saying so.
    ///     </para>
    /// </remarks>
    public static Func<InputKey, ModifierKeys, string> Formatter { get; set; } = Describe;

    /// <summary>Writes a combination the way a menu would.</summary>
    /// <param name="key">The key.</param>
    /// <param name="modifiers">What is held with it.</param>
    /// <returns>Something like <c>Ctrl+Shift+S</c>.</returns>
    /// <remarks>
    ///     <para>
    ///         The modifier order is Ctrl, Alt, Shift, Meta, which is the order Windows, GTK and Qt
    ///         all write them in. It is not alphabetical and it is not the flag order; it is a
    ///         convention, and a menu that used a different one would look wrong beside every other
    ///         application on the machine.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not localised and not platform-adapted.</b> A Mac writes <c>⌘⇧S</c> with no
    ///         separators and a different modifier order, and getting that right needs to know what
    ///         it is running on — which this assembly deliberately does not. <see cref="Formatter" />
    ///         is where an application says otherwise; this is the default, not the answer.
    ///     </para>
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

    /// <summary>What a key is called on a menu.</summary>
    /// <param name="key">The key.</param>
    /// <returns>Its legend — <c>1</c> for <see cref="InputKey.Number1" />, a backtick for Grave.</returns>
    /// <remarks>
    ///     <para>
    ///         The enum's own name for everything except the handful whose member name is a
    ///         description rather than a legend — <c>Number1</c> is the <c>1</c> key and no menu has
    ///         ever written it that way.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Public, because a platform-adapted formatter needs exactly this half and nothing
    ///         else.</b> The macOS form is glyphs for the modifiers and the ordinary legend for the
    ///         key, and both of the editor's Mac formatters reached it by calling
    ///         <c>Describe(key, ModifierKeys.None)</c> — asking for a whole rendering in order to
    ///         get the part after the modifiers. The list of exceptions is long and belongs in one
    ///         place; so does the way of asking for it.
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
