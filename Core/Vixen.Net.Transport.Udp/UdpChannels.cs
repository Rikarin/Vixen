// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Numerics;

namespace Vixen.Net.Transport.Udp;

/// <summary>One datagram that has been sent and not yet acknowledged.</summary>
sealed class Unacked {
    public byte[] Datagram { get; set; } = [];
    public int Length { get; set; }
    public double SentAt { get; set; }
    public int Retries { get; set; }
}

/// <summary>
///     The sending half of one channel on one connection.
/// </summary>
/// <remarks>
///     Retransmission keeps the whole datagram rather than the payload, so a resend is a copy of the
///     bytes that went the first time. Rebuilding it would mean rebuilding a header, and a header
///     rebuilt from state that has moved on is how a retransmission ends up meaning something
///     different from what it is retransmitting.
/// </remarks>
sealed class ChannelSender {
    readonly Dictionary<ushort, Unacked> pending = [];
    readonly List<ushort> expired = [];

    ushort next;
    ushort nextFragmentId;

    /// <summary>How many datagrams are waiting to be acknowledged.</summary>
    public int PendingCount => pending.Count;

    /// <summary>Datagrams sent again because no acknowledgement came.</summary>
    public long RetransmitCount { get; private set; }

    /// <summary>Datagrams sent on this channel for the first time.</summary>
    /// <remarks>
    ///     ⚠ <b>The denominator <see cref="RetransmitCount" /> never had, and it is counted here
    ///     rather than at the socket on purpose.</b> Only a reliable channel remembers a datagram,
    ///     so only a reliable channel can send one again — and a share whose numerator is drawn from
    ///     reliable traffic and whose denominator counted acknowledgements, keep-alives and
    ///     unreliable snapshots would fall whenever a game sent more of those, with the link
    ///     unchanged. See <c>TransportLoss.Sent</c>.
    /// </remarks>
    public long SentCount { get; private set; }

    /// <summary>Takes the next sequence number.</summary>
    public ushort NextSequence() => next++;

    /// <summary>Takes the next fragment set id.</summary>
    public ushort NextFragmentId() => nextFragmentId++;

    /// <summary>Remembers a datagram until it is acknowledged.</summary>
    public void Remember(ushort sequence, ReadOnlySpan<byte> datagram, double now) {
        var buffer = ArrayPool<byte>.Shared.Rent(datagram.Length);
        datagram.CopyTo(buffer);

        pending[sequence] = new() { Datagram = buffer, Length = datagram.Length, SentAt = now };
        SentCount++;
    }

    /// <summary>
    ///     Takes an acknowledgement: one sequence and the thirty-two before it.
    /// </summary>
    /// <param name="latest">The newest sequence the peer has seen.</param>
    /// <param name="history">Which of the thirty-two before it, as bits.</param>
    /// <param name="now">The clock.</param>
    /// <param name="roundTrip">Where to put a round-trip sample, if this ack produced one.</param>
    /// <returns>Whether anything was acknowledged that had not been.</returns>
    public bool Acknowledge(ushort latest, uint history, double now, out double roundTrip) {
        roundTrip = -1;
        var acknowledged = false;

        for (var i = -1; i < 32; i++) {
            if (i >= 0 && (history & (1u << i)) == 0) {
                continue;
            }

            var sequence = (ushort)(latest - (i + 1));

            if (i < 0) {
                sequence = latest;
            }

            if (!pending.Remove(sequence, out var entry)) {
                continue;
            }

            acknowledged = true;

            // Karn's algorithm: a datagram that was sent twice cannot tell you which of the two the
            // acknowledgement is for, so it does not get to say how long the trip took.
            if (entry.Retries == 0 && roundTrip < 0) {
                roundTrip = now - entry.SentAt;
            }

            ArrayPool<byte>.Shared.Return(entry.Datagram);
        }

        return acknowledged;
    }

