// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Live.Cluster;

namespace Vixen.Live.Orchestration;

/// <summary>How long a lease survives without a renewal, and how the cluster tells the time.</summary>
/// <remarks>
///     ⚠ <b>The lease has to outlive a realm's worst pause, and no longer.</b> Too short and a
///     garbage collection costs a realm its right to write; too long and a crashed realm's character
///     is unplayable until it lapses. Twenty seconds is ten heartbeats.
/// </remarks>
/// <param name="Lifetime">How long a lease lasts from its last renewal.</param>
/// <param name="Now">The clock. A parameter so a test does not have to wait.</param>
public sealed record LeaseOptions(TimeSpan Lifetime, Func<DateTimeOffset> Now) {
    /// <summary>The defaults.</summary>
    public static LeaseOptions Default { get; } = new(TimeSpan.FromSeconds(20), () => DateTimeOffset.UtcNow);
}

/// <summary>One character's lease. ADR-021, and the reason duplication is not expressible.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>There is no lock in this file and there must never be one.</b> What makes this
///         correct is that <see cref="PlayerGrain" /> takes one turn at a time; the moment somebody
///         adds a lock, the reason becomes "we remembered to" rather than "the runtime guarantees
///         it", and the next method somebody adds will not remember.
///     </para>
///     <para>
///         The epoch is monotonic and never reused. A realm that has been superseded discovers it on
///         its next renewal, and every durable write names the epoch it was made under — so a write
///         arriving late from a realm that has already lost is a no-op rather than a conflict to
///         resolve.
///     </para>
///     <para>
///         ⚠ <b>Durable <em>state</em> is not here.</b> Doc 27 § Persistence puts inventory and
///         currency behind <c>IPlayerRepository</c> rather than in grain storage, precisely so it can
///         be queried by the support tool, the economy dashboard and the analytics job. This is
///         coordination — the lease and where the player is — which is the half a transfer needs
///         before it can be written. The repository is L3.
///     </para>
/// </remarks>
public sealed class PlayerLeaseState {
    readonly LeaseOptions options;

    long epoch;
    ShardId holder;
    DateTimeOffset expires;

    /// <summary>Stands one up.</summary>
    /// <param name="options">The lifetime and clock, or null for the defaults.</param>
    public PlayerLeaseState(LeaseOptions? options = null) => this.options = options ?? LeaseOptions.Default;

    /// <summary>Takes the lease, superseding whoever held it.</summary>
    /// <param name="shard">Which shard is asking.</param>
    /// <returns>The lease, always granted.</returns>
    /// <remarks>
    ///     ⚠ <b>Always granted, and this is the decision the transfer protocol rests on.</b> A
    ///     transfer must be able to take the lease from a realm that has crashed, and nothing in the
    ///     cluster can tell a crashed realm from a slow one. So the epoch moves and the previous
    ///     holder finds out — rather than the acquisition failing and the character being stuck until
    ///     a timeout nobody can see has elapsed.
    /// </remarks>
    public PlayerLease Acquire(ShardId shard) {
        epoch++;
        holder = shard;
        expires = options.Now() + options.Lifetime;

        return new(true, epoch, holder, expires);
    }

    /// <summary>Says the holder is still alive.</summary>
    /// <param name="shard">Which shard claims to hold it.</param>
    /// <param name="presented">Which epoch it thinks it has.</param>
    /// <returns>The lease. Not granted when it has been superseded.</returns>
    public PlayerLease Renew(ShardId shard, long presented) {
        var now = options.Now();

        if (presented != epoch || holder != shard) {
            // Superseded. The realm keeps simulating — doc 27 is explicit that a lease loss
            // mid-combat must be survivable — and buffers its durable mutations until the lease
            // returns or the transfer hands them to the new holder.
            return new(false, epoch, holder, expires);
        }

        if (expires <= now) {
            // It lapsed while they were away, so the epoch moves and anything written under the old
            // one is refused. A renewal that resurrected a lapsed lease would let two realms believe
            // they hold the same character across a partition, which is the one thing this type
            // exists to make impossible.
            epoch++;
        }

        expires = now + options.Lifetime;

        return new(true, epoch, holder, expires);
    }

    /// <summary>Gives it back. A stale epoch is ignored rather than refused.</summary>
    /// <param name="shard">Which shard held it.</param>
    /// <param name="presented">Which epoch.</param>
    public void Release(ShardId shard, long presented) {
        if (presented != epoch || holder != shard) {
            return;
        }

        holder = ShardId.None;
        expires = options.Now();
    }

    /// <summary>Who holds it, without taking it.</summary>
    /// <returns>The lease as it stands.</returns>
    public PlayerLease Current() => new(IsHeld, epoch, holder, expires);

    /// <summary>Which shard this character is on, as far as the cluster knows.</summary>
    public ShardId Holder => IsHeld ? holder : ShardId.None;

    /// <summary>Whether anybody currently holds it.</summary>
    public bool IsHeld => holder.IsValid && expires > options.Now();
}

/// <summary>The grain around <see cref="PlayerLeaseState" />. A scheduling decision, and nothing more.</summary>
/// <remarks>
///     ⚠ <b>Every grain in this assembly is this thin, on purpose.</b> The logic is a plain class
///     that a test constructs and drives; the grain supplies the one property the logic depends on
///     and cannot provide for itself — that it is never re-entered. Writing the state machine inside
///     the grain would make it untestable without a silo, which is how a coordination layer ends up
///     with no tests at all.
/// </remarks>
public sealed class PlayerGrain(LeaseOptions? options = null) : Grain, IPlayerGrain {
    readonly PlayerLeaseState lease = new(options);

    /// <inheritdoc />
    public Task<PlayerLease> AcquireLease(ShardId shard) => Task.FromResult(lease.Acquire(shard));

    /// <inheritdoc />
    public Task<PlayerLease> RenewLease(ShardId shard, long epoch) => Task.FromResult(lease.Renew(shard, epoch));

    /// <inheritdoc />
    public Task ReleaseLease(ShardId shard, long epoch) {
        lease.Release(shard, epoch);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<PlayerLease> Lease() => Task.FromResult(lease.Current());

    /// <inheritdoc />
    public Task<ShardId> Where() => Task.FromResult(lease.Holder);
}
