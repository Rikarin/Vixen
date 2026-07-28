// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Events;

/// <summary>Layers waiting for their moment.</summary>
/// <remarks>
///     <para>
///         A gunshot's tail does not start when its report does — it starts twenty milliseconds
///         later, and the twenty milliseconds is most of what makes the two read as one event rather
///         than as two sounds. Something has to hold the second one until then, and it cannot be the
///         event: an event is played and forgotten, and nothing calls it again.
///     </para>
///     <para>
///         <b>Fixed capacity, and a full table drops rather than grows.</b> The alternative is a list
///         that allocates in the frame loop, which <c>docs/plan/00</c> forbids. A hundred and
///         twenty-eight pending layers is far past anything real — a layer waits milliseconds, not
///         seconds — and the count of what was dropped is reported rather than being silent.
///     </para>
/// </remarks>
sealed class DeferredSpawns {
    struct Pending {
        public AudioEvent? Sound;
        public AudioEventPlayback Attributes;
        public float Remaining;
    }

    readonly Pending[] pending;
    int count;
    long dropped;

    public DeferredSpawns(int capacity) => pending = new Pending[capacity];

    /// <summary>How many layers were never played because the table was full.</summary>
    public long Dropped => dropped;

    /// <summary>How many are waiting.</summary>
    public int Count => count;

    /// <summary>Holds a play until its delay has run out.</summary>
    /// <returns>Whether there was room.</returns>
    public bool Schedule(AudioEvent sound, in AudioEventPlayback attributes, float seconds) {
        if (count >= pending.Length) {
            dropped++;
            return false;
        }

        pending[count++] = new Pending { Sound = sound, Attributes = attributes, Remaining = seconds };
        return true;
    }

    /// <summary>Drops everything waiting on one event.</summary>
    /// <remarks>
    ///     What <c>AudioEvent.StopAll</c> calls. A gunshot stopped before its tail has fired should
    ///     not produce the tail a moment later, which is what "stop this sound" means to anybody who
    ///     asked for it.
    /// </remarks>
    public void Cancel(AudioEvent sound) {
        var kept = 0;

        for (var i = 0; i < count; i++) {
            if (!ReferenceEquals(pending[i].Sound, sound)) {
                pending[kept++] = pending[i];
            }
        }

        count = kept;
    }

    /// <summary>Ticks every wait and plays the ones that have run out.</summary>
    /// <param name="deltaSeconds">How much game time has passed.</param>
    /// <remarks>
    ///     <b>Firing a layer may schedule more of them</b> — a layer is an event and an event may have
    ///     layers of its own. New entries are appended with their full delay, and a delay of zero is
    ///     played outright rather than scheduled, so nothing appended here can be ready in this same
    ///     pass. That is what makes walking the table while it is being appended to safe.
    /// </remarks>
    public void Step(float deltaSeconds) {
        for (var i = 0; i < count; i++) {
            pending[i].Remaining -= deltaSeconds;
        }

        var index = 0;

        while (index < count) {
            if (pending[index].Remaining > 0f) {
                index++;
                continue;
            }

            var sound = pending[index].Sound;
            var attributes = pending[index].Attributes;
            Remove(index);
            sound?.Play(attributes);
        }
    }

    /// <summary>Forgets everything, for a scene change.</summary>
    public void Clear() => count = 0;

    void Remove(int index) {
        // Shifted rather than swapped with the last, so layers scheduled together still fire in the
        // order they were written. Which matters: two layers at the same offset are two parts of one
        // sound, and a designer who put the low one first meant it.
        for (var i = index; i < count - 1; i++) {
            pending[i] = pending[i + 1];
        }

        count--;
        pending[count] = default;
    }
}
