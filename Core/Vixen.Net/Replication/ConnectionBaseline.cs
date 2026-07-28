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
    readonly Dictionary<uint, List<Sent>> pendingByTick = [];
    readonly List<uint> pendingOrder = [];
    readonly List<BaselineKey> forgetting = [];

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

    /// <summary>Records that a value went out in a snapshot.</summary>
    /// <param name="tick">The tick of the snapshot.</param>
    /// <param name="key">Which entity's which component.</param>
    /// <param name="hash">The hash of what was sent.</param>
    /// <param name="capturedAt">The tick the value that was sent was captured at.</param>
    public void RecordSent(Tick tick, in BaselineKey key, uint hash, Tick capturedAt) {
        if (!pendingByTick.TryGetValue(tick.Value, out var sent)) {
            sent = [];
            pendingByTick[tick.Value] = sent;
            pendingOrder.Add(tick.Value);
            Trim();
        }

        sent.Add(new(key, hash, capturedAt));
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

        // Oldest first, because a value sent at two of the ticks being folded should end up in the
        // baseline as the newer of the two. Folding newest-first would leave the older one there,
        // and the baseline would then claim the connection holds a value it has already replaced —
        // which costs a redundant re-send if records are whole, and is a silent desync if the next
        // one is a difference measured from it.
        foreach (var pending in pendingOrder) {
            if (new Tick(pending).IsAfter(tick)) {
                continue;
            }

            foreach (var sent in pendingByTick[pending]) {
                acknowledged[sent.Key] = sent;
            }
        }

        // Newest first, so removing does not move the entries still to be looked at.
        for (var i = pendingOrder.Count - 1; i >= 0; i--) {
            if (new Tick(pendingOrder[i]).IsAfter(tick)) {
                continue;
            }

            pendingByTick.Remove(pendingOrder[i]);
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

        foreach (var sent in pendingByTick.Values) {
            sent.RemoveAll(entry => entry.Key.Entity == id);
        }
    }

    /// <summary>Forgets everything, for a connection that is starting again.</summary>
    public void Clear() {
        acknowledged.Clear();
        pendingByTick.Clear();
        pendingOrder.Clear();
        HasAcknowledged = false;
        AcknowledgedTick = default;
    }

    void Trim() {
        while (pendingOrder.Count > MaxPendingTicks) {
            // Dropped rather than folded in: a tick this old was not acknowledged because it did not
            // arrive, and forgetting that we sent it is what makes it be sent again.
            pendingByTick.Remove(pendingOrder[0]);
            pendingOrder.RemoveAt(0);
        }
    }

    readonly record struct Sent(BaselineKey Key, uint Hash, Tick CapturedAt);
}

/// <summary>Which entity's which component.</summary>
/// <param name="Entity">The networked entity.</param>
/// <param name="TypeId">The component type's wire id.</param>
public readonly record struct BaselineKey(NetworkId Entity, uint TypeId);
