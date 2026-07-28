// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio;
using Vixen.Audio.Streaming;
using Vixen.Video.Containers;

namespace Vixen.Video.Audio;

/// <summary>The audio track of a Matroska segment, behind the interface the mixer already streams.</summary>
/// <remarks>
///     <para>
///         <b>A video's audio is not a separate file, and this is what makes that not a problem.</b>
///         The picture and the sound are interleaved in one segment and have to be read by one
///         demuxer — reading the same file twice would mean two file handles, two positions, and a
///         seek in one that the other knows nothing about. So the video decoder owns the demuxer and
///         this shares it, and a seek in either lands both on the same cluster.
///     </para>
///     <para>
///         <b>It is an <c>IAudioStreamDecoder</c>, so the mixer needs to know nothing about video.</b>
///         <c>AudioEngine.PlayStream</c> takes one of these exactly as it takes an Ogg, the streaming
///         pump fills its ring buffer on the same thread as everything else, and the fact that the
///         bytes came out of a film is invisible from there.
///     </para>
///     <para>
///         <b>Both halves must be drained.</b> A player that reads video and never reads audio makes
///         the demuxer hold every audio packet of the file — see <see cref="MatroskaDemuxer" />. The
///         pump does the draining in practice, which is why <c>VideoPlayer</c> starts the audio
///         stream before it starts decoding pictures.
///     </para>
/// </remarks>
public sealed class MatroskaAudioStreamDecoder : IAudioStreamDecoder {
    readonly IAudioPacketDecoder decoder;
    readonly MatroskaDemuxer demuxer;
    readonly bool ownsDecoder;
    readonly bool ownsDemuxer;
    readonly int trackNumber;

    float[] buffered = [];
    int bufferedFrames;
    int bufferedOffset;
    bool ended;
    bool resynchronise;

