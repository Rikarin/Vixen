// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Video.Codecs;
using Vixen.Video.Containers;

namespace Vixen.Video;

/// <summary>A WebM file, decoded as it plays.</summary>
/// <remarks>
///     <para>
///         The join between the two halves of this module: a <see cref="MatroskaDemuxer" /> that
///         turns a file into packets, and whatever <see cref="VideoCodecRegistry" /> has that can
///         turn those packets into pictures. Neither knows about the other, and this is the fifty
///         lines that make them one thing a player can hold.
///     </para>
///     <para>
///         <b>It never returns <see cref="VideoDecodeStatus.NeedMoreData" />.</b> The interface
///         allows it, for a codec that reorders; this loops internally until a frame comes out or the
///         file ends, because the caller is a decode thread whose entire job is to wait for a frame
///         and there is nothing else it could usefully do with the answer.
///     </para>
///     <para>
///         <b>The container stays reachable.</b> <see cref="Container" /> is public because a WebM's
///         audio track is in the same file and has to be demuxed by the same reader — see
///         <see cref="Audio.MatroskaAudioStreamDecoder" />, which shares this demuxer rather than
///         opening the file twice.
///     </para>
/// </remarks>
public sealed class WebMVideoStreamDecoder : IVideoStreamDecoder {
    readonly IVideoCodec codec;
    readonly MatroskaDemuxer demuxer;
    readonly bool ownsDemuxer;
    readonly int trackNumber;

    bool drained;
    VideoFormat lastFormat;

    /// <summary>Opens a file.</summary>
    /// <param name="path">Where it is.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path" /> is null.</exception>
    /// <exception cref="InvalidDataException">It is not a WebM segment, or it has no video track.</exception>
    /// <exception cref="NotSupportedException">Its video codec is not registered.</exception>
    public WebMVideoStreamDecoder(string path)
        : this(new MatroskaDemuxer(path), ownsDemuxer: true) { }

    /// <summary>Opens a stream.</summary>
    /// <param name="stream">The bytes. Must be seekable for <see cref="Seek" /> to work.</param>
    /// <param name="leaveOpen">Whether the stream outlives this decoder.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream" /> is null.</exception>
    /// <exception cref="InvalidDataException">It is not a WebM segment, or it has no video track.</exception>
    /// <exception cref="NotSupportedException">Its video codec is not registered.</exception>
    public WebMVideoStreamDecoder(Stream stream, bool leaveOpen = false)
        : this(new MatroskaDemuxer(stream, leaveOpen), ownsDemuxer: true) { }

    /// <summary>Decodes the video track of a demuxer somebody else owns.</summary>
    /// <param name="demuxer">The container.</param>
    /// <param name="ownsDemuxer">Whether disposing this disposes it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="demuxer" /> is null.</exception>
    /// <exception cref="InvalidDataException">The segment has no video track.</exception>
    /// <exception cref="NotSupportedException">Its video codec is not registered.</exception>
    public WebMVideoStreamDecoder(MatroskaDemuxer demuxer, bool ownsDemuxer) {
        ArgumentNullException.ThrowIfNull(demuxer);

        this.demuxer = demuxer;
        this.ownsDemuxer = ownsDemuxer;

        var track = demuxer.FindTrack(MatroskaTrackKind.Video)
            ?? throw new InvalidDataException("The segment has no video track.");

        trackNumber = track.Number;

        var info = new VideoTrackInfo(
            track.CodecId,
            track.PixelWidth,
            track.PixelHeight,
            track.CodecPrivate,
            track.ColourSpace,
            RateOf(track),
            track.ColourRange,
            track.ColourMatrix
        );

        if (!VideoCodecRegistry.TryCreate(in info, out var created) || created is null) {
            if (ownsDemuxer) {
                demuxer.Dispose();
            }

            throw new NotSupportedException(
                $"Nothing registered decodes '{track.CodecId}'. Registered: "
                + string.Join(", ", VideoCodecRegistry.RegisteredNames())
                + ". See VideoCodecRegistry.Register."
            );
        }

        codec = created;
        lastFormat = codec.Format;
        Track = track;

        // Before anything is decoded, so that a caller that opens the audio decoder later does not
        // find the first second of it already skipped. See MatroskaDemuxer.Follow.
        demuxer.Follow(trackNumber);
    }

