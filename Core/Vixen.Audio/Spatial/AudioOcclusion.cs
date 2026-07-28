// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Mixing;
using Vixen.Core.Mathematics;

namespace Vixen.Audio.Spatial;

/// <summary>Keeps every voice's occlusion up to date, without casting a ray for each of them.</summary>
/// <remarks>
///     <para>
///         <b>Two problems, and neither of them is the raycast.</b> Asking whether a wall is in the
///         way is one call. Doing it for every audible voice every frame is sixty-four calls a frame,
///         and taking the answer at face value makes sound flicker — a source near the edge of a
///         doorway alternates between blocked and clear as either end moves a few centimetres, and
///         the ear hears that as a stutter far more clearly than it hears the occlusion itself.
///     </para>
///     <para>
///         <b>So the queries are rationed and the answers are smoothed.</b> <see cref="Budget" />
///         casts per frame, handed out round-robin, so the cost is fixed no matter how many voices
///         there are — a voice simply keeps last frame's answer until its turn comes round.
///         <see cref="SeekSeconds" /> is how long a full swing from clear to blocked takes, so the
///         flicker at a boundary becomes a slow settle instead.
///     </para>
///     <para>
///         <b>What it does with the number is nothing.</b> The value is written onto the voice and
///         that is the end of it; turning "0.8 blocked" into a level and a cutoff is
///         <see cref="Vixen.Audio.Parameters.AudioBuiltinParameter.Occlusion" /> and an authored curve, exactly like
///         distance. A muffling that sounded right for a stone corridor and wrong for a canvas tent
///         is then an asset edit rather than a constant in here.
///     </para>
///     <para>
///         <b>Only voices that are spatial and not virtual are asked about.</b> A 2D sound has no
///         position for a ray to start at, and a virtualised one is not being heard — spending the
///         frame's budget on it would be taking casts away from something audible.
///     </para>
/// </remarks>
public sealed class AudioOcclusion {
    readonly float[] targets;

    int cursor;

    /// <summary>An occlusion pass over a voice pool.</summary>
    /// <param name="capacity">How many voices the pool has.</param>
    public AudioOcclusion(int capacity) => targets = new float[capacity];

    /// <summary>Who answers the question. Null turns the whole thing off.</summary>
    public IAudioOcclusionProvider? Provider { get; set; }

    /// <summary>How many voices are asked about per frame.</summary>
    /// <remarks>
    ///     Eight at sixty frames a second is four hundred and eighty casts a second, which is nothing
    ///     against a physics budget and means a pool of sixty-four voices is fully refreshed about
    ///     seven times a second. Occlusion changes when somebody walks through a door, and seven times
    ///     a second is far quicker than anybody walks.
    /// </remarks>
    public int Budget { get; set; } = 8;

    /// <summary>How long a swing from wide open to fully blocked takes, in seconds.</summary>
    /// <remarks>
    ///     The number that decides whether a doorway sounds like a doorway or like a switch. Long
    ///     enough that a boundary does not chatter, short enough that walking through a door is not
    ///     audibly late — a quarter of a second is both.
    /// </remarks>
    public float SeekSeconds { get; set; } = 0.25f;

    /// <summary>How many queries have been made, for a profiler to look at.</summary>
    public long Queries { get; private set; }

    /// <summary>Asks about a few voices, and moves all of them towards their answers.</summary>
    /// <param name="voices">The pool.</param>
    /// <param name="listeners">Where the ears are.</param>
    /// <param name="deltaSeconds">How much game time has passed.</param>
    internal void Update(Voice[] voices, in AudioListenerSet listeners, float deltaSeconds) {
        if (Provider is null || listeners.Count == 0) {
            return;
        }

        Query(voices, listeners);
        Seek(voices, deltaSeconds);
    }

    /// <summary>Forgets everything, for a scene change.</summary>
    public void Reset() {
        Array.Clear(targets);
        cursor = 0;
    }

    /// <summary>Drops one voice's answer, for a slot that has been taken by something else.</summary>
    /// <remarks>
    ///     Called when a voice is reset or stolen. Without it a footstep taking a voice's slot would
    ///     inherit however occluded that voice was, and be muffled behind a wall it is not behind.
    /// </remarks>
    internal void Clear(int index) {
        if ((uint)index < (uint)targets.Length) {
            targets[index] = 0f;
        }
    }

    /// <summary>Spends the frame's budget, starting where the last frame stopped.</summary>
    void Query(Voice[] voices, in AudioListenerSet listeners) {
        var spent = 0;

        // Bounded by the pool and not by the budget, so a pool of mostly-silent voices still reaches
        // the audible ones instead of spending every frame skipping the same dead slots.
        for (var scanned = 0; scanned < voices.Length && spent < Budget; scanned++) {
            var index = cursor;
            cursor = (cursor + 1) % voices.Length;

            var voice = voices[index];

            if (!voice.IsSpatial || voice.Virtual || Volatile.Read(ref voice.State) != (int)VoiceState.Playing) {
                // Not asked about, and not left at whatever it was: a slot that has gone quiet must
                // not hand its occlusion to whatever plays next.
                targets[index] = 0f;
                continue;
            }

            targets[index] = Math.Clamp(Nearest(voice.PublishedSpatial.Position, listeners), 0f, 1f);
            spent++;
            Queries++;
        }
    }

    /// <summary>
    ///     The clearest path to any listener, because a sound two players can both hear is occluded
    ///     only as much as the one with the better view of it.
    /// </summary>
    float Nearest(in Vector3 position, in AudioListenerSet listeners) {
        var least = 1f;

        for (var i = 0; i < listeners.Count; i++) {
            least = MathF.Min(least, Provider!.Occlusion(position, listeners.Get(i).Position));

            if (least <= 0f) {
                break;
            }
        }

        return least;
    }

    /// <summary>Moves every voice towards its answer at a fixed rate.</summary>
    void Seek(Voice[] voices, float deltaSeconds) {
        // A rate across the whole range rather than a time to arrive, so a small correction is quick
        // and a full swing is not — the same shape a parameter's own seek has, for the same reason.
        var step = SeekSeconds > 0f ? deltaSeconds / SeekSeconds : float.MaxValue;

        for (var i = 0; i < voices.Length; i++) {
            var voice = voices[i];
            var target = targets[i];
            var current = voice.Occlusion;

            if (current == target) {
                continue;
            }

            voice.Occlusion = MathF.Abs(target - current) <= step
                ? target
                : current + (MathF.Sign(target - current) * step);
        }
    }
}
