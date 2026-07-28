// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Text;

namespace Vixen.Video.Containers;

/// <summary>An element's identifier and how many bytes of payload follow it.</summary>
/// <param name="Id">The element id, marker bits included, as the specification writes it.</param>
/// <param name="Size">The payload size in bytes, or <c>-1</c> when the writer said it did not know.</param>
/// <param name="HeaderSize">How many bytes the id and the size took, for position arithmetic.</param>
public readonly record struct EbmlElement(uint Id, long Size, int HeaderSize) {
    /// <summary>Whether the writer declined to state a size.</summary>
    /// <remarks>
    ///     Legal, and not rare: a live muxer does not know how long a segment is until it ends, so it
    ///     writes the all-ones size and the reader is expected to stop at the first element that
    ///     cannot be a child. Every WebM stream produced while recording has at least one.
    /// </remarks>
    public bool IsUnknownSize => Size < 0;
}

/// <summary>
///     The primitive layer of EBML: variable-width integers, and the handful of scalar types
///     Matroska stores its numbers as.
/// </summary>
/// <remarks>
///     <para>
///         EBML is two ideas. A number is written with its length in unary — the leading zero bits of
///         the first byte say how many bytes it takes — and everything is an id, a length and either
///         a payload or more elements. That is the whole format; the rest of Matroska is a very long
///         list of which ids mean what.
///     </para>
///     <para>
///         <b>Ids keep their marker bit and sizes lose theirs.</b> This trips up every first
///         implementation. <c>0xA3</c> is the id of a SimpleBlock and stays <c>0xA3</c>; a size byte
///         of <c>0xA3</c> means 35. The specification is written that way — ids are quoted with their
///         markers — so reading them the same way is what lets a table of ids be compared against the
///         specification by eye.
///     </para>
///     <para>
///         <b>It reads a <see cref="Stream" />, not a byte array.</b> A video file is the one asset
///         that will not be resident, and the demuxer above this is built to hold a few hundred
///         kilobytes of it at a time regardless of whether the file is four megabytes or four
///         gigabytes.
///     </para>
/// </remarks>
public sealed class EbmlReader {
    readonly byte[] scratch = new byte[8];

    /// <summary>Reads over a stream.</summary>
    /// <param name="stream">The bytes. Owned by the caller.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream" /> is null.</exception>
    public EbmlReader(Stream stream) {
        ArgumentNullException.ThrowIfNull(stream);

        Stream = stream;
    }

    /// <summary>The stream being read.</summary>
    public Stream Stream { get; }

    /// <summary>Where the next read starts.</summary>
    public long Position {
        get => Stream.Position;
        set => Stream.Position = value;
    }

    /// <summary>How long the stream is, or <c>-1</c> if it will not say.</summary>
    public long Length => Stream.CanSeek ? Stream.Length : -1;

    /// <summary>Whether the stream has ended.</summary>
    public bool AtEnd => Stream.CanSeek && Stream.Position >= Stream.Length;

    /// <summary>Reads an element's id and size.</summary>
    /// <param name="element">The element, when there was one.</param>
    /// <returns>Whether one was read. False at the end of the stream.</returns>
    /// <exception cref="InvalidDataException">The bytes are not a well-formed element header.</exception>
    public bool TryReadElement(out EbmlElement element) {
        element = default;

        var start = Position;
        var first = Stream.ReadByte();

        if (first < 0) {
            return false;
        }

        var idLength = LengthOf((byte)first);

        if (idLength == 0) {
            throw new InvalidDataException(
                $"The byte at {start} is 0x{first:X2}, which starts no valid EBML id."
            );
        }

        var id = (uint)first;

        for (var index = 1; index < idLength; index++) {
            var next = Stream.ReadByte();

            if (next < 0) {
                throw new InvalidDataException($"The element id at {start} runs past the end of the stream.");
            }

            id = (id << 8) | (uint)next;
        }

        var size = ReadSize(out var sizeLength);

        element = new EbmlElement(id, size, idLength + sizeLength);

        return true;
    }

    /// <summary>Reads a size, whose marker bit is stripped.</summary>
    /// <param name="length">How many bytes it took.</param>
    /// <returns>The size, or <c>-1</c> for the all-ones "unknown".</returns>
    /// <exception cref="InvalidDataException">The bytes are not a well-formed size.</exception>
    public long ReadSize(out int length) {
        var start = Position;
        var first = Stream.ReadByte();

        if (first < 0) {
            throw new InvalidDataException($"An element size was expected at {start} and the stream ended.");
        }

        length = LengthOf((byte)first);

        if (length == 0) {
            throw new InvalidDataException(
                $"The byte at {start} is 0x{first:X2}, which starts no valid EBML size."
            );
        }

        // The marker is the highest set bit of the first byte; what remains is the top of the value.
        var value = (ulong)(first & ((1 << (8 - length)) - 1));
        var allOnes = value == (ulong)((1 << (8 - length)) - 1);

        for (var index = 1; index < length; index++) {
            var next = Stream.ReadByte();

            if (next < 0) {
                throw new InvalidDataException($"The element size at {start} runs past the end of the stream.");
            }

            value = (value << 8) | (uint)next;
            allOnes &= next == 0xFF;
        }

        return allOnes ? -1 : (long)value;
    }

