// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui;

namespace Vixen.Platform.Ui;

/// <summary>Turns "this element has the focus and takes text" into text input being on, in that window.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The wire the framework shipped without, and the reason desktop looked fine.</b>
///         <c>ITextInput.Activate</c>'s only caller in the whole repository was the game host's debug
///         console, and <c>SetCandidateArea</c> — implemented on every one of the six platforms — had
///         no caller at all outside a headless test. SDL leaves text input running, so a desktop
///         <c>TextField</c> received characters anyway and the gap was invisible; on the web and on a
///         phone a focused field receives nothing, and everywhere the IME's candidate list is drawn
///         at a corner of the screen instead of under the caret.
///     </para>
///     <para>
///         <b>Once a frame, after the update, exactly like <see cref="PlatformCursor.Apply" />.</b>
///         The focus moves between frames and the caret moves within a frame, so there is no event
///         to hang either half on that is not "the frame".
///     </para>
///     <para>
///         ⚠ <b>Stateful, and that is what makes it affordable.</b> <c>SetCandidateArea</c> is a
///         window-manager call on the desktop and a DOM write on the web; issuing one every frame for
///         a caret that has not moved is sixty a second for nothing. So the last window and the last
///         rectangle are remembered and only a change is pushed — which also makes "how many times
///         was the platform told" a thing a test can count, and counting it is how this file's own
///         tests prove the wire without a display server.
///     </para>
/// </remarks>
/// <param name="textInput">The platform's text input, usually <c>IPlatform.TextInput</c>.</param>
public sealed class PlatformTextInput(ITextInput textInput) {
    readonly ITextInput textInput = textInput
        ?? throw new ArgumentNullException(nameof(textInput));

    IWindow? active;
    Rectangle area;

    /// <summary>The window text input was last turned on for, or <see langword="null" /> when it is off.</summary>
    public IWindow? Active => active;

    /// <summary>Brings text input into line with what the document says has the focus.</summary>
    /// <param name="host">The host that knows which window shows which surface.</param>
    /// <returns>The window text input is on for, or <see langword="null" /> when it was turned
    /// off.</returns>
    /// <remarks>
    ///     ⚠ <b>Deactivated when the focus leaves a text target, and that half is not optional.</b>
    ///     While text input is on the platform hands keystrokes to the input method first, so a game
    ///     that closed its chat box and left it running has a <c>W</c> that composes instead of
    ///     walking. The contract on <c>ITextInput</c> says as much; nothing was keeping it.
    /// </remarks>
    public IWindow? Apply(PlatformWindowHost host) {
        ArgumentNullException.ThrowIfNull(host);

        var document = host.Document;

        if (document.Focused is not { } element
            || element is not ITextInputTarget { AcceptsTextInput: true } target
            || document.SurfaceOf(element) is not { } surface
            || !host.TryWindow(surface, out var window)) {
            Deactivate();
            return null;
        }

        if (!ReferenceEquals(active, window) || !textInput.IsActive) {
            // ⚠ Re-activated when the *window* changes and not only when it was off. A focus dragged
            // from the main window to a torn-off panel leaves text input running for the window that
            // no longer has it, and the platform delivers the characters there.
            textInput.Activate(window);
            active = window;

            // Forgotten with the window, so the first caret of the new one is always pushed. The
            // rectangle is window-relative, so the same numbers mean somewhere else.
            area = default;
        }

        var caret = target.CaretArea;

        if (caret != area) {
            area = caret;
            textInput.SetCandidateArea(window, caret);
        }

        return window;
    }

    /// <summary>Turns text input off, if this turned it on.</summary>
    /// <remarks>
    ///     ⚠ <b>Only if this turned it on.</b> The game host's debug console drives the same
    ///     <c>ITextInput</c> directly, and a UI host that deactivated unconditionally every frame
    ///     would close the console's input on the frame after it opened it.
    /// </remarks>
    public void Deactivate() {
        if (active is null) {
            return;
        }

        active = null;
        area = default;
        textInput.Deactivate();
    }
}
