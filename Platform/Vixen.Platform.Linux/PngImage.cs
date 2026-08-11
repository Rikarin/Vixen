// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using Vixen.Core.Mathematics;

namespace Vixen.Platform.Linux;

/// <summary>PNG, which is what an image on a Linux clipboard is.</summary>
/// <remarks>
///     <para>
///         X11 and Wayland carry a selection as bytes under a MIME type, and the type every toolkit
///         offers and accepts for a picture is <c>image/png</c>. So an image on the clipboard here
///         is an encoded file rather than the raw pixels Windows and macOS hand over, and something
///         has to encode and decode it.
///     </para>
///     <para>
///         <b>There is a second hand-written PNG codec in this repository</b> —
///         <c>Vixen.Core.Imaging.PngCodec</c>, which the golden-image suites, the UI baselines and
///         <c>--vixen-capture</c> all write through. The reference this file's comment used to give
///         as the reason the two could not be merged is gone: that codec sat in
///         <c>Vixen.Ui.Testing</c>, which <c>Platform/</c> may not reference, and it has since moved
///         down into <c>Vixen.Core.Imaging</c>, which <c>Platform/</c> may.
///     </para>
///     <para>
///         What still keeps them apart is what each reads. The decoder here accepts greyscale
///         and truecolour, with and without alpha, because that is what a toolkit puts on a
///         clipboard; <c>PngCodec</c> accepts 8-bit RGBA and refuses the rest, deliberately, because
///         a golden reference in an unexpected format is a broken fixture rather than something to
///         guess about. Merging them means deciding which of those two contracts the one codec has,
///         and that is a decision rather than a tidy-up.
///     </para>
///     <para>
///         <b>What is supported is what a clipboard produces.</b> Eight bits per channel,
///         non-interlaced, greyscale or truecolour with or without alpha. Not 16-bit, not
///         palettised, not Adam7 — a toolkit writing a screenshot to the clipboard writes none of
///         them, and refusing to decode one is a paste that does nothing rather than a paste that is
///         wrong.
///     </para>
/// </remarks>
static class PngImage {
    /// <summary>The largest image accepted, per side.</summary>
    const int MaxDimension = 32768;

    /// <summary>The largest image accepted, in pixels.</summary>
    /// <remarks>
    ///     A header is four bytes of somebody else's width and four of height, and two
    ///     individually plausible numbers multiply into a multi-gigabyte allocation — or, at
    ///     32768 × 32768 × 4, into an <see cref="int" /> that has wrapped and a buffer that is
    ///     smaller than the loop writing into it. Sixty-four megapixels is twice an 8K screenshot
    ///     and keeps every product here inside an <see cref="int" />.
    /// </remarks>
    const long MaxPixels = 64L * 1024 * 1024;

    static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Decodes a PNG into straight RGBA8, top-down.</summary>
    /// <param name="data">The file's bytes.</param>
    /// <param name="image">The decoded image.</param>
    /// <returns><see langword="false" /> for anything malformed or outside the supported subset.</returns>
    public static bool TryDecode(ReadOnlySpan<byte> data, out ClipboardImage image) {
        image = default;

        if (data.Length < Signature.Length || !data[..Signature.Length].SequenceEqual(Signature)) {
            return false;
        }

        var offset = Signature.Length;
        var width = 0;
        var height = 0;
        var colourType = -1;
        using var compressed = new MemoryStream();

        while (offset + 12 <= data.Length) {
            var length = BinaryPrimitives.ReadInt32BigEndian(data[offset..]);

            if (length < 0 || offset + 12 + length > data.Length) {
                return false;
            }

            var type = data.Slice(offset + 4, 4);
            var payload = data.Slice(offset + 8, length);
            offset += 12 + length;

            if (type.SequenceEqual("IHDR"u8)) {
                if (length != 13) {
                    return false;
                }

                width = BinaryPrimitives.ReadInt32BigEndian(payload);
                height = BinaryPrimitives.ReadInt32BigEndian(payload[4..]);
                colourType = payload[9];

                // Bit depth, compression method, filter method, interlace method. Only one value of
                // each is in the supported subset, and every one of them is what a toolkit writes.
                if (payload[8] != 8 || payload[10] != 0 || payload[11] != 0 || payload[12] != 0) {
                    return false;
                }
            } else if (type.SequenceEqual("IDAT"u8)) {
                compressed.Write(payload);
            } else if (type.SequenceEqual("IEND"u8)) {
                break;
            }
        }

        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension
            || (long)width * height > MaxPixels) {
            return false;
        }

        var channels = colourType switch {
            0 => 1,
            2 => 3,
            4 => 2,
            6 => 4,
            _ => 0
        };

        if (channels == 0 || compressed.Length == 0) {
            return false;
        }

        compressed.Position = 0;

        var stride = width * channels;
        var raw = new byte[(stride + 1) * height];

