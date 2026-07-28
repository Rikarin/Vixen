// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Parameters;
using Vixen.Audio.Spatial;

namespace Vixen.Audio.Events;

/// <summary>What happens when an event is asked to play and it already has all it is allowed.</summary>
public enum EventStealMode {
    /// <summary>Nothing. The request is refused and returns <c>VoiceHandle.None</c>.</summary>
    /// <remarks>
    ///     Right for a sound whose beginning is the point — a weapon's report, a UI confirmation. A
    ///     twelfth simultaneous copy adds nothing but level, and cutting one of the eleven already
    ///     playing to make room for it is strictly worse than dropping it.
    /// </remarks>
    None = 0,

    /// <summary>The one that started first gives way.</summary>
    /// <remarks>
    ///     The default, and right for anything with a tail: the oldest copy is the furthest through
    ///     its decay, so it is the one whose loss is least audible.
    /// </remarks>
    Oldest = 1,

    /// <summary>The one that started last gives way.</summary>
    /// <remarks>
    ///     Which sounds like a strange thing to want until the event is a long one — a looping engine,
    ///     a held note. Then the copy that has been going for a minute is the one the player is
    ///     listening to, and the one that started a moment ago is the interloper.
    /// </remarks>
    Newest = 2,

    /// <summary>The one that is hardest to hear gives way.</summary>
    /// <remarks>
    ///     Audibility, so distance and attenuation count and not just the fader. The most defensible
    ///     answer and the most expensive: it walks the event's live instances and asks the mixer about
    ///     each. That is a handful of reads on a request that was about to be refused anyway.
    /// </remarks>
    Quietest = 3
}

/// <summary>One of the sounds an event can play, and how it differs from its siblings.</summary>
/// <param name="Clip">The audio.</param>
/// <remarks>
///     The offsets exist because variants are recorded, not synthesised: four takes of a footstep
///     arrive at four different levels, and correcting that in the asset is better than correcting it
///     in the wav, which loses the original.
/// </remarks>
public readonly record struct AudioEventVariant(AudioClip Clip) {
    /// <summary>How likely it is, against its siblings. Honoured by the random modes.</summary>
    /// <remarks>
    ///     Relative, not a probability — three variants weighted 1, 1 and 2 make the last one half of
    ///     all plays. Zero means never, which is how a variant is auditioned out without deleting it.
    /// </remarks>
    public float Weight { get; init; } = 1f;

    /// <summary>A level correction for this take alone.</summary>
    public float GainDb { get; init; }

    /// <summary>A tuning correction for this take alone.</summary>
    public float PitchSemitones { get; init; }
}

/// <summary>Another event played alongside this one, a moment later.</summary>
/// <param name="Sound">What to play.</param>
/// <remarks>
///     <para>
///         <b>A gunshot is not one sound.</b> It is a mechanism, a report and a tail, each with its
///         own takes, its own level and its own distance behaviour — the mechanism is quiet and
///         carries ten metres, the report is loud and carries five hundred. One clip cannot be both,
///         and three <c>Play</c> calls in gameplay put the layering in C# where the person who can
///         hear it is not.
///     </para>
///     <para>
///         <b>The layer is a whole event, not a variant.</b> So it has its own variants, its own
///         instance limit and its own parameters, and it can be played on its own as well — a tail
///         that a scripted moment fires directly is the same event the gunshot layers.
///     </para>
///     <para>
///         <b>Cycles are impossible by construction</b>, which is why nothing checks for them: a
///         layer holds an <see cref="AudioEvent" /> that already exists, so A can only layer B if B
///         was built first, and B cannot then layer A.
///     </para>
/// </remarks>
public sealed record AudioEventLayer(AudioEvent Sound) {
    /// <summary>How long after the parent it starts.</summary>
    /// <remarks>
    ///     Usually a few tens of milliseconds, and it is most of what makes two sounds read as one
    ///     event rather than as two. Zero plays it in the same call.
    /// </remarks>
    public float DelaySeconds { get; init; }

    /// <summary>A level trim, on top of whatever the layer's own event says.</summary>
    public float GainDb { get; init; }

    /// <summary>A tuning trim, on top of whatever the layer's own event says.</summary>
    public float PitchSemitones { get; init; }

    /// <summary>How often it happens at all, from 0 to 1.</summary>
    /// <remarks>
    ///     A ricochet that follows one impact in four is the cheapest variety there is, and it costs
    ///     one comparison. One is always.
    /// </remarks>
    public float Probability { get; init; } = 1f;
}

