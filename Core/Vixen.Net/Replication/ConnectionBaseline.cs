// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Replication;

/// <summary>What one connection is known to have, and what it has only been sent.</summary>
/// <remarks>
///     <para>
///         The difference between those two is the whole of delta replication over an unreliable
///         channel. A value that was <i>sent</i> may be in a packet that never arrived; only a value
///         that was <b>acknowledged</b> may be assumed to be there. So a send records what it sent
///         against the tick it sent it at, and nothing enters the baseline until an ack for that tick
///         comes back.
///     </para>
///     <para>
///         The consequence is the behaviour the design asks for and is easy to get wrong: <b>on loss,
///         the next snapshot is computed against the older baseline</b> rather than resending the
///         lost packet. The client gets the current value, not the one it missed — which is both
///         cheaper and more correct, because the value it missed is stale by now.
///     </para>
///     <para>
///         Unacked ticks are bounded. Past <see cref="MaxPendingTicks" /> the oldest is dropped
///         without entering the baseline, which is exactly right: a tick that old was lost, and
///         forgetting that we sent it is what makes it be sent again.
///     </para>
/// </remarks>
public sealed class ConnectionBaseline {
    /// <summary>How many unacknowledged ticks are kept before the oldest is given up on.</summary>
    public const int MaxPendingTicks = 64;

    readonly Dictionary<BaselineKey, Sent> acknowledged = [];

    // The most recent send of each value, which is what says whether re-sending it now would be
    // repeating something already on its way.
    readonly Dictionary<BaselineKey, Sent> inFlight = [];
    readonly Dictionary<uint, List<Sent>> pendingByTick = [];
    readonly List<uint> pendingOrder = [];
    readonly List<BaselineKey> forgetting = [];

    // The per-tick lists are handed back rather than dropped. A connection opens one a tick and
    // closes it a round trip later, so without this a hundred connections at thirty hertz make
    // three thousand lists a second for the collector — which the soak measured before this existed.
    readonly Stack<List<Sent>> spare = [];

    /// <summary>The newest tick this connection has acknowledged.</summary>
    public Tick AcknowledgedTick { get; private set; }

    /// <summary>Whether anything has been acknowledged at all.</summary>
    public bool HasAcknowledged { get; private set; }

    /// <summary>How many component values this connection is known to hold.</summary>
    public int BaselineCount => acknowledged.Count;

    /// <summary>How many ticks have been sent and not yet acknowledged.</summary>
    public int PendingTickCount => pendingOrder.Count;

    /// <summary>Whether a value is one this connection is known to already have.</summary>
    /// <param name="key">Which entity's which component.</param>
    /// <param name="hash">The hash of the value now.</param>
    /// <returns>Whether sending it would tell them something they already know.</returns>
    /// <remarks>
    ///     <see cref="Acknowledge" /> folds oldest-first so that this answers about the newest value
    ///     acknowledged rather than an older one that happened to be in the same batch. Getting that
    ///     backwards costs a redundant re-send when records go whole, and is a silent desync when the
    ///     next one is a difference measured from it.
    /// </remarks>
    public bool IsCurrent(in BaselineKey key, uint hash) =>
        acknowledged.TryGetValue(key, out var known) && known.Hash == hash;

    /// <summary>
    ///     Which capture of a value this connection has acknowledged, so a difference can be measured
    ///     from it.
    /// </summary>
    /// <param name="key">Which entity's which component.</param>
    /// <param name="capturedAt">The tick that capture was taken at.</param>
    /// <param name="hash">Its hash.</param>
    /// <returns>Whether they have acknowledged any capture of it.</returns>
    public bool TryGetBaseline(in BaselineKey key, out Tick capturedAt, out uint hash) {
        if (acknowledged.TryGetValue(key, out var known)) {
            capturedAt = known.CapturedAt;
            hash = known.Hash;

            return true;
        }

        capturedAt = default;
        hash = 0;

        return false;
    }

    /// <summary>
    ///     Whether this exact value has gone out recently enough that it could still be in flight.
    /// </summary>
    /// <param name="key">Which entity's which component.</param>
    /// <param name="hash">The hash of the value now.</param>
    /// <param name="now">The tick being written.</param>
    /// <param name="within">How many ticks a send is assumed to still be travelling for.</param>
    /// <returns>Whether sending it again would be repeating something already on its way.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Without this a record is re-sent every tick until it is acknowledged</b>, so a
    ///         four-tick round trip sends every change four times — measured as most of a connection's
    ///         bandwidth. An acknowledgement cannot arrive before the round trip is over, so the ticks
    ///         in between are spent repeating something nobody could have answered yet.
    ///     </para>
    ///     <para>
    ///         <b>Only the same value is suppressed.</b> The hash is part of the question, so a value
    ///         that changed again goes out at once; the delay applies to repeating oneself, never to
    ///         saying something new. What it costs is that a genuinely lost record waits this long
    ///         rather than going again next tick, which is the trade every retransmission timer makes.
    ///     </para>
    /// </remarks>
    public bool WasSentRecently(in BaselineKey key, uint hash, Tick now, int within) =>
        within > 0
        && inFlight.TryGetValue(key, out var sent)
        && sent.Hash == hash
        && now.Subtract(sent.SentAt) < within;

