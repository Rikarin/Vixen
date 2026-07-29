// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Video;

/// <summary>Turns whatever a video is stored as into frames, one at a time.</summary>
/// <remarks>
///     <para>
///         <b>This is the seam a codec plugs into, and the reason the engine links none.</b> It is
///         deliberately the same bargain <c>IAudioStreamDecoder</c> makes: the runtime knows the
///         interface and decides nothing about what implements it. A game whose only video is an
///         uncompressed logo sting should not carry a VP9 decoder, and a game that needs one should
///         not have the engine's choice of decoder forced on it.
///     </para>
///     <para>
///         <b>Called from the decode thread, never from the frame.</b> Blocking here is expected —
///         <see cref="DecodeNext" /> may take a disk read and several milliseconds of arithmetic. The
///         frame queue in front of it is what stops that being visible. See <c>VideoPlayer</c>, which
///         owns the thread.
///     </para>
///     <para>
///         <b>The caller owns the frame.</b> Decoding writes into a frame the caller rented, rather
///         than returning one the decoder owns, so that a pool can exist at all and so that a decoder
///         never has to answer "when may I reuse this?".
///     </para>
/// </remarks>
public interface IVideoStreamDecoder : IDisposable {
    /// <summary>The size and layout it produces.</summary>
    /// <remarks>
    ///     Fixed for the life of the decoder. A stream that changes resolution mid-play — legal in
    ///     WebM, and rare outside adaptive streaming — is reported through
    ///     <see cref="VideoDecodeStatus.FormatChanged" /> rather than by mutating this, so that a
    ///     caller holding buffers is told rather than silently handed a different picture.
    /// </remarks>
    VideoFormat Format { get; }

    /// <summary>How big the picture is meant to <em>look</em>, which is not always how big it is.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not a property of the codec, which is why it is defaulted here rather than derived
    ///         from <see cref="Format" />.</b> A decoder produces a grid of samples; whether those
    ///         samples are square is something only the container states. Anamorphic content — a
    ///         720×480 clip meant to be shown at 853×480 — decodes to exactly the samples it has and
    ///         is a fifth too narrow on screen unless somebody reads the container's own answer.
    ///     </para>
    ///     <para>
    ///         The default is the sample count, which is right for everything with square pixels and
    ///         is therefore right for almost everything. An implementation over a container that
    ///         states a display size overrides it.
    ///     </para>
    /// </remarks>
    Int2 DisplaySize => new(Format.Width, Format.Height);

    /// <summary>How long the whole video is, or <see cref="TimeSpan.Zero" /> if the container does not say.</summary>
    TimeSpan Duration { get; }

    /// <summary>Where the decoder has got to.</summary>
    /// <remarks>
    ///     The timestamp of the last frame produced, or where the last <see cref="Seek" /> left it.
    ///     Not the timestamp of the <em>next</em> frame, which no decoder with reordering can know
    ///     without decoding it.
    /// </remarks>
    TimeSpan Position { get; }

    /// <summary>Whether <see cref="Seek" /> works.</summary>
    /// <remarks>False for a live source, which also cannot loop; the player stops it at the end.</remarks>
    bool CanSeek { get; }

    /// <summary>Decodes the next frame.</summary>
    /// <param name="destination">
    ///     Where to put it. Resized to <see cref="Format" /> by the decoder, so a caller may pass a
    ///     frame of any shape — including a fresh one.
    /// </param>
    /// <returns>What happened.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="destination" /> is null.</exception>
    /// <exception cref="InvalidDataException">The stream is damaged beyond the decoder's tolerance.</exception>
    VideoDecodeStatus DecodeNext(VideoFrame destination);

    /// <summary>Moves to a position.</summary>
    /// <param name="position">
    ///     Where to go. Clamped to the video. The decoder lands on the last frame at or before it
    ///     that the stream can be joined at, so the next <see cref="DecodeNext" /> may return a frame
    ///     slightly earlier than asked for — which is what a seek to a non-keyframe means everywhere.
    /// </param>
    /// <exception cref="NotSupportedException"><see cref="CanSeek" /> is false.</exception>
    void Seek(TimeSpan position);
}

/// <summary>What a call to <see cref="IVideoStreamDecoder.DecodeNext" /> did.</summary>
/// <remarks>
///     A status rather than a <see langword="bool" /> because the three outcomes are genuinely
///     different and a caller has to act differently on each. <see cref="NeedMoreData" /> in
///     particular is not an error and not an end: a codec with B-frames or a container with a partial
///     cluster will return it several times in a row while it accumulates, and a player that treated
///     it as the end would stop a second before every file finished.
/// </remarks>
public enum VideoDecodeStatus : byte {
    /// <summary>A frame was written. Its timestamp says when to show it.</summary>
    Decoded = 0,

    /// <summary>
    ///     Nothing was written yet and the stream has not ended. Call again; the decoder is buffering.
    /// </summary>
    NeedMoreData = 1,

    /// <summary>The stream has ended. Nothing more will be produced without a <c>Seek</c>.</summary>
    EndOfStream = 2,

    /// <summary>
    ///     A frame was written and it is a different shape from the last one.
    ///     <see cref="IVideoStreamDecoder.Format" /> has already been updated; anything the caller
    ///     sized against it — a texture, a pool — needs rebuilding.
    /// </summary>
    FormatChanged = 3
}
