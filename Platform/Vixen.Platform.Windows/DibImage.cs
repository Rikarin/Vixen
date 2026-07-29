// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Vixen.Core.Mathematics;

namespace Vixen.Platform.Windows;

/// <summary>The device-independent bitmap the Windows clipboard carries images as.</summary>
/// <remarks>
///     <para>
///         Pure, and separate from <see cref="WindowsClipboard" /> for that reason: this is the half
///         of clipboard images that can be wrong, and it is the half that can be tested on a machine
///         with no clipboard — or no Windows.
///     </para>
///     <para>
///         <b>Reading is permissive and writing is not.</b> What arrives on the clipboard was
///         written by somebody else's application: 24-bit from a screenshot tool, 32-bit
///         <c>BI_RGB</c> with a garbage alpha channel from an image editor, <c>BI_BITFIELDS</c> with
///         16-bit channels from a video player, bottom-up from almost everything and top-down from
///         the rest. All of that is decoded. What is written back is one shape —
///         <c>BITMAPV5HEADER</c>, 32-bit, <c>BI_BITFIELDS</c>, sRGB, bottom-up — because there is no
///         reason to have two.
///     </para>
///     <para>
///         <b>Alpha is straight, and 32-bit <c>BI_RGB</c> has none.</b> The fourth byte of a
///         <c>BI_RGB</c> pixel is undefined by the format and is in practice either a real alpha
///         channel or zeroes, which are the same bytes and opposite meanings. Reading it literally
///         turns every screenshot from those applications into a fully transparent image, so an
///         all-zero alpha plane is read as opaque — the only reading under which both kinds of
///         producer are handled correctly.
///     </para>
/// </remarks>
static class DibImage {
    const int InfoHeaderSize = 40;
    const int V4HeaderSize = 108;
    const int V5HeaderSize = 124;

    const int BiRgb = 0;
    const int BiBitfields = 3;

    /// <summary><c>'sRGB'</c> big-endian, which is what <c>LCS_sRGB</c> is.</summary>
    const uint LcsSrgb = 0x7352_4742;

    /// <summary><c>LCS_GM_IMAGES</c>: the rendering intent for a picture rather than a diagram.</summary>
    const uint LcsGmImages = 4;

    /// <summary>The largest image accepted from the clipboard, per side.</summary>
    const int MaxDimension = 32768;

    /// <summary>The largest image accepted, in pixels.</summary>
    /// <remarks>
    ///     A header is four bytes of somebody else's width and four of height, and two individually
    ///     plausible numbers multiply into a multi-gigabyte allocation — or, at
    ///     32768 × 32768 × 4, into an <see cref="int" /> that has wrapped and a buffer that is
    ///     smaller than the loop writing into it. Sixty-four megapixels is twice an 8K screenshot
    ///     and keeps every product here inside an <see cref="int" />.
    /// </remarks>
    const long MaxPixels = 64L * 1024 * 1024;

    /// <summary>Reads a clipboard DIB into straight RGBA8, top-down.</summary>
    /// <param name="dib">The bytes behind <c>CF_DIB</c> or <c>CF_DIBV5</c>, with no file header.</param>
    /// <param name="image">The decoded image.</param>
    /// <returns><see langword="false" /> for a truncated, palettised or otherwise unreadable
    /// bitmap, which is not an error: the caller's contract is that a clipboard read can fail.</returns>
    public static bool TryDecode(ReadOnlySpan<byte> dib, out ClipboardImage image) {
        image = default;

        if (dib.Length < InfoHeaderSize) {
            return false;
        }

        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);

        if (headerSize < InfoHeaderSize || headerSize > dib.Length) {
            return false;
        }

