// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Mixing;

/// <summary>
///     A value too big to write atomically, published from one thread and read by another without
///     either of them waiting.
/// </summary>
/// <typeparam name="T">The value. A struct, and small enough that copying it twice is cheap.</typeparam>
/// <remarks>
///     <para>
///         A sequence lock, and the reason <c>AudioEngine</c> has no command queue. Moving a
///         positioned sound means writing a <c>SpatialSettings</c> — sixty-odd bytes — every frame
///         for every emitter in the scene. Three ways to do that were considered:
///     </para>
///     <para>
///         <b>A queue.</b> Correct, and it allocates: a hundred emitters at sixty frames a second is
///         six thousand enqueues, and <c>ConcurrentQueue</c> grows a new segment every few hundred of
///         them. That is a few hundred kilobytes a second of garbage in the frame loop, which
///         <c>docs/plan/00</c> forbids in as many words.
///     </para>
///     <para>
///         <b>A lock.</b> Correct, and it can make the audio thread wait on the game thread — the one
///         thing an audio callback must never do. A missed callback is a click in every sound at
///         once.
///     </para>
///     <para>
///         <b>This.</b> The writer bumps a counter to odd, writes, and bumps it to even. A reader
///         that sees an odd counter, or a different counter after copying than before, knows it read
///         a value that was being written and tries again — and after a few attempts gives up and
///         keeps the value it already had, which for one block of audio is inaudible. Nothing blocks
///         in either direction and nothing allocates.
///     </para>
///     <para>
///         <b>One writer only.</b> Two threads publishing to the same slot would both bump the
///         counter and a reader could see an even count over a mixed value. Every use here is one
///         game thread writing one voice.
///     </para>
/// </remarks>
struct Published<T> where T : struct {
    int sequence;
    T value;

    /// <summary>Publishes a value.</summary>
    /// <param name="update">The value.</param>
    public void Write(in T update) {
        // Both increments are full fences, so the value write cannot be seen before the counter goes
        // odd or after it goes even.
        Interlocked.Increment(ref sequence);
        value = update;
        Interlocked.Increment(ref sequence);
    }

    /// <summary>Reads the value, if it can be read cleanly.</summary>
    /// <param name="result">The value, untouched if this returns false.</param>
    /// <returns>Whether a consistent value was read.</returns>
    public bool TryRead(ref T result) {
        for (var attempt = 0; attempt < 4; attempt++) {
            var before = Volatile.Read(ref sequence);

            if ((before & 1) != 0) {
                continue;
            }

            var candidate = value;

            // The copy above must not float past this read, and an acquire on its own does not stop
            // that — it only stops later reads moving earlier.
            Interlocked.MemoryBarrier();

            if (Volatile.Read(ref sequence) == before) {
                result = candidate;
                return true;
            }
        }

        return false;
    }
}
