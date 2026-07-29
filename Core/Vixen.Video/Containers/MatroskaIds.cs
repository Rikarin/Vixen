// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Video.Containers;

/// <summary>The element ids this reader knows, with their markers, as the specification writes them.</summary>
/// <remarks>
///     <para>
///         A deliberately short list. Matroska has several hundred elements and a player needs
///         twenty-odd of them; everything absent here is skipped by size, which is what makes an
///         unknown element harmless rather than fatal and is the property the format was designed
///         for.
///     </para>
///     <para>
///         The values are quoted the way the specification quotes them — id markers included — so
///         that checking this table against it is reading two columns rather than doing arithmetic.
///     </para>
/// </remarks>
static class MatroskaIds {
    // ── The file ────────────────────────────────────────────────────────────────────────────

    public const uint EbmlHeader = 0x1A45DFA3;
    public const uint DocType = 0x4282;
    public const uint Segment = 0x18538067;

    // ── Segment children ────────────────────────────────────────────────────────────────────

    public const uint SeekHead = 0x114D9B74;
    public const uint Info = 0x1549A966;
    public const uint Tracks = 0x1654AE6B;
    public const uint Cluster = 0x1F43B675;
    public const uint Cues = 0x1C53BB6B;

    // ── Info ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Nanoseconds per timestamp tick. A million unless stated, which is a millisecond.</summary>
    public const uint TimestampScale = 0x2AD7B1;

    /// <summary>The segment's length in timestamp ticks, as a float.</summary>
    public const uint Duration = 0x4489;

    // ── Tracks ──────────────────────────────────────────────────────────────────────────────

    public const uint TrackEntry = 0xAE;
    public const uint TrackNumber = 0xD7;
    public const uint TrackUid = 0x73C5;
    public const uint TrackType = 0x83;
    public const uint CodecId = 0x86;
    public const uint CodecPrivate = 0x63A2;

    /// <summary>How long one frame of this track lasts, in nanoseconds.</summary>
    public const uint DefaultDuration = 0x23E383;

    /// <summary>How much of the decoder's output at the start is priming, in nanoseconds.</summary>
    public const uint CodecDelay = 0x56AA;

    /// <summary>How much must be decoded and discarded after a seek, in nanoseconds.</summary>
    public const uint SeekPreRoll = 0x56BB;

    public const uint TrackVideo = 0xE0;
    public const uint PixelWidth = 0xB0;
    public const uint PixelHeight = 0xBA;
    public const uint DisplayWidth = 0x54B0;
    public const uint DisplayHeight = 0x54BA;

    /// <summary>The FourCC an uncompressed track's samples are in.</summary>
    public const uint ColourSpace = 0x2EB524;

    public const uint Colour = 0x55B0;
    public const uint MatrixCoefficients = 0x55B1;
    public const uint ColourRange = 0x55B9;

    public const uint TrackAudio = 0xE1;
    public const uint SamplingFrequency = 0xB5;
    public const uint Channels = 0x9F;
    public const uint BitDepth = 0x6264;

    // ── Cluster ─────────────────────────────────────────────────────────────────────────────

    public const uint ClusterTimestamp = 0xE7;
    public const uint SimpleBlock = 0xA3;
    public const uint BlockGroup = 0xA0;
    public const uint Block = 0xA1;
    public const uint BlockDuration = 0x9B;
    public const uint ReferenceBlock = 0xFB;

    // ── Cues ────────────────────────────────────────────────────────────────────────────────

    public const uint CuePoint = 0xBB;
    public const uint CueTime = 0xB3;
    public const uint CueTrackPositions = 0xB7;
    public const uint CueTrack = 0xF7;
    public const uint CueClusterPosition = 0xF1;
}
