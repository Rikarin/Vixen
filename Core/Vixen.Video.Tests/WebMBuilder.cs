// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;

namespace Vixen.Video.Tests;

/// <summary>Writes the WebM files these tests read.</summary>
/// <remarks>
///     <para>
///         <b>A writer rather than a committed binary.</b> A fixture file says "these bytes decode to
///         this"; a builder says <em>which</em> bytes, in the test that depends on them. Every case
///         here — an unknown-size cluster, a Xiph lace, a block group with a reference, an odd
///         resolution — is a shape a real muxer produces and none of them are shapes that can be
///         asserted about a single checked-in file without twenty of them.
///     </para>
///     <para>
///         It is also the second implementation of the format in the repository, written from the
///         specification rather than from <c>EbmlReader</c>, which is what makes a round trip
///         through both of them worth anything.
///     </para>
/// </remarks>
sealed class WebMBuilder {
    readonly List<PendingCluster> clusters = [];
    readonly List<byte[]> trackEntries = [];

    long durationTicks;
    int cueTrack = 1;
    bool writeCues;

    /// <summary>Nanoseconds per timestamp tick. A million is a millisecond, and is what muxers write.</summary>
    public long TimestampScale { get; set; } = 1_000_000;

    /// <summary>What the file calls itself.</summary>
    public string DocType { get; set; } = "webm";

    /// <summary>Whether the segment states its own size, or leaves it unknown as a live muxer does.</summary>
    public bool UnknownSegmentSize { get; set; }

    /// <summary>Whether the last cluster states its own size.</summary>
    public bool UnknownLastClusterSize { get; set; }

    public WebMBuilder Duration(long ticks) {
        durationTicks = ticks;

        return this;
    }

    public WebMBuilder Cues(int track = 1) {
        writeCues = true;
        cueTrack = track;

        return this;
    }

    public WebMBuilder VideoTrack(
        int number,
        int width,
        int height,
        string fourCc = "I420",
        string codecId = "V_UNCOMPRESSED",
        long defaultDurationNanoseconds = 0,
        int matrixCoefficients = -1,
        int range = -1
    ) {
        using var video = new MemoryStream();

        Element(video, 0xB0, Unsigned((ulong)width));
        Element(video, 0xBA, Unsigned((ulong)height));

        if (fourCc.Length > 0) {
            Element(video, 0x2EB524, Ascii(fourCc));
        }

        if (matrixCoefficients >= 0 || range >= 0) {
            using var colour = new MemoryStream();

            if (matrixCoefficients >= 0) {
                Element(colour, 0x55B1, Unsigned((ulong)matrixCoefficients));
            }

            if (range >= 0) {
                Element(colour, 0x55B9, Unsigned((ulong)range));
            }

            Element(video, 0x55B0, colour.ToArray());
        }

        using var entry = new MemoryStream();

        Element(entry, 0xD7, Unsigned((ulong)number));
        Element(entry, 0x73C5, Unsigned((ulong)(1000 + number)));
        Element(entry, 0x83, Unsigned(1));
        Element(entry, 0x86, Ascii(codecId));

        if (defaultDurationNanoseconds > 0) {
            Element(entry, 0x23E383, Unsigned((ulong)defaultDurationNanoseconds));
        }

        Element(entry, 0xE0, video.ToArray());
        trackEntries.Add(entry.ToArray());

        return this;
    }

    public WebMBuilder AudioTrack(
        int number,
        int sampleRate,
        int channels,
        int bitDepth = 32,
        string codecId = "A_PCM/FLOAT/IEEE",
        byte[]? codecPrivate = null,
        long codecDelayNanoseconds = 0,
        long seekPreRollNanoseconds = 0
    ) {
        using var audio = new MemoryStream();

        Element(audio, 0xB5, Float(sampleRate));
        Element(audio, 0x9F, Unsigned((ulong)channels));
        Element(audio, 0x6264, Unsigned((ulong)bitDepth));

        using var entry = new MemoryStream();

        Element(entry, 0xD7, Unsigned((ulong)number));
        Element(entry, 0x73C5, Unsigned((ulong)(1000 + number)));
        Element(entry, 0x83, Unsigned(2));
        Element(entry, 0x86, Ascii(codecId));

        if (codecPrivate is { Length: > 0 }) {
            Element(entry, 0x63A2, codecPrivate);
        }

        if (codecDelayNanoseconds > 0) {
            Element(entry, 0x56AA, Unsigned((ulong)codecDelayNanoseconds));
        }

        if (seekPreRollNanoseconds > 0) {
            Element(entry, 0x56BB, Unsigned((ulong)seekPreRollNanoseconds));
        }

        Element(entry, 0xE1, audio.ToArray());
        trackEntries.Add(entry.ToArray());

        return this;
    }

