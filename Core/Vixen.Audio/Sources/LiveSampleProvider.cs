// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Streaming;

namespace Vixen.Audio.Sources;

/// <summary>A voice fed by something that arrives when it arrives, rather than something that is read.</summary>
/// <remarks>
///     <para>
///         <b>Voice chat is what this is for.</b> Every other source in the engine is a <em>pull</em>:
///         the mixer asks a clip or a decoder for frames and gets them. A remote player's voice is a
///         <em>push</em> — packets land on a network thread, get decoded, and have to go somewhere
///         until the mixer next wants a block. This is that somewhere.
///     </para>
///     <para>
///         Same ring buffer as the streaming path, and the same rule: the writer may block, the
///         reader may not. If the network is late the mixer finds the ring empty, writes silence, and
///         counts it — which is exactly what a dropped voice packet should sound like.
///     </para>
///     <para>
///         <b>It never ends on its own.</b> A clip runs out; a person stops talking and starts again.
///         So an empty ring is silence rather than the end of the voice, and the voice lives until
///         somebody calls <see cref="Complete" /> or stops it. A voice-chat system holds one of these
///         per remote player for as long as that player is in the session, which also means the
///         mixer's spatialisation, the player's bus and its effects all stay put between utterances
///         rather than being rebuilt per packet.
///     </para>
///     <para>
///         <b>The bus is where an underwater player gets muffled.</b> Effects are per bus, not per
///         voice, so a session with some players submerged and some not routes them to two buses —
///         one with a low-pass and a send to the underwater reverb, one without. That is one bus per
///         <em>environment</em> rather than per player, and it is how a mixer is meant to be used.
///     </para>
/// </remarks>
public sealed class LiveSampleProvider : IAudioSampleProvider {
    readonly AudioRingBuffer ring;
    readonly int channels;

    long delivered;
    long underruns;
    long dropped;
    volatile bool completed;

    /// <summary>A live source at a format.</summary>
    /// <param name="format">What the writer will push. A voice codec is usually mono at 48 kHz.</param>
    /// <param name="bufferedFrames">
    ///     How much may be queued. 4 800 frames — a hundred milliseconds at 48 kHz — is a sensible
    ///     jitter buffer for a game: enough to ride out a late packet, short enough that a
    ///     conversation does not feel like a phone call on a bad line.
    /// </param>
    /// <exception cref="ArgumentException">The format is not one anything can be mixed from.</exception>
    public LiveSampleProvider(AudioFormat format, int bufferedFrames = 4_800) {
        if (!format.IsValid) {
            throw new ArgumentException($"{format} is not a format a voice can be fed at.", nameof(format));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferedFrames);

        Format = format;
        channels = format.Channels;
        ring = new AudioRingBuffer(bufferedFrames * channels);
    }

    /// <inheritdoc />
    public AudioFormat Format { get; }

    /// <summary>Never known: a live source has no length.</summary>
    public long FrameCount => -1;

    /// <inheritdoc />
    public long Position => Interlocked.Read(ref delivered);

    /// <summary>Never. There is nothing to wrap round to.</summary>
    public bool IsLooping => false;

    /// <summary>How many times the mixer wanted frames that had not arrived.</summary>
    /// <remarks>
    ///     The number that says the network is not keeping up. In a voice-chat system it belongs next
    ///     to the packet-loss figure, because they are usually the same story told twice.
    /// </remarks>
    public long Underruns => Interlocked.Read(ref underruns);

    /// <summary>How many frames were pushed and thrown away because the buffer was full.</summary>
    /// <remarks>
    ///     The other failure, and the one that is easy to miss: a writer running faster than real
    ///     time — a burst of packets after a stall — fills the ring and the excess is dropped. Growing
    ///     the buffer to hide it just adds latency to every word.
    /// </remarks>
    public long DroppedFrames => Interlocked.Read(ref dropped);

    /// <summary>How many frames are waiting to be played.</summary>
    public int BufferedFrames => ring.Count / channels;

    /// <summary>Whether the writer has said there will be no more.</summary>
    public bool IsCompleted => completed;

    /// <summary>Adds frames for the mixer to play.</summary>
    /// <param name="samples">Interleaved, a whole number of frames.</param>
    /// <returns>How many frames were taken. Fewer than offered means the buffer is full.</returns>
    /// <remarks>Called by whatever decodes the packets. One writer, like every other use of the ring.</remarks>
    public int Write(ReadOnlySpan<float> samples) {
        var taken = ring.Write(samples);
        var offered = samples.Length;

        if (taken < offered) {
            Interlocked.Add(ref dropped, (offered - taken) / channels);
        }

        return taken / channels;
    }

    /// <summary>Says that nothing more will be written.</summary>
    /// <remarks>
    ///     What a player leaving the session calls. Whatever is already buffered still plays, and the
    ///     voice ends when it runs out — so the last word is not cut off by the disconnection that
    ///     followed it.
    /// </remarks>
    public void Complete() => completed = true;

    /// <inheritdoc />
    /// <remarks>Runs on the audio thread. Reads the ring and nothing else.</remarks>
    public int Read(Span<float> destination, int frameCount) {
        var wanted = frameCount * channels;
        var got = ring.Read(destination[..wanted]);

        if (got == wanted) {
            Interlocked.Add(ref delivered, frameCount);
            return frameCount;
        }

        if (completed) {
            var frames = got / channels;
            Interlocked.Add(ref delivered, frames);
            return frames;
        }

        // Silence, not the end. Somebody who has stopped talking is not somebody who has left, and a
        // voice that ended every time a packet was late would be rebuilt several times a sentence.
        destination[got..wanted].Clear();

        if (got < wanted) {
            Interlocked.Increment(ref underruns);
        }

        Interlocked.Add(ref delivered, frameCount);
        return frameCount;
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always. There is no past to seek to.</exception>
    public void Seek(long frame) =>
        throw new NotSupportedException(
            "A live source cannot be seeked: what has been played is gone and what has not arrived "
            + "does not exist yet."
        );
}
