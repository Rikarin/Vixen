// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Effects;

namespace Vixen.Audio.Codecs;

/// <summary>The microphone end of a voice channel: capture in, packets out.</summary>
/// <remarks>
///     <para>
///         <b>It knows nothing about a network.</b> Packets are pulled out of it with
///         <see cref="TryRead" /> and handed to whatever is doing the sending —
///         <c>Channel.Sequenced</c> over <c>Vixen.Net</c>, a loopback in a test, a file. Taking a
///         transport dependency here would mean a game that wants voice over its own socket layer
///         cannot use any of this, to save it four lines.
///     </para>
///     <para>
///         <b>The gate is the bandwidth decision, not just an effect.</b> It runs before the encoder,
///         so room tone is not encoded and not transmitted; and its <see cref="GateEffect.IsOpen" />
///         decides whether the frame is sent at all. A player not talking costs nothing — not a small
///         packet, nothing — which is the difference between a thirty-two player voice channel being
///         affordable and being a feature people turn off.
///     </para>
///     <para>
///         <b>The hold is what makes that safe.</b> Speech drops below any useful threshold between
///         syllables; a gate without a hold would cut the first consonant off every word after a
///         pause. <see cref="GateEffect.HoldSeconds" /> defaults to 150 ms for that reason, and the
///         gate is exposed rather than wrapped so it can be tuned against a real microphone.
///     </para>
///     <para>
///         <b>Pull, not push.</b> A callback would either allocate a closure per packet or make the
///         caller implement an interface to avoid it. A ring the caller drains costs neither, and it
///         means the send can happen on the caller's schedule rather than inside a capture callback.
///     </para>
/// </remarks>
public sealed class VoiceSender : IDisposable {
    /// <summary>How many encoded packets are held before the oldest is dropped.</summary>
    /// <remarks>
    ///     Eight is 160 ms at the default frame length. A caller that has not drained in 160 ms is not
    ///     going to catch up, and holding more would turn a stall into latency rather than into the
    ///     loss it already is.
    /// </remarks>
    public const int Backlog = 8;

    readonly OpusPacketEncoder encoder;
    readonly GateEffect gate;
    readonly float[] pending;
    readonly byte[][] packets = new byte[Backlog][];
    readonly VoicePacketHeader[] headers = new VoicePacketHeader[Backlog];
    readonly int[] lengths = new int[Backlog];

    int filled;
    int head;
    int count;
    ushort sequence;
    uint timestamp;

    /// <summary>A sender over an encoder of its own.</summary>
    /// <param name="channels">1 or 2.</param>
    /// <param name="frameMilliseconds">How long each packet is.</param>
    /// <param name="bitrate">Bits a second.</param>
    public VoiceSender(int channels = 1, int frameMilliseconds = 20, int bitrate = 24_000)
        : this(new OpusPacketEncoder(channels, frameMilliseconds, bitrate)) { }

    /// <summary>A sender over an encoder somebody else configured.</summary>
    /// <param name="encoder">It. Disposed with this sender.</param>
    /// <exception cref="ArgumentNullException"><paramref name="encoder" /> is null.</exception>
    public VoiceSender(OpusPacketEncoder encoder) {
        ArgumentNullException.ThrowIfNull(encoder);
        this.encoder = encoder;

        pending = new float[encoder.FrameSize * encoder.Format.Channels];
        gate = new GateEffect { ThresholdDb = -45f, HoldSeconds = 0.15f, RangeDb = -80f };
        gate.Prepare(encoder.Format, encoder.FrameSize);

        for (var i = 0; i < Backlog; i++) {
            packets[i] = new byte[OpusPacketEncoder.MaxPacketBytes];
        }
    }

    /// <summary>What it encodes at.</summary>
    public AudioFormat Format => encoder.Format;

    /// <summary>The frame length, in frames.</summary>
    public int FrameSize => encoder.FrameSize;

    /// <summary>
    ///     The gate, which is both the noise gate and the voice-activity detector. Tune it; it is
    ///     exposed on purpose.
    /// </summary>
    public GateEffect Gate => gate;

    /// <summary>Whether the gate is currently letting audio through, for a name plate to light up from.</summary>
    public bool IsTransmitting => gate.IsOpen;

    /// <summary>Whether to send frames the gate closed on anyway.</summary>
    /// <remarks>
    ///     For a push-to-talk button that a player is holding down while saying nothing, where the
    ///     silence is meaningful, or for a link where the receiver's jitter buffer behaves better
    ///     with a dense sequence. Off by default: not sending is the whole point of the gate.
    /// </remarks>
    public bool SendWhileSilent { get; set; }

