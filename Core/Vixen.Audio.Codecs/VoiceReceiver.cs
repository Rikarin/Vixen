// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Sources;

namespace Vixen.Audio.Codecs;

/// <summary>The far end of a voice channel: packets in, a sample provider the engine can play.</summary>
/// <remarks>
///     <para>
///         <b>One of these per talker.</b> Opus is stateful — the decoder carries pitch and spectrum
///         between packets, and concealment extrapolates from them — so two talkers through one
///         decoder would each be extrapolating from the other's voice. The mixer was always going to
///         want them as separate voices anyway, which is what makes a talker positionable and what
///         lets one of them be underwater while another is not.
///     </para>
///     <para>
///         <b>The buffer is the whole trade.</b> Packets sent every 20 ms do not arrive every 20 ms;
///         holding a few before playing them is what turns jitter into latency instead of into gaps.
///         <see cref="Depth" /> is that choice, in packets, and there is no right answer — only a
///         local link, where two is generous, and a bad one, where five is not enough.
///     </para>
///     <para>
///         <b>A gap is not automatically a loss.</b> The sender's gate stops transmitting when nobody
///         is talking, so most gaps are silence. <see cref="VoicePacketHeader" /> carries both a
///         sequence and a timestamp precisely so this can tell them apart: concealment runs for
///         packets that went missing, and never for a pause.
///     </para>
///     <para>
///         <b>When a packet really is missing, its successor is handed to the decoder too.</b> If the
///         far end was spending bitrate on redundancy, that successor carries a coarse copy of the
///         missing frame and the decoder reconstructs from it rather than extrapolating. If it was
///         not, the decoder extrapolates and says nothing about the difference — which is why this
///         is one call and not a choice made here.
///     </para>
/// </remarks>
public sealed class VoiceReceiver : IDisposable {
    /// <summary>How many out-of-order packets can be held at once.</summary>
    public const int Window = 16;

    /// <summary>
    ///     The longest deliberate silence that is played out as silence rather than skipped over.
    /// </summary>
    /// <remarks>
    ///     A short pause between words is part of how somebody talks, and playing it keeps their
    ///     rhythm. A long one is somebody who stopped talking, and buffering ten seconds of nothing
    ///     would just add ten seconds of latency to whatever they say next. Half a second is past any
    ///     pause inside a sentence and short of any pause between them.
    /// </remarks>
    public const int MaxSilenceFrames = OpusPacketDecoder.Rate / 2;

    readonly OpusPacketDecoder decoder;
    readonly LiveSampleProvider provider;
    readonly float[] scratch;

    readonly byte[][] packets = new byte[Window][];
    readonly VoicePacketHeader[] headers = new VoicePacketHeader[Window];
    readonly int[] lengths = new int[Window];
    readonly bool[] occupied = new bool[Window];

    int held;
    bool started;
    uint nextTimestamp;
    ushort lastSequence;

    /// <summary>A receiver for one talker.</summary>
    /// <param name="channels">1 or 2, matching the far end.</param>
    /// <param name="frameMilliseconds">What the far end packetises at.</param>
    /// <param name="depth">How many packets to hold before playing, trading latency for tolerance.</param>
    public VoiceReceiver(int channels = 1, int frameMilliseconds = 20, int depth = 2)
        : this(new OpusPacketDecoder(channels, frameMilliseconds), depth) { }

    /// <summary>A receiver over a decoder somebody else configured.</summary>
    /// <param name="decoder">It. Disposed with this receiver.</param>
    /// <param name="depth">How many packets to hold before playing.</param>
    /// <exception cref="ArgumentNullException"><paramref name="decoder" /> is null.</exception>
    public VoiceReceiver(OpusPacketDecoder decoder, int depth = 2) {
        ArgumentNullException.ThrowIfNull(decoder);
        this.decoder = decoder;
        Depth = Math.Clamp(depth, 0, Window - 1);

        scratch = new float[decoder.FrameSize * decoder.Format.Channels];

        // Room for the jitter window plus a little, so a caller that pumps once a frame and reads
        // once a block is not fighting the ring for space.
        provider = new LiveSampleProvider(decoder.Format, decoder.FrameSize * (Window + Depth));

        for (var i = 0; i < Window; i++) {
            packets[i] = new byte[OpusPacketEncoder.MaxPacketBytes];
        }
    }

    /// <summary>What comes out, and what to hand <c>AudioEngine.Play</c>.</summary>
    public LiveSampleProvider Provider => provider;

    /// <summary>What it decodes to.</summary>
    public AudioFormat Format => decoder.Format;

    /// <summary>How many packets are held before playing.</summary>
    public int Depth { get; }

    /// <summary>How many packets have been taken in.</summary>
    public long Received { get; private set; }

    /// <summary>How many arrived after their moment had passed, and were dropped.</summary>
    public long Late { get; private set; }

    /// <summary>How many arrived out of order and were put back in it.</summary>
    public long Reordered { get; private set; }

    /// <summary>How many frames had to be produced without the packet for them.</summary>
    public long Concealed => decoder.Concealed;

    /// <summary>How many deliberate silences were played through rather than concealed.</summary>
    public long Silences { get; private set; }

