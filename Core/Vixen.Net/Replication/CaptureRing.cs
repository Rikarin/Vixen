// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Replication;

/// <summary>One encoding of one value, and the tick it was captured at.</summary>
internal sealed class Capture {
    public byte[] Bits { get; set; } = [];
    public int BitCount { get; set; }
    public uint Hash { get; set; }
    public Tick At { get; set; }
}

/// <summary>The last few encodings of one value, oldest first.</summary>
/// <remarks>
///     <para>
///         <b>Why a difference needs more than the value before it.</b> A connection's baseline is the
///         newest capture it has acknowledged, and an acknowledgement takes a round trip to come
///         back — so on a connection with any latency at all the baseline is several captures old,
///         not one. A server that could only difference against the immediately previous capture
///         would therefore send whole records to everybody except a peer on the same machine, which
///         is the shape of a feature that works in the test and not in the game.
///     </para>
///     <para>
///         So both ends keep a short history keyed by the tick the value was captured at, the record
///         says which entry the difference was measured from, and the receiver applies it to that one
///         rather than to whatever it happens to be holding. That last part is what makes it correct
///         under loss: a client that has applied a newer value than it has managed to acknowledge
///         still has the older one here to apply the difference to.
///     </para>
///     <para>
///         <see cref="Depth" /> is how far behind a connection may fall and still be sent
///         differences. Beyond it the value goes whole, which is what a connection that far behind
///         wants anyway.
///     </para>
/// </remarks>
internal sealed class CaptureRing {
    /// <summary>How many encodings are kept.</summary>
    /// <remarks>
    ///     Sixteen, to match what <c>ReplicationServer.MaxBaselineAge</c> can name — the two are one
    ///     decision and there is no point in either being the smaller. For a value changing every
    ///     tick that is half a second of history at the default rate, which covers the round trip of
    ///     any connection a game is playable over. Beyond it, values go whole.
    /// </remarks>
    public const int Depth = 16;

    readonly Capture[] entries = new Capture[Depth];

    /// <summary>
    ///     The difference most recently encoded for this value, kept so the connections that share a
    ///     baseline share the encoding.
    /// </summary>
    /// <remarks>
    ///     <b>One slot, not a table.</b> Connections cluster: they are all about the same distance
    ///     behind, so within a tick they almost all ask for a difference from the same capture, and
    ///     the second one gets the answer the first paid for. A table keyed by baseline would serve
    ///     the rare case where they do not — and would allocate an entry per value per tick to do it,
    ///     which the soak measured at four megabytes a tick.
    /// </remarks>
    public Capture Memo { get; } = new();

    /// <summary>How long the memoised difference is, or zero if there is not one.</summary>
    public int MemoBits { get; set; }

    /// <summary>The capture the memoised difference was measured from.</summary>
    public Tick MemoFrom { get; set; }

    /// <summary>The value it produces, so a stale memo is not mistaken for a fresh one.</summary>
    public uint MemoFor { get; set; }

    int count;
    int oldest;

    /// <summary>How many are held.</summary>
    public int Count => count;

    /// <summary>The one most recently added.</summary>
    public Capture Newest => entries[(oldest + count - 1) % Depth];

    /// <summary>Whether anything is held at all.</summary>
    public bool HasAny => count > 0;

    /// <summary>Takes the slot the next capture should be written into.</summary>
    /// <param name="bytes">How many bytes it needs.</param>
    /// <returns>A slot, reusing the oldest one's buffer when the ring is full.</returns>
    /// <remarks>
    ///     The buffer is handed back rather than allocated, so a value captured every tick for an hour
    ///     allocates <see cref="Depth" /> arrays and then stops.
    /// </remarks>
    public Capture Advance(int bytes) {
        Capture slot;

        if (count == Depth) {
            slot = entries[oldest];
            oldest = (oldest + 1) % Depth;
            count--;
        } else {
            slot = entries[(oldest + count) % Depth] ??= new();
        }

        entries[(oldest + count) % Depth] = slot;
        count++;

        if (slot.Bits.Length < bytes) {
            slot.Bits = new byte[bytes];
        }

        return slot;
    }

    /// <summary>Finds the capture taken at a tick.</summary>
    /// <param name="at">The tick.</param>
    /// <param name="capture">It, if it is still held.</param>
    /// <returns>Whether it is.</returns>
    public bool TryFind(Tick at, out Capture? capture) {
        for (var i = 0; i < count; i++) {
            var entry = entries[(oldest + i) % Depth];

            if (entry.At == at) {
                capture = entry;

                return true;
            }
        }

        capture = null;

        return false;
    }

    /// <summary>Forgets everything.</summary>
    public void Clear() {
        count = 0;
        oldest = 0;
    }
}