    /// <summary>Finds what has waited longer than the retransmission timeout.</summary>
    /// <param name="now">The clock.</param>
    /// <param name="timeout">How long to wait before sending again.</param>
    /// <param name="into">Where to put them.</param>
    public void CollectDue(double now, double timeout, List<Unacked> into) {
        foreach (var entry in pending.Values) {
            if (now - entry.SentAt >= timeout) {
                entry.SentAt = now;
                entry.Retries++;
                RetransmitCount++;
                into.Add(entry);
            }
        }
    }

    /// <summary>Gives up on anything older than the window, so a lost peer cannot leak memory.</summary>
    /// <param name="maximum">How many datagrams may be in flight before the oldest are dropped.</param>
    public void Trim(int maximum) {
        if (pending.Count <= maximum) {
            return;
        }

        expired.Clear();

        foreach (var (sequence, _) in pending) {
            if (UdpProtocol.Distance(next, sequence) > maximum) {
                expired.Add(sequence);
            }
        }

        foreach (var sequence in expired) {
            if (pending.Remove(sequence, out var entry)) {
                ArrayPool<byte>.Shared.Return(entry.Datagram);
            }
        }
    }

    /// <summary>Drops everything, for a connection that has ended.</summary>
    public void Clear() {
        foreach (var entry in pending.Values) {
            ArrayPool<byte>.Shared.Return(entry.Datagram);
        }

        pending.Clear();
    }
}

/// <summary>
///     The receiving half of one channel on one connection: dedupe, acknowledgement, reassembly, and
///     whatever ordering the channel promised.
/// </summary>
/// <remarks>
///     <para>
///         <b>A message's fragments occupy consecutive sequences.</b> That is the decision the whole
///         of this rests on: a fragment carries its index and its set's size, so the set that a
///         fragment at sequence <c>S</c> with index <c>i</c> belongs to is exactly
///         <c>S - i</c> through <c>S - i + count - 1</c>. There is no separate reassembly table to
///         keep, no separate timeout to tune, and an incomplete set falls out of the window on its
///         own when the window moves past it.
///     </para>
///     <para>
///         It also makes ordering fall out: an ordered channel walks sequences in order and delivers
///         a message when it reaches that message's <i>last</i> fragment, which is where the message
///         would have been in the stream if it had never been split.
///     </para>
/// </remarks>
sealed class ChannelReceiver {
    /// <summary>How many sequences are kept before the oldest are forgotten.</summary>
    public const int WindowSize = 512;

    readonly Channel channel;
    readonly Dictionary<ushort, Fragment> received = [];
    readonly List<ushort> expired = [];

    ushort latest;
    bool hasLatest;

    // How many of History's thirty-two slots stand for a sequence that really was in this channel's
    // stream. Below this, a zero bit is a datagram that has not come; at or above it, a zero bit is
    // a sequence from before the first one that ever arrived — which is not a loss, it is a channel
    // that had not started. Without it every connection would open by reporting thirty-two losses.
    int tracked;

    // Zero, and not the first sequence that turns up. A sender starts at zero, so a channel whose
    // first datagram was lost has to wait for it rather than adopt the second one as the beginning —
    // which is the difference between "in order" and "in the order they happened to arrive".
    ushort nextExpected;
    ushort lastDelivered;
    bool hasDelivered;

    /// <summary>The newest sequence seen, which is what an acknowledgement is about.</summary>
    public ushort Latest => latest;

    /// <summary>Which of the thirty-two before it were seen.</summary>
    public uint History { get; private set; }

    /// <summary>Whether anything has been received at all.</summary>
    public bool HasReceived => hasLatest;

    /// <summary>Whether something has arrived that the peer has not been told about.</summary>
    public bool AckPending { get; set; }

    /// <summary>Datagrams ignored because they had already been seen.</summary>
    public long DuplicateCount { get; private set; }

    /// <summary>Messages dropped because something newer had already been delivered.</summary>
    public long StaleCount { get; private set; }

