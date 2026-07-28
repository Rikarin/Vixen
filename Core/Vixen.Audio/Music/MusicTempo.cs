// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Music;

/// <summary>Where a musical change is allowed to happen.</summary>
/// <remarks>
///     <b>The single idea interactive music is built out of.</b> Gameplay decides <em>that</em> the
///     music should change — a fight started, a door opened — and it decides it at whatever arbitrary
///     moment the fight started at. Music cannot change at an arbitrary moment: a cut that lands off
///     the beat is heard as a mistake by people who could not name what a beat is. So the request and
///     the change are separated, and this is what separates them.
/// </remarks>
public enum MusicQuantize {
    /// <summary>Now, wherever "now" falls.</summary>
    /// <remarks>
    ///     For a stinger over the top of something, or for stopping. Not for a cut between two pieces
    ///     of music, which is the thing it will be reached for and the thing it is worst at.
    /// </remarks>
    Immediate = 0,

    /// <summary>At the next beat.</summary>
    Beat = 1,

    /// <summary>At the next bar line.</summary>
    /// <remarks>
    ///     The usual answer. A bar is the unit a listener is actually counting, even when they do not
    ///     know it, and at 120 beats in four it is never more than two seconds away.
    /// </remarks>
    Bar = 2,

    /// <summary>When the segment currently playing runs out.</summary>
    /// <remarks>
    ///     The most musical and the least responsive: a thirty-second loop means up to thirty seconds
    ///     between the fight starting and the music noticing. Right for an ending, wrong for a
    ///     reaction.
    /// </remarks>
    Segment = 3
}

/// <summary>How fast the music is, and how it is counted.</summary>
/// <remarks>
///     <para>
///         <b>Everything is derived in frames, not seconds.</b> A bar at 128 beats a minute is
///         1.875 seconds, which is 90 000 frames at 48 kHz exactly; expressed in seconds and converted
///         later it is 1.8749999 and the error compounds every bar until a four-minute track has
///         drifted audibly against a loop it was supposed to sit on. Integers of frames do not drift.
///     </para>
///     <para>
///         <b>Written <c>new MusicTempo()</c> and never <c>default</c></b>, as every options type here
///         is: the sensible values live in the initialisers, and a <c>default</c> is a tempo of zero
///         beats a minute — which is not slow music, it is arithmetic that divides by nothing.
///     </para>
///     <para>
///         The bottom of the time signature is deliberately absent. What a beat <em>is</em> — a
///         crotchet, a quaver — is a question about notation, and this only needs to know how long one
///         lasts and how many make a bar.
///     </para>
/// </remarks>
public readonly record struct MusicTempo() {
    /// <summary>The tempo.</summary>
    public float BeatsPerMinute { get; init; } = 120f;

    /// <summary>The top of the time signature — four for common time, three for a waltz.</summary>
    public int BeatsPerBar { get; init; } = 4;

    /// <summary>How long one beat lasts, in seconds.</summary>
    public float SecondsPerBeat => BeatsPerMinute > 0f ? 60f / BeatsPerMinute : 0f;

    /// <summary>How long one bar lasts, in seconds.</summary>
    public float SecondsPerBar => SecondsPerBeat * Math.Max(BeatsPerBar, 1);

    /// <summary>How many device frames one beat lasts.</summary>
    /// <param name="sampleRate">The device's rate.</param>
    /// <returns>The count, rounded to the nearest frame.</returns>
    /// <remarks>
    ///     Rounded once, here, and then multiplied — so a bar is exactly four beats and a hundred bars
    ///     is exactly four hundred. Rounding at the bar instead would let a beat and a bar disagree
    ///     about where the same moment is.
    /// </remarks>
    public long FramesPerBeat(int sampleRate) =>
        BeatsPerMinute > 0f && sampleRate > 0 ? (long)MathF.Round(sampleRate * 60f / BeatsPerMinute) : 0;

    /// <summary>How many device frames one bar lasts.</summary>
    /// <param name="sampleRate">The device's rate.</param>
    /// <returns>The count.</returns>
    public long FramesPerBar(int sampleRate) => FramesPerBeat(sampleRate) * Math.Max(BeatsPerBar, 1);
}