    public WebMBuilder Cluster(long timestamp) {
        clusters.Add(new PendingCluster(timestamp));

        return this;
    }

    public WebMBuilder SimpleBlock(int track, short relative, bool keyFrame, byte[] data) {
        using var payload = new MemoryStream();

        WriteSize(payload, track);
        WriteInt16(payload, relative);
        payload.WriteByte(keyFrame ? (byte)0x80 : (byte)0x00);
        payload.Write(data);

        Last().Children.Add((0xA3, payload.ToArray()));

        return this;
    }

    /// <summary>A laced simple block. <paramref name="lacing" /> is 1 Xiph, 2 fixed, 3 EBML.</summary>
    public WebMBuilder LacedBlock(int track, short relative, bool keyFrame, int lacing, params byte[][] frames) {
        using var payload = new MemoryStream();

        WriteSize(payload, track);
        WriteInt16(payload, relative);
        payload.WriteByte((byte)((keyFrame ? 0x80 : 0x00) | (lacing << 1)));
        payload.WriteByte((byte)(frames.Length - 1));

        switch (lacing) {
            case 1:
                for (var index = 0; index < frames.Length - 1; index++) {
                    var remaining = frames[index].Length;

                    while (remaining >= 255) {
                        payload.WriteByte(255);
                        remaining -= 255;
                    }

                    payload.WriteByte((byte)remaining);
                }

                break;

            case 3: {
                WriteSize(payload, frames[0].Length);

                for (var index = 1; index < frames.Length - 1; index++) {
                    WriteSignedSize(payload, frames[index].Length - frames[index - 1].Length);
                }

                break;
            }

            default:
                break;
        }

        foreach (var frame in frames) {
            payload.Write(frame);
        }

        Last().Children.Add((0xA3, payload.ToArray()));

        return this;
    }

    /// <summary>An element nothing knows, inside the current cluster.</summary>
    /// <remarks>
    ///     <c>0xEC</c> is Void, whose payload means nothing by definition — the closest thing the
    ///     format has to "an element written by a muxer that did not exist when the reader did".
    /// </remarks>
    public WebMBuilder Void(int bytes) {
        Last().Children.Add((0xEC, new byte[bytes]));

        return this;
    }

    public WebMBuilder BlockGroup(
        int track,
        short relative,
        byte[] data,
        long durationTicksOrZero = 0,
        bool referenced = false
    ) {
        using var block = new MemoryStream();

        WriteSize(block, track);
        WriteInt16(block, relative);
        block.WriteByte(0);
        block.Write(data);

        using var group = new MemoryStream();

        Element(group, 0xA1, block.ToArray());

        if (durationTicksOrZero > 0) {
            Element(group, 0x9B, Unsigned((ulong)durationTicksOrZero));
        }

        if (referenced) {
            Element(group, 0xFB, Signed(-1));
        }

        Last().Children.Add((0xA0, group.ToArray()));

        return this;
    }

    /// <summary>Assembles the file.</summary>
    public byte[] Build() {
        using var body = new MemoryStream();

        using (var info = new MemoryStream()) {
            Element(info, 0x2AD7B1, Unsigned((ulong)TimestampScale));

            if (durationTicks > 0) {
                Element(info, 0x4489, Float(durationTicks));
            }

            Element(body, 0x1549A966, info.ToArray());
        }

        using (var tracks = new MemoryStream()) {
            foreach (var entry in trackEntries) {
                Element(tracks, 0xAE, entry);
            }

            Element(body, 0x1654AE6B, tracks.ToArray());
        }

        var positions = new List<(long Time, long Offset)>();

        for (var index = 0; index < clusters.Count; index++) {
            var cluster = clusters[index];
            var offset = body.Position;

            using var payload = new MemoryStream();

            Element(payload, 0xE7, Unsigned((ulong)cluster.Timestamp));

            foreach (var (id, bytes) in cluster.Children) {
                Element(payload, id, bytes);
            }

            var unknown = UnknownLastClusterSize && index == clusters.Count - 1;

            WriteId(body, 0x1F43B675);

            if (unknown) {
                body.Write([0xFF]);
            } else {
                WriteSize(body, payload.Length);
            }

            payload.Position = 0;
            payload.CopyTo(body);

            positions.Add((cluster.Timestamp, offset));
        }

        if (writeCues) {
            using var cues = new MemoryStream();

            foreach (var (time, offset) in positions) {
                using var trackPositions = new MemoryStream();

                Element(trackPositions, 0xF7, Unsigned((ulong)cueTrack));
                Element(trackPositions, 0xF1, Unsigned((ulong)offset));

                using var point = new MemoryStream();

                Element(point, 0xB3, Unsigned((ulong)time));
                Element(point, 0xB7, trackPositions.ToArray());
                Element(cues, 0xBB, point.ToArray());
            }

            Element(body, 0x1C53BB6B, cues.ToArray());
        }

        using var file = new MemoryStream();

        using (var header = new MemoryStream()) {
            Element(header, 0x4282, Ascii(DocType));
            Element(file, 0x1A45DFA3, header.ToArray());
        }

        WriteId(file, 0x18538067);

        if (UnknownSegmentSize) {
            file.Write([0xFF]);
        } else {
            WriteSize(file, body.Length);
        }

        body.Position = 0;
        body.CopyTo(file);

        return file.ToArray();
    }

