// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Music;

/// <summary>A named place in a segment, in beats from its start.</summary>
/// <param name="Name">What it is called.</param>
/// <param name="Beat">How far in, in beats.</param>
/// <remarks>
///     What gameplay hangs on: a door that opens on the downbeat, a light that flashes on the snare.
///     <c>MusicPlayer.MarkerPassed</c> is raised as the playhead crosses one, on the game thread and
///     therefore late by up to a frame — which is fine for anything visual and is not what a
///     <em>musical</em> change is scheduled with.
/// </remarks>
public readonly record struct MusicMarker(string Name, float Beat);

/// <summary>One piece of music, and what follows it.</summary>
/// <remarks>
///     <para>
///         <b>A segment is a clip with a tempo written on it.</b> The tempo is not derived from the
///         audio and cannot be — it is what the composer wrote, and it is the only thing that makes a
///         bar line a real position rather than a guess. A segment whose declared tempo disagrees with
///         its recording will transition in the wrong place, which is a content mistake this cannot
///         detect and which is why the value belongs beside the clip rather than in the player.
///     </para>
///     <para>
///         <b><see cref="Next" /> is what makes a sequence rather than a playlist.</b> An intro that
///         names a loop, and a loop that names itself, is the whole structure of most game music —
///         and it is arranged so that gameplay says nothing at all until it wants something to change.
///     </para>
/// </remarks>
public sealed record MusicSegment {
    /// <summary>What it is called, and what a transition names it by.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The audio.</summary>
    public AudioClip? Clip { get; init; }

    /// <summary>How fast it is, and how it is counted, where it begins.</summary>
    public MusicTempo Tempo { get; init; } = new();

    /// <summary>Where it changes tempo or metre, if it does.</summary>
    public MusicTempoChange[] TempoChanges { get; init; } = [];

    /// <summary>Whether it vamps here rather than moving on, until gameplay releases it.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>An intro that waits for the player.</b> A door that opens when they are ready, a
    ///         conversation that ends when they say so — the music has to hold, and it has to hold
    ///         musically rather than by looping four bars of nothing. A sustain point is the composer
    ///         saying "here is where this can wait".
    ///     </para>
    ///     <para>
    ///         Off, so a segment does what it says without one. What sustaining means is simply that
    ///         <see cref="Next" /> is not taken and a queued transition does not land until
    ///         <c>MusicPlayer.Release</c> is called — the audio keeps looping, which it was doing
    ///         anyway.
    ///     </para>
    /// </remarks>
    public bool Sustains { get; init; }

    /// <summary>How many times it repeats before moving on. Zero plays it once; negative is forever.</summary>
    /// <remarks>
    ///     Forever is the normal state of a loop and the reason a piece of music can be left alone.
    ///     A segment that loops forever and names no <see cref="Next" /> is where music sits between
    ///     the moments gameplay cares about.
    /// </remarks>
    public int LoopCount { get; init; } = -1;

    /// <summary>Which segment follows when this one runs out. Empty stops.</summary>
    public string Next { get; init; } = string.Empty;

    /// <summary>Named places in it, for gameplay to hang on.</summary>
    public MusicMarker[] Markers { get; init; } = [];
}

/// <summary>A route from one segment to another, and when it is allowed to be taken.</summary>
/// <remarks>
///     <para>
///         <b>Declared rather than called, so the rule is content.</b> Gameplay sets a parameter —
///         <c>intensity = 0.8</c> — and which piece of music that means is an asset edit. The
///         alternative is a switch statement in whichever system happened to notice the fight start,
///         and a composer who wants a different arrangement at 0.6 has to find a programmer.
///     </para>
///     <para>
///         Transitions are checked in the order they are declared and the first that applies wins,
///         so a specific one goes above a general one exactly as it would in any other rule list.
///     </para>
/// </remarks>
public sealed record MusicTransition {
    /// <summary>Which segment it leads out of. Empty means any of them.</summary>
    public string From { get; init; } = string.Empty;

    /// <summary>Which segment it leads to.</summary>
    public string To { get; init; } = string.Empty;

    /// <summary>Where it is allowed to land.</summary>
    public MusicQuantize Quantize { get; init; } = MusicQuantize.Bar;

    /// <summary>Which engine-wide parameter decides, or empty for one gameplay takes by name.</summary>
    public string Parameter { get; init; } = string.Empty;

    /// <summary>The bottom of the range it applies over.</summary>
    public float Minimum { get; init; }

    /// <summary>The top of the range it applies over.</summary>
    public float Maximum { get; init; } = 1f;
}
