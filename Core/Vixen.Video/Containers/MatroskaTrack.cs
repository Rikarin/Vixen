// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Video.Containers;

/// <summary>What a track carries.</summary>
public enum MatroskaTrackKind : byte {
    /// <summary>Subtitles, buttons, metadata — anything this reader has no use for.</summary>
    Other = 0,

    /// <summary>Pictures.</summary>
    Video = 1,

    /// <summary>Sound.</summary>
    Audio = 2
}

/// <summary>One track of a Matroska segment, as the header described it.</summary>
/// <remarks>
///     <para>
///         The fields a player needs and no more. What is deliberately absent is anything to do with
///         editing — track names, languages, flags, chapters, tags — which a container reader for
///         playback never looks at and which would be a tenfold larger parser.
///     </para>
///     <para>
///         <see cref="CodecPrivate" /> is passed through untouched. It is the codec's own header —
///         Opus's <c>OpusHead</c>, VP9's nothing, AVC's parameter sets — and the demuxer is
///         emphatically not in the business of understanding it.
///     </para>
/// </remarks>
public sealed class MatroskaTrack {
    /// <summary>The number blocks refer to this track by.</summary>
    public int Number { get; internal set; }

    /// <summary>The writer's unique id for it, for logs.</summary>
    public ulong Uid { get; internal set; }

    /// <summary>What it carries.</summary>
    public MatroskaTrackKind Kind { get; internal set; }

    /// <summary>The codec id — <c>V_VP9</c>, <c>A_OPUS</c>, <c>V_UNCOMPRESSED</c>.</summary>
    public string CodecId { get; internal set; } = string.Empty;

    /// <summary>The codec's own initialisation bytes, or empty if it needs none.</summary>
    public ReadOnlyMemory<byte> CodecPrivate { get; internal set; }

    /// <summary>How long one frame lasts, or zero if the track does not say.</summary>
    public TimeSpan DefaultDuration { get; internal set; }

    /// <summary>How much of the decoder's output at the start of the stream is priming, not sound.</summary>
    /// <remarks>
    ///     Opus's pre-skip, stated by the muxer rather than by the codec header — and it is the one
    ///     that wins when the two disagree, because it is the one written by whoever put the packets
    ///     in the clusters. A decoder that plays the priming samples starts every track with a few
    ///     milliseconds of artefact.
    /// </remarks>
    public TimeSpan CodecDelay { get; internal set; }

    /// <summary>How much must be decoded and thrown away after a seek before the output is right.</summary>
    /// <remarks>
    ///     Carried rather than acted on. This reader seeks to a cluster boundary and decodes forward
    ///     from there, so the pre-roll is already covered by the distance between the boundary and
    ///     the target — but a caller doing something cleverer needs the number, and it costs a field.
    /// </remarks>
    public TimeSpan SeekPreRoll { get; internal set; }

    // ── Video ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The coded width in samples. Zero for a track that is not video.</summary>
    public int PixelWidth { get; internal set; }

    /// <summary>The coded height in samples.</summary>
    public int PixelHeight { get; internal set; }

    /// <summary>The width it should be shown at, which is the coded width unless it says otherwise.</summary>
    /// <remarks>
    ///     Anamorphic content — a 720×480 picture meant to be seen at 853×480 — is the reason this is
    ///     separate. Nothing in the decode path uses it; a renderer that squares the pixels does.
    /// </remarks>
    public int DisplayWidth { get; internal set; }

    /// <summary>The height it should be shown at.</summary>
    public int DisplayHeight { get; internal set; }

    /// <summary>The FourCC an uncompressed video track's samples are in, or empty.</summary>
    public string ColourSpace { get; internal set; } = string.Empty;

    /// <summary>Which coefficients the samples were made with.</summary>
    public VideoColourMatrix ColourMatrix { get; internal set; } = VideoColourMatrix.Bt709;

    /// <summary>Whether the samples use the whole range.</summary>
    public VideoColourRange ColourRange { get; internal set; } = VideoColourRange.Limited;

    // ── Audio ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Frames a second. Zero for a track that is not audio.</summary>
    public int SampleRate { get; internal set; }

    /// <summary>How many channels.</summary>
    public int Channels { get; internal set; }

    /// <summary>How many bits one sample takes, for the uncompressed codecs. Zero otherwise.</summary>
    public int BitDepth { get; internal set; }
}

/// <summary>One block of one track: the bytes a codec is given, and when they belong.</summary>
/// <remarks>
///     <b>Rented, not owned.</b> A packet comes from <see cref="MatroskaDemuxer.ReadPacket" /> and
///     goes back through <see cref="MatroskaDemuxer.Release" />. Its <see cref="Data" /> is valid
///     until then and meaningless afterwards — a demuxed hour of 1080p is a hundred thousand packets,
///     and allocating an array for each of them is the one thing a container reader must not do.
/// </remarks>
public sealed class MatroskaPacket {
    byte[] buffer = [];

    /// <summary>Which track it belongs to.</summary>
    public int TrackNumber { get; internal set; }

    /// <summary>When it is due, measured from the start of the segment.</summary>
    public TimeSpan Timestamp { get; internal set; }

    /// <summary>How long it lasts, or zero if neither the block nor the track said.</summary>
    public TimeSpan Duration { get; internal set; }

    /// <summary>Whether the stream can be joined here.</summary>
    public bool IsKeyFrame { get; internal set; }

    /// <summary>How many bytes it holds.</summary>
    public int Length { get; internal set; }

    /// <summary>The bytes.</summary>
    public ReadOnlySpan<byte> Data => buffer.AsSpan(0, Length);

    internal Span<byte> Allocate(int length) {
        if (buffer.Length < length) {
            buffer = new byte[length];
        }

        Length = length;

        return buffer.AsSpan(0, length);
    }
}