    /// <summary>How many packets have been encoded and made available.</summary>
    public long Sent { get; private set; }

    /// <summary>How many frames the gate suppressed instead of encoding.</summary>
    public long Suppressed { get; private set; }

    /// <summary>How many encoded packets were dropped because the caller was not draining.</summary>
    public long Overrun { get; private set; }

    /// <summary>How many packets are waiting to be read.</summary>
    public int Available => count;

    /// <summary>Bits a second, changeable while running.</summary>
    public int Bitrate {
        get => encoder.Bitrate;
        set => encoder.Bitrate = value;
    }

    /// <summary>How lossy the link is, and with it whether to carry error correction.</summary>
    /// <remarks>Feed it something measured; see <see cref="OpusPacketEncoder.ExpectedPacketLoss" />.</remarks>
    public int ExpectedPacketLoss {
        get => encoder.ExpectedPacketLoss;
        set => encoder.ExpectedPacketLoss = value;
    }

    /// <summary>Takes captured audio, and encodes whatever whole frames it completes.</summary>
    /// <param name="pcm">Interleaved, at this sender's format. Any length.</param>
    /// <returns>How many packets became available.</returns>
    /// <remarks>
    ///     Audio arrives in whatever block the capture device felt like; Opus wants exactly one frame.
    ///     The remainder is held here, so a caller may hand over 441 samples at a time forever and
    ///     still get correctly framed packets out.
    /// </remarks>
    public int Write(ReadOnlySpan<float> pcm) {
        var produced = 0;
        var channels = Format.Channels;

        while (!pcm.IsEmpty) {
            var wanted = pending.Length - filled;
            var taking = Math.Min(wanted, pcm.Length);

            pcm[..taking].CopyTo(pending.AsSpan(filled));
            filled += taking;
            pcm = pcm[taking..];

            if (filled < pending.Length) {
                break;
            }

            filled = 0;

            if (Encode(channels)) {
                produced++;
            }
        }

        return produced;
    }

    /// <summary>Takes the oldest packet waiting.</summary>
    /// <param name="destination">Where the bytes go. <see cref="OpusPacketEncoder.MaxPacketBytes" /> is always enough.</param>
    /// <param name="header">Its sequence and timestamp, which the far end needs.</param>
    /// <param name="length">How many bytes of <paramref name="destination" /> are the packet.</param>
    /// <returns>Whether there was one.</returns>
    public bool TryRead(Span<byte> destination, out VoicePacketHeader header, out int length) {
        if (count == 0) {
            header = default;
            length = 0;
            return false;
        }

        var slot = head;
        length = lengths[slot];

        if (destination.Length < length) {
            header = default;
            length = 0;
            return false;
        }

        packets[slot].AsSpan(0, length).CopyTo(destination);
        header = headers[slot];
        head = (head + 1) % Backlog;
        count--;
        return true;
    }

    /// <summary>Drops anything held, for a talker who stopped.</summary>
    /// <remarks>
    ///     The counters and the timestamp survive: a receiver's idea of the timeline must not go
    ///     backwards just because the sender's buffer was cleared.
    /// </remarks>
    public void Reset() {
        filled = 0;
        head = 0;
        count = 0;
        gate.Reset();
        encoder.Reset();
    }

    /// <inheritdoc />
    public void Dispose() => encoder.Dispose();

    /// <summary>Gates one held frame, and encodes it if anything is happening.</summary>
    /// <returns>Whether a packet came out.</returns>
    bool Encode(int channels) {
        gate.Process(pending, FrameSize, channels);

        // Before the early return, because the timestamp is the talker's clock and it runs whether or
        // not anybody is talking. A timestamp that only advanced on transmitted frames would make a
        // pause indistinguishable from a burst of loss, which is the one thing it exists to prevent.
        var at = timestamp;
        timestamp += (uint)FrameSize;

        if (!gate.IsOpen && !SendWhileSilent) {
            Suppressed++;
            return false;
        }

        var slot = (head + count) % Backlog;
        var written = encoder.Encode(pending, packets[slot]);

        if (written <= 0) {
            return false;
        }

        lengths[slot] = written;
        headers[slot] = new VoicePacketHeader(sequence, at);
        sequence++;
        Sent++;

        if (count == Backlog) {
            // Full: the oldest goes, not the newest. A listener would rather lose the syllable from
            // 160 ms ago than the one being spoken now.
            head = (head + 1) % Backlog;
            Overrun++;
        } else {
            count++;
        }

        return true;
    }
}
