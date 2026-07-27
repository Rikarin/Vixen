// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core;

namespace Vixen.Audio;

/// <summary>How one sample is stored.</summary>
/// <remarks>
///     Two, and no more, because these are the two every audio API on every platform accepts without
///     a conversion pass. Twenty-four-bit and packed formats exist in files and not in device buffers;
///     the importer converts on the way in, which is the only place the cost is paid once.
/// </remarks>
public enum AudioSampleFormat {
    /// <summary>Signed 16-bit, the baseline for sound effects. Half the memory, and below the noise floor of most of them.</summary>
    Int16,

    /// <summary>32-bit float in −1..1. What a mixer wants, and what a 24-bit or float source keeps its headroom in.</summary>
    Float32
}

/// <summary>Decoded audio: interleaved samples, and what they mean.</summary>
/// <remarks>
///     <para>
///         <b>Interleaved and not planar</b>, because that is what every backend's buffer submission
///         takes — OpenAL, WASAPI, AudioUnit, WebAudio's <c>copyToChannel</c> being the exception that
///         deinterleaves on the way in anyway. Planar would be better for a mixer that processes
///         channels independently, and worse for the far more common job of handing bytes to a driver.
///     </para>
///     <para>
///         <b>The samples are bytes, not <c>short[]</c> or <c>float[]</c>.</b> The format is a value
///         on the clip rather than a fact about the type, so one array is the only representation that
///         does not need the clip to be generic — and a generic <c>[DataContract]</c> is a build error
///         here for good reasons. <see cref="AsInt16" /> and <see cref="AsFloat32" /> reinterpret
///         without copying. Little-endian, like everything else the serializer writes, so a clip built
///         on one machine loads on another.
///     </para>
///     <para>
///         <b><c>set</c> and not <c>init</c>, which is not the style this repository prefers.</b> The
///         binary serializer's generator treats an <c>init</c> accessor as unsettable, finds no
///         constructor to match, and emits a serializer with no members at all — one that writes
///         nothing and reads every field back as its default, without a diagnostic. The generator
///         declares <c>VXS0102</c> for precisely that case and never reports it. Until it either
///         reaches <c>init</c> setters through <c>[UnsafeAccessor]</c>, as the YAML binder already
///         does, or says so out loud, a type that is stored as a chunk has to be settable.
///     </para>
///     <para>
///         <b>What this is not.</b> There is no streaming here — a clip is entirely in memory. Doc 08
///         wants Ogg or Opus kept compressed for music and decoded as it plays, which needs a decoder
///         in the <em>runtime</em> and a clip that is a handle to a stream rather than a buffer. That
///         is a different type and it is owed; see the README.
///     </para>
/// </remarks>
[DataContract("AudioClip")]
public sealed record AudioClip {
    /// <summary>Frames per second — 44 100, 48 000.</summary>
    public int SampleRate { get; set; }

    /// <summary>How many channels are interleaved. One for a sound that will be positioned in the world.</summary>
    public int Channels { get; set; }

    /// <summary>How one sample is stored.</summary>
    public AudioSampleFormat Format { get; set; }

    /// <summary>The interleaved samples.</summary>
    public byte[] Samples { get; set; } = [];

    /// <summary>How many bytes one sample of one channel takes.</summary>
    public int BytesPerSample => Format is AudioSampleFormat.Float32 ? 4 : 2;

    /// <summary>How many frames — one sample for every channel — the clip holds.</summary>
    public int FrameCount => Channels <= 0 ? 0 : Samples.Length / (BytesPerSample * Channels);

    /// <summary>How long it plays for.</summary>
    public TimeSpan Duration =>
        SampleRate <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)FrameCount / SampleRate);

    /// <summary>The samples as signed 16-bit values.</summary>
    /// <returns>The span, or empty if the clip is not <see cref="AudioSampleFormat.Int16" />.</returns>
    /// <remarks>
    ///     Empty rather than throwing, and empty rather than converting. A caller that asked for the
    ///     wrong format wants to find that out where it asked; converting silently would hide a clip
    ///     that shipped as float when the settings said otherwise, which is a real thing to notice.
    /// </remarks>
    public ReadOnlySpan<short> AsInt16() =>
        Format is AudioSampleFormat.Int16 ? MemoryMarshal.Cast<byte, short>(Samples) : [];

    /// <summary>The samples as floats in −1..1.</summary>
    /// <returns>The span, or empty if the clip is not <see cref="AudioSampleFormat.Float32" />.</returns>
    public ReadOnlySpan<float> AsFloat32() =>
        Format is AudioSampleFormat.Float32 ? MemoryMarshal.Cast<byte, float>(Samples) : [];
}
