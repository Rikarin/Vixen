// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;
using Vixen.Core.Mathematics;

namespace Vixen.Platform.Web;

/// <summary>Text entry and the IME, through an invisible input element over the caret.</summary>
/// <remarks>
///     <para>
///         <b>A canvas cannot host an input method.</b> An IME attaches to an editable element, and
///         a canvas is not one — there is no way to make it one, and no browser exposes the
///         composition state any other way. So text input creates a real <c>&lt;input&gt;</c>,
///         positions it over the caret, focuses it, and reads <c>compositionstart</c>,
///         <c>compositionupdate</c> and <c>compositionend</c> off it. That is the same trick every
///         browser-based editor uses, for the same reason.
///     </para>
///     <para>
///         <b>Invisible, not absent, and not off-screen.</b> An element with <c>display: none</c> or
///         zero size gets no IME at all in Safari, and one parked at the corner of the page makes
///         the candidate window open at the corner of the page — which is the exact failure
///         <see cref="SetCandidateArea" /> exists to prevent. It is a one-pixel transparent element
///         at the caret's position, which is the arrangement that gets a Japanese, Chinese or Korean
///         user a candidate list under their cursor.
///     </para>
///     <para>
///         While it is focused the canvas is not, so key events would stop reaching the game — a
///         chat box that swallowed <c>Escape</c> could not be closed. The JavaScript forwards them
///         from the hidden element, so a key is still a key while text is being typed.
///     </para>
/// </remarks>
[SupportedOSPlatform("browser")]
internal sealed class WebTextInput : ITextInput {
    readonly double[] area = new double[4];

    /// <inheritdoc />
    public bool IsActive => WebInterop.IsTextInputActive();

    /// <inheritdoc />
    /// <remarks>
    ///     Coarse-pointer, which is what CSS itself uses to mean "touch device". There is no API
    ///     that answers the question directly, and inferring it from the user agent is inferring it
    ///     from a string every browser lies in.
    /// </remarks>
    public bool HasOnScreenKeyboard => WebInterop.HasOnScreenKeyboard();

    /// <inheritdoc />
    public bool IsOnScreenKeyboardVisible => !OnScreenKeyboardArea.IsEmpty;

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         Measured rather than guessed. Chromium's <c>VirtualKeyboard.boundingRect</c> is the
    ///         exact answer where it exists; elsewhere it is <c>visualViewport</c>, which shrinks by
    ///         exactly the keyboard's height when one appears.
    ///     </para>
    ///     <para>
    ///         The usual trick — comparing the window's inner height against its outer height — is
    ///         not used, because it misreports split-screen, a device with a hardware keyboard, and
    ///         a browser whose address bar has just collapsed. <see cref="Rectangle.Empty" /> when
    ///         nothing can be measured is a fallback a caller can handle; a wrong rectangle is not.
    ///     </para>
    /// </remarks>
    public Rectangle OnScreenKeyboardArea =>
        WebInterop.OnScreenKeyboardArea(area)
            ? new((float)area[0], (float)area[1], (float)area[2], (float)area[3])
            : Rectangle.Empty;

    /// <inheritdoc />
    public void Activate(IWindow window) {
        ArgumentNullException.ThrowIfNull(window);

        if (window is not WebWindow web) {
            throw new ArgumentException("The window was not made by this platform.", nameof(window));
        }

        WebInterop.ActivateTextInput(web.Canvas);
    }

    /// <inheritdoc />
    /// <remarks>Returns focus to the canvas, so the keyboard goes back to being a set of keys.</remarks>
    public void Deactivate() => WebInterop.DeactivateTextInput();

    /// <inheritdoc />
    public void SetCandidateArea(IWindow window, Rectangle area) {
        ArgumentNullException.ThrowIfNull(window);

        if (window is not WebWindow web) {
            throw new ArgumentException("The window was not made by this platform.", nameof(window));
        }

        WebInterop.SetCandidateArea(web.Canvas, area.X, area.Y, area.Width, area.Height);
    }
}
