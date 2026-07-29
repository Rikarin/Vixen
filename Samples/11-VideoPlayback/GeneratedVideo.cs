// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Text;
using Vixen.Audio.Codecs;

namespace Vixen.Samples.VideoPlayback;

/// <summary>Makes the WebM this sample plays, at start-up, in memory.</summary>
/// <remarks>
///     <para>
///         <b>Why the sample writes its own file instead of carrying one.</b> The engine ships no
///         video codec — that is the whole design of <c>Vixen.Video</c>, and
///         <c>UncompressedVideoCodec</c> is the one decoder it does ship — so a committed fixture
///         would have to be uncompressed too, which at any size worth looking at is megabytes of
///         binary in the repository for something a hundred lines can produce. Generating it also
///         means the sample has no content dependency at all: it runs from a clean clone with no
///         asset pipeline, exactly as <c>01-HelloTriangle</c> does.
///     </para>
///     <para>
///         <b>The bars are authored in RGB and written as YUV</b>, using the forward BT.709
///         limited-range transform — the exact inverse of what the fragment shader does on the way
///         back. That is the point of choosing colour bars: if either half of the arithmetic is
///         wrong, the bars come out the wrong colours on screen, and a green picture or a washed-out
///         one says which half. A test asserts the same thing numerically; this is the version a
///         person can see.
///     </para>
///     <para>
///         The muxer here is deliberately the smallest thing that produces a legal segment — a
///         header, one track, and a cluster per frame. It is not <c>Vixen.Video</c>'s: that module
///         reads containers and does not write them, and a writer added to justify a sample would be
///         a feature nobody asked for.
///     </para>
/// </remarks>
static class GeneratedVideo {
    /// <summary>The picture's width. Small, because every frame is stored uncompressed.</summary>
    public const int Width = 320;

    /// <summary>Its height, at 16:9 — so the letterboxing in the shader is visible in a square window.</summary>
    public const int Height = 180;

    /// <summary>How many frames. Three seconds at 25, which loops without anybody noticing the seam.</summary>
    public const int FrameCount = 75;

    /// <summary>
    ///     Nanoseconds a frame lasts. 25 rather than 30 for one reason: 40 ms is exact in the
    ///     millisecond ticks a segment's timestamp scale defaults to, and 33⅓ is not.
    /// </summary>
    public const long FrameNanoseconds = 40_000_000;

    /// <summary>Milliseconds a frame lasts, which is what a block's timestamp is in.</summary>
    const long FrameMilliseconds = FrameNanoseconds / 1_000_000;

    /// <summary>The rate Opus decodes at, whatever it was fed.</summary>
    const int AudioRate = 48_000;

    /// <summary>Frames in one Opus packet — twenty milliseconds, which is what every muxer writes.</summary>
    const int PacketFrames = 960;

    /// <summary>How many Opus packets fit in one video frame's worth of time.</summary>
    const int PacketsPerFrame = (int)(FrameMilliseconds / 20);

    /// <summary>
    ///     The encoder's lookahead, in 48 kHz samples: the priming a decoder must discard.
    /// </summary>
    /// <remarks>
    ///     312 is Opus's own for every configuration this uses, and it is what a muxer writes into
    ///     both <c>CodecDelay</c> and the <c>OpusHead</c>. Stating it is what makes the sample
    ///     exercise the pre-skip path rather than merely have one.
    /// </remarks>
    const int PreSkip = 312;

    /// <summary>Writes the whole segment.</summary>
    /// <returns>A WebM, complete and legal, about eight megabytes of it.</returns>
    public static byte[] Build() {
        using var body = new MemoryStream();

        WriteInfo(body);
        WriteTracks(body);

        var frame = new byte[(Width * Height) + (2 * ((Width + 1) / 2) * ((Height + 1) / 2))];

        using var encoder = new OpusPacketEncoder(channels: 1, frameMilliseconds: 20, bitrate: 48_000);

        var pcm = new float[PacketFrames];
        var packet = new byte[OpusPacketEncoder.MaxPacketBytes];

        for (var index = 0; index < FrameCount; index++) {
            Paint(frame, index);
            WriteCluster(body, index, frame, encoder, pcm, packet);
        }

        using var file = new MemoryStream();

        using (var header = new MemoryStream()) {
            Element(header, 0x4282, Encoding.UTF8.GetBytes("webm"));
            Element(file, 0x1A45DFA3, header.ToArray());
        }

        WriteId(file, 0x18538067);
        WriteSize(file, body.Length);
        body.Position = 0;
        body.CopyTo(file);

        return file.ToArray();
    }

    // ── The picture ─────────────────────────────────────────────────────────────────────────