    /// <summary>
    ///     Sequences that have passed out of the acknowledgement window, and so can no longer
    ///     arrive: the denominator of this channel's observed inbound loss.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The window is what makes this an observation rather than a guess.</b> A sender's
    ///     sequences are consecutive, so the gaps in what arrives are exactly what did not — but a
    ///     gap that is a second old may still be a datagram in flight, and counting it the moment it
    ///     appeared would report every reordering as a loss and then never take it back. A sequence
    ///     is judged when the thirty-second one after it arrives, at which point it is out of the
    ///     window, out of <see cref="History" />, and out of the reckoning either way.
    /// </remarks>
    public long ExpectedCount { get; private set; }

    /// <summary>How many of <see cref="ExpectedCount" /> never arrived.</summary>
    public long MissingCount { get; private set; }

    public ChannelReceiver(Channel channel) => this.channel = channel;

    /// <summary>Takes a fragment and delivers whatever that completes.</summary>
    /// <param name="sequence">Its sequence.</param>
    /// <param name="fragmentIndex">Which fragment of its message it is.</param>
    /// <param name="fragmentCount">How many the message was split into.</param>
    /// <param name="payload">The bytes.</param>
    /// <param name="delivered">Where completed messages go, as rented buffers with their lengths.</param>
    public void Receive(
        ushort sequence,
        byte fragmentIndex,
        byte fragmentCount,
        ReadOnlySpan<byte> payload,
        List<(byte[] Buffer, int Length)> delivered
    ) {
        if (fragmentCount == 0 || fragmentIndex >= fragmentCount) {
            return;
        }

        if (IsDuplicate(sequence)) {
            DuplicateCount++;
            AckPending = true;

            return;
        }

        Note(sequence);

        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, payload.Length));
        payload.CopyTo(buffer);
        received[sequence] = new(buffer, payload.Length, fragmentIndex, fragmentCount);
        AckPending = true;

        if (channel.IsOrdered() && channel.IsReliable()) {
            DeliverInOrder(delivered);

            return;
        }

        TryComplete(sequence, delivered, sequenced: channel == Channel.Sequenced);
    }

    /// <summary>Drops everything, for a connection that has ended.</summary>
    public void Clear() {
        foreach (var fragment in received.Values) {
            ArrayPool<byte>.Shared.Return(fragment.Buffer);
        }

        received.Clear();
    }

    bool IsDuplicate(ushort sequence) {
        if (!hasLatest) {
            return false;
        }

        var distance = UdpProtocol.Distance(latest, sequence);

        if (distance < 0) {
            return false;
        }

        if (distance == 0) {
            return true;
        }

        return distance <= 32 && (History & (1u << (distance - 1))) != 0;
    }

    void Note(ushort sequence) {
        if (!hasLatest) {
            hasLatest = true;
            latest = sequence;

            return;
        }

        var distance = UdpProtocol.Distance(sequence, latest);

        if (distance > 0) {
            // Before the shift, because the shift is what throws the evidence away.
            Retire(distance);

            // ⚠ A shift of thirty-two is a shift of zero on this machine, so the exactly-32 case is
            // written rather than shifted: everything History held leaves, and the sequence that was
            // newest — which arrived — takes the top slot. Folded in with the wider jumps it would
            // lose that bit, which is a duplicate this could no longer recognise and, since the
            // counters read the same bits, one arrival reported as a loss.
            History = distance > 32
                ? 0
                : (distance == 32 ? 0u : History << distance) | (1u << (distance - 1));

            latest = sequence;

            return;
        }

        var back = -distance;

        if (back <= 32) {
            History |= 1u << (back - 1);

            // A straggler older than anything seen so far proves its own gap was real: the sequences
            // between it and the newest are a stream this channel was in the middle of, not the
            // silence before it started.
            tracked = Math.Max(tracked, back);
        }
    }

    /// <summary>Judges the sequences that this advance pushes out of the window.</summary>
    /// <param name="distance">How far the newest sequence moves forward.</param>
    /// <remarks>
    ///     <para>
    ///         Sliding by <paramref name="distance" /> puts that many more sequences under judgement:
    ///         the one that was newest, and the gaps between it and the one that has just arrived.
    ///         Whatever no longer fits in the thirty-two is final — a bit set is a datagram that came,
    ///         a bit clear is one that never will.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A jump wider than the window is taken at face value, and that is the one thing
    ///         here that is arithmetic rather than observation.</b> Sequences that never got to be in
    ///         the window are counted missing because the sender's numbering says they existed. It is
    ///         the right answer for a burst that outran the window; it is the wrong answer for a
    ///         corrupt sequence field, which shows up once as a spike and then stops.
    ///     </para>
    /// </remarks>
    void Retire(int distance) {
        var held = tracked;
        var slots = held + distance;
        tracked = Math.Min(32, slots);

        var falling = slots - 32;

        if (falling <= 0) {
            return;
        }

        ExpectedCount += falling;

        if (falling <= held) {
            // The oldest of the slots that were real: History's top bits, counted in one instruction.
            var leaving = (History >> (held - falling)) & Mask(falling);
            MissingCount += falling - BitOperations.PopCount(leaving);

            return;
        }

        // Wider than the window: everything History held, then the sequence that was newest — which
        // arrived, so it is expected and not missing — and then the gaps below the new window's floor.
        MissingCount += held - BitOperations.PopCount(History & Mask(held)) + (falling - held - 1);
    }

    /// <summary>The low <paramref name="bits" /> of a word, for a count that may be the whole word.</summary>
    /// <remarks>A shift of thirty-two is a shift of zero on this machine, which would mask off everything.</remarks>
    static uint Mask(int bits) => bits >= 32 ? uint.MaxValue : (1u << bits) - 1;

    void DeliverInOrder(List<(byte[] Buffer, int Length)> delivered) {
        while (received.ContainsKey(nextExpected)) {
            TryComplete(nextExpected, delivered, sequenced: false);
            nextExpected++;
        }

        Prune();
    }

    void TryComplete(ushort sequence, List<(byte[] Buffer, int Length)> delivered, bool sequenced) {
        if (!received.TryGetValue(sequence, out var fragment)) {
            return;
        }

        var first = (ushort)(sequence - fragment.Index);
        var total = 0;

        for (var i = 0; i < fragment.Count; i++) {
            if (!received.TryGetValue((ushort)(first + i), out var part) || part.Count != fragment.Count) {
                return;
            }

            total += part.Length;
        }

        var last = (ushort)(first + fragment.Count - 1);

        if (sequenced && hasDelivered && !UdpProtocol.IsNewer(last, lastDelivered)) {
            // Sequenced: an old message after a newer one is dropped rather than applied. That is
            // the channel's whole promise, and the reason it is cheaper than a reliable one.
            StaleCount++;
            Forget(first, fragment.Count);

            return;
        }

        var message = ArrayPool<byte>.Shared.Rent(Math.Max(1, total));
        var at = 0;

        for (var i = 0; i < fragment.Count; i++) {
            var part = received[(ushort)(first + i)];
            part.Buffer.AsSpan(0, part.Length).CopyTo(message.AsSpan(at));
            at += part.Length;
        }

        Forget(first, fragment.Count);
        delivered.Add((message, total));

        if (!hasDelivered || UdpProtocol.IsNewer(last, lastDelivered)) {
            hasDelivered = true;
            lastDelivered = last;
        }

        if (!channel.IsOrdered()) {
            Prune();
        }
    }

    void Forget(ushort first, int count) {
        for (var i = 0; i < count; i++) {
            if (received.Remove((ushort)(first + i), out var part)) {
                ArrayPool<byte>.Shared.Return(part.Buffer);
            }
        }
    }

    void Prune() {
        if (received.Count <= WindowSize) {
            return;
        }

        expired.Clear();

        foreach (var (sequence, _) in received) {
            if (UdpProtocol.Distance(latest, sequence) > WindowSize) {
                expired.Add(sequence);
            }
        }

        foreach (var sequence in expired) {
            if (received.Remove(sequence, out var part)) {
                ArrayPool<byte>.Shared.Return(part.Buffer);
            }
        }
    }

    readonly record struct Fragment(byte[] Buffer, int Length, byte Index, byte Count);
}
