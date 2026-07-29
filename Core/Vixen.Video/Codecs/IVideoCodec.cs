// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Video.Codecs;

/// <summary>Everything a codec needs to know before it sees a packet.</summary>
/// <param name="CodecId">
///     What the container called it — <c>V_VP9</c>, <c>V_UNCOMPRESSED</c>. Matroska's spelling is
///     used even by a container that spells it differently, because a registry has to agree on one.
/// </param>
/// <param name="Width">The coded width in samples.</param>
/// <param name="Height">The coded height in samples.</param>
/// <param name="CodecPrivate">The codec's own header, or empty if it needs none.</param>
/// <param name="FourCc">The sample format of an uncompressed track, or empty.</param>
/// <param name="FrameRate">The nominal rate, or unknown.</param>
/// <param name="Range">What range the container said the samples use.</param>
/// <param name="Matrix">What coefficients it said they were made with.</param>
/// <remarks>
///     A record rather than the container's own track type, so that a codec never references a
///     demuxer. That is not tidiness: it is what lets an MP4 reader arrive later and reuse every
///     codec anybody has written against this.
/// </remarks>
public readonly record struct VideoTrackInfo(
    string CodecId,
    int Width,
    int Height,
    ReadOnlyMemory<byte> CodecPrivate = default,
    string FourCc = "",
    VideoRational FrameRate = default,
    VideoColourRange Range = VideoColourRange.Limited,
    VideoColourMatrix Matrix = VideoColourMatrix.Bt709
);

/// <summary>One compressed unit, on its way to a codec.</summary>
/// <param name="data">The bytes. Valid for the duration of the call and no longer.</param>
/// <param name="timestamp">When the picture it carries is due.</param>
/// <param name="duration">How long it lasts, or zero if nothing said.</param>
/// <param name="isKeyFrame">Whether the stream could be joined here.</param>
/// <remarks>
///     A <see langword="ref" /> struct, so the span cannot be captured. A codec that wanted to keep
///     the bytes — and a codec with B-frames does — has to copy them into its own storage, which is
///     the decision it should be making explicitly rather than by holding a reference into a demuxer's
///     pool.
/// </remarks>
public readonly ref struct VideoPacket(
    ReadOnlySpan<byte> data,
    TimeSpan timestamp,
    TimeSpan duration,
    bool isKeyFrame
) {
    /// <summary>The bytes.</summary>
    public ReadOnlySpan<byte> Data { get; } = data;

    /// <summary>When the picture is due.</summary>
    public TimeSpan Timestamp { get; } = timestamp;

    /// <summary>How long it lasts, or zero.</summary>
    public TimeSpan Duration { get; } = duration;

    /// <summary>Whether the stream can be joined here.</summary>
    public bool IsKeyFrame { get; } = isKeyFrame;
}

/// <summary>Turns packets into pictures.</summary>
/// <remarks>
///     <para>
///         Deliberately smaller than <see cref="IVideoStreamDecoder" />: a codec knows nothing about
///         files, positions or seeking. It is fed packets in the order the container had them and
///         produces frames, and everything else is the stream decoder's job. That split is what lets
///         one codec serve every container and one container serve every codec.
///     </para>
///     <para>
///         <b>A codec may hold packets back.</b> Returning <see cref="VideoDecodeStatus.NeedMoreData" />
///         is ordinary for anything with frame reordering: the first B-frame cannot be output until
///         the picture it refers forward to has arrived. <see cref="Drain" /> is how the frames still
///         inside come out at the end of a stream.
///     </para>
/// </remarks>
public interface IVideoCodec : IDisposable {
    /// <summary>What it produces. Valid once the first frame has been decoded.</summary>
    VideoFormat Format { get; }

    /// <summary>Decodes a packet.</summary>
    /// <param name="packet">The bytes and their timing.</param>
    /// <param name="destination">Where to put the picture, if one comes out.</param>
    /// <returns>What happened.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="destination" /> is null.</exception>
    /// <exception cref="InvalidDataException">The packet is not something this codec can decode.</exception>
    VideoDecodeStatus Decode(in VideoPacket packet, VideoFrame destination);

    /// <summary>Asks for a frame the codec is holding, now that no more packets are coming.</summary>
    /// <param name="destination">Where to put it.</param>
    /// <returns>
    ///     <see cref="VideoDecodeStatus.Decoded" /> while frames remain, then
    ///     <see cref="VideoDecodeStatus.EndOfStream" />.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="destination" /> is null.</exception>
    VideoDecodeStatus Drain(VideoFrame destination);