    /// <summary>The demuxer, so that the same file's audio track can be read from it.</summary>
    public MatroskaDemuxer Container => demuxer;

    /// <summary>What the container said about the track being decoded.</summary>
    public MatroskaTrack Track { get; }

    /// <inheritdoc />
    public VideoFormat Format => codec.Format;

    /// <inheritdoc />
    /// <remarks>
    ///     Matroska's <c>DisplayWidth</c> and <c>DisplayHeight</c>, which the demuxer defaults to the
    ///     pixel size when the track states neither — so this is the sample count for every file with
    ///     square pixels and the intended shape for the ones without.
    /// </remarks>
    public Vixen.Core.Mathematics.Int2 DisplaySize => new(Track.DisplayWidth, Track.DisplayHeight);

    /// <inheritdoc />
    public TimeSpan Duration => demuxer.Duration;

    /// <inheritdoc />
    public TimeSpan Position { get; private set; }

    /// <inheritdoc />
    public bool CanSeek => demuxer.CanSeek;

    /// <inheritdoc />
    public VideoDecodeStatus DecodeNext(VideoFrame destination) {
        ArgumentNullException.ThrowIfNull(destination);

        while (true) {
            if (drained) {
                return VideoDecodeStatus.EndOfStream;
            }

            var packet = demuxer.ReadPacket(trackNumber);

            if (packet is null) {
                // The file has ended and the codec may still be holding pictures. Draining is not
                // optional: a stream with reordering keeps up to its reference depth in hand, and a
                // player that stopped here would cut the last half-second off every video.
                var status = codec.Drain(destination);

                if (status == VideoDecodeStatus.Decoded) {
                    Position = destination.Timestamp;

                    return Report(destination);
                }

                drained = true;

                return VideoDecodeStatus.EndOfStream;
            }

            VideoDecodeStatus decoded;

            try {
                decoded = codec.Decode(
                    new VideoPacket(packet.Data, packet.Timestamp, packet.Duration, packet.IsKeyFrame),
                    destination
                );
            } finally {
                demuxer.Release(packet);
            }

            if (decoded is VideoDecodeStatus.Decoded or VideoDecodeStatus.FormatChanged) {
                Position = destination.Timestamp;

                return Report(destination);
            }

            if (decoded == VideoDecodeStatus.EndOfStream) {
                drained = true;

                return VideoDecodeStatus.EndOfStream;
            }
        }
    }

    /// <inheritdoc />
    public void Seek(TimeSpan position) {
        if (!CanSeek) {
            throw new NotSupportedException("The underlying stream cannot seek.");
        }

        demuxer.SeekTo(position, trackNumber);
        codec.Reset();
        drained = false;
        Position = position;
    }

    /// <inheritdoc />
    public void Dispose() {
        codec.Dispose();

        if (ownsDemuxer) {
            demuxer.Dispose();
        }
    }

    static VideoRational RateOf(MatroskaTrack track) {
        if (track.DefaultDuration <= TimeSpan.Zero) {
            return VideoRational.Unknown;
        }

        // The track states nanoseconds per frame; a rational rate is that inverted. A second is
        // 1e9 ns, so the ratio is exact for every rate a muxer can write — including 30000/1001,
        // whose default duration is 33 366 667 ns and whose float is 29.970029970029970…
        return new VideoRational(1_000_000_000, (int)Math.Min(int.MaxValue, track.DefaultDuration.Ticks * 100));
    }

    /// <summary>Says whether the frame just decoded is a different shape from the last one.</summary>
    VideoDecodeStatus Report(VideoFrame frame) {
        if (lastFormat.IsCompatibleWith(frame.Format)) {
            return VideoDecodeStatus.Decoded;
        }

        lastFormat = frame.Format;

        return VideoDecodeStatus.FormatChanged;
    }
}