    /// <summary>Reads a big-endian unsigned integer of a stated width.</summary>
    /// <param name="size">How many bytes, 0 to 8. Zero is legal and means the element's default.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidDataException">The stream ended, or the width is impossible.</exception>
    public ulong ReadUnsigned(long size) {
        if (size is < 0 or > 8) {
            throw new InvalidDataException($"An integer element cannot be {size} bytes wide.");
        }

        Fill(scratch.AsSpan(0, (int)size));

        var value = 0UL;

        for (var index = 0; index < size; index++) {
            value = (value << 8) | scratch[index];
        }

        return value;
    }

    /// <summary>Reads a big-endian signed integer of a stated width.</summary>
    /// <param name="size">How many bytes, 0 to 8.</param>
    /// <returns>The value, sign-extended from its width.</returns>
    /// <exception cref="InvalidDataException">The stream ended, or the width is impossible.</exception>
    public long ReadSigned(long size) {
        var value = ReadUnsigned(size);

        if (size is <= 0 or >= 8) {
            return (long)value;
        }

        // Sign-extend from the top bit of the width that was actually written: a one-byte −1 is
        // 0xFF, and reading it as 255 would make a reference block point forwards.
        var shift = 64 - (int)(size * 8);

        return (long)(value << shift) >> shift;
    }

    /// <summary>Reads an IEEE float of a stated width.</summary>
    /// <param name="size">Either 4 or 8. Zero is legal and reads as zero.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidDataException">The width is neither 4 nor 8.</exception>
    public double ReadFloat(long size) {
        switch (size) {
            case 0:
                return 0d;

            case 4:
                Fill(scratch.AsSpan(0, 4));

                return BinaryPrimitives.ReadSingleBigEndian(scratch);

            case 8:
                Fill(scratch.AsSpan(0, 8));

                return BinaryPrimitives.ReadDoubleBigEndian(scratch);

            default:
                throw new InvalidDataException($"A float element cannot be {size} bytes wide.");
        }
    }

    /// <summary>Reads an ASCII or UTF-8 string of a stated length.</summary>
    /// <param name="size">How many bytes.</param>
    /// <returns>The string, with any trailing padding zeroes removed.</returns>
    /// <exception cref="InvalidDataException">The stream ended.</exception>
    /// <remarks>
    ///     The trailing zeroes are not paranoia: Matroska allows a string element to be padded with
    ///     them so a writer can rewrite it in place without moving the file, and a codec id compared
    ///     with <c>"V_VP9\0\0"</c> matches nothing.
    /// </remarks>
    public string ReadString(long size) {
        if (size <= 0) {
            return string.Empty;
        }

        var bytes = new byte[size];

        Fill(bytes);

        var end = bytes.Length;

        while (end > 0 && bytes[end - 1] == 0) {
            end--;
        }

        return Encoding.UTF8.GetString(bytes, 0, end);
    }

    /// <summary>Reads raw bytes.</summary>
    /// <param name="destination">Where to put them. Filled completely.</param>
    /// <exception cref="InvalidDataException">The stream ended first.</exception>
    public void ReadBytes(Span<byte> destination) => Fill(destination);

    /// <summary>Moves past an element's payload.</summary>
    /// <param name="size">How many bytes to skip.</param>
    /// <exception cref="InvalidDataException">The stream ended first.</exception>
    /// <remarks>
    ///     Seeks where it can and reads where it cannot, because a non-seekable stream is a legal
    ///     source — a video played straight off a network read — and skipping an element the reader
    ///     does not care about is most of what parsing a container is.
    /// </remarks>
    public void Skip(long size) {
        if (size <= 0) {
            return;
        }

        if (Stream.CanSeek) {
            var target = Stream.Position + size;

            if (target > Stream.Length) {
                throw new InvalidDataException($"An element claims {size} bytes and the stream has fewer.");
            }

            Stream.Position = target;

            return;
        }

        Span<byte> sink = stackalloc byte[512];

        while (size > 0) {
            var wanted = (int)Math.Min(size, sink.Length);
            var read = Stream.Read(sink[..wanted]);

            if (read <= 0) {
                throw new InvalidDataException($"An element claims {size} more bytes and the stream ended.");
            }

            size -= read;
        }
    }

    /// <summary>How many bytes a variable-width integer starting with a byte occupies.</summary>
    /// <param name="first">The first byte.</param>
    /// <returns>The length, 1 to 8, or <c>0</c> if the byte is zero and therefore starts nothing.</returns>
    /// <remarks>
    ///     A leading zero byte would mean a nine-byte or longer integer, which EBML does not have.
    ///     Reporting zero rather than throwing lets the caller say which of the two things — an id or
    ///     a size — was malformed, and where.
    /// </remarks>
    public static int LengthOf(byte first) =>
        first == 0 ? 0 : System.Numerics.BitOperations.LeadingZeroCount((uint)first << 24) + 1;

    void Fill(Span<byte> destination) {
        var filled = 0;

        while (filled < destination.Length) {
            var read = Stream.Read(destination[filled..]);

            if (read <= 0) {
                throw new InvalidDataException(
                    $"{destination.Length} bytes were expected and the stream ended after {filled}."
                );
            }

            filled += read;
        }
    }
}
