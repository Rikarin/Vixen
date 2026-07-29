// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;

namespace Vixen.Platform.Windows;

/// <summary>The Windows clipboard: images and application-defined formats.</summary>
/// <remarks>
///     <para>
///         Text is left to <paramref name="fallback" />, which on the desktop platform is SDL's and
///         is correct. What SDL has no answer for is everything that is not text, and on Windows
///         that is one API — <c>OpenClipboard</c> and a numbered format — rather than the three
///         unrelated ones the three desktops need between them.
///     </para>
///     <para>
///         <b>Setting anything replaces everything.</b> Windows gives the clipboard to one owner at
///         a time, and the owner declares every format it offers before it lets go, so
///         <see cref="SetImage" /> and <see cref="SetData" /> each begin by emptying it. That is the
///         platform's model rather than a simplification: an application that pastes from an
///         application that has quit is reading what the system kept from the last owner, and there
///         is no version of this in which two owners contribute to one clipboard.
///     </para>
///     <para>
///         <b><c>OpenClipboard</c> is retried.</b> It fails while another process holds the
///         clipboard open, which is a normal thing to happen for the microsecond somebody else's
///         paste handler takes and not an error worth reporting to a user. Ten attempts a
///         millisecond apart is longer than any well-behaved application holds it and short enough
///         to be invisible in a frame.
///     </para>
/// </remarks>
/// <param name="fallback">The portable clipboard, which keeps the text half.</param>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsClipboard(IClipboard fallback) : IClipboard {
    const int OpenAttempts = 10;

    /// <inheritdoc />
    public bool HasText => fallback.HasText;

    /// <inheritdoc />
    public bool HasImage => Win32.IsClipboardFormatAvailable(Win32.CfDibV5)
        || Win32.IsClipboardFormatAvailable(Win32.CfDib);

    /// <inheritdoc />
    public bool TryGetText([NotNullWhen(true)] out string? text) => fallback.TryGetText(out text);

    /// <inheritdoc />
    public bool SetText(string text) => fallback.SetText(text);

    /// <inheritdoc />
    public bool TryGetImage(out ClipboardImage image) {
        image = default;

        // V5 first: it is the header that can say the fourth channel is alpha, and an application
        // that offers both offers the same picture twice.
        if (!TryRead(Win32.CfDibV5, out var dib) && !TryRead(Win32.CfDib, out dib)) {
            return false;
        }

        return DibImage.TryDecode(dib.Span, out image);
    }

    /// <inheritdoc />
    public bool SetImage(in ClipboardImage image) {
        var dib = DibImage.Encode(image);
        return dib is not null && Write(Win32.CfDibV5, dib);
    }

    /// <summary>Reads an application-defined format's bytes.</summary>
    /// <param name="format">
    ///     A format name, registered with <c>RegisterClipboardFormat</c> verbatim. That registry is
    ///     Windows' own namespace for exactly this question and is what <c>"PNG"</c>,
    ///     <c>"HTML Format"</c> and every application-defined name already live in, so a name is
    ///     passed through rather than translated — translating it would mean inventing a fourth
    ///     namespace and mapping it onto the three that exist.
    /// </param>
    /// <param name="data">The bytes.</param>
    /// <returns><see langword="false" /> if the clipboard holds nothing in that format.</returns>
    public bool TryGetData(string format, out ReadOnlyMemory<byte> data) {
        ArgumentException.ThrowIfNullOrEmpty(format);

        var id = Win32.RegisterClipboardFormat(format);

        if (id == 0) {
            data = default;
            return false;
        }

        return TryRead(id, out data);
    }

    /// <inheritdoc cref="TryGetData" />
    public bool SetData(string format, ReadOnlySpan<byte> data) {
        ArgumentException.ThrowIfNullOrEmpty(format);

        var id = Win32.RegisterClipboardFormat(format);
        return id != 0 && Write(id, data);
    }

    /// <inheritdoc />
    public void Clear() {
        if (!TryOpen()) {
            return;
        }

        try {
            Win32.EmptyClipboard();
        } finally {
            Win32.CloseClipboard();
        }
    }

    static bool TryRead(uint format, out ReadOnlyMemory<byte> data) {
        data = default;

        if (!Win32.IsClipboardFormatAvailable(format) || !TryOpen()) {
            return false;
        }

        try {
            var handle = Win32.GetClipboardData(format);

            if (handle == 0) {
                return false;
            }

            var size = Win32.GlobalSize(handle);
            var pointer = Win32.GlobalLock(handle);

            if (pointer is null || size == 0) {
                return false;
            }

            try {
                // Copied rather than wrapped. The handle belongs to whoever set it and stops being
                // readable the moment the clipboard changes owner, which can be before the caller
                // has looked at what it asked for.
                data = new ReadOnlySpan<byte>(pointer, checked((int)size)).ToArray();
                return true;
            } finally {
                Win32.GlobalUnlock(handle);
            }
        } finally {
            Win32.CloseClipboard();
        }
    }

    static bool Write(uint format, ReadOnlySpan<byte> data) {
        if (!TryOpen()) {
            return false;
        }

        var memory = nint.Zero;

        try {
            Win32.EmptyClipboard();

            memory = Win32.GlobalAlloc(Win32.GmemMoveable, (nuint)data.Length);

            if (memory == 0) {
                return false;
            }

            var pointer = Win32.GlobalLock(memory);

            if (pointer is null) {
                return false;
            }

            data.CopyTo(new(pointer, data.Length));
            Win32.GlobalUnlock(memory);

            if (Win32.SetClipboardData(format, memory) == 0) {
                return false;
            }

            // Handed over on success: the system owns the block from here and freeing it is how a
            // paste in another application reads freed memory.
            memory = nint.Zero;
            return true;
        } finally {
            if (memory != 0) {
                Win32.GlobalFree(memory);
            }

            Win32.CloseClipboard();
        }
    }

    static bool TryOpen() {
        for (var attempt = 0; attempt < OpenAttempts; attempt++) {
            if (Win32.OpenClipboard(nint.Zero)) {
                return true;
            }

            Thread.Sleep(1);
        }

        return false;
    }
}
