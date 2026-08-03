// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core.Mathematics;

namespace Vixen.Platform;

/// <summary>An image on the clipboard, as straight (non-premultiplied) RGBA8 pixels.</summary>
/// <param name="Pixels"><c>Size.X * Size.Y * 4</c> bytes, row-major from the top-left.</param>
/// <param name="Size">The image's dimensions in pixels.</param>
public readonly record struct ClipboardImage(ReadOnlyMemory<byte> Pixels, Int2 Size);

/// <summary>The system clipboard.</summary>
/// <remarks>
///     <para>
///         <b>Synchronous, which is a decision and not an oversight.</b> Reading a browser's
///         clipboard is asynchronous and gated on a user gesture, so an async API would look like it
///         worked there and would still return nothing when called from anywhere but a paste
///         handler. Making that the shape of the interface would push a lie into every caller. The
///         web implementation instead serves what the last paste event delivered, which is the only
///         thing a browser will ever let it have, and the desktop implementations do the
///         straightforward thing.
///     </para>
///     <para>
///         Reads can fail for reasons that are nobody's fault: another application owns the
///         clipboard and has stopped responding, the format is not what was asked for, or — on a
///         headless platform — there is no clipboard at all. Hence the <c>Try</c> shape throughout;
///         none of these are exceptional.
///     </para>
/// </remarks>
public interface IClipboard {
    /// <summary>Whether the clipboard currently holds text.</summary>
    bool HasText { get; }

    /// <summary>Whether the clipboard currently holds an image.</summary>
    bool HasImage { get; }

    /// <summary>Reads the clipboard's text.</summary>
    /// <param name="text">What was on the clipboard.</param>
    /// <returns><see langword="false" /> if there was no text, or it could not be read.</returns>
    bool TryGetText([NotNullWhen(true)] out string? text);

    /// <summary>Puts text on the clipboard.</summary>
    /// <param name="text">The text. Empty clears it.</param>
    /// <returns><see langword="false" /> if the platform refused.</returns>
    bool SetText(string text);

    /// <summary>Reads the clipboard's image.</summary>
    /// <param name="image">The decoded image.</param>
    /// <returns><see langword="false" /> if there was no image, or it could not be decoded.</returns>
    bool TryGetImage(out ClipboardImage image);

    /// <summary>Puts an image on the clipboard.</summary>
    /// <param name="image">The image, as RGBA8 pixels.</param>
    /// <returns><see langword="false" /> if the platform refused.</returns>
    bool SetImage(in ClipboardImage image);

    /// <summary>Reads a custom format's bytes.</summary>
    /// <param name="format">
    ///     A platform-neutral format name. Implementations map it to the platform's own vocabulary —
    ///     a registered Win32 clipboard format, an <c>NSPasteboard</c> UTI, an X11 atom, a MIME type
    ///     in a browser — because those four namespaces have nothing in common and the caller should
    ///     not have to know which one it is talking to.
    /// </param>
    /// <param name="data">The bytes.</param>
    /// <returns><see langword="false" /> if the clipboard holds nothing in that format.</returns>
    bool TryGetData(string format, out ReadOnlyMemory<byte> data);

    /// <summary>Puts a custom format's bytes on the clipboard.</summary>
    /// <param name="format">The format name, as in <see cref="TryGetData" />.</param>
    /// <param name="data">The bytes.</param>
    /// <returns><see langword="false" /> if the platform refused.</returns>
    bool SetData(string format, ReadOnlySpan<byte> data);

    /// <summary>Empties the clipboard, if the platform allows it.</summary>
    void Clear();
}
