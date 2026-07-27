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

    readonly Dictionary<BaselineKey, uint> acknowledged = [];
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
    public bool IsCurrent(in BaselineKey key, uint hash) =>
        acknowledged.TryGetValue(key, out var known) && known == hash;

    /// <summary>Records that a value went out in a snapshot.</summary>
    /// <param name="tick">The tick of the snapshot.</param>
    /// <param name="key">Which entity's which component.</param>
    /// <param name="hash">The hash of what was sent.</param>
    public void RecordSent(Tick tick, in BaselineKey key, uint hash) {
        if (!pendingByTick.TryGetValue(tick.Value, out var sent)) {
            sent = [];
            pendingByTick[tick.Value] = sent;
            pendingOrder.Add(tick.Value);
            Trim();
        }

        sent.Add(new(key, hash));
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

        for (var i = pendingOrder.Count - 1; i >= 0; i--) {
            var pending = new Tick(pendingOrder[i]);

            if (pending.IsAfter(tick)) {
                continue;
            }

            foreach (var sent in pendingByTick[pending.Value]) {
                acknowledged[sent.Key] = sent.Hash;
            }

            pendingByTick.Remove(pending.Value);
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

    readonly record struct Sent(BaselineKey Key, uint Hash);
}

/// <summary>Which entity's which component.</summary>
/// <param name="Entity">The networked entity.</param>
/// <param name="TypeId">The component type's wire id.</param>
public readonly record struct BaselineKey(NetworkId Entity, uint TypeId);