    /// <summary>Paints one frame: colour bars, and a white column sweeping across them.</summary>
    /// <param name="frame">The planes, in I420 order.</param>
    /// <param name="index">Which frame.</param>
    /// <remarks>
    ///     The sweep is what makes the clock visible. A still picture proves the decode and nothing
    ///     about the timing; a column that crosses the screen in exactly three seconds shows a
    ///     dropped frame as a stutter and a wrong frame rate as the wrong speed.
    /// </remarks>
    static void Paint(byte[] frame, int index) {
        var chromaWidth = (Width + 1) / 2;
        var chromaHeight = (Height + 1) / 2;
        var lumaSize = Width * Height;
        var sweep = index * Width / FrameCount;

        for (var y = 0; y < Height; y++) {
            for (var x = 0; x < Width; x++) {
                var (red, green, blue) = ColourAt(x, y, sweep);

                frame[(y * Width) + x] = Luma(red, green, blue);
            }
        }

        for (var y = 0; y < chromaHeight; y++) {
            for (var x = 0; x < chromaWidth; x++) {
                // Sampled at the top-left of each two-by-two block rather than averaged over it. The
                // bars are constant within a block everywhere except at one edge, so averaging would
                // buy one softer column and cost the clarity of what this is doing.
                var (red, green, blue) = ColourAt(x * 2, y * 2, sweep);

                frame[lumaSize + (y * chromaWidth) + x] = Blue(red, green, blue);
                frame[lumaSize + (chromaWidth * chromaHeight) + (y * chromaWidth) + x] = Red(red, green, blue);
            }
        }
    }

    /// <summary>The colour of one pixel, in 0..1 RGB.</summary>
    static (float Red, float Green, float Blue) ColourAt(int x, int y, int sweep) {
        if (x >= sweep && x < sweep + 6) {
            return (1f, 1f, 1f);
        }

        // The bottom fifth is a black-to-white ramp, which is where a range error shows: on limited
        // range decoded as full, the black end lifts off the floor and the white end never arrives.
        if (y >= Height * 4 / 5) {
            var level = x / (float)(Width - 1);

            return (level, level, level);
        }

        // The classic seven, in order of descending luminance.
        return (x * 7 / Width) switch {
            0 => (1f, 1f, 1f),
            1 => (1f, 1f, 0f),
            2 => (0f, 1f, 1f),
            3 => (0f, 1f, 0f),
            4 => (1f, 0f, 1f),
            5 => (1f, 0f, 0f),
            _ => (0f, 0f, 1f)
        };
    }

    // BT.709, which is what the track declares and what the shader converts back with. Kg is
    // whatever is left, and writing it out rather than deriving it would be a fourth constant to
    // keep in step with the other three.
    const float Kr = 0.2126f;
    const float Kb = 0.0722f;

    static byte Luma(float red, float green, float blue) =>
        Clamp(16f + (219f * Brightness(red, green, blue)));

    static byte Blue(float red, float green, float blue) =>
        Clamp(128f + (224f * 0.5f * (blue - Brightness(red, green, blue)) / (1f - Kb)));

    static byte Red(float red, float green, float blue) =>
        Clamp(128f + (224f * 0.5f * (red - Brightness(red, green, blue)) / (1f - Kr)));

    static float Brightness(float red, float green, float blue) =>
        (Kr * red) + ((1f - Kr - Kb) * green) + (Kb * blue);

    static byte Clamp(float value) => (byte)Math.Clamp(MathF.Round(value), 0f, 255f);

    // ── The container ───────────────────────────────────────────────────────────────────────

    static void WriteInfo(Stream body) {
        using var info = new MemoryStream();

        Element(info, 0x2AD7B1, Unsigned(1_000_000));                       // TimestampScale: a millisecond
        Element(info, 0x4489, Float(FrameCount * FrameMilliseconds));       // Duration, in those ticks
        Element(body, 0x1549A966, info.ToArray());
    }

    static void WriteTracks(Stream body) {
        using var video = new MemoryStream();

        Element(video, 0xB0, Unsigned(Width));
        Element(video, 0xBA, Unsigned(Height));
        Element(video, 0x2EB524, Encoding.UTF8.GetBytes("I420"));

        using (var colour = new MemoryStream()) {
            Element(colour, 0x55B1, Unsigned(1));                           // MatrixCoefficients: BT.709
            Element(colour, 0x55B9, Unsigned(1));                           // Range: broadcast
            Element(video, 0x55B0, colour.ToArray());
        }

        using var entry = new MemoryStream();

        Element(entry, 0xD7, Unsigned(1));                                  // TrackNumber
        Element(entry, 0x73C5, Unsigned(1));                                // TrackUID
        Element(entry, 0x83, Unsigned(1));                                  // TrackType: video
        Element(entry, 0x86, Encoding.UTF8.GetBytes("V_UNCOMPRESSED"));
        Element(entry, 0x23E383, Unsigned(FrameNanoseconds));               // DefaultDuration
        Element(entry, 0xE0, video.ToArray());

        using var tracks = new MemoryStream();

        Element(tracks, 0xAE, entry.ToArray());
        Element(tracks, 0xAE, AudioTrack());
        Element(body, 0x1654AE6B, tracks.ToArray());
    }

