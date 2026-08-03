// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Platform;

/// <summary>Text entry, including composition through an input method.</summary>
/// <remarks>
///     <para>
///         Off by default, and that is not an optimisation. While text input is active the platform
///         hands keystrokes to the IME first, so <c>W</c> may produce a composition instead of a
///         key the game can bind — which is correct in a chat box and wrong everywhere else. So a
///         text field turns it on when it takes focus and off when it loses it, and the rest of the
///         time the keyboard is a set of keys.
///     </para>
///     <para>
///         <see cref="SetCandidateArea" /> is the part that is easy to skip and immediately visible
///         to anyone typing Japanese, Chinese or Korean: without it the candidate window is drawn at
///         the corner of the screen, covering something, instead of under the caret.
///     </para>
/// </remarks>
public interface ITextInput {
    /// <summary>Whether text input is running.</summary>
    bool IsActive { get; }

    /// <summary>Whether this platform shows an on-screen keyboard when text input starts.</summary>
    bool HasOnScreenKeyboard { get; }

    /// <summary>Whether an on-screen keyboard is showing now.</summary>
    bool IsOnScreenKeyboardVisible { get; }

    /// <summary>
    ///     The area the on-screen keyboard is covering, in logical points relative to the window —
    ///     <see cref="Rectangle.Empty" /> when nothing is covered.
    /// </summary>
    /// <remarks>
    ///     A field at the bottom of a phone screen is behind the keyboard unless something scrolls
    ///     it into view, and this is the number that makes that possible.
    /// </remarks>
    Rectangle OnScreenKeyboardArea { get; }

    /// <summary>Starts text input for a window.</summary>
    /// <param name="window">The window that will receive
    /// <see cref="PlatformEventKind.TextInput" />.</param>
    void Activate(IWindow window);

    /// <summary>Stops text input, abandoning any composition in progress.</summary>
    void Deactivate();

    /// <summary>Tells the IME where the caret is, so the candidate window can be put near it.</summary>
    /// <param name="window">The window the caret is in.</param>
    /// <param name="area">The caret's rectangle in logical points, relative to the window's client
    /// area.</param>
    void SetCandidateArea(IWindow window, Rectangle area);
}
