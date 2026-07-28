// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;

namespace Vixen.Audio.Codecs;

/// <summary>An Ogg page, without its body.</summary>
/// <param name="Granule">Where the last packet finishing on this page ends, in the codec's own units.</param>
/// <param name="Serial">Which logical stream it belongs to.</param>
/// <param name="Sequence">Its number within that stream.</param>
/// <param name="Continued">Whether its first segment continues a packet from the previous page.</param>
/// <param name="First">Whether it is that stream's first page.</param>
/// <param name="Last">Whether it is that stream's last.</param>
readonly record struct OggPageHeader(
    long Granule,
    uint Serial,
    uint Sequence,
    bool Continued,
    bool First,
    bool Last
);

/// <summary>Takes Ogg pages apart into the packets a codec wants.</summary>
/// <remarks>
///     <para>
///         <b>Written here because Concentus has no container.</b> Opus is a codec and Ogg is a
///         container, and the two are separate packages by separate people — the decoder takes a
///         packet and knows nothing about where it came from. NVorbis is the exception rather than
///         the rule: a Vorbis stream is only ever in an Ogg, so the library that decodes one reads
///         the other, and there is nothing here for it to use.
///     </para>
///     <para>
///         <b>The format is small.</b> A page is a fixed header, a table of segment lengths, and the
///         segments; a packet is however many segments it takes until one of them is shorter than
///         255. That rule is the whole of the framing, and it is why a page can hold many small
///         packets or a fraction of one large one.
///     </para>
///     <para>
///         <b>The checksum is not verified.</b> It would catch a corrupt file, and a corrupt file is
///         a content-build problem rather than a runtime one — a game that has shipped is reading
///         bytes it produced. Verifying costs a pass over every byte of every page, on a thread that
///         is decoding audio in real time, for an error nobody can act on.
///     </para>
///     <para>
///         One logical stream. A multiplexed Ogg — video with sound — is a different job, and the
///         first serial number seen is the one this follows.
///     </para>
/// </remarks>
sealed class OggReader(Stream stream, bool leaveOpen = false) : IDisposable {
    const int MaxSegments = 255;

    readonly byte[] header = new byte[27];
    readonly byte[] segments = new byte[MaxSegments];
    byte[] packet = new byte[4_096];

    int packetLength;
    int carried;

    /// <summary>The serial number of the stream being followed, once one has been seen.</summary>
    public uint Serial { get; private set; }

    /// <summary>The granule of the last page read.</summary>
    public long Granule { get; private set; }

    /// <summary>Whether the last page read said it was the stream's last.</summary>
    public bool AtEnd { get; private set; }

    /// <summary>Whether the underlying stream can be rewound.</summary>
    public bool CanSeek => stream.CanSeek;

    /// <summary>Reads the next whole packet.</summary>
    /// <param name="length">How many bytes of the returned buffer are the packet.</param>
    /// <returns>The buffer it is in, or null at the end of the stream.</returns>
    /// <remarks>
    ///     The buffer is this reader's and is valid until the next call. A packet spanning pages is
    ///     assembled here, which is the one thing that makes this more than a loop over pages.
    /// </remarks>
    public byte[]? ReadPacket(out int length) {
        while (true) {
            if (pending > 0) {
                if (TakeSegments(out length)) {
                    return packet;
                }

                // The page ran out mid-packet. Its tail is carried and the next page continues it.
                continue;
            }

            if (!ReadPage()) {
                // A packet left half-assembled at the end of the file is a truncated file. What was
                // collected is not a packet and handing it to a decoder would be worse than silence.
                length = 0;
                return null;
            }
        }
    }

    /// <summary>Goes back to the beginning and forgets everything.</summary>
    /// <exception cref="NotSupportedException">The stream cannot seek.</exception>
    public void Rewind() {
        if (!CanSeek) {
            throw new NotSupportedException("This Ogg stream is not seekable.");
        }

        stream.Position = 0;
        pending = 0;
        cursor = 0;
        packetLength = 0;
        carried = 0;
        Granule = 0;
        AtEnd = false;
        Serial = 0;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (!leaveOpen) {
            stream.Dispose();
        }
    }

    int pending;
    int cursor;

    /// <summary>Pulls segments off the current page until one of them ends a packet.</summary>
    /// <returns>Whether a whole packet came out.</returns>
    bool TakeSegments(out int length) {
        while (pending > 0) {
            var size = segments[cursor++];
            pending--;

            Grow(packetLength + size);
            stream.ReadExactly(packet.AsSpan(packetLength, size));
            packetLength += size;

            // A segment shorter than the maximum is the end of a packet. One exactly at the maximum
            // means the packet continues — which is why a packet whose length is a multiple of 255
            // is followed by a zero-length segment, and why that is not a special case here.
            if (size < MaxSegments) {
                length = packetLength;
                packetLength = 0;
                carried = 0;
                return true;
            }

            carried = packetLength;
        }

        length = 0;
        return false;
    }

    /// <summary>Reads the next page's header and segment table, leaving the body to be read segment by segment.</summary>
    bool ReadPage() {
        while (true) {
            if (!TryFillHeader()) {
                return false;
            }

            var page = Parse();
            var count = header[26];
            stream.ReadExactly(segments.AsSpan(0, count));

            if (Serial == 0) {
                Serial = page.Serial;
            }

            if (page.Serial != Serial) {
                // Another logical stream multiplexed into the same file. Its segments are skipped
                // wholesale rather than being fed to a decoder that would make nonsense of them.
                var skip = 0;

                for (var i = 0; i < count; i++) {
                    skip += segments[i];
                }

                Skip(skip);
                continue;
            }

            // A page that does not continue what came before invalidates a half-assembled packet.
            // That happens after a seek, and dropping the fragment is the only correct answer.
            if (!page.Continued && carried > 0) {
                packetLength = 0;
                carried = 0;
            }

            Granule = page.Granule;
            AtEnd = page.Last;
            pending = count;
            cursor = 0;
            return true;
        }
    }

    /// <summary>Finds the next capture pattern and reads the fixed part of the header.</summary>
    /// <remarks>
    ///     Resynchronising rather than failing, because a stream may be joined part way through — a
    ///     seek lands in the middle of a page, and the next <c>OggS</c> is where the format says to
    ///     pick it up again.
    /// </remarks>
    bool TryFillHeader() {
        var matched = 0;

        while (matched < 4) {
            var next = stream.ReadByte();

            if (next < 0) {
                return false;
            }

            matched = next == "OggS"u8[matched] ? matched + 1 : next == 'O' ? 1 : 0;
        }

        "OggS"u8.CopyTo(header);

        try {
            stream.ReadExactly(header.AsSpan(4, header.Length - 4));
        } catch (EndOfStreamException) {
            return false;
        }

        return true;
    }

    OggPageHeader Parse() {
        var flags = header[5];

        return new(
            BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(6)),
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(14)),
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(18)),
            (flags & 0x01) != 0,
            (flags & 0x02) != 0,
            (flags & 0x04) != 0
        );
    }

    void Skip(int count) {
        if (stream.CanSeek) {
            stream.Position += count;
            return;
        }

        Span<byte> discard = stackalloc byte[256];

        while (count > 0) {
            var taken = stream.Read(discard[..Math.Min(count, discard.Length)]);

            if (taken <= 0) {
                return;
            }

            count -= taken;
        }
    }

    void Grow(int wanted) {
        if (packet.Length >= wanted) {
            return;
        }

        var size = packet.Length;

        while (size < wanted) {
            size *= 2;
        }

        Array.Resize(ref packet, size);
    }
}
