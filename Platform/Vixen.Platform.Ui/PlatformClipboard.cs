// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Ui;

namespace Vixen.Platform.Ui;

/// <summary>Fills <see cref="IUiClipboard" /> from the platform's real pasteboard.</summary>
/// <remarks>
///     <para>
///         <b>The third join this assembly exists for</b>, and the same shape as the other two: a
///         <c>Core/</c> assembly may not name <see cref="IClipboard" />, so <c>Vixen.Ui</c> declares
///         what a text control needs and this turns it into the platform's.
///     </para>
///     <para>
///         ⚠ <b>Gated on <see cref="PlatformCapabilities.Clipboard" />, unlike
///         <see cref="PlatformCursor" />.</b> The difference is what absence looks like: a platform
///         with no cursor implements <c>CursorShape</c> as a setter that does nothing, so wiring it
///         unconditionally costs a no-op. A platform with no clipboard has no honest answer to
///         "is there text on it" — and a document whose <see cref="UiDocument.Clipboard" /> is left
///         null greys Cut, Copy and Paste out, which is the truth and is what
///         <see cref="UiDocument.HasClipboard" /> is asked for.
///     </para>
/// </remarks>
/// <param name="clipboard">The platform's clipboard.</param>
public sealed class PlatformClipboard(IClipboard clipboard) : IUiClipboard {
    /// <inheritdoc />
    public bool HasText => clipboard.HasText;

    /// <inheritdoc />
    public bool TryGetText([NotNullWhen(true)] out string? text) => clipboard.TryGetText(out text);

    /// <inheritdoc />
    public bool SetText(string text) => clipboard.SetText(text);

    /// <summary>Installs the platform's clipboard on a document, if it has one.</summary>
    /// <param name="document">The document.</param>
    /// <param name="platform">The platform.</param>
    /// <returns>Whether the document now has a clipboard.</returns>
    /// <remarks>
    ///     ⚠ <b>Called by the head at boot, the way <c>UiDocument.Windows</c> is.</b> A document
    ///     that nobody calls this for has no clipboard and says so — which is exactly the state
    ///     every non-editor Vixen application was in, with <see cref="IClipboard" /> implemented on
    ///     three desktops and reached from nothing above the platform layer.
    /// </remarks>
    public static bool Install(UiDocument document, IPlatform platform) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(platform);

        if (!platform.Has(PlatformCapabilities.Clipboard)) {
            return false;
        }

        document.Clipboard = new PlatformClipboard(platform.Clipboard);

        return true;
    }
}
