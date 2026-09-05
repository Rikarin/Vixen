// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Ui;

/// <summary>The system clipboard, as much of it as a user interface needs.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is not <c>Vixen.Platform.IClipboard</c>, and it cannot be.</b> <c>Vixen.Ui</c>
///         is a <c>Core/</c> assembly and doc 00's layering forbids it a reference to
///         <c>Platform/</c> — the same rule that put <see cref="IUiWindowHost" /> here rather than
///         letting a docking host name a real window. So the seam is declared here and filled in by
///         <c>Vixen.Platform.Ui</c>, which is the assembly that exists to join the two.
///     </para>
///     <para>
///         <b>Text only, deliberately.</b> The platform interface carries images and arbitrary
///         formats and the backends implement all of it; what a <i>text control</i> can do with a
///         pasteboard is text, and an interface promising a control something it has no code to
///         accept is the kind of surface that reads as done and is not. An application that wants
///         the image flavours has <c>IPlatform.Clipboard</c> and always did.
///     </para>
///     <para>
///         <b>Synchronous, and <c>Try</c>-shaped, for the reasons <c>IClipboard</c> states.</b> A
///         browser will only ever serve what its last paste event delivered, and a read can fail
///         because another application owns the pasteboard and has stopped answering. Neither is
///         exceptional.
///     </para>
/// </remarks>
public interface IUiClipboard {
    /// <summary>Whether the clipboard currently holds text.</summary>
    bool HasText { get; }

    /// <summary>Reads the clipboard's text.</summary>
    /// <param name="text">What was on it.</param>
    /// <returns><see langword="false" /> if there was no text, or it could not be read.</returns>
    bool TryGetText([NotNullWhen(true)] out string? text);

    /// <summary>Puts text on the clipboard.</summary>
    /// <param name="text">The text. Empty clears it.</param>
    /// <returns><see langword="false" /> if the platform refused.</returns>
    bool SetText(string text);
}

public sealed partial class UiDocument {
    /// <summary>The system clipboard, if this document's head installed one.</summary>
    /// <remarks>
    ///     ⚠ <b>Null is the ordinary case rather than a failure</b>, exactly as
    ///     <see cref="Windows" /> is: a document under test, a headless platform and a document
    ///     built before its head has run all have no clipboard, and a text control asked to copy
    ///     into one does nothing rather than throwing. <see cref="HasClipboard" /> is what a menu
    ///     item asks before enabling itself.
    /// </remarks>
    public IUiClipboard? Clipboard { get; set; }

    /// <summary>Whether cut, copy and paste can reach the operating system from this document.</summary>
    public bool HasClipboard => Clipboard is not null;
}