    /// <summary>Takes a packet off the wire.</summary>
    /// <param name="header">Its sequence and timestamp.</param>
    /// <param name="packet">Its bytes.</param>
    /// <returns>Whether it was kept. False means it was too late to be any use, or the window is full.</returns>
    public bool Receive(in VoicePacketHeader header, ReadOnlySpan<byte> packet) {
        Received++;

        if (packet.IsEmpty || packet.Length > OpusPacketEncoder.MaxPacketBytes) {
            return false;
        }

        if (!started) {
            started = true;
            nextTimestamp = header.Timestamp;

            // So the first packet reads as contiguous rather than as a loss of everything before it.
            lastSequence = (ushort)(header.Sequence - 1);
        }

        // Signed difference, so this stays right across the timestamp's 24-hour wrap.
        if ((int)(header.Timestamp - nextTimestamp) < 0) {
            Late++;
            return false;
        }

        if (header.Timestamp != nextTimestamp) {
            Reordered++;
        }

        var slot = FreeSlot();

        if (slot < 0) {
            return false;
        }

        packet.CopyTo(packets[slot]);
        lengths[slot] = packet.Length;
        headers[slot] = header;
        occupied[slot] = true;
        held++;
        return true;
    }

    /// <summary>Decodes whatever is ready, and writes it out.</summary>
    /// <returns>How many frames were written.</returns>
    /// <remarks>
    ///     Call it once a frame. Nothing happens until <see cref="Depth" /> packets are held, which is
    ///     the cushion; after that it drains one packet per call per packet available, so a burst
    ///     that arrives together is played in order rather than at once.
    /// </remarks>
    public int Pump() {
        var written = 0;

        while (held > Depth) {
            var produced = Step();

            if (produced == 0) {
                break;
            }

            written += produced;
        }

        return written;
    }

    /// <summary>Plays out everything held, for a talker who has stopped and is not coming back.</summary>
    public int Flush() {
        var written = 0;

        while (held > 0) {
            var produced = Step();

            if (produced == 0) {
                break;
            }

            written += produced;
        }

        return written;
    }

    /// <summary>Forgets the talker entirely.</summary>
    public void Reset() {
        Array.Clear(occupied);
        held = 0;
        started = false;
        nextTimestamp = 0;
        lastSequence = 0;
        Silences = 0;
        decoder.Reset();
    }

    /// <inheritdoc />
    public void Dispose() => decoder.Dispose();

    /// <summary>Emits exactly one frame: the real one, a recovered one, an invented one, or a skip.</summary>
    /// <returns>Frames written, or 0 if there was nothing to do.</returns>
    int Step() {
        if (TryTake(nextTimestamp, out var slot)) {
            var frames = decoder.Decode(packets[slot].AsSpan(0, lengths[slot]), scratch);
            lastSequence = headers[slot].Sequence;
            Release(slot);
            nextTimestamp += (uint)decoder.FrameSize;
            return Emit(frames);
        }

        if (!TryEarliest(out var earliest)) {
            return 0;
        }

        var ahead = headers[earliest];

        // Contiguous sequence with a hole in the timeline means nothing was lost — the far end's gate
        // was shut. Concealing here would put invented speech into a pause the talker chose.
        if (ahead.Sequence == (ushort)(lastSequence + 1)) {
            var gap = ahead.Timestamp - nextTimestamp;
            Silences++;

            // Short enough to be part of how somebody talks: play it, so their timing survives.
            // Longer than that and it is somebody who stopped, so skip to them starting again rather
            // than buffering the wait.
            if (gap <= MaxSilenceFrames) {
                scratch.AsSpan().Clear();
                nextTimestamp += (uint)decoder.FrameSize;
                return Emit(decoder.FrameSize);
            }

            nextTimestamp = ahead.Timestamp;
            return Step();
        }

        // Genuinely missing. If its immediate successor is already in hand it may be carrying a copy
        // of it, so hand that over — never worse than extrapolating, and often very much better.
        var successor = ahead.Timestamp == nextTimestamp + (uint)decoder.FrameSize
            ? packets[earliest].AsSpan(0, lengths[earliest])
            : default;

        var invented = decoder.Conceal(scratch, successor);

        lastSequence++;
        nextTimestamp += (uint)decoder.FrameSize;
        return Emit(invented);
    }

    // Write already answers in frames rather than samples.
    int Emit(int frames) => frames <= 0 ? 0 : provider.Write(scratch.AsSpan(0, frames * Format.Channels));

    bool TryTake(uint timestamp, out int slot) {
        for (var i = 0; i < Window; i++) {
            if (occupied[i] && headers[i].Timestamp == timestamp) {
                slot = i;
                return true;
            }
        }

        slot = -1;
        return false;
    }

    bool TryEarliest(out int slot) {
        slot = -1;

        for (var i = 0; i < Window; i++) {
            if (occupied[i] && (slot < 0 || (int)(headers[i].Timestamp - headers[slot].Timestamp) < 0)) {
                slot = i;
            }
        }

        return slot >= 0;
    }

    int FreeSlot() {
        for (var i = 0; i < Window; i++) {
            if (!occupied[i]) {
                return i;
            }
        }

        return -1;
    }

    void Release(int slot) {
        occupied[slot] = false;
        held--;
    }
}
