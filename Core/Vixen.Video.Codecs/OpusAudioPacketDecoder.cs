// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Vixen.Audio;
using Vixen.Audio.Codecs;
using Vixen.Video.Audio;

namespace Vixen.Video.Codecs;

/// <summary>Opus, as a container's audio track rather than as a voice packet.</summary>
/// <remarks>
///     <para>
///         <b>The adapter is the whole of it.</b> <c>Vixen.Audio.Codecs.OpusPacketDecoder</c> already
///         takes a packet and produces frames, which is exactly the shape a Matroska audio track
///         needs — the codec and the container come from different places, which is the reason
///         <c>IAudioPacketDecoder</c> exists at all. What this adds is the pre-skip, and the
///         registration that makes <c>MatroskaAudioStreamDecoder.TryOpen</c> find it.
///     </para>
///     <para>
///         <b>Why it is a separate assembly.</b> The same argument <c>Vixen.Audio.Codecs</c> makes:
///         a game whose only video is an uncompressed sting should not carry Concentus, so the
///         module that reads containers registers nothing and the module that references a codec is
///         the one you opt into. <c>Vixen.Video</c> would otherwise drag a general-purpose audio
///         codec into every project that plays a logo.
///     </para>
///     <para>
///         <b>Opus always decodes at 48 kHz</b>, whatever the track's <c>SamplingFrequency</c> says.
///         That element describes what was fed to the encoder and has no bearing on what comes out —
///         a reader that resampled to honour it would be undoing nothing and costing a pass.
///     </para>
/// </remarks>
public sealed class OpusAudioPacketDecoder : IAudioPacketDecoder {
    /// <summary>The largest frame count Concentus's decoder is asked to have room for.</summary>
    /// <remarks>
    ///     Sixty milliseconds at 48 kHz. Opus's own maximum is a hundred and twenty, assembled from
    ///     two sixties in one packet, and no muxer writes it — a WebM's Opus track is twenty
    ///     milliseconds a packet essentially always. A packet longer than this is decoded to the
    ///     first sixty rather than refused, which is the failure mode a stream nobody produces
    ///     deserves.
    /// </remarks>
    public const int MaximumFrames = 2_880;

    readonly OpusPacketDecoder decoder;
    readonly int preSkip;

    int remainingPreSkip;

    /// <summary>Creates a decoder for a track.</summary>
    /// <param name="track">What the container said about it.</param>
    /// <exception cref="NotSupportedException">Opus has no such channel count.</exception>
    public OpusAudioPacketDecoder(in AudioTrackInfo track) {
        var channels = track.Channels;

        if (channels is < 1 or > 2) {
            // Concentus decodes mono and stereo. Opus's channel mapping families do more, and a
            // 5.1 WebM is a thing that exists — saying so is more useful than a silent downmix.
            throw new NotSupportedException(
                $"This Opus decoder handles mono and stereo; the track declares {channels} channels."
            );
        }

        decoder = new OpusPacketDecoder(channels, frameMilliseconds: 60);
        preSkip = PreSkipOf(in track);
        remainingPreSkip = preSkip;
    }

    /// <inheritdoc />
    public AudioFormat Format => decoder.Format;

    /// <inheritdoc />
    public int MaxFramesPerPacket => MaximumFrames;

    /// <summary>How many frames of priming this stream declared.</summary>
    public int PreSkip => preSkip;

    /// <inheritdoc />
    /// <remarks>
    ///     The priming samples are discarded here rather than by the caller, because the caller has
    ///     no way to know how many there are — the number is the codec's, and it arrives in the
    ///     codec's own header.
    /// </remarks>
    public int Decode(ReadOnlySpan<byte> packet, Span<float> destination) {
        var frames = decoder.Decode(packet, destination);

        if (frames <= 0 || remainingPreSkip <= 0) {
            return frames;
        }

        var dropped = Math.Min(remainingPreSkip, frames);

        remainingPreSkip -= dropped;

        if (dropped == frames) {
            return 0;
        }

        // Whatever is left of the packet, moved to the front. A whole packet of priming is the
        // ordinary case and returns zero above; this is the packet the priming ends inside.
        var channels = Format.Channels;

        destination[(dropped * channels)..(frames * channels)].CopyTo(destination);

        return frames - dropped;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <b>The pre-skip is not re-armed.</b> It is a property of the start of the stream, and a
    ///     seek has already passed it — discarding it again would drop six milliseconds of sound that
    ///     should be heard, and would put the position six milliseconds out of step with the
    ///     timestamps the container states. The cost is that looping back to zero plays the priming
    ///     samples once, which is an artefact shorter than a frame of video.
    /// </remarks>
    public void Reset() => decoder.Reset();

    /// <inheritdoc />
    public void Dispose() => decoder.Dispose();

    /// <summary>How many frames of priming to discard, from whichever source stated it.</summary>
    /// <param name="track">What the container said.</param>
    /// <returns>The frame count, at 48 kHz.</returns>
    /// <remarks>
    ///     Matroska's <c>CodecDelay</c> wins over the <c>OpusHead</c>'s own field when both are
    ///     present, because it is the one the muxer wrote knowing what it had actually put in the
    ///     clusters — and a remux that trimmed the start updates it and cannot update the codec's
    ///     header, which is passed through untouched.
    /// </remarks>
    public static int PreSkipOf(in AudioTrackInfo track) {
        if (track.CodecDelay > TimeSpan.Zero) {
            return (int)Math.Round(track.CodecDelay.TotalSeconds * OpusPacketDecoder.Rate);
        }

        var header = track.CodecPrivate.Span;

        // "OpusHead", version, channels, then the pre-skip as a little-endian sixteen-bit count of
        // 48 kHz samples. Nineteen bytes is the shortest legal header.
        if (header.Length < 19 || !header[..8].SequenceEqual("OpusHead"u8)) {
            return 0;
        }

        return BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
    }
}

/// <summary>Makes <see cref="OpusAudioPacketDecoder" />s for <c>A_OPUS</c> tracks.</summary>
public sealed class OpusAudioPacketDecoderFactory : IAudioPacketDecoderFactory {
    /// <inheritdoc />
    public string Name => "opus";

    /// <inheritdoc />
    public bool CanDecode(in AudioTrackInfo track) =>
        track.CodecId.Equals("A_OPUS", StringComparison.OrdinalIgnoreCase) && track.Channels is 1 or 2;

    /// <inheritdoc />
    public IAudioPacketDecoder Create(in AudioTrackInfo track) => new OpusAudioPacketDecoder(in track);
}

/// <summary>Turns this assembly on.</summary>
/// <remarks>
///     <para>
///         One call, and every WebM with an Opus track becomes playable through
///         <c>MatroskaAudioStreamDecoder.TryOpen</c> — which is the only thing above the seam that
///         changes. Registration rather than a reference doing it by itself, because a module that
///         alters global state when it is merely linked is a module whose behaviour depends on the
///         trimmer.
///     </para>
///     <para>
///         Idempotent: calling it twice registers one factory, so a library and its host may both
///         ask without arranging not to.
///     </para>
/// </remarks>
public static class VideoAudioCodecs {
    static readonly Lock Gate = new();

    static bool registered;

    /// <summary>Registers Opus as an audio packet decoder.</summary>
    public static void RegisterOpus() {
        lock (Gate) {
            if (registered) {
                return;
            }

            AudioPacketDecoderRegistry.Register(new OpusAudioPacketDecoderFactory());
            registered = true;
        }
    }
}
