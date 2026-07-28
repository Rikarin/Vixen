// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Concentus;
using Vixen.Audio.Streaming;

namespace Vixen.Audio.Codecs;

/// <summary>Opus in an Ogg, decoded as it plays.</summary>
/// <remarks>
///     <para>
///         <b>Opus is the better codec and the more awkward one.</b> It beats Vorbis at every bitrate
///         and is the only sensible choice for voice, but the codec and the container come from
///         different places — Concentus takes a packet and knows nothing about Ogg, so
///         <see cref="OggReader" /> is ours.
///     </para>
///     <para>
///         <b>Always 48 kHz, whatever the file says.</b> Opus decodes at 8, 12, 16, 24 or 48, and
///         48 is the only rate at which nothing is resampled on the way out — the <c>OpusHead</c>'s
///         "input sample rate" is a note about what was fed to the encoder and has no bearing on
///         what comes out.
///     </para>
///     <para>
///         <b>The pre-skip is not optional.</b> Every Opus stream begins with priming samples the
///         encoder needed and the listener must not hear; a decoder that ignores it starts every
///         track with a few milliseconds of an artefact. The count is in the header and those samples
///         are discarded here.
///     </para>
/// </remarks>
public sealed class OpusStreamDecoder : IAudioStreamDecoder {
    /// <summary>The rate Opus is decoded at, whatever the file was encoded from.</summary>
    public const int DecodeRate = 48_000;

    // 120 ms is the longest packet Opus defines, which at 48 kHz is 5 760 frames.
    const int MaxPacketFrames = 5_760;

    readonly OggReader ogg;
    readonly IOpusDecoder decoder;
    readonly float[] decoded;
    readonly int preSkip;

    int available;
    int consumed;
    long position;
    bool finished;

    /// <summary>Opens a track from a file.</summary>
    /// <param name="path">Where it is.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path" /> is null.</exception>
    /// <exception cref="InvalidDataException">It is not an Ogg Opus stream.</exception>
    public OpusStreamDecoder(string path)
        : this(OpenFile(path), leaveOpen: false) { }

    /// <summary>Opens a track from a stream.</summary>
    /// <param name="stream">The bytes. Must be seekable for <see cref="Seek" /> to work.</param>
    /// <param name="leaveOpen">Whether the stream outlives this decoder.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream" /> is null.</exception>
    /// <exception cref="InvalidDataException">It is not an Ogg Opus stream.</exception>
    public OpusStreamDecoder(Stream stream, bool leaveOpen = false) {
        ArgumentNullException.ThrowIfNull(stream);
        ogg = new(stream, leaveOpen);

        try {
            var head = ogg.ReadPacket(out var length);

            if (head is null || length < 19 || !head.AsSpan(0, 8).SequenceEqual("OpusHead"u8)) {
                throw new InvalidDataException("The stream does not begin with an OpusHead packet.");
            }

            var channels = head[9];

            if (channels is < 1 or > 2) {
                throw new InvalidDataException($"An Opus stream of {channels} channels is not supported.");
            }

            preSkip = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(10));
            Format = new AudioFormat(DecodeRate, channels);

            // OpusTags, which is metadata and is skipped. A stream without it is malformed, and a
            // decoder that tried to decode it as audio would produce noise.
            var tags = ogg.ReadPacket(out var tagLength);

            if (tags is null || tagLength < 8 || !tags.AsSpan(0, 8).SequenceEqual("OpusTags"u8)) {
                throw new InvalidDataException("The stream has no OpusTags packet after its header.");
            }

            decoder = OpusCodecFactory.CreateDecoder(DecodeRate, channels);
            decoded = new float[MaxPacketFrames * channels];
            CanSeek = ogg.CanSeek;
            Skip(preSkip);
        } catch {
            ogg.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public AudioFormat Format { get; }

    /// <inheritdoc />
    /// <remarks>
    ///     <b>Not known without reading the whole file.</b> The length is the last page's granule,
    ///     which is at the end — and finding it means seeking there and scanning back for a capture
    ///     pattern, on a stream that may not seek. A track's length is a thing the content build
    ///     knows and can put in the asset; the streaming path does not need it.
    /// </remarks>
    public long FrameCount => -1;

    /// <inheritdoc />
    public long Position => position;

    /// <inheritdoc />
    public bool CanSeek { get; }

    /// <inheritdoc />
    public int Decode(Span<float> destination, int frameCount) {
        var channels = Format.Channels;
        var wanted = Math.Min(frameCount, destination.Length / channels);
        var written = 0;

        while (written < wanted) {
            if (consumed >= available && !Fill()) {
                break;
            }

            var taking = Math.Min(wanted - written, available - consumed);

            decoded.AsSpan(consumed * channels, taking * channels)
                .CopyTo(destination[(written * channels)..]);

            consumed += taking;
            written += taking;
        }

        position += written;
        return written;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <b>Rewind and decode forward.</b> Seeking an Opus stream properly means bisecting the file
    ///     on page granules and then decoding a little before the target to let the decoder's state
    ///     settle — which is a real amount of code for something a game does at a loop point and
    ///     almost nowhere else. Decoding forward is correct at every position, costs a few
    ///     milliseconds a second of audio skipped, and happens on the pump thread where that is
    ///     affordable.
    /// </remarks>
    public void Seek(long frame) {
        if (!CanSeek) {
            throw new NotSupportedException("This Opus stream is not seekable.");
        }

        Restart();
        Skip(Math.Max(frame, 0));
        position = Math.Max(frame, 0);
    }

    /// <inheritdoc />
    public void Dispose() {
        decoder.Dispose();
        ogg.Dispose();
    }

    /// <summary>Decodes one packet into the holding buffer.</summary>
    /// <returns>Whether there was one.</returns>
    bool Fill() {
        if (finished) {
            return false;
        }

        var packet = ogg.ReadPacket(out var length);

        if (packet is null || length <= 0) {
            finished = true;
            return false;
        }

        int frames;

        try {
            frames = decoder.Decode(packet.AsSpan(0, length), decoded.AsSpan(), MaxPacketFrames, false);
        } catch (OpusException) {
            // A packet the decoder would not take. Treating it as the end rather than throwing: a
            // damaged tail is a content problem, and stopping the music is a better answer than
            // stopping the game.
            finished = true;
            return false;
        }

        available = Math.Max(frames, 0);
        consumed = 0;
        return available > 0;
    }

    /// <summary>Decodes and discards, for the pre-skip and for seeking.</summary>
    void Skip(long frames) {
        while (frames > 0) {
            if (consumed >= available && !Fill()) {
                return;
            }

            var taking = (int)Math.Min(frames, available - consumed);
            consumed += taking;
            frames -= taking;
        }
    }

    void Restart() {
        ogg.Rewind();
        decoder.ResetState();
        available = 0;
        consumed = 0;
        position = 0;
        finished = false;

        // The two header packets again, and then the priming samples — a rewound stream is a new
        // stream as far as the decoder is concerned.
        ogg.ReadPacket(out _);
        ogg.ReadPacket(out _);
        Skip(preSkip);
    }

    static FileStream OpenFile(string path) {
        ArgumentNullException.ThrowIfNull(path);

        return new(path, new FileStreamOptions {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.SequentialScan
        });
    }
}
