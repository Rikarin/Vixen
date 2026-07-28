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
}
