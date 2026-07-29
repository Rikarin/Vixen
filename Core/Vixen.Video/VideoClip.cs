// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Video;

/// <summary>A video the content build knows about: where its bytes are, and what is in them.</summary>
/// <remarks>
///     <para>
///         <b>A reference, not the bytes.</b> Every other clip in the engine holds its data —
///         <c>AudioClip</c> holds the samples, a texture holds the texels — and a video cannot. A
///         two-minute cutscene is a hundred megabytes; loading it to play it would mean the loading
///         screen for a cutscene being longer than the cutscene. So the asset is the metadata the
///         editor and the game need before opening the file, and the file is streamed.
///     </para>
///     <para>
///         <b>The metadata is the importer's, not the demuxer's.</b> Everything here is readable from
///         the container, and reading it means opening the file — which is exactly what a game
///         choosing between three cutscenes, or an editor drawing a list of them, does not want to
///         do. The importer reads it once at build time and writes it here.
///     </para>
/// </remarks>
[DataContract("VideoClip")]
public sealed record VideoClip {
    /// <summary>The address of the file, as the content build knows it.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>Where the container's bytes are, for whatever opens them.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Separate from <see cref="Address" />, and it is the address of a different thing.</b>
    ///         This record is a few dozen bytes an editor lists a hundred of; the container is the
    ///         hundred megabytes nobody wants to touch to draw that list. They are two sub-assets of
    ///         one import for exactly that reason, so a clip has to name the other one or a game
    ///         holding a clip still has nothing to open.
    ///     </para>
    ///     <para>
    ///         Empty when the import did not embed the container — a video left loose beside the
    ///         executable, which is a reasonable thing to do with something that is streamed — and
    ///         <c>VideoPlayback.Open</c> falls back to <see cref="Address" /> when it is.
    ///     </para>
    /// </remarks>
    public string ContainerAddress { get; set; } = string.Empty;

    /// <summary>The picture's width in samples.</summary>
    public int Width { get; set; }

    /// <summary>Its height in samples.</summary>
    public int Height { get; set; }

    /// <summary>How long it is, or zero if the container did not say.</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>The nominal frame rate's numerator.</summary>
    /// <remarks>
    ///     Stored as two integers rather than as a <see cref="VideoRational" /> because a serialised
    ///     asset is a flat record and a rate is two numbers — and because the whole point of the
    ///     rational is that the value it represents is not writable as one.
    /// </remarks>
    public int FrameRateNumerator { get; set; }

    /// <summary>The nominal frame rate's denominator.</summary>
    public int FrameRateDenominator { get; set; }

    /// <summary>The video codec the container names — <c>V_VP9</c>, <c>V_UNCOMPRESSED</c>.</summary>
    /// <remarks>
    ///     Here so that a game can find out it has no decoder for a clip <em>before</em> the cutscene
    ///     is supposed to start, which is the difference between a fallback and a black screen.
    /// </remarks>
    public string CodecId { get; set; } = string.Empty;

    /// <summary>The sample format of an uncompressed track — <c>I420</c>, <c>BGRA</c> — or empty.</summary>
    /// <remarks>
    ///     Carried because <see cref="CodecId" /> alone does not answer the question
    ///     <see cref="CodecId" /> exists to answer for the one codec the engine does ship:
    ///     <c>V_UNCOMPRESSED</c> is a family, and whether it can be played depends on which member of
    ///     it the file holds. Empty for every compressed codec, where the id is the whole answer.
    /// </remarks>
    public string FourCc { get; set; } = string.Empty;

    /// <summary>The audio codec the container names, or empty if there is no audio track.</summary>
    public string AudioCodecId { get; set; } = string.Empty;

    /// <summary>Whether the file has an audio track at all.</summary>
    public bool HasAudio { get; set; }

    /// <summary>The nominal frame rate.</summary>
    public VideoRational FrameRate => new(FrameRateNumerator, FrameRateDenominator);
}
