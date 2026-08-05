// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Gameplay;
using Vixen.Gameplay.Instances;

namespace Vixen.Live.Gameplay;

/// <summary>A lockout that has been recorded here and not yet written down.</summary>
/// <param name="Player">Whose, durably.</param>
/// <param name="Instance">Which instance, by address hash.</param>
/// <param name="Difficulty">Which difficulty.</param>
/// <param name="Expires">When it lifts, on the realm's clock.</param>
/// <param name="Completions">How many times they have finished it.</param>
public readonly record struct PendingLockout(
    PlayerKey Player,
    DefId Instance,
    string Difficulty,
    double Expires,
    int Completions
);

/// <summary>
///     Doc 28's lockouts against doc 27's <c>IInstanceGrain</c>: answered in the frame, written down
///     afterwards, and never awaited.
/// </summary>
/// <remarks>
///     <para>
///         <b><see cref="LedgerBridge" />'s shape for the same reason.</b>
///         <see cref="ILockoutStore" /> is synchronous because a zone-in asks it mid-frame; the grain
///         that owns the answer is a round trip. ADR-016 forbids awaiting one, so the bridge answers
///         from a view and posts the write.
///     </para>
///     <para>
///         ⚠ <b>But a lockout is not a balance, and the difference is dangerous.</b> An unknown
///         balance reads as zero and a player is refused a purchase — annoying, and safe. An unknown
///         <em>lockout</em> reads as <c>null</c>, which <see cref="ILockoutStore.Find" /> defines as
///         <em>"not locked"</em> — so a player whose lockouts have not been loaded is admitted to a
///         raid they are already saved to, and the run they get is one the fleet cannot take back.
///         Doc 28's whole reason for making this fleet-wide is that *"a lockout one shard knew about
///         is a lockout a player evades by zoning"*, and a cold cache is that hole reopened from
///         inside.
///     </para>
///     <para>
///         ⚠ <b>The interface cannot express "I do not know", so the mistake is counted instead.</b>
///         <see cref="IsWarm" /> is what admission checks before letting anybody in, and a
///         <see cref="Find" /> for somebody who was never warmed increments
///         <see cref="ColdReads" /> and raises <see cref="Cold" />. That is the same posture
///         <see cref="LedgerBridge.Divergences" /> takes: a wrong answer that cannot be prevented by
///         the type is made loud rather than quiet.
///     </para>
/// </remarks>
public sealed class LockoutBridge : ILockoutStore {
    readonly Dictionary<(PlayerId Player, uint Instance, string Difficulty), Lockout> view = [];
    readonly HashSet<PlayerId> warm = [];
    readonly List<PendingLockout> outbox = [];
    readonly IGameplayIdentity identity;

    /// <summary>Makes one.</summary>
    /// <param name="identity">Who a gameplay id is, durably.</param>
    public LockoutBridge(IGameplayIdentity identity) {
        ArgumentNullException.ThrowIfNull(identity);

        this.identity = identity;
    }

    /// <summary>How many lockouts the view holds.</summary>
    public int Count => view.Count;

    /// <summary>How many players have been loaded.</summary>
    public int Warm => warm.Count;

    /// <summary>How many writes are waiting.</summary>
    public int Pending => outbox.Count;

    /// <summary>How many times somebody was asked about who had never been loaded. Never anything but zero.</summary>
    public int ColdReads { get; private set; }

    /// <summary>Raised when a lockout is read for somebody who was never loaded.</summary>
    /// <remarks>
    ///     The one event a realm must not ignore: the answer it just gave was <em>"not locked"</em>
    ///     and the honest answer was <em>"ask the cluster"</em>.
    /// </remarks>
    public event Action<PlayerId>? Cold { add => cold += value; remove => cold -= value; }

    Action<PlayerId>? cold;

    /// <summary>Whether somebody's lockouts have been loaded.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Whether they have.</returns>
    /// <remarks>What admission checks before letting anybody in. See the remarks on the type.</remarks>
    public bool IsWarm(PlayerId player) => warm.Contains(player);

