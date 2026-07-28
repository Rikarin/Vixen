// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.Ui;

/// <summary>A key and what is held with it.</summary>
/// <param name="Key">The physical key, by its US-QWERTY legend.</param>
/// <param name="Modifiers">What is held down at the time.</param>
/// <remarks>
///     <para>
///         ⚠ <b>A key <i>position</i>, not a character</b>, because that is what
///         <see cref="KeyEvent" /> reports and what a keymap has to be written in — a binding stored
///         as the letter somebody typed would move around the keyboard when they switched layout.
///         The consequence is honest and worth knowing: <c>Ctrl+Z</c> is the key labelled Z on a
///         US keyboard, which is where the undo shortcut is on a French one too, because that is how
///         every application on the machine behaves.
///     </para>
///     <para>
///         <b>Written as text, because a keymap is a file a person edits.</b> <c>Ctrl+Shift+S</c>
///         round-trips exactly, and an unparseable chord is <see cref="None" /> rather than an
///         exception — a hand-edited keymap with one bad line loses that line rather than every
///         binding in the file.
///     </para>
/// </remarks>
public readonly record struct KeyChord(InputKey Key, ModifierKeys Modifiers) {
    /// <summary>No chord: what an unbound command has.</summary>
    public static KeyChord None => default;

    /// <summary>Whether this is a chord at all.</summary>
    public bool IsBound => Key != InputKey.Unknown;

    /// <summary>What a menu shows against the command.</summary>
    /// <returns>Something like <c>Ctrl+Shift+S</c>.</returns>
    /// <remarks>
    ///     <see cref="KeyboardShortcut.Describe" />'s, so a chord in a menu and a chord in the
    ///     palette are written the same way — and so a Mac head that wants <c>⌘⇧S</c> changes one
    ///     place rather than three.
    /// </remarks>
    public string Describe() => IsBound ? KeyboardShortcut.Describe(Key, Modifiers) : string.Empty;

    /// <summary>What a keymap file writes.</summary>
    /// <returns>Something like <c>Ctrl+Shift+S</c>, or the empty string when unbound.</returns>
    /// <remarks>
    ///     ⚠ <b>Not <see cref="Describe" />, however alike the two look today.</b> One is a
    ///     serialisation format and the other is a label; the day the label becomes
    ///     platform-adapted, a keymap written on a Mac must still load on Linux.
    /// </remarks>
    public string Save() {
        if (!IsBound) {
            return string.Empty;
        }

        var text = new StringBuilder();

        if (Modifiers.HasFlag(ModifierKeys.Control)) {
            text.Append("Ctrl+");
        }

        if (Modifiers.HasFlag(ModifierKeys.Alt)) {
            text.Append("Alt+");
        }

        if (Modifiers.HasFlag(ModifierKeys.Shift)) {
            text.Append("Shift+");
        }

        if (Modifiers.HasFlag(ModifierKeys.Meta)) {
            text.Append("Meta+");
        }

        return text.Append(Key.ToString()).ToString();
    }

    /// <summary>Reads one back.</summary>
    /// <param name="text">What <see cref="Save" /> wrote.</param>
    /// <param name="chord">The chord.</param>
    /// <returns>Whether it was one.</returns>
    public static bool TryParse(string? text, out KeyChord chord) {
        chord = None;

        if (string.IsNullOrWhiteSpace(text)) {
            return false;
        }

        var modifiers = ModifierKeys.None;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < parts.Length - 1; i++) {
            var modifier = parts[i] switch {
                "Ctrl" or "Control" => ModifierKeys.Control,
                "Alt" or "Option" => ModifierKeys.Alt,
                "Shift" => ModifierKeys.Shift,
                "Meta" or "Cmd" or "Command" or "Super" or "Win" => ModifierKeys.Meta,
                _ => ModifierKeys.None
            };

            if (modifier == ModifierKeys.None) {
                return false;
            }

            modifiers |= modifier;
        }

        if (!Enum.TryParse(parts[^1], ignoreCase: true, out InputKey key) || key == InputKey.Unknown) {
            return false;
        }

        chord = new KeyChord(key, modifiers);
        return true;
    }

    /// <summary>The chord a key event is.</summary>
    /// <param name="args">The event.</param>
    /// <returns>The chord.</returns>
    /// <remarks>
    ///     ⚠ <b>A modifier pressed on its own is not a chord.</b> Holding Ctrl produces a
    ///     <see cref="KeyEvent" /> whose key <i>is</i> Ctrl and whose modifiers already include it,
    ///     so without this every shortcut beginning with Ctrl would be reachable by pressing Ctrl
    ///     alone — matched against a binding of <c>Ctrl+Ctrl</c> that nobody wrote, which is worse:
    ///     it is a chord that can never be typed and therefore never tested.
    /// </remarks>
    public static KeyChord Of(KeyEvent args) {
        ArgumentNullException.ThrowIfNull(args);

        return args.Key is InputKey.LeftControl or InputKey.RightControl
            or InputKey.LeftShift or InputKey.RightShift
            or InputKey.LeftAlt or InputKey.RightAlt
            or InputKey.LeftMeta or InputKey.RightMeta
            ? None
            : new KeyChord(args.Key, args.Modifiers);
    }

    /// <inheritdoc />
    public override string ToString() => Save();
}
