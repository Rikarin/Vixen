// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Platform;

/// <summary>The modifier keys held when an event happened.</summary>
/// <remarks>
///     Left and right are distinguished because <c>AltGr</c> is right alt on layouts that have one,
///     and a shortcut bound to "alt" that fires while the user types <c>@</c> on a German keyboard
///     is a real bug. <see cref="Shift" />, <see cref="Control" />, <see cref="Alt" /> and
///     <see cref="Meta" /> are the either-side masks for the common case.
/// </remarks>
[Flags]
public enum KeyModifiers : ushort {
    /// <summary>No modifier held.</summary>
    None = 0,

    /// <summary>The left shift key is held.</summary>
    LeftShift = 1 << 0,

    /// <summary>The right shift key is held.</summary>
    RightShift = 1 << 1,

    /// <summary>The left control key is held.</summary>
    LeftControl = 1 << 2,

    /// <summary>The right control key is held.</summary>
    RightControl = 1 << 3,

    /// <summary>The left alt key is held.</summary>
    LeftAlt = 1 << 4,

    /// <summary>The right alt key is held — <c>AltGr</c> on layouts that have one.</summary>
    RightAlt = 1 << 5,

    /// <summary>The left <c>Windows</c>/<c>Command</c>/<c>Super</c> key is held.</summary>
    LeftMeta = 1 << 6,

    /// <summary>The right <c>Windows</c>/<c>Command</c>/<c>Super</c> key is held.</summary>
    RightMeta = 1 << 7,

    /// <summary><c>Caps Lock</c> is on. A state, not a held key.</summary>
    CapsLock = 1 << 8,

    /// <summary><c>Num Lock</c> is on. A state, not a held key.</summary>
    NumLock = 1 << 9,

    /// <summary>Either shift. See <see cref="KeyModifierExtensions.HasAny" />.</summary>
    Shift = LeftShift | RightShift,

    /// <summary>Either control. See <see cref="KeyModifierExtensions.HasAny" />.</summary>
    Control = LeftControl | RightControl,

    /// <summary>Either alt. See <see cref="KeyModifierExtensions.HasAny" />.</summary>
    Alt = LeftAlt | RightAlt,

    /// <summary>Either <c>Windows</c>/<c>Command</c>/<c>Super</c>. See
    /// <see cref="KeyModifierExtensions.HasAny" />.</summary>
    Meta = LeftMeta | RightMeta
}

/// <summary>Asking a <see cref="KeyModifiers" /> the question you meant to ask.</summary>
/// <remarks>
///     <para>
///         <b><see cref="Enum.HasFlag" /> is the wrong operator for the either-side masks, and
///         quietly so.</b> <see cref="KeyModifiers.Shift" /> is two bits, so
///         <c>modifiers.HasFlag(KeyModifiers.Shift)</c> asks whether <em>both</em> shift keys are
///         held — which is false in every situation anyone means by "shift is down". The mistake
///         compiles, reads correctly, and produces a shortcut that never fires.
///     </para>
///     <para>
///         <see cref="HasAny" /> is what a shortcut wants; <see cref="HasAll" /> is what a chord
///         wants; <see cref="Exactly" /> is what distinguishes <c>Ctrl+S</c> from <c>Ctrl+Shift+S</c>
///         and is the one most menu code gets wrong.
///     </para>
/// </remarks>
public static class KeyModifierExtensions {
    /// <summary>Whether any bit of <paramref name="mask" /> is held.</summary>
    /// <param name="modifiers">What is held.</param>
    /// <param name="mask">What to look for.</param>
    public static bool HasAny(this KeyModifiers modifiers, KeyModifiers mask) => (modifiers & mask) != 0;

    /// <summary>Whether every bit of <paramref name="mask" /> is held.</summary>
    /// <param name="modifiers">What is held.</param>
    /// <param name="mask">What to look for.</param>
    public static bool HasAll(this KeyModifiers modifiers, KeyModifiers mask) => (modifiers & mask) == mask;

    /// <summary>
    ///     Whether exactly the modifiers in <paramref name="mask" /> are held and no others, ignoring
    ///     the <see cref="KeyModifiers.CapsLock" /> and <see cref="KeyModifiers.NumLock" /> states.
    /// </summary>
    /// <param name="modifiers">What is held.</param>
    /// <param name="mask">What the shortcut wants, in either-side form.</param>
    /// <remarks>
    ///     What a keyboard shortcut should use. <c>Ctrl+S</c> must not fire on <c>Ctrl+Shift+S</c>,
    ///     and the lock states must not stop it firing — a user with caps lock on still expects
    ///     their save shortcut to work.
    /// </remarks>
    public static bool Exactly(this KeyModifiers modifiers, KeyModifiers mask) {
        const KeyModifiers locks = KeyModifiers.CapsLock | KeyModifiers.NumLock;

        // The caller writes `Shift` meaning either, and what is held is one of the two. Widening
        // both sides to the either-side masks is what makes them comparable at all.
        return Widen(modifiers & ~locks) == Widen(mask & ~locks);
    }

    static KeyModifiers Widen(KeyModifiers modifiers) {
        var widened = KeyModifiers.None;

        if (modifiers.HasAny(KeyModifiers.Shift)) {
            widened |= KeyModifiers.Shift;
        }

        if (modifiers.HasAny(KeyModifiers.Control)) {
            widened |= KeyModifiers.Control;
        }

        if (modifiers.HasAny(KeyModifiers.Alt)) {
            widened |= KeyModifiers.Alt;
        }

        if (modifiers.HasAny(KeyModifiers.Meta)) {
            widened |= KeyModifiers.Meta;
        }

        return widened;
    }
}