    /// <summary>The Opus track, and the two places its priming has to be written down.</summary>
    static byte[] AudioTrack() {
        using var audio = new MemoryStream();

        Element(audio, 0xB5, Float(AudioRate));
        Element(audio, 0x9F, Unsigned(1));

        using var entry = new MemoryStream();

        Element(entry, 0xD7, Unsigned(2));                                  // TrackNumber
        Element(entry, 0x73C5, Unsigned(2));                                // TrackUID
        Element(entry, 0x83, Unsigned(2));                                  // TrackType: audio
        Element(entry, 0x86, Encoding.UTF8.GetBytes("A_OPUS"));
        Element(entry, 0x63A2, OpusHead());                                 // CodecPrivate
        Element(entry, 0x56AA, Unsigned(PreSkip * 1_000_000_000L / AudioRate));   // CodecDelay
        Element(entry, 0x56BB, Unsigned(80_000_000));                       // SeekPreRoll: Opus's own
        Element(entry, 0xE1, audio.ToArray());

        return entry.ToArray();
    }

    /// <summary>The nineteen bytes an Opus track's CodecPrivate is.</summary>
    static byte[] OpusHead() {
        var header = new byte[19];

        "OpusHead"u8.CopyTo(header);
        header[8] = 1;                                                      // version
        header[9] = 1;                                                      // channels
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), PreSkip);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), AudioRate);

        return header;
    }

    /// <summary>One cluster: a picture, and the two Opus packets that play under it.</summary>
    /// <remarks>
    ///     Interleaved in one cluster rather than written as two runs, because that is what a muxer
    ///     does and what the demuxer's buffering is shaped for — a file with all the video first
    ///     would make a player hold the entire sound track in memory.
    /// </remarks>
    static void WriteCluster(
        Stream body,
        int index,
        byte[] frame,
        OpusPacketEncoder encoder,
        float[] pcm,
        byte[] packet
    ) {
        using var cluster = new MemoryStream();

        Element(cluster, 0xE7, Unsigned(index * FrameMilliseconds));        // Timestamp
        Element(cluster, 0xA3, Block(1, 0, frame));                         // the picture

        for (var slot = 0; slot < PacketsPerFrame; slot++) {
            var first = ((index * PacketsPerFrame) + slot) * PacketFrames;

            Sound(pcm, first);

            var written = encoder.Encode(pcm, packet);

            Element(cluster, 0xA3, Block(2, (short)(slot * 20), packet.AsSpan(0, written)));
        }

        Element(body, 0x1F43B675, cluster.ToArray());
    }

    /// <summary>A SimpleBlock's payload: a track, a relative time, a flags byte, and the data.</summary>
    static byte[] Block(int track, short relative, ReadOnlySpan<byte> data) {
        using var block = new MemoryStream();

        WriteSize(block, track);
        WriteInt16(block, relative);
        block.WriteByte(0x80);                                              // a key frame, no lacing
        block.Write(data);

        return block.ToArray();
    }

    /// <summary>Twenty milliseconds of sound, starting at a frame.</summary>
    /// <remarks>
    ///     <b>A beep on every second, and silence between them.</b> A continuous tone would prove the
    ///     sound plays and nothing about when: what a sync check needs is a moment audible enough to
    ///     line up against something visible, and the sweep is at a third of the screen per second.
    ///     If the beep and the bar drift apart, the clock is wrong — which is the one failure a
    ///     picture on its own cannot show.
    /// </remarks>
    static void Sound(float[] pcm, int firstFrame) {
        const int beepFrames = AudioRate * 60 / 1_000;

        for (var index = 0; index < pcm.Length; index++) {
            var frame = firstFrame + index;
            var intoSecond = frame % AudioRate;

            pcm[index] = intoSecond < beepFrames
                ? 0.35f * MathF.Sin(2f * MathF.PI * 880f * frame / AudioRate)
                : 0f;
        }
    }

    // ── EBML primitives ─────────────────────────────────────────────────────────────────────

    static void Element(Stream stream, uint id, ReadOnlySpan<byte> payload) {
        WriteId(stream, id);
        WriteSize(stream, payload.Length);
        stream.Write(payload);
    }

    /// <summary>Writes an id, which keeps its marker bits and is therefore already the whole value.</summary>
    static void WriteId(Stream stream, uint id) {
        Span<byte> bytes = stackalloc byte[4];

        BinaryPrimitives.WriteUInt32BigEndian(bytes, id);

        var start = 0;

        while (start < 3 && bytes[start] == 0) {
            start++;
        }

        stream.Write(bytes[start..]);
    }

    /// <summary>Writes a size, whose marker bit says how many bytes it took.</summary>
    static void WriteSize(Stream stream, long value) {
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

    static void WriteInt16(Stream stream, short value) {
        Span<byte> bytes = stackalloc byte[2];

        BinaryPrimitives.WriteInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }

    static byte[] Unsigned(long value) {
        Span<byte> bytes = stackalloc byte[8];

        BinaryPrimitives.WriteInt64BigEndian(bytes, value);

        var start = 0;

        while (start < 7 && bytes[start] == 0) {
            start++;
        }

        return bytes[start..].ToArray();
    }

    static byte[] Float(double value) {
        var bytes = new byte[8];

        BinaryPrimitives.WriteDoubleBigEndian(bytes, value);

        return bytes;
    }
}
