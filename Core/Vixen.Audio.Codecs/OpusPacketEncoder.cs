// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Concentus;
using Concentus.Enums;

namespace Vixen.Audio.Codecs;

/// <summary>Turns captured audio into Opus packets, one packet at a time.</summary>
/// <remarks>
///     <para>
///         <b>A packet, not a stream.</b> <see cref="OpusStreamDecoder" /> reads Opus out of an Ogg,
///         because a track on disk is a container full of packets. Voice is the other shape: one
///         datagram is one packet, it is self-contained, and the next one may never arrive. Framing it
///         in an Ogg would be adding a container so it can immediately be taken off again.
///     </para>
///     <para>
///         <b>Twenty milliseconds, because a conversation notices latency before it notices
///         bandwidth.</b> Opus offers 2.5 to 60 ms. Ten halves the delay and spends about a third more
///         on per-packet overhead; sixty saves bandwidth and adds delay two people talking over each
///         other can feel. Twenty is what every telephony system converged on, and it is the default
///         here for the same reason.
///     </para>
///     <para>
///         <b>VOIP and not AUDIO.</b> The application hint changes what the encoder protects when it
///         runs out of bits: speech intelligibility, rather than the top octave of a cymbal. For a
///         game's voice channel that is the right trade every time, and it is why this is a separate
///         type from the music path rather than a flag on it.
///     </para>
///     <para>
///         <b>Forward error correction is off until somebody says what the link is like.</b> Opus can
///         carry a coarse copy of the previous frame inside the current one, so a single lost packet
///         is recoverable from its successor — but only if <see cref="ExpectedPacketLoss" /> is set,
///         and it costs bitrate whether or not anything is lost. An encoder that guessed would be
///         spending a player's bandwidth on a problem they may not have.
///     </para>
/// </remarks>
public sealed class OpusPacketEncoder : IDisposable {
    /// <summary>The rate Opus encodes at. Anything else would be resampled twice on the way through.</summary>
    public const int Rate = 48_000;

    /// <summary>The largest a single-frame Opus packet can be, from the specification.</summary>
    public const int MaxPacketBytes = 1_275;

    readonly IOpusEncoder encoder;

    /// <summary>An encoder for one voice.</summary>
    /// <param name="channels">1 or 2. Voice is mono; stereo doubles the cost to carry a centred talker.</param>
    /// <param name="frameMilliseconds">2.5 is expressed as 3 and rounded down. One of 5, 10, 20, 40 or 60 otherwise.</param>
    /// <param name="bitrate">Bits a second. 24 kbit is transparent for speech; 16 is usable; 8 is a radio.</param>
    /// <exception cref="ArgumentOutOfRangeException">The channel count or frame length is not one Opus has.</exception>
    public OpusPacketEncoder(int channels = 1, int frameMilliseconds = 20, int bitrate = 24_000) {
        if (channels is < 1 or > 2) {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Opus encodes 1 or 2 channels.");
        }

        FrameSize = FramesPer(frameMilliseconds);
        Format = new AudioFormat(Rate, channels);

        OpusRuntime.Ensure();
        encoder = OpusCodecFactory.CreateEncoder(Rate, channels, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
        encoder.Bitrate = bitrate;

        // Five of ten. The encoder's complexity is almost entirely about how hard it looks for a
        // better representation; the top settings cost several times the middle for a difference
        // nobody has picked out of a game's voice channel, on a thread that is also running a game.
        encoder.Complexity = 5;
    }

    /// <summary>How many frames go into one packet.</summary>
    public int FrameSize { get; }

    /// <summary>What it expects to be handed.</summary>
    public AudioFormat Format { get; }

    /// <summary>Bits a second. Changing it takes effect on the next packet.</summary>
    public int Bitrate {
        get => encoder.Bitrate;
        set => encoder.Bitrate = value;
    }

    /// <summary>
    ///     What percentage of packets the link is losing, and with it whether to carry error
    ///     correction at all.
    /// </summary>
    /// <remarks>
    ///     Zero — the default — turns the redundancy off entirely. Anything above it turns it on and
    ///     tells the encoder how much of the frame to spend on it: the number is not a threshold but
    ///     the actual budget, so setting 30 on a clean link wastes almost a third of the bitrate.
    ///     Feed it something measured.
    /// </remarks>
    public int ExpectedPacketLoss {
        get => encoder.PacketLossPercent;
        set {
            var clamped = Math.Clamp(value, 0, 100);
            encoder.PacketLossPercent = clamped;
            encoder.UseInbandFEC = clamped > 0;
        }
    }

    /// <summary>
    ///     Whether the encoder may emit a two-byte packet instead of a real one when nothing is
    ///     happening.
    /// </summary>
    /// <remarks>
    ///     An alternative to <see cref="VoiceSender" />'s gate rather than a companion to it: DTX
    ///     still sends something every frame, which keeps the sequence dense at the cost of a packet
    ///     per talker per 20 ms across the whole session. Not sending at all is cheaper, and the
    ///     timestamp is what lets the receiver tell that silence from a loss.
    /// </remarks>
    public bool UseDiscontinuous {
        get => encoder.UseDTX;
        set => encoder.UseDTX = value;
    }

    /// <summary>Encodes exactly one frame.</summary>
    /// <param name="pcm">Interleaved, and exactly <see cref="FrameSize" /> frames of it.</param>
    /// <param name="packet">Where it goes. <see cref="MaxPacketBytes" /> is always enough.</param>
    /// <returns>How many bytes of <paramref name="packet" /> are the packet.</returns>
    /// <exception cref="ArgumentException"><paramref name="pcm" /> is not one whole frame.</exception>
    public int Encode(ReadOnlySpan<float> pcm, Span<byte> packet) {
        var wanted = FrameSize * Format.Channels;

        if (pcm.Length < wanted) {
            throw new ArgumentException(
                $"An Opus frame is {FrameSize} frames, which is {wanted} samples; got {pcm.Length}.",
                nameof(pcm)
            );
        }

        return encoder.Encode(pcm[..wanted], FrameSize, packet, packet.Length);
    }

    /// <summary>Forgets everything it knew about the signal, for a new talker in the same slot.</summary>
    public void Reset() => encoder.ResetState();

    /// <inheritdoc />
    public void Dispose() => encoder.Dispose();

    /// <summary>Turns a frame length in milliseconds into one Opus actually has.</summary>
    internal static int FramesPer(int milliseconds) => milliseconds switch {
        3 => 120, // 2.5 ms, which cannot be written as an integer number of them.
        5 => 240,
        10 => 480,
        20 => 960,
        40 => 1_920,
        60 => 2_880,
        _ => throw new ArgumentOutOfRangeException(
            nameof(milliseconds),
            milliseconds,
            "An Opus frame is 3 (for 2.5), 5, 10, 20, 40 or 60 milliseconds."
        )
    };
}
