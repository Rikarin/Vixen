// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Spatial;

/// <summary>Where the speakers are, for a given channel count.</summary>
/// <remarks>
///     <para>
///         <b>Channel order is the one every consumer API agrees on</b> — the WAVE_FORMAT_EXTENSIBLE
///         order, which OpenAL, WASAPI, CoreAudio and every console SDK use: front left, front right,
///         centre, LFE, then the sides, then the backs. Getting it wrong does not sound wrong, it
///         sounds like the room is inside out, and it is the one thing about surround that cannot be
///         debugged by ear without a reference.
///     </para>
///     <para>
///         <b>The angles are the ITU-R BS.775 arrangement</b>, which is what a domestic system is set
///         up to and what a console's own panner assumes. They are not adjustable: a game that lets a
///         player nudge them is guessing about a room it cannot see, and every platform already
///         exposes a speaker-position setting that its own mixer honours downstream of this.
///     </para>
///     <para>
///         <b>The LFE is not a direction and is never panned into.</b> It is a band, not a place —
///         the ".1" carries everything below about 120 Hz from the other channels, and a mixer that
///         panned a source into it would put that source's whole spectrum through a subwoofer. What
///         feeds it is a bus with a low-pass on it, which is a mix decision.
///     </para>
/// </remarks>
public static class SpeakerLayout {
    // Front left and right at ±30, which is the sixty-degree stereo triangle every arrangement is
    // built out from. Sides at ±110 and backs at ±150, per BS.775.
    static readonly float[] Mono = [0f];
    static readonly float[] Stereo = [-30f, 30f];
    static readonly float[] Quad = [-45f, 45f, -135f, 135f];
    static readonly float[] Surround51 = [-30f, 30f, 0f, 0f, -110f, 110f];
    static readonly float[] Surround71 = [-30f, 30f, 0f, 0f, -110f, 110f, -150f, 150f];

    /// <summary>The angle of each speaker, in degrees: 0 straight ahead, positive to the right.</summary>
    /// <param name="channels">How many channels the device has.</param>
    /// <returns>One angle per channel. Empty for a count with no known arrangement.</returns>
    /// <remarks>
    ///     A count nothing knows about — three, five, seven — gets an empty span rather than a guess,
    ///     and the caller falls back to the stereo law across the first two. Inventing an arrangement
    ///     for an unknown count is how a sound ends up in a speaker that is not where the layout
    ///     thought it was.
    /// </remarks>
    public static ReadOnlySpan<float> Angles(int channels) => channels switch {
        1 => Mono,
        2 => Stereo,
        4 => Quad,
        6 => Surround51,
        8 => Surround71,
        _ => []
    };

    /// <summary>Which channel is the low-frequency effects channel, or −1 if there is none.</summary>
    /// <param name="channels">How many channels the device has.</param>
    /// <returns>The index.</returns>
    public static int LowFrequencyChannel(int channels) => channels is 6 or 8 ? 3 : -1;

    /// <summary>Whether a channel count is one this knows how to place a sound in.</summary>
    /// <param name="channels">How many channels the device has.</param>
    /// <returns>Whether there is a layout for it.</returns>
    public static bool IsKnown(int channels) => !Angles(channels).IsEmpty;
}
