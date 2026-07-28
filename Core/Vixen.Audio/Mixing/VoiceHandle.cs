// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Spatial;

namespace Vixen.Audio.Mixing;

/// <summary>What a voice is doing.</summary>
public enum VoiceState {
    /// <summary>Nothing. The slot is available.</summary>
    Free = 0,

    /// <summary>Taken by a caller that has not finished describing it yet.</summary>
    /// <remarks>
    ///     The state a slot is in between <c>AudioEngine.Play</c> minting a handle and the audio
    ///     thread seeing the command. It exists so that the handle is valid the instant it is
    ///     returned — a caller that stops a sound in the same frame it started it must not have its
    ///     stop arrive at a slot that has not been claimed yet.
    /// </remarks>
    Claimed = 1,

    /// <summary>Producing sound.</summary>
    Playing = 2,

    /// <summary>Holding its position and producing silence.</summary>
    Paused = 3,

    /// <summary>Fading out over one block, because something asked it to stop.</summary>
    Stopping = 4,

    /// <summary>Done, and waiting for the game thread to collect it.</summary>
    Finished = 5
}

/// <summary>A reference to a playing sound that cannot be confused with a later one.</summary>
/// <param name="Index">Which slot in the pool.</param>
/// <param name="Generation">Which use of that slot.</param>
/// <remarks>
///     <para>
///         Index plus generation, because a bare index is a use-after-free waiting to happen: a
///         footstep finishes, its slot is reused by an explosion, and the stale handle the footstep
///         code kept now stops the explosion. Every call checks the generation and a stale handle
///         does nothing at all — which is what a caller wants, because "the sound I was holding has
///         already finished" is the normal case rather than an error.
///     </para>
///     <para>
///         Eight bytes, so it can live in a component, be compared, and be copied without a thought.
///     </para>
/// </remarks>
public readonly record struct VoiceHandle(int Index, int Generation) {
    /// <summary>The handle that refers to nothing.</summary>
    public static VoiceHandle None => new(-1, 0);

    /// <summary>Whether it refers to a slot at all. Says nothing about whether that slot is still this sound.</summary>
    public bool IsValid => Index >= 0;
}

/// <summary>How a sound should be played.</summary>
/// <remarks>
///     A value with defaults that describe the ordinary case — full volume, unaltered pitch, on the
///     master bus, in the room rather than in the world — so <c>engine.Play(clip)</c> means something
///     sensible and every field is an override of a stated default rather than a thing that must be
///     filled in.
/// </remarks>
public readonly record struct PlaybackSettings() {
    /// <summary>Which bus to route into. Zero is the master.</summary>
    public int Bus { get; init; }

    /// <summary>An extra bus a copy of this sound also goes to, or −1 for none.</summary>
    /// <remarks>
    ///     What a bus send cannot express: a send on a bus is one amount for everything routed
    ///     through it, so every emitter in a room shares a reverb level. This is per sound, which is
    ///     what lets one that is deeper into the room be wetter than one by the door.
    /// </remarks>
    public int SendBus { get; init; } = -1;

    /// <summary>How much goes to <see cref="SendBus" />, as a linear gain.</summary>
    public float SendLevel { get; init; }

    /// <summary>A linear gain. One is unaltered.</summary>
    public float Gain { get; init; } = 1f;

    /// <summary>A playback rate multiplier. Two is an octave up and half the length.</summary>
    /// <remarks>
    ///     Pitch and speed together, because resampling is what this does and there is no
    ///     time-stretch in the mixer. A sound that must change pitch without changing length is
    ///     authored that way.
    /// </remarks>
    public float Pitch { get; init; } = 1f;

    /// <summary>Where it sits between the speakers when it is not spatialised. −1 left, +1 right.</summary>
    public float Pan { get; init; }

    /// <summary>Whether the clip wraps round instead of ending.</summary>
    /// <remarks>
    ///     Honoured by the overloads that take an <see cref="AudioClip" />, which build the provider.
    ///     A caller supplying its own <c>IAudioSampleProvider</c> has already decided this and the
    ///     field is ignored.
    /// </remarks>
    public bool Loop { get; init; }

    /// <summary>Whether it is a thing in the world rather than a sound in the room.</summary>
    public bool IsSpatial { get; init; }

    /// <summary>Where in the world, and how it behaves there. Read only when <see cref="IsSpatial" />.</summary>
    public SpatialSettings Spatial { get; init; }

    /// <summary>Whether it starts paused, so a caller can position it before a single frame is heard.</summary>
    public bool StartPaused { get; init; }

    /// <summary>How hard this sound is to displace when the voice pool is full. Higher survives.</summary>
    /// <remarks>
    ///     <para>
    ///         Zero is ordinary. Music, dialogue and anything a player is waiting to hear the end of
    ///         goes above it; footsteps, impacts and ambience stay at it or below.
    ///     </para>
    ///     <para>
    ///         <b>Higher wins, which is the opposite of Unity's convention</b> — there, 0 is the most
    ///         important and 256 the least, inherited from a table where the number was a sort key.
    ///         The inversion is a documented trap in every project that uses it, and there is no
    ///         reason to reproduce it: "more important" reading as "bigger" is what everybody
    ///         guesses.
    ///     </para>
    ///     <para>
    ///         A sound is only ever displaced by one of at least equal priority, so a pool full of
    ///         high-priority sounds refuses a low-priority request rather than making room for it.
    ///     </para>
    /// </remarks>
    public int Priority { get; init; }

    /// <summary>The device frame at which it should begin. Zero is "as soon as the mixer sees it".</summary>
    /// <remarks>
    ///     <para>
    ///         A position on <c>AudioEngine.RenderedFrames</c>, which is the only clock here that
    ///         counts samples actually produced. Scheduling against it is sample-accurate: the audio
    ///         thread knows which frame its block begins at, so a start half way through a block
    ///         happens half way through that block.
    ///     </para>
    ///     <para>
    ///         <b>What it is for is music.</b> Two segments joined on a bar line have to join on the
    ///         sample, not on the frame the game thread happened to notice — the difference is a flam,
    ///         and a flam is the difference between one piece of music and two recordings of one.
    ///         Everything else in the mixer starts when it is asked to and is right to.
    ///     </para>
    /// </remarks>
    public long StartFrame { get; init; }
}