    /// <summary>The file as a seekable stream, which is what a demuxer is usually given.</summary>
    public MemoryStream Stream() => new(Build(), writable: false);

    // ── Primitives ──────────────────────────────────────────────────────────────────────────

    public static void WriteId(Stream stream, uint id) {
        Span<byte> bytes = stackalloc byte[4];

        BinaryPrimitives.WriteUInt32BigEndian(bytes, id);

        var start = 0;

        while (start < 3 && bytes[start] == 0) {
            start++;
        }

        stream.Write(bytes[start..]);
    }

    public static void WriteSize(Stream stream, long value) {
        var length = 1;

        while (length < 8 && value >= (1L << (7 * length)) - 1) {
            length++;
        }

        Span<byte> bytes = stackalloc byte[8];

        for (var index = 0; index < length; index++) {
            bytes[length - 1 - index] = (byte)(value >> (8 * index));
        }

        bytes[0] |= (byte)(1 << (8 - length));
        stream.Write(bytes[..length]);
    }

    /// <summary>A lace's size difference, biased so that it can be negative without a sign bit.</summary>
    static void WriteSignedSize(Stream stream, long value) {
        var length = 1;

        while (length < 8) {
            var half = (1L << ((7 * length) - 1)) - 1;

            if (value >= -half && value <= half) {
                break;
            }

            length++;
        }

        WriteSizeOfWidth(stream, value + ((1L << ((7 * length) - 1)) - 1), length);
    }

    static void WriteSizeOfWidth(Stream stream, long value, int length) {
        Span<byte> bytes = stackalloc byte[8];

        for (var index = 0; index < length; index++) {
            bytes[length - 1 - index] = (byte)(value >> (8 * index));
        }

        bytes[0] |= (byte)(1 << (8 - length));
        stream.Write(bytes[..length]);
    }

    static void Element(Stream stream, uint id, ReadOnlySpan<byte> payload) {
        WriteId(stream, id);
        WriteSize(stream, payload.Length);
        stream.Write(payload);
    }

    static void WriteInt16(Stream stream, short value) {
        Span<byte> bytes = stackalloc byte[2];

        BinaryPrimitives.WriteInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }

    static byte[] Unsigned(ulong value) {
        Span<byte> bytes = stackalloc byte[8];

        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);

        var start = 0;

        while (start < 7 && bytes[start] == 0) {
            start++;
        }

        return bytes[start..].ToArray();
    }

    static byte[] Signed(long value) {
        Span<byte> bytes = stackalloc byte[8];

        BinaryPrimitives.WriteInt64BigEndian(bytes, value);

        var start = 0;

        // Keep one byte of sign: trim leading 0xFF while the next byte still says negative.
        while (start < 7 && bytes[start] == 0xFF && (bytes[start + 1] & 0x80) != 0) {
            start++;
        }

        return bytes[start..].ToArray();
    }

    static byte[] Float(double value) {
        var bytes = new byte[8];

        BinaryPrimitives.WriteDoubleBigEndian(bytes, value);

        return bytes;
    }

    static byte[] Ascii(string value) => System.Text.Encoding.UTF8.GetBytes(value);

    PendingCluster Last() =>
        clusters.Count > 0 ? clusters[^1] : throw new InvalidOperationException("No cluster to add a block to.");

    sealed class PendingCluster(long timestamp) {
        public long Timestamp { get; } = timestamp;

        public List<(uint Id, byte[] Payload)> Children { get; } = [];
    }
}
