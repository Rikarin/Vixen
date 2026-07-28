// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Devices;

namespace Vixen.Audio;

/// <summary>How many frames a second, across how many channels.</summary>
/// <param name="SampleRate">Frames per second — 44 100, 48 000.</param>
/// <param name="Channels">How many channels are interleaved in one frame.</param>
/// <remarks>
///     <para>
///         The pair, and not the sample format, because everything downstream of the decoder is
///         <see langword="float" />. <see cref="AudioClip" /> stores bytes so that a shipped clip can
///         be half the size; the mixer converts once on the way in and the rest of the pipeline —
///         resampling, panning, effects, the sum into a bus — never sees an integer again. A format
///         that carried <see cref="AudioSampleFormat" /> would be describing a decision that has
///         already been taken by the time anybody asks.
///     </para>
///     <para>
///         A device's format is negotiated, not demanded: <see cref="IAudioBackend.OpenDevice" /> is
///         asked for one and <see cref="IAudioDevice.Format" /> says what was granted. A clip at a
///         different rate is resampled per voice, which is the only place the conversion can happen
///         given that two clips at two rates can play at once.
///     </para>
/// </remarks>
public readonly record struct AudioFormat(int SampleRate, int Channels) {
    /// <summary>The most channels the mixer will render into.</summary>
    /// <remarks>
    ///     Eight is 7.1, which is the widest layout any of the six target platforms will hand back
    ///     from a default device. The panner only knows how to place a sound in one or two of them
    ///     today — see <c>Spatializer</c> — and a wider device is rendered to its first two channels
    ///     rather than refused.
    /// </remarks>
    public const int MaxChannels = 8;

    /// <summary>One channel at 48 kHz.</summary>
    public static AudioFormat Mono48k => new(48_000, 1);

    /// <summary>Two channels at 48 kHz — what a default device almost always is.</summary>
    public static AudioFormat Stereo48k => new(48_000, 2);

    /// <summary>Whether this describes something that could actually be rendered.</summary>
    public bool IsValid => SampleRate > 0 && Channels is > 0 and <= MaxChannels;

    /// <summary>How many floats one frame takes.</summary>
    public int SamplesPerFrame => Channels;

    /// <summary>How many frames cover a span of time, rounded up.</summary>
    /// <param name="duration">How long.</param>
    /// <returns>The frame count, never negative.</returns>
    public int FramesFor(TimeSpan duration) =>
        duration <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(duration.TotalSeconds * SampleRate);

    /// <summary>How long a number of frames lasts.</summary>
    /// <param name="frames">How many frames.</param>
    /// <returns>The duration, or zero if the rate is not set.</returns>
    public TimeSpan DurationOf(long frames) =>
        SampleRate <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)frames / SampleRate);

    /// <inheritdoc />
    public override string ToString() => $"{SampleRate} Hz × {Channels} ch";
}