    /// <summary>Records that a value went out in a snapshot.</summary>
    /// <param name="tick">The tick of the snapshot.</param>
    /// <param name="key">Which entity's which component.</param>
    /// <param name="hash">The hash of what was sent.</param>
    /// <param name="capturedAt">The tick the value that was sent was captured at.</param>
    public void RecordSent(Tick tick, in BaselineKey key, uint hash, Tick capturedAt) {
        if (!pendingByTick.TryGetValue(tick.Value, out var sent)) {
            sent = spare.Count > 0 ? spare.Pop() : [];
            sent.Clear();
            pendingByTick[tick.Value] = sent;
            pendingOrder.Add(tick.Value);
            Trim();
        }

        var record = new Sent(key, hash, capturedAt, tick);
        sent.Add(record);
        inFlight[key] = record;
    }

    /// <summary>Takes an acknowledgement, folding everything up to it into the baseline.</summary>
    /// <param name="tick">The newest tick the client says it applied.</param>
    /// <returns>Whether this ack told us anything new.</returns>
    public bool Acknowledge(Tick tick) {
        if (HasAcknowledged && !tick.IsAfter(AcknowledgedTick)) {
            // Acks arrive on an unreliable channel and may be reordered. An older one says nothing.
            return false;
        }

        AcknowledgedTick = tick;
        HasAcknowledged = true;

        // Only this tick's records, never everything up to it — and that is the pairing with
        // WasSentRecently that took a broken commit to find. Folding cumulatively was sound only
        // because every snapshot carried everything unacknowledged, so a later one always repeated
        // an earlier one and acking the later proved the earlier. Backoff stops the repeating, and
        // the moment it does, folding an unacked tick claims the connection holds a value that was
        // in a packet it never received — which is never sent again, because the baseline says they
        // have it. A value stuck for ever rather than for a while, which is exactly how it looked.
        if (pendingByTick.TryGetValue(tick.Value, out var arrived)) {
            foreach (var sent in arrived) {
                acknowledged[sent.Key] = sent;
            }

            Recycle(tick.Value);
            pendingOrder.Remove(tick.Value);
        }

        // Everything older is given up on rather than believed. It was sent, it was not acknowledged,
        // and the connection has since acknowledged something newer — so whatever was in it either
        // arrived and was superseded, or did not arrive at all. Either way the value's own hash
        // decides whether it goes again, which is the check that was always there.
        for (var i = pendingOrder.Count - 1; i >= 0; i--) {
            if (new Tick(pendingOrder[i]).IsAfter(tick)) {
                continue;
            }

            Recycle(pendingOrder[i]);
            pendingOrder.RemoveAt(i);
        }

        return true;
    }

    /// <summary>Forgets an entity, because it was destroyed or left this connection's interest.</summary>
    /// <param name="id">The entity.</param>
    /// <remarks>
    ///     Everything it held has to go, or an entity that comes back into interest would be
    ///     considered already known and never sent — which looks like an object that is there for
    ///     everyone except one player.
    /// </remarks>
    public void Forget(NetworkId id) {
        forgetting.Clear();

        foreach (var key in acknowledged.Keys) {
            if (key.Entity == id) {
                forgetting.Add(key);
            }
        }

        foreach (var key in forgetting) {
            acknowledged.Remove(key);
        }

        foreach (var key in inFlight.Keys.Where(entry => entry.Entity == id).ToList()) {
            inFlight.Remove(key);
        }

        foreach (var sent in pendingByTick.Values) {
            sent.RemoveAll(entry => entry.Key.Entity == id);
        }
    }

    /// <summary>Forgets everything, for a connection that is starting again.</summary>
    public void Clear() {
        acknowledged.Clear();
        inFlight.Clear();

        foreach (var sent in pendingByTick.Values) {
            spare.Push(sent);
        }

        pendingByTick.Clear();
        pendingOrder.Clear();
        HasAcknowledged = false;
        AcknowledgedTick = default;
    }

    void Recycle(uint tick) {
        if (pendingByTick.Remove(tick, out var sent)) {
            spare.Push(sent);
        }
    }

    void Trim() {
        while (pendingOrder.Count > MaxPendingTicks) {
            // Dropped rather than folded in: a tick this old was not acknowledged because it did not
            // arrive, and forgetting that we sent it is what makes it be sent again.
            Recycle(pendingOrder[0]);
            pendingOrder.RemoveAt(0);
        }
    }

    readonly record struct Sent(BaselineKey Key, uint Hash, Tick CapturedAt, Tick SentAt);
}

/// <summary>Which entity's which component.</summary>
/// <param name="Entity">The networked entity.</param>
/// <param name="TypeId">The component type's wire id.</param>
public readonly record struct BaselineKey(NetworkId Entity, uint TypeId);