    /// <summary>Puts somebody's lockouts in, as the cluster gave them.</summary>
    /// <param name="player">Who.</param>
    /// <param name="lockouts">What they are saved to. Empty is a real answer and marks them warm.</param>
    /// <remarks>
    ///     ⚠ <b>Empty marks them warm, which is the point.</b> "This player is saved to nothing" and
    ///     "nobody has asked" are the same absence in the view and must not be the same fact.
    /// </remarks>
    public void Warmed(PlayerId player, IEnumerable<Lockout> lockouts) {
        ArgumentNullException.ThrowIfNull(lockouts);

        warm.Add(player);

        foreach (var lockout in lockouts) {
            view[Key(lockout.Subject, lockout.Instance, lockout.Difficulty)] = lockout;
        }
    }

    /// <summary>Forgets somebody who left.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Whether they were here.</returns>
    /// <remarks>
    ///     ⚠ <b>Their pending writes are kept.</b> A lockout recorded a moment before a disconnect is
    ///     the one most worth not losing — it is the run they just did.
    /// </remarks>
    public bool Forget(PlayerId player) {
        foreach (var key in view.Keys.Where(key => key.Player == player).ToArray()) {
            view.Remove(key);
        }

        return warm.Remove(player);
    }

    /// <inheritdoc />
    public Lockout? Find(PlayerId subject, DefId instance, string difficulty) {
        if (!warm.Contains(subject)) {
            ColdReads++;
            cold?.Invoke(subject);
        }

        return view.TryGetValue(Key(subject, instance, difficulty), out var lockout) ? lockout : null;
    }

    /// <inheritdoc />
    public void Save(Lockout lockout) {
        view[Key(lockout.Subject, lockout.Instance, lockout.Difficulty)] = lockout;

        // Recording a lockout for somebody the realm has never heard of would be a durable write
        // against nobody, so it stays local and the cold counter is what says so.
        if (!identity.TryResolve(lockout.Subject, out var key)) {
            ColdReads++;
            cold?.Invoke(lockout.Subject);

            return;
        }

        var pending = new PendingLockout(
            key,
            lockout.Instance,
            lockout.Difficulty ?? string.Empty,
            lockout.Expires,
            lockout.Completions
        );

        // One write per lockout, replaced rather than appended: extending a lockout twice before a
        // drain is one durable state, not two, and the last one is the truth.
        var index = outbox.FindIndex(waiting =>
            waiting.Player == pending.Player
            && waiting.Instance == pending.Instance
            && string.Equals(waiting.Difficulty, pending.Difficulty, StringComparison.Ordinal)
        );

        if (index >= 0) {
            outbox[index] = pending;
        } else {
            outbox.Add(pending);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Purging the view is not releasing a lockout.</b> This drops what has lifted from a
    ///     realm's memory; what decides that it has lifted is the reset the cluster holds, and
    ///     nothing here writes a release. A realm that could would be a realm that ends a raid
    ///     lockout by restarting.
    /// </remarks>
    public int Purge(double now) {
        var lifted = view.Where(entry => entry.Value.Expires <= now).Select(entry => entry.Key).ToArray();

        foreach (var key in lifted) {
            view.Remove(key);
        }

        return lifted.Length;
    }

    /// <summary>Takes everything waiting to be written down.</summary>
    /// <returns>The writes, oldest first. Not removed — see <see cref="Settle" />.</returns>
    public ImmutableArray<PendingLockout> Drain() => [.. outbox];

    /// <summary>Says a write landed.</summary>
    /// <param name="write">Which.</param>
    /// <returns>Whether it was waiting.</returns>
    public bool Settle(PendingLockout write) => outbox.Remove(write);

    static (PlayerId, uint, string) Key(PlayerId player, DefId instance, string? difficulty) =>
        (player, instance.Value, difficulty ?? string.Empty);
}