        var width = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
        var signedHeight = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]);
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        var compression = BinaryPrimitives.ReadInt32LittleEndian(dib[16..]);
        var paletteEntries = BinaryPrimitives.ReadInt32LittleEndian(dib[32..]);

        // int.MinValue has no positive counterpart, so it is rejected before it is negated.
        if (width <= 0 || width > MaxDimension || signedHeight == 0 || signedHeight == int.MinValue) {
            return false;
        }

        var topDown = signedHeight < 0;
        var height = Math.Abs(signedHeight);

        if (height > MaxDimension || (long)width * height > MaxPixels) {
            return false;
        }

        if (bits is not (16 or 24 or 32)) {
            // 1, 4 and 8 bits per pixel are palettised, and nothing has put one on a clipboard since
            // the display it was written for went out of production.
            return false;
        }

        if (compression is not (BiRgb or BiBitfields)) {
            // BI_JPEG and BI_PNG exist and nothing produces them for the clipboard.
            return false;
        }

        uint redMask, greenMask, blueMask, alphaMask;
        var offset = headerSize;

        if (compression == BiBitfields) {
            if (headerSize >= V4HeaderSize) {
                // A V4 or V5 header carries the masks in the header itself.
                redMask = BinaryPrimitives.ReadUInt32LittleEndian(dib[40..]);
                greenMask = BinaryPrimitives.ReadUInt32LittleEndian(dib[44..]);
                blueMask = BinaryPrimitives.ReadUInt32LittleEndian(dib[48..]);
                alphaMask = BinaryPrimitives.ReadUInt32LittleEndian(dib[52..]);
            } else {
                // A V3 header carries three of them immediately after it, and never a fourth.
                if (dib.Length < headerSize + 12) {
                    return false;
                }

                redMask = BinaryPrimitives.ReadUInt32LittleEndian(dib[headerSize..]);
                greenMask = BinaryPrimitives.ReadUInt32LittleEndian(dib[(headerSize + 4)..]);
                blueMask = BinaryPrimitives.ReadUInt32LittleEndian(dib[(headerSize + 8)..]);
                alphaMask = 0;
                offset += 12;
            }
        } else {
            // BI_RGB is BGR(A) in memory, which is these masks read as one little-endian word.
            (redMask, greenMask, blueMask) = (0x00FF_0000u, 0x0000_FF00u, 0x0000_00FFu);
            alphaMask = bits == 32 ? 0xFF00_0000u : 0u;
        }

        if (redMask == 0 || greenMask == 0 || blueMask == 0) {
            return false;
        }

        // Present on a V5 header even at 32 bits per pixel, where it is a colour table for the
        // benefit of palettised displays and has to be stepped over rather than read.
        offset += paletteEntries * 4;

        var stride = ((width * bits + 31) / 32) * 4;

        if (offset < 0 || stride <= 0 || (long)offset + (long)stride * height > dib.Length) {
            return false;
        }

        var pixels = new byte[width * height * 4];
        var alphaSeen = false;

        for (var y = 0; y < height; y++) {
            // A bottom-up DIB stores the last row first, which is the common case and not the
            // interesting one — it is only ever this arithmetic.
            var source = dib.Slice(offset + (topDown ? y : height - 1 - y) * stride, stride);
            var destination = pixels.AsSpan(y * width * 4);

            for (var x = 0; x < width; x++) {
                var value = bits switch {
                    32 => BinaryPrimitives.ReadUInt32LittleEndian(source[(x * 4)..]),
                    24 => (uint)(source[x * 3] | (source[x * 3 + 1] << 8) | (source[x * 3 + 2] << 16)),
                    _ => BinaryPrimitives.ReadUInt16LittleEndian(source[(x * 2)..])
                };

                var alpha = alphaMask == 0 ? (byte)255 : Extract(value, alphaMask);
                alphaSeen |= alpha != 0;

                destination[x * 4] = Extract(value, redMask);
                destination[x * 4 + 1] = Extract(value, greenMask);
                destination[x * 4 + 2] = Extract(value, blueMask);
                destination[x * 4 + 3] = alpha;
            }
        }

        if (!alphaSeen) {
            // Every pixel fully transparent is what an application that left the fourth byte alone
            // produces, and is never what one that meant it produces — an image nobody can see is
            // not something anybody copies. See the remarks.
            for (var index = 3; index < pixels.Length; index += 4) {
                pixels[index] = 255;
            }
        }

        image = new(pixels, new(width, height));
        return true;
    }

    /// <summary>Writes straight RGBA8 as a <c>CF_DIBV5</c> bitmap.</summary>
    /// <param name="image">The image, <c>Size.X * Size.Y * 4</c> bytes from the top-left.</param>
    /// <returns>The bytes to put on the clipboard, or <see langword="null" /> if the image is not
    /// the size it says it is.</returns>
    /// <remarks>
    ///     <c>BITMAPV5HEADER</c> rather than <c>BITMAPINFOHEADER</c> because only V5 can say that the
    ///     fourth channel is alpha and that the colours are sRGB, and Windows synthesises
    ///     <c>CF_DIB</c> and <c>CF_BITMAP</c> from it for applications that ask for those instead.
    ///     Writing the older header would mean writing all three.
    /// </remarks>
    public static byte[]? Encode(in ClipboardImage image) {
        var (width, height) = (image.Size.X, image.Size.Y);

        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension
            || (long)width * height > MaxPixels) {
            return null;
        }

        var pixels = image.Pixels.Span;

        if (pixels.Length < width * height * 4) {
            return null;
        }

        var stride = width * 4;
        var dib = new byte[V5HeaderSize + stride * height];
        var header = dib.AsSpan();

        BinaryPrimitives.WriteInt32LittleEndian(header, V5HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], width);

        // Positive, so bottom-up. A negative height is legal and is mishandled by enough
        // applications that writing one is a way to be pasted upside down.
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], height);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], BiBitfields);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], stride * height);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], 0x00FF_0000);
        BinaryPrimitives.WriteUInt32LittleEndian(header[44..], 0x0000_FF00);
        BinaryPrimitives.WriteUInt32LittleEndian(header[48..], 0x0000_00FF);
        BinaryPrimitives.WriteUInt32LittleEndian(header[52..], 0xFF00_0000);
        BinaryPrimitives.WriteUInt32LittleEndian(header[56..], LcsSrgb);
        BinaryPrimitives.WriteUInt32LittleEndian(header[108..], LcsGmImages);

        for (var y = 0; y < height; y++) {
            var source = pixels.Slice(y * stride, stride);
            var destination = header.Slice(V5HeaderSize + (height - 1 - y) * stride, stride);

            for (var x = 0; x < width; x++) {
                destination[x * 4] = source[x * 4 + 2];
                destination[x * 4 + 1] = source[x * 4 + 1];
                destination[x * 4 + 2] = source[x * 4];
                destination[x * 4 + 3] = source[x * 4 + 3];
            }
        }

        return dib;
    }

    /// <summary>Reads one channel out of a pixel and scales it to eight bits.</summary>
    /// <remarks>
    ///     The scaling matters for the 16-bit formats, where a five-bit channel's maximum is 31 and
    ///     multiplying by 8 gives 248 rather than 255 — a white that is visibly not white. Dividing
    ///     by the channel's own maximum is the correct expansion and costs nothing here.
    /// </remarks>
    static byte Extract(uint value, uint mask) {
        var shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        var maximum = mask >> shift;
        var channel = (value & mask) >> shift;

        return maximum == 0 ? (byte)0 : (byte)((channel * 255 + maximum / 2) / maximum);
    }
}