    /// <summary>Throws away everything held, because the stream has jumped.</summary>
    /// <remarks>
    ///     Called by the stream decoder after a seek. A codec that did not reset would emit the
    ///     frames either side of the jump in the wrong order and reference pictures that are no
    ///     longer on screen.
    /// </remarks>
    void Reset();
}

/// <summary>Makes codecs for the ids it recognises.</summary>
/// <remarks>
///     One factory per implementation, rather than a delegate, so that "can you decode this?" is
///     answerable without constructing anything — which is what lets a registry try candidates in
///     order and lets a factory say no to a codec id it half-supports.
/// </remarks>
public interface IVideoCodecFactory {
    /// <summary>A name for logs.</summary>
    string Name { get; }

    /// <summary>Whether it could decode a track.</summary>
    /// <param name="track">What the container said about the track.</param>
    /// <returns>Whether <see cref="Create" /> would succeed.</returns>
    bool CanDecode(in VideoTrackInfo track);

    /// <summary>Creates a codec for a track.</summary>
    /// <param name="track">What the container said about it.</param>
    /// <returns>The codec.</returns>
    /// <exception cref="NotSupportedException">It cannot, in fact, decode this track.</exception>
    IVideoCodec Create(in VideoTrackInfo track);
}

/// <summary>Which codecs this process has.</summary>
/// <remarks>
///     <para>
///         <b>A registry rather than a switch, because the engine ships one codec.</b>
///         <see cref="UncompressedVideoCodec" /> is to video what <c>PcmStreamDecoder</c> is to
///         audio: the implementation that needs no codec at all, and the reason a game with a single
///         uncompressed logo sting carries no decoder. A game that needs VP9 registers one — its own,
///         a package, a native binding — and everything above this seam is unchanged.
///     </para>
///     <para>
///         <b>Static, and therefore process-wide.</b> The alternative is threading a registry
///         through every call that opens a video, for a decision no application makes twice. It is
///         guarded by a lock because registration happens during start-up on whatever thread got
///         there first, and lookups happen on decode threads.
///     </para>
/// </remarks>
public static class VideoCodecRegistry {
    static readonly Lock Gate = new();
    static readonly List<IVideoCodecFactory> Factories = [new UncompressedVideoCodecFactory()];

    /// <summary>Adds a codec, ahead of everything already registered.</summary>
    /// <param name="factory">What makes it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="factory" /> is null.</exception>
    /// <remarks>
    ///     Ahead, so that a game registering a decoder for a codec the engine also handles gets its
    ///     own. Last-registered-wins is the only rule that lets an application override a default
    ///     without being able to unregister one.
    /// </remarks>
    public static void Register(IVideoCodecFactory factory) {
        ArgumentNullException.ThrowIfNull(factory);

        lock (Gate) {
            Factories.Insert(0, factory);
        }
    }

    /// <summary>Whether anything registered claims a track, without making a codec for it.</summary>
    /// <param name="track">What is known about it, which may be only its codec id.</param>
    /// <returns>Whether one does.</returns>
    /// <remarks>
    ///     ⚠ <b>The question is asked before the file is open, so the answer is about the codec id
    ///     and not about the stream.</b> That is what makes it useful — a title finds out it has no
    ///     VP9 decoder while it is drawing a menu rather than when the cutscene was due — and it is
    ///     also its limit: <c>V_UNCOMPRESSED</c> is claimed here on the codec id and refused later on
    ///     a sample format only the file states. A yes means "worth opening", not "will play".
    /// </remarks>
    public static bool CanDecode(in VideoTrackInfo track) {
        IVideoCodecFactory[] candidates;

        lock (Gate) {
            candidates = [.. Factories];
        }

        foreach (var factory in candidates) {
            if (factory.CanDecode(in track)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Finds a codec for a track.</summary>
    /// <param name="track">What the container said about it.</param>
    /// <param name="codec">The codec, if one was found.</param>
    /// <returns>Whether one was.</returns>
    public static bool TryCreate(in VideoTrackInfo track, out IVideoCodec? codec) {
        IVideoCodecFactory[] candidates;

        lock (Gate) {
            candidates = [.. Factories];
        }

        foreach (var factory in candidates) {
            if (factory.CanDecode(in track)) {
                codec = factory.Create(in track);

                return true;
            }
        }

        codec = null;

        return false;
    }

    /// <summary>The names of every registered codec, for a log line that explains a failure.</summary>
    /// <returns>The names, in the order they are tried.</returns>
    public static IReadOnlyList<string> RegisteredNames() {
        lock (Gate) {
            var names = new string[Factories.Count];

            for (var index = 0; index < Factories.Count; index++) {
                names[index] = Factories[index].Name;
            }

            return names;
        }
    }
}
