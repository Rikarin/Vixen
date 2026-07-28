// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Devices;

namespace Vixen.Audio.Sources;

/// <summary>Where a voice's samples come from.</summary>
/// <remarks>
///     <para>
///         One interface for a clip in memory, a compressed track being decoded as it plays, and a
///         tone a test generates, because the mixer has no reason to tell them apart: it wants
///         interleaved floats at some rate, and it resamples whatever it gets to the device's.
///     </para>
///     <para>
///         <b>Called from the audio thread.</b> Everything <see cref="IAudioRenderSource" /> is
///         forbidden applies here — no locks, no allocation, no I/O. A provider that has to wait for
///         a disk keeps a buffer somebody else fills; see
///         <c>Vixen.Audio.Streaming.StreamingSampleProvider</c>, which is that arrangement.
///     </para>
///     <para>
///         <b>Looping belongs here and not in the voice.</b> A loop is a seek, and only the provider
///         knows whether it can seek and what it costs — a clip wraps an index, a stream may have to
///         re-open a file. Putting the wrap in the voice would mean every provider grew a seek the
///         voice could call from the audio thread, including the ones that cannot.
///     </para>
/// </remarks>
public interface IAudioSampleProvider {
    /// <summary>The rate and channel count of what <see cref="Read" /> produces.</summary>
    /// <remarks>Fixed for the provider's life. A voice reads it once when it starts.</remarks>
    AudioFormat Format { get; }

    /// <summary>How many frames there are in total, or <c>-1</c> if that is not knowable.</summary>
    /// <remarks>A looping provider still reports the length of one pass through it.</remarks>
    long FrameCount { get; }

    /// <summary>Where the next <see cref="Read" /> will start, in frames.</summary>
    long Position { get; }

    /// <summary>Whether it will keep producing after it has run out.</summary>
    bool IsLooping { get; }

    /// <summary>Produces the next frames.</summary>
    /// <param name="destination">Interleaved, at least <c>frameCount × Format.Channels</c> long.</param>
    /// <param name="frameCount">How many frames are wanted.</param>
    /// <returns>
    ///     How many frames were written. Fewer than asked for means the source has run out and the
    ///     voice ends; zero means it has already run out.
    /// </returns>
    int Read(Span<float> destination, int frameCount);

    /// <summary>Starts again from a frame.</summary>
    /// <param name="frame">Which frame. Clamped to the source.</param>
    /// <exception cref="NotSupportedException">This provider cannot seek.</exception>
    void Seek(long frame);
}