    /// <summary>Reads a track with a decoder for its codec.</summary>
    /// <param name="demuxer">The container. Shared, not owned, unless <paramref name="ownsDemuxer" />.</param>
    /// <param name="track">Which track.</param>
    /// <param name="decoder">What decodes its packets.</param>
    /// <param name="ownsDemuxer">Whether disposing this disposes the demuxer.</param>
    /// <param name="ownsDecoder">Whether disposing this disposes the packet decoder.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public MatroskaAudioStreamDecoder(
        MatroskaDemuxer demuxer,
        MatroskaTrack track,
        IAudioPacketDecoder decoder,
        bool ownsDemuxer = false,
        bool ownsDecoder = true
    ) {
        ArgumentNullException.ThrowIfNull(demuxer);
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(decoder);

        this.demuxer = demuxer;
        this.decoder = decoder;
        this.ownsDemuxer = ownsDemuxer;
        this.ownsDecoder = ownsDecoder;
        trackNumber = track.Number;
        Track = track;

        // Before anything is read, so the blocks in front of the first Decode are buffered rather
        // than skipped. See MatroskaDemuxer.Follow.
        demuxer.Follow(trackNumber);

        FrameCount = demuxer.Duration > TimeSpan.Zero
            ? (long)(demuxer.Duration.TotalSeconds * Format.SampleRate)
            : -1;
    }

    /// <summary>What the container said about the track.</summary>
    public MatroskaTrack Track { get; }

    /// <inheritdoc />
    public AudioFormat Format => decoder.Format;

    /// <inheritdoc />
    public long FrameCount { get; }

    /// <inheritdoc />
    public long Position { get; private set; }

    /// <inheritdoc />
    public bool CanSeek => demuxer.CanSeek;

    /// <inheritdoc />
    public int Decode(Span<float> destination, int frameCount) {
        var channels = Format.Channels;

        if (channels <= 0 || frameCount <= 0) {
            return 0;
        }

        var produced = 0;

        while (produced < frameCount) {
            if (bufferedFrames == 0 && !Fill()) {
                break;
            }

            var take = Math.Min(frameCount - produced, bufferedFrames);
            var from = buffered.AsSpan(bufferedOffset * channels, take * channels);

            from.CopyTo(destination[(produced * channels)..]);

            bufferedOffset += take;
            bufferedFrames -= take;
            produced += take;
        }

        Position += produced;

        return produced;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Lands on the cluster covering the frame and plays from there, so the position afterwards is
    ///     the first packet's rather than the one asked for. That is what seeking a container without
    ///     sample-accurate indexing means, and a caller that needs the exact frame drops the
    ///     difference on the floor — which for a video's audio track is the right answer anyway,
    ///     since the picture lands on the same cluster.
    /// </remarks>
    public void Seek(long frame) {
        if (!CanSeek) {
            throw new NotSupportedException("The underlying stream cannot seek.");
        }

        var rate = Format.SampleRate;
        var position = rate > 0 ? TimeSpan.FromSeconds((double)Math.Max(0, frame) / rate) : TimeSpan.Zero;

        demuxer.SeekTo(position, trackNumber);
        decoder.Reset();

        bufferedFrames = 0;
        bufferedOffset = 0;
        ended = false;
        resynchronise = true;
        Position = Math.Max(0, frame);
    }

    /// <inheritdoc />
    public void Dispose() {
        if (ownsDecoder) {
            decoder.Dispose();
        }

        if (ownsDemuxer) {
            demuxer.Dispose();
        }
    }

    /// <summary>Opens the first audio track of a segment, if anything registered can decode it.</summary>
    /// <param name="demuxer">The container.</param>
    /// <param name="stream">The decoder, when there was a track this could read.</param>
    /// <returns>Whether there was.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="demuxer" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         False rather than throwing for a codec nothing registered decodes, because a video with
    ///         an Opus track and no Opus decoder linked is an ordinary situation with an obvious
    ///         behaviour — play the picture, say so once — and not an error the caller can act on.
    ///     </para>
    ///     <para>
    ///         What is registered by default is the uncompressed pair. Referencing
    ///         <c>Vixen.Video.Codecs</c> and calling <c>VideoAudioCodecs.RegisterOpus</c> adds the
    ///         codec WebM actually ships with.
    ///     </para>
    /// </remarks>
    public static bool TryOpen(MatroskaDemuxer demuxer, out MatroskaAudioStreamDecoder? stream) {
        ArgumentNullException.ThrowIfNull(demuxer);

        stream = null;

        var track = demuxer.FindTrack(MatroskaTrackKind.Audio);

        if (track is null) {
            return false;
        }

        var info = new AudioTrackInfo(
            track.CodecId,
            track.SampleRate,
            track.Channels,
            track.BitDepth,
            track.CodecPrivate,
            track.CodecDelay
        );

        if (!AudioPacketDecoderRegistry.TryCreate(in info, out var decoder) || decoder is null) {
            return false;
        }

        stream = new MatroskaAudioStreamDecoder(demuxer, track, decoder);

        return true;
    }

    /// <summary>Pulls one packet and decodes it into the buffer.</summary>
    /// <returns>Whether anything came out.</returns>
    bool Fill() {
        while (!ended) {
            var packet = demuxer.ReadPacket(trackNumber);

            if (packet is null) {
                ended = true;

                return false;
            }

            if (resynchronise) {
                // The seek landed on a cluster boundary rather than on a frame, so the truth about
                // where playback actually resumed is the first packet's timestamp — not the frame
                // that was asked for.
                Position = (long)(packet.Timestamp.TotalSeconds * Format.SampleRate);
                resynchronise = false;
            }

            var needed = decoder.MaxFramesPerPacket * Format.Channels;

            if (buffered.Length < needed) {
                buffered = new float[needed];
            }

            int frames;

            try {
                frames = decoder.Decode(packet.Data, buffered);
            } finally {
                demuxer.Release(packet);
            }

            if (frames <= 0) {
                continue;
            }

            bufferedFrames = frames;
            bufferedOffset = 0;

            return true;
        }

        return false;
    }
}