        try {
            using var inflate = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true);
            inflate.ReadExactly(raw);
        } catch (Exception error) when (error is InvalidDataException or EndOfStreamException) {
            return false;
        }

        var pixels = new byte[width * height * 4];
        var previous = new byte[stride];
        var current = new byte[stride];

        for (var y = 0; y < height; y++) {
            var filter = raw[y * (stride + 1)];
            raw.AsSpan(y * (stride + 1) + 1, stride).CopyTo(current);

            if (!Unfilter(filter, current, previous, channels)) {
                return false;
            }

            for (var x = 0; x < width; x++) {
                var source = x * channels;
                var destination = (y * width + x) * 4;

                switch (channels) {
                    case 1:
                        pixels[destination] = pixels[destination + 1] = pixels[destination + 2] = current[source];
                        pixels[destination + 3] = 255;
                        break;

                    case 2:
                        pixels[destination] = pixels[destination + 1] = pixels[destination + 2] = current[source];
                        pixels[destination + 3] = current[source + 1];
                        break;

                    case 3:
                        pixels[destination] = current[source];
                        pixels[destination + 1] = current[source + 1];
                        pixels[destination + 2] = current[source + 2];
                        pixels[destination + 3] = 255;
                        break;

                    default:
                        current.AsSpan(source, 4).CopyTo(pixels.AsSpan(destination));
                        break;
                }
            }

            (previous, current) = (current, previous);
        }

        image = new(pixels, new(width, height));
        return true;
    }

    /// <summary>Encodes straight RGBA8 as a PNG.</summary>
    /// <param name="image">The image, <c>Size.X * Size.Y * 4</c> bytes from the top-left.</param>
    /// <returns>The file's bytes, or <see langword="null" /> if the image is not the size it says it
    /// is.</returns>
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
        var raw = new byte[(stride + 1) * height];

        for (var y = 0; y < height; y++) {
            // Filter type 0 on every row. Choosing one per row compresses better and is a heuristic
            // with no right answer; what goes on a clipboard is read once, by one application,
            // seconds later.
            raw[y * (stride + 1)] = 0;
            pixels.Slice(y * stride, stride).CopyTo(raw.AsSpan(y * (stride + 1) + 1));
        }

        using var output = new MemoryStream();
        output.Write(Signature);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 6;

        WriteChunk(output, "IHDR"u8, header);

        using var deflated = new MemoryStream();

        using (var deflate = new ZLibStream(deflated, CompressionLevel.Fastest, leaveOpen: true)) {
            deflate.Write(raw);
        }

        WriteChunk(output, "IDAT"u8, deflated.ToArray());
        WriteChunk(output, "IEND"u8, []);

        return output.ToArray();
    }

    static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload) {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
        output.Write(length);
        output.Write(type);
        output.Write(payload);

        // The CRC covers the type and the payload and not the length, which is the one detail of
        // PNG's framing that everybody gets wrong once.
        var crc = new Crc32();
        crc.Append(type);
        crc.Append(payload);

        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc.GetCurrentHashAsUInt32());
        output.Write(checksum);
    }

    /// <summary>Undoes one scanline's filter, in place.</summary>
    static bool Unfilter(byte filter, Span<byte> current, ReadOnlySpan<byte> previous, int channels) {
        switch (filter) {
            case 0:
                return true;

            case 1:
                for (var index = channels; index < current.Length; index++) {
                    current[index] += current[index - channels];
                }

                return true;

            case 2:
                for (var index = 0; index < current.Length; index++) {
                    current[index] += previous[index];
                }

                return true;

            case 3:
                for (var index = 0; index < current.Length; index++) {
                    var left = index >= channels ? current[index - channels] : 0;
                    current[index] += (byte)((left + previous[index]) / 2);
                }

                return true;

            case 4:
                for (var index = 0; index < current.Length; index++) {
                    var left = index >= channels ? current[index - channels] : (byte)0;
                    var upperLeft = index >= channels ? previous[index - channels] : (byte)0;
                    current[index] += Paeth(left, previous[index], upperLeft);
                }

                return true;

            default:
                return false;
        }
    }

    /// <summary>PNG's Paeth predictor: whichever neighbour the gradient points at.</summary>
    static byte Paeth(byte left, byte above, byte upperLeft) {
        var estimate = left + above - upperLeft;
        var fromLeft = Math.Abs(estimate - left);
        var fromAbove = Math.Abs(estimate - above);
        var fromUpperLeft = Math.Abs(estimate - upperLeft);

        // The order of the comparisons is part of the specification rather than a preference: a tie
        // has to break the same way in every decoder or the image drifts.
        if (fromLeft <= fromAbove && fromLeft <= fromUpperLeft) {
            return left;
        }

        return fromAbove <= fromUpperLeft ? above : upperLeft;
    }
}
