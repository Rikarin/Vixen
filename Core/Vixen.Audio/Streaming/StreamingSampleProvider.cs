// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Sources;

namespace Vixen.Audio.Streaming;

/// <summary>A voice source that is being decoded while it plays.</summary>
/// <remarks>
///     <para>
///         What stops a five-minute track costing fifty megabytes of resident memory. The decoder
///         runs on <see cref="AudioStreamPump" />'s thread and fills a ring buffer; the mixer drains
///         that ring inside the audio callback and never touches the decoder, the file, or a lock.
///     </para>
///     <para>
///         <b>An empty ring is silence and a counter, not a stall.</b> If the pump has fallen behind
///         — a slow disk, a stalled network read, a machine under load — <see cref="Read" /> writes
///         zeros for the frames it could not fill and increments <see cref="Underruns" />. The
///         alternative is blocking the audio thread, which turns one late track into every sound in
///         the game stuttering.
///     </para>
/// </remarks>
public sealed class StreamingSampleProvider : IAudioSampleProvider, IDisposable {
    readonly IAudioStreamDecoder decoder;
    readonly AudioRingBuffer ring;
    readonly float[] decodeBuffer;
    readonly int channels;

    long delivered;
    long underruns;
    volatile bool exhausted;
    volatile bool pumping;

    /// <summary>A provider over a decoder.</summary>
    /// <param name="decoder">What produces the frames. Disposed with this provider.</param>
    /// <param name="loop">Whether to seek back to the start when the track ends.</param>
    /// <param name="bufferedFrames">
    ///     How far ahead the pump may run. Half a second by default, which survives a disk that
    ///     takes a hundred milliseconds to answer and costs 192 kB for a stereo track.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="decoder" /> is null.</exception>
    /// <exception cref="ArgumentException">The decoder cannot seek and looping was asked for.</exception>
    public StreamingSampleProvider(IAudioStreamDecoder decoder, bool loop = false, int bufferedFrames = 24_000) {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferedFrames);

        if (loop && !decoder.CanSeek) {
            throw new ArgumentException(
                "A stream that cannot seek cannot loop: the wrap is a seek to frame zero, and there "
                + "is nothing else it could be.",
                nameof(loop)
            );
        }

        this.decoder = decoder;
        channels = decoder.Format.Channels;
        IsLooping = loop;
        Format = decoder.Format;
        FrameCount = decoder.FrameCount;
        ring = new AudioRingBuffer(bufferedFrames * channels);
        decodeBuffer = new float[Math.Min(bufferedFrames, 4_096) * channels];
    }

    /// <inheritdoc />
    public AudioFormat Format { get; }

    /// <inheritdoc />
    public long FrameCount { get; }

    /// <inheritdoc />
    public long Position => Volatile.Read(ref delivered);

    /// <inheritdoc />
    public bool IsLooping { get; }

    /// <summary>How many times the mixer wanted frames the decoder had not produced yet.</summary>
    /// <remarks>
    ///     Counted in <em>calls</em> rather than frames, because one starved block is one audible
    ///     gap whether it was short of two frames or two hundred. Anything above zero in a shipping
    ///     build is worth a look at the streaming budget.
    /// </remarks>
    public long Underruns => Interlocked.Read(ref underruns);

    /// <summary>How many frames are decoded and waiting.</summary>
    public int BufferedFrames => ring.Count / channels;

    /// <summary>Whether the decoder has reached the end and will produce nothing more.</summary>
    public bool IsExhausted => exhausted;

    /// <inheritdoc />
    /// <remarks>Runs on the audio thread. Reads the ring and nothing else.</remarks>
    public int Read(Span<float> destination, int frameCount) {
        var wanted = frameCount * channels;
        var got = ring.Read(destination[..wanted]);

        if (got == wanted) {
            Interlocked.Add(ref delivered, frameCount);
            return frameCount;
        }

        if (exhausted) {
            // Genuinely the end of the track, so a short read is the truth and the voice should
            // finish on it.
            var frames = got / channels;
            Interlocked.Add(ref delivered, frames);
            return frames;
        }

        destination[got..wanted].Clear();
        Interlocked.Increment(ref underruns);
        Interlocked.Add(ref delivered, frameCount);
        return frameCount;
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">
    ///     The provider is attached to the pump. Seeking would have to reach across the ring buffer
    ///     from the wrong side of it, and there is nowhere to put the frames already decoded and not
    ///     yet played. Stop the voice and start another — which is what a cross-fade is anyway.
    /// </exception>
    public void Seek(long frame) {
        if (pumping) {
            throw new NotSupportedException(
                "A stream that is being pumped cannot be seeked: there are already-decoded frames "
                + "in flight that belong to the old position, and no side of the ring buffer owns "
                + "both cursors. Stop the voice and start a new one at the position wanted."
            );
        }

        decoder.Seek(frame);
        ring.Clear();
        exhausted = false;
        Volatile.Write(ref delivered, decoder.Position);
    }

    /// <summary>Decodes until the ring is full or the track has ended.</summary>
    /// <returns>How many frames were decoded.</returns>
    /// <remarks>
    ///     Called by <see cref="AudioStreamPump" />, and by a test that wants to control exactly how
    ///     far ahead the decoder has got — which is the only way to write a test that asserts on
    ///     what an underrun sounds like.
    /// </remarks>
    public int Fill() {
        var total = 0;

        while (!exhausted) {
            var room = ring.Free / channels;

            if (room <= 0) {
                break;
            }

            var block = Math.Min(room, decodeBuffer.Length / channels);
            var decoded = decoder.Decode(decodeBuffer, block);

            if (decoded <= 0) {
                if (!IsLooping) {
                    exhausted = true;
                    break;
                }

                decoder.Seek(0);
                decoded = decoder.Decode(decodeBuffer, block);

                if (decoded <= 0) {
                    // A looping track that decodes nothing from frame zero is empty, and looping it
                    // would spin this thread at a hundred per cent for as long as the voice lives.
                    exhausted = true;
                    break;
                }
            }

            ring.Write(decodeBuffer.AsSpan(0, decoded * channels));
            total += decoded;
        }

        return total;
    }

    /// <inheritdoc />
    public void Dispose() => decoder.Dispose();

    internal void SetPumping(bool value) => pumping = value;
}
