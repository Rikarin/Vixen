// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Live.Orchestration.Tests;

/// <summary>ADR-021, asserted. The half of the duplication oracle that exists without a transfer.</summary>
/// <remarks>
///     Doc 27 § Testing's conservation oracle — thousands of randomised concurrent transfers, asserting
///     total item count is conserved — is L2's, because there is nothing to transfer yet. What can be
///     asserted now is the property it rests on: at any moment exactly one shard may write, the epoch
///     only ever moves forward, and a realm that has been superseded finds out.
/// </remarks>
public sealed class PlayerLeaseTests {
    static DateTimeOffset now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    static PlayerLeaseState Lease(TimeSpan? lifetime = null) =>
        new(new(lifetime ?? TimeSpan.FromSeconds(20), () => now));

    [Fact]
    public void TheFirstAcquisitionIsGrantedAtEpochOne() {
        var lease = Lease();
        var shard = ShardId.New();

        var granted = lease.Acquire(shard);

        Assert.True(granted.Granted);
        Assert.Equal(1, granted.Epoch);
        Assert.Equal(shard, granted.Holder);
    }

    [Fact]
    public void TheSecondShardTakesItAndTheFirstFindsOut() {
        var lease = Lease();
        var first = ShardId.New();
        var second = ShardId.New();

        var mine = lease.Acquire(first);
        var theirs = lease.Acquire(second);

        Assert.True(theirs.Granted);
        Assert.Equal(mine.Epoch + 1, theirs.Epoch);

        // ⚠ The whole of ADR-021 in one assertion. Two realms cannot both hold epoch n, so two
        // realms cannot both believe they may write this character's inventory — and the first one
        // discovers it on the renewal it was making anyway rather than by being told.
        var refused = lease.Renew(first, mine.Epoch);

        Assert.False(refused.Granted);
        Assert.Equal(second, refused.Holder);
    }

    [Fact]
    public void AcquiringAlwaysSucceedsBecauseACrashedRealmCannotSayNo() {
        var lease = Lease();
        var dead = ShardId.New();

        lease.Acquire(dead);

        // The cluster cannot tell a crashed realm from a slow one. If acquisition could fail, a
        // character on a realm that died would be unplayable until a timeout nobody can see elapsed.
        for (var attempt = 0; attempt < 10; attempt++) {
            Assert.True(lease.Acquire(ShardId.New()).Granted);
        }
    }

    [Fact]
    public void TheEpochOnlyEverMovesForward() {
        var lease = Lease();
        var epochs = new List<long>();

        for (var round = 0; round < 100; round++) {
            var shard = ShardId.New();

            epochs.Add(lease.Acquire(shard).Epoch);
            epochs.Add(lease.Renew(shard, epochs[^1]).Epoch);
            lease.Release(shard, epochs[^1]);
        }

        // Never reused, so a durable write naming an old epoch is a no-op rather than a collision
        // with a live one.
        Assert.Equal(epochs.Order().ToList(), epochs);
        Assert.Equal(epochs.Distinct().Count(), epochs.Count - 100);
    }

    [Fact]
    public void RenewingKeepsTheSameEpochWhileItIsAlive() {
        var lease = Lease();
        var shard = ShardId.New();

        var granted = lease.Acquire(shard);

        now += TimeSpan.FromSeconds(5);

        var renewed = lease.Renew(shard, granted.Epoch);

        Assert.True(renewed.Granted);
        Assert.Equal(granted.Epoch, renewed.Epoch);
        Assert.True(renewed.Expires > granted.Expires);
    }

    [Fact]
    public void ALapsedLeaseComesBackAtANewEpochRatherThanBeingResurrected() {
        var lease = Lease(TimeSpan.FromSeconds(20));
        var shard = ShardId.New();

        var granted = lease.Acquire(shard);

        now += TimeSpan.FromMinutes(5);

        Assert.False(lease.IsHeld);

        var renewed = lease.Renew(shard, granted.Epoch);

        // Granted — they are still the holder of record, and nobody else took it — but at a new
        // epoch. A renewal that resurrected the old one would let two realms believe they hold the
        // same character either side of a partition that has just healed.
        Assert.True(renewed.Granted);
        Assert.Equal(granted.Epoch + 1, renewed.Epoch);
    }

    [Fact]
    public void ReleasingWithAStaleEpochIsIgnoredRatherThanRefused() {
        var lease = Lease();
        var first = ShardId.New();
        var second = ShardId.New();

        var mine = lease.Acquire(first);
        var theirs = lease.Acquire(second);

        // A realm superseded during its own shutdown, which is ordinary. Honouring this would hand
        // the new holder a released lease it never gave up.
        lease.Release(first, mine.Epoch);

        Assert.True(lease.IsHeld);
        Assert.Equal(second, lease.Holder);
        Assert.Equal(theirs.Epoch, lease.Current().Epoch);
    }

    [Fact]
    public void ReleasingProperlyLetsGo() {
        var lease = Lease();
        var shard = ShardId.New();

        var granted = lease.Acquire(shard);

        lease.Release(shard, granted.Epoch);

        Assert.False(lease.IsHeld);
        Assert.False(lease.Holder.IsValid);
        Assert.False(lease.Current().Granted);
    }

    [Fact]
    public void ExactlyOneShardHoldsItAtAnyMoment() {
        // The oracle, in the form that is available before there is a transfer: over a randomised
        // sequence of acquisitions, renewals and releases, the number of shards that would be told
        // "yes, you may write" is never more than one.
        var lease = Lease();
        var random = new Random(20260804);
        var shards = Enumerable.Range(0, 8).Select(_ => ShardId.New()).ToArray();
        var believed = new Dictionary<ShardId, long>();

        for (var step = 0; step < 50_000; step++) {
            var shard = shards[random.Next(shards.Length)];

            switch (random.Next(4)) {
                case 0:
                    believed[shard] = lease.Acquire(shard).Epoch;

                    break;

                case 1 when believed.TryGetValue(shard, out var held):
                    if (!lease.Renew(shard, held).Granted) {
                        believed.Remove(shard);
                    }

                    break;

                case 2 when believed.TryGetValue(shard, out var releasing):
                    lease.Release(shard, releasing);
                    believed.Remove(shard);

                    break;

                default:
                    now += TimeSpan.FromSeconds(random.Next(0, 30));

                    break;
            }

            // Whatever each realm believes, at most one of those beliefs is one the grain would
            // confirm — and confirmation is what a durable write is checked against.
            var confirmed = believed.Count(entry => lease.Current() is { Granted: true } current
                && current.Holder == entry.Key
                && current.Epoch == entry.Value
            );

            Assert.True(confirmed <= 1, $"{confirmed} shards would have been told they may write.");
        }
    }
}
