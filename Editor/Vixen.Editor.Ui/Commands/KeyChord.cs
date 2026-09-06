// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Input;
using Vixen.Ui;

namespace Vixen.Editor.Ui;

// ⚠ **The `using Vixen.Ui.Controls` that used to be here is gone, and it was the whole of what
// stopped this type moving.** `Describe()`, `UsePlatformFormat` and both `MacFormat`/`MacWords` went
// through `KeyboardShortcut.Formatter` and `KeyboardShortcut.Describe` — two statics on a *Control*
// in `Vixen.Ui.Controls`, an assembly above `Vixen.Ui`, which references neither it nor anything in
// it — so #650's "move the self-contained leaf first" could not start here, and the real leaf was
// the split rather than this type. They are `Vixen.Ui`'s `ShortcutFormat` now, the control's two members
// forward to it, and nothing in this file names anything above `Vixen.Ui`.
//
// The same shape still blocks two of the other four: `EditorCommand` holds an `IconArt`, also
// `Vixen.Ui.Controls`, and `KeyMap` reads `Vixen.Core.Yaml`, which `Vixen.Ui` does not reference
// either. Only `CommandRegistry` and `CommandDispatcher` name nothing above `Vixen.Ui` — and they
// name `EditorCommand`, so they cannot land below it.

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

    /// <summary>Which modifier this machine uses for an application's own shortcuts.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Command on a Mac and Control everywhere else, and it is not a preference.</b> A
    ///         Mac user pressing Ctrl+S expects a terminal control code, not a save; every
    ///         application on the machine binds ⌘, and one that did not would be the only thing on
    ///         the desktop whose Save is somewhere else.
    ///     </para>
    ///     <para>
    ///         <b>Settable, for the tests and for a head that has a reason.</b> It is read at bind
    ///         time and at lookup time rather than captured, so changing it takes effect on the next
    ///         keystroke — which is what makes it testable at all.
    ///     </para>
    /// </remarks>
    public static ModifierKeys Primary { get; set; } =
        OperatingSystem.IsMacOS() ? ModifierKeys.Meta : ModifierKeys.Control;

    /// <summary>Whether this is a chord at all.</summary>
    public bool IsBound => Key != InputKey.Unknown;

    /// <summary>This chord with Control and <see cref="Primary" /> exchanged.</summary>
    /// <returns>The swapped chord, or this one where the two are the same modifier.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An involution, used in both directions, and that is what makes one method
    ///         enough.</b> A keymap stores and a menu model declares <c>Ctrl+S</c> — one portable
    ///         vocabulary, in the file and in the table, so a keymap written on a Mac still loads on
    ///         Linux. What adapts is the two ends: an arriving key event is swapped <i>into</i> that
    ///         vocabulary before it is looked up, and a stored chord is swapped <i>out</i> of it
    ///         before it is drawn.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both ways, not just one.</b> Mapping ⌘ onto Ctrl and leaving Ctrl alone would
    ///         make a Mac answer to both, so a real Ctrl+S — which on that machine means something
    ///         else entirely — would silently save. Exchanging them means Ctrl+S on a Mac resolves to
    ///         a chord nothing has claimed, which is the honest answer.
    ///     </para>
    /// </remarks>
    public KeyChord ForPlatform() => ForPlatform(Primary);

    /// <inheritdoc cref="ForPlatform()" />
    /// <param name="primary">Which modifier to exchange Control with.</param>
    /// <remarks>
    ///     ⚠ <b>The pure form, and the one the tests use.</b> <see cref="Primary" /> is process-wide,
    ///     so a test that set it would change what every test running beside it means — and the
    ///     failure would land somewhere else entirely. Taking it as an argument is what makes the
    ///     swap checkable without a global.
    /// </remarks>
    public KeyChord ForPlatform(ModifierKeys primary) {
        if (primary == ModifierKeys.Control) {
            return this;
        }

        var swapped = Modifiers & ~(ModifierKeys.Control | primary);

        if (Modifiers.HasFlag(ModifierKeys.Control)) {
            swapped |= primary;
        }

        if (Modifiers.HasFlag(primary)) {
            swapped |= ModifierKeys.Control;
        }

        return this with { Modifiers = swapped };
    }

    /// <summary>What a menu shows against the command.</summary>
    /// <returns>Something like <c>Ctrl+Shift+S</c>.</returns>
    /// <remarks>
    ///     ⚠ <b>Through <see cref="ShortcutFormat.Formatter" /> and after
    ///     <see cref="ForPlatform" />, which is the two halves of writing a shortcut the way the
    ///     machine's other applications do.</b> The swap turns the stored <c>Ctrl+S</c> into the
    ///     <c>Meta+S</c> a Mac user actually presses; the formatter turns that into <c>⌘S</c>. A
    ///     chord in a menu, in a toolbar tooltip and in the palette all go through here, so they
    ///     cannot disagree.
    /// </remarks>
    public string Describe() =>
        IsBound ? ShortcutFormat.Formatter(Key, ForPlatform().Modifiers) : string.Empty;

    /// <summary>Makes every shortcut in the process read the way this machine writes them.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Called once by the shell, and it replaces a static.</b>
    ///         <see cref="ShortcutFormat.Formatter" /> is process-wide because a shortcut is drawn
    ///         by three views and an application that adapted each one would miss the fourth. On
    ///         anything but a Mac this does nothing at all, so it costs a branch at start-up.
    ///     </para>
    ///     <para>
    ///         <b>The Mac form is glyphs in a fixed order and no separators</b> — ⌃⌥⇧⌘ then the key,
    ///         which is what the platform's own menus have written since 1984 and what a user reads
    ///         without stopping.
    ///     </para>
    /// </remarks>
    public static void UsePlatformFormat(UiDocument? document = null) {
        if (Primary != ModifierKeys.Meta) {
            return;
        }

        // ⚠ Only where the face can actually draw them. The editor borrows a font from the machine
        // rather than shipping one — see `Fonts` — and on macOS what it finds is Arial, which has
        // none of ⌘ ⇧ ⌥ ⌃. An unmapped codepoint does not draw as a box or as nothing: it resolves
        // to whatever glyph zero happens to be, and the menu bar read "L+S" for Save. A shortcut
        // nobody can read is worse than one written the long way.
        ShortcutFormat.Formatter = CanDrawGlyphs(document) ? MacFormat : MacWords;
    }

    /// <summary>Whether a document's default face has the four modifier glyphs.</summary>
    /// <remarks>
    ///     All four rather than any: a label reading <c>⇧Cmd+S</c> — half glyphs, half words — is
    ///     worse than either, and the Command symbol alone is the one most likely to be present.
    /// </remarks>
    static bool CanDrawGlyphs(UiDocument? document) =>
        document?.Fonts.Default is { } face
        && face.Supports('⌃')
        && face.Supports('⌥')
        && face.Supports('⇧')
        && face.Supports('⌘');

    /// <summary>The same combination in words, for a face that cannot draw the glyphs.</summary>
    /// <param name="key">The key.</param>
    /// <param name="modifiers">What is held with it.</param>
    /// <returns>Something like <c>Shift+Cmd+S</c>.</returns>
    /// <remarks>
    ///     ⚠ <b>The platform's order and the platform's names, with separators.</b> It is not the
    ///     <see cref="ShortcutFormat.Describe" /> form: that writes <c>Meta</c>, which is what the
    ///     modifier is called in an event and not what it is called on the key — a Mac user reading
    ///     "Meta+S" has to translate, and the whole point of adapting is that they should not have to.
    /// </remarks>
    public static string MacWords(InputKey key, ModifierKeys modifiers) {
        var text = new StringBuilder();

        if (modifiers.HasFlag(ModifierKeys.Control)) {
            text.Append("Ctrl+");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt)) {
            text.Append("Opt+");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift)) {
            text.Append("Shift+");
        }

        if (modifiers.HasFlag(ModifierKeys.Meta)) {
            text.Append("Cmd+");
        }

        return text.Append(ShortcutFormat.Name(key)).ToString();
    }

    /// <summary>Writes a combination the way macOS has written them since 1984.</summary>
    /// <param name="key">The key.</param>
    /// <param name="modifiers">What is held with it.</param>
    /// <returns>Something like <c>⇧⌘S</c>.</returns>
    /// <remarks>
    ///     Glyphs, in the platform's fixed order, with no separators — which is what a user of that
    ///     machine reads without stopping. Public and pure so it can be checked without replacing a
    ///     process-wide formatter.
    /// </remarks>
    public static string MacFormat(InputKey key, ModifierKeys modifiers) {
        var text = new StringBuilder();

        if (modifiers.HasFlag(ModifierKeys.Control)) {
            text.Append('⌃');
        }

        if (modifiers.HasFlag(ModifierKeys.Alt)) {
            text.Append('⌥');
        }

        if (modifiers.HasFlag(ModifierKeys.Shift)) {
            text.Append('⇧');
        }

        if (modifiers.HasFlag(ModifierKeys.Meta)) {
            text.Append('⌘');
        }

        // ⚠ The key's own name comes from the control set, because the list of exceptions —
        // `Number1` is the `1` key, `Grave` is a backtick — is long and belongs in one place. Only
        // the modifiers are written differently here.
        return text.Append(ShortcutFormat.Name(key)).ToString();
    }

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