/// <summary>An event, with everything resolved: the clips are clips and the bus is an index.</summary>
/// <remarks>
///     <para>
///         The runtime half of the parallel model <c>MixerAsset</c> established. <c>AudioEventAsset</c>
///         is what a file holds — names and chunk ids — and this is what an engine can act on. Keeping
///         them apart is what lets an event be built in code without inventing a chunk id for a clip
///         that is already in hand, and what keeps the loading concerns out of the play path.
///     </para>
///     <para>
///         A record with defaults throughout, so <c>new AudioEventDescription { Variants = [...] }</c>
///         is a complete, sensible event: shuffled, full volume, unlooped, on the master bus.
///     </para>
/// </remarks>
public sealed record AudioEventDescription {
    /// <summary>What it is called, for logs and for an editor's list.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The sounds it can play. One is normal; none plays nothing and is not an error.</summary>
    public AudioEventVariant[] Variants { get; init; } = [];

    /// <summary>How it chooses between them.</summary>
    public VariantSelection Selection { get; init; } = VariantSelection.Shuffle;

    /// <summary>Where the random sequence starts.</summary>
    /// <remarks>
    ///     Left at zero every event in the game shares a starting point, which is invisible in play
    ///     and convenient in a test. Set it per event — from a hash of the name, say — if two events
    ///     drawing in lockstep would ever be noticeable.
    /// </remarks>
    public uint Seed { get; init; }

    /// <summary>Which bus it routes into. Zero is the master.</summary>
    public int Bus { get; init; }

    /// <summary>Its level, before any variation.</summary>
    public float GainDb { get; init; }

    /// <summary>How far either side of <see cref="GainDb" /> a play may land.</summary>
    /// <remarks>
    ///     Two or three decibels is the usual amount, and it is the other half of what stops a
    ///     repeated sound sounding mechanical — a listener who cannot name what changed still hears
    ///     that something did.
    /// </remarks>
    public float GainVarianceDb { get; init; }

    /// <summary>How far either side of the written pitch a play may land, in semitones.</summary>
    /// <remarks>
    ///     A semitone or two. Beyond about three it stops reading as variation and starts reading as
    ///     a different object, because pitch is most of how a listener judges size.
    /// </remarks>
    public float PitchVarianceSemitones { get; init; }

    /// <summary>Whether a play wraps round instead of ending.</summary>
    public bool Loop { get; init; }

    /// <summary>How hard a play is to displace when the voice pool is full. Higher survives.</summary>
    public int Priority { get; init; }

    /// <summary>How many copies may sound at once. Zero is no limit.</summary>
    /// <remarks>
    ///     <para>
    ///         The setting that stops forty simultaneous bullet impacts from being forty voices and
    ///         a wall of level. Four is a lot for most impacts; one is right for anything a player
    ///         should hear as a single object.
    ///     </para>
    ///     <para>
    ///         <b>Per event, not per pool.</b> <c>PlaybackSettings.Priority</c> decides who loses when
    ///         the whole engine is out of voices; this decides how much of the engine one event is
    ///         allowed to be in the first place, which is the limit that actually gets hit.
    ///     </para>
    /// </remarks>
    public int MaxInstances { get; init; }

    /// <summary>What gives way when <see cref="MaxInstances" /> is reached.</summary>
    public EventStealMode Steal { get; init; } = EventStealMode.Oldest;

    /// <summary>The named values its plays read, or null for a sound nothing drives.</summary>
    /// <remarks>
    ///     Shared by every play: one sheet describes the sound and each play gets its own set of
    ///     values, so ten players talking through one "underwater" description is one sheet and ten
    ///     sets of floats. <c>AudioEvent.Play</c> attaches it, and the handle it returns is what a
    ///     caller sets the values through.
    /// </remarks>
    public AudioParameterSheet? Parameters { get; init; }

    /// <summary>Other events played alongside this one, each a moment later.</summary>
    /// <remarks>
    ///     Played in addition to the variant, not instead of it. An event with layers and no variants
    ///     of its own is the ordinary way to write a composite: the parent is a container that decides
    ///     where the whole thing is, and every sound comes from a layer.
    /// </remarks>
    public AudioEventLayer[] Layers { get; init; } = [];

    /// <summary>Whether it is a thing in the world rather than a sound in the room.</summary>
    public bool IsSpatial { get; init; }

    /// <summary>How it attenuates, cones and dopplers. The position comes from the caller.</summary>
    /// <remarks>
    ///     <b>The split that makes an event worth having.</b> How a sound behaves in space is a
    ///     property of the sound and belongs with it; where it is, is a property of the frame and
    ///     belongs to gameplay. So a designer changes a rolloff without a programmer, and gameplay
    ///     passes a position without knowing what a rolloff is.
    /// </remarks>
    public SpatialSettings Spatial { get; init; } = new();
}
