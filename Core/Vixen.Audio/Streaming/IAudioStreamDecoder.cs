// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Streaming;

/// <summary>Turns whatever a track is stored as into frames, a block at a time.</summary>
/// <remarks>
///     <para>
///         <b>This is the seam a codec plugs into, and the reason the engine links none.</b>
///         <c>docs/plan/08</c> wants Ogg or Opus kept compressed for music and decoded as it plays.
///         A decoder for either is a native library or a large managed one, and a game with no music
///         should not carry it — so the runtime knows this interface and the content pipeline
///         decides what implements it. <see cref="PcmStreamDecoder" /> is the one implementation
///         that needs no codec at all, and it is what a clip too big for memory is streamed through
///         today.
///     </para>
///     <para>
///         <b>Called from the pump thread, never from the audio callback.</b> Blocking here is
///         expected — that is the whole point of the arrangement. <see cref="Decode" /> may take a
///         disk seek; the ring buffer in front of it is what stops that being audible.
///     </para>
/// </remarks>
public interface IAudioStreamDecoder : IDisposable {
    /// <summary>The rate and channel count it produces.</summary>
    AudioFormat Format { get; }

    /// <summary>How many frames the whole track is, or <c>-1</c> if the container does not say.</summary>
    long FrameCount { get; }

    /// <summary>Which frame the next <see cref="Decode" /> starts at.</summary>
    long Position { get; }

    /// <summary>Whether <see cref="Seek" /> works.</summary>
    /// <remarks>
    ///     False for a live source. A stream that cannot seek also cannot loop, and the pump stops
    ///     it at the end rather than pretending.
    /// </remarks>
    bool CanSeek { get; }

    /// <summary>Decodes the next frames.</summary>
    /// <param name="destination">Interleaved, at least <c>frameCount × Format.Channels</c> long.</param>
    /// <param name="frameCount">How many frames are wanted.</param>
    /// <returns>How many were produced. Zero means the end of the track.</returns>
    int Decode(Span<float> destination, int frameCount);

    /// <summary>Moves to a frame.</summary>
    /// <param name="frame">Which one. Clamped to the track.</param>
    /// <exception cref="NotSupportedException"><see cref="CanSeek" /> is false.</exception>
    void Seek(long frame);
}
