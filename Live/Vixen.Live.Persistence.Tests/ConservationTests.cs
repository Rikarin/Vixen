// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Xunit;

namespace Vixen.Live.Persistence.Tests;

/// <summary>
///     The duplication oracle. Doc 27 § Testing calls this <i>"the test the whole design exists to
///     pass"</i>, and M2 calls what it catches unrecoverable reputationally.
/// </summary>
/// <remarks>
///     <para>
///         The shape is deliberately hostile: thousands of operations, run concurrently, with
///         duplicate deliveries, stale lease epochs, overdrafts and interleaved transfers all mixed
///         in. Every one of those is a way a real fleet fails, and none of them may move the total.
///     </para>
///     <para>
///         ⚠ <b>What is asserted is a sum over <em>every</em> account, world accounts included.</b>
///         That is what makes it total rather than approximate: a bug that created gold out of
///         nothing would show up as a non-zero sum, and there is nowhere for the missing side of a
///         movement to hide because <c>LedgerAccount</c> has no "outside".
///     </para>
///     <para>
///         Deterministic: the operations are generated up front from a fixed seed and then run in an
///         arbitrary order across tasks. A failure is reproducible even though the interleaving is
///         not, which is the only kind of concurrency test worth having.
///     </para>
/// </remarks>
public class ConservationTests {
    const int Characters = 12;
    const int Operations = 4_000;

    static readonly AssetId Gold = new("currency/gold");
    static readonly AssetId Sword = new("items/greatsword");
    static readonly AssetId Potion = new("items/potion");
    static readonly ImmutableArray<AssetId> Assets = [Gold, Sword, Potion];
    static readonly DateTimeOffset Noon = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(20260804)]
    [InlineData(1)]
    [InlineData(0xC0FFEE)]
    public async Task Value_is_conserved_under_concurrency_duplicates_and_lost_leases(int seed) {
        await using var store = new MemoryPersistence { Clock = () => Noon };

        var cast = await Cast(store);
        var random = new Random(seed);
        var work = Plan(random, cast);

        // Eight workers over one store, because a realm per shard writing the same characters is
        // precisely the situation ADR-021's lease exists inside of.
        var lanes = Enumerable.Range(0, 8)
            .Select(lane => Task.Run(async () => {
                for (var index = lane; index < work.Length; index += 8) {
                    await store.Ledger.AppendAsync(work[index], TestContext.Current.CancellationToken).ConfigureAwait(false);
                }
            }));

        await Task.WhenAll(lanes);

        // ── The oracle ──────────────────────────────────────────────────────────────────────────
        foreach (var asset in Assets) {
            var total = 0L;

            foreach (var row in await store.Ledger.HistoryAsync(new() { Asset = asset, Limit = int.MaxValue }, TestContext.Current.CancellationToken)) {
                total += row.Delta;
            }

            Assert.Equal(0, total);
        }

        Assert.Empty(await store.Ledger.ReconcileAsync(TestContext.Current.CancellationToken));

        // Nobody is overdrawn, which is the same statement as "nothing was taken twice".
        foreach (var who in cast) {
            foreach (var (asset, quantity) in await store.Ledger.HoldingsAsync(LedgerAccount.Of(who), TestContext.Current.CancellationToken)) {
                Assert.True(quantity >= 0, $"{who} holds {quantity} {asset}");
            }
        }
    }

    /// <summary>
    ///     The same run, asserting the other half: an operation appears in the journal once no matter
    ///     how many times it was delivered. Conservation would still hold if every duplicate applied
    ///     both legs — it is this that says the duplicate did nothing at all.
    /// </summary>
    [Fact]
    public async Task An_operation_appears_in_the_journal_exactly_once() {
        await using var store = new MemoryPersistence { Clock = () => Noon };

        var cast = await Cast(store);
        var work = Plan(new(20260804), cast);

        await Task.WhenAll(
            Enumerable.Range(0, 8)
                .Select(lane => Task.Run(async () => {
                    for (var index = lane; index < work.Length; index += 8) {
                        await store.Ledger.AppendAsync(work[index], TestContext.Current.CancellationToken).ConfigureAwait(false);
                    }
                }))
        );

        var rows = await store.Ledger.HistoryAsync(new() { Limit = int.MaxValue }, TestContext.Current.CancellationToken);
        var perOperation = new Dictionary<IdempotencyKey, int>();
        var movementsOf = new Dictionary<IdempotencyKey, int>();

        foreach (var intent in work) {
            movementsOf[intent.Key] = intent.Movements.Length;
        }

        foreach (var row in rows) {
            perOperation[row.Key] = perOperation.GetValueOrDefault(row.Key) + 1;
        }

        Assert.NotEmpty(perOperation);

        foreach (var (key, count) in perOperation) {
            // The opening grants are not part of the plan, and they are the one kind of row in the
            // journal that nothing here delivered twice.
            if (!movementsOf.TryGetValue(key, out var movements)) {
                Assert.Equal("seed", key.Kind);
                Assert.Equal(2, count);

                continue;
            }

            Assert.Equal(movements, count);
        }
    }

    /// <summary>
    ///     A realm that crashed mid-transfer took nothing with it: the operations it never got to
    ///     append are simply absent, and the ones it appended before losing the lease stand.
    /// </summary>
    [Fact]
    public async Task A_realm_that_loses_its_lease_stops_being_able_to_write() {
        await using var store = new MemoryPersistence { Clock = () => Noon };

        var cast = await Cast(store);
        var who = cast[0];

        var before = await store.Ledger.AppendAsync(Spend(who, 3, "before"), TestContext.Current.CancellationToken);

        // The transfer: a new realm takes the lease at epoch 4 and writes the row.
        var record = await store.Players.ReadAsync(who, TestContext.Current.CancellationToken);

        Assert.NotNull(record);
        Assert.Equal(WriteOutcome.Written, await store.Players.WriteAsync(record with { LeaseEpoch = 4 }, TestContext.Current.CancellationToken));

        // The old realm is still simulating and still flushing. Everything it says now is declined,
        // and it never has to find out in time.
        var after = await store.Ledger.AppendAsync(Spend(who, 3, "after"), TestContext.Current.CancellationToken);
        var stale = await store.Players.WriteAsync(record with { LeaseEpoch = 3, Name = record.Name }, TestContext.Current.CancellationToken);

        Assert.Equal(LedgerVerdict.Applied, before.Verdict);
        Assert.Equal(LedgerVerdict.Superseded, after.Verdict);
        Assert.Equal(WriteOutcome.Superseded, stale);
        Assert.Empty(await store.Ledger.ReconcileAsync(TestContext.Current.CancellationToken));
    }

    static LedgerIntent Spend(PlayerKey who, long epoch, string operation) =>
        LedgerIntent.Transfer(
            new(who, "vendor", operation),
            epoch,
            Noon,
            LedgerAccount.Of(who),
            LedgerAccount.Of(LedgerAccount.Vendor),
            Gold,
            1
        );

    static async Task<ImmutableArray<PlayerKey>> Cast(MemoryPersistence store) {
        var builder = ImmutableArray.CreateBuilder<PlayerKey>(Characters);

        for (var index = 0; index < Characters; index++) {
            var who = new PlayerKey(Guid.NewGuid(), Guid.NewGuid());

            await store.Players.CreateAsync(
                new(
                    who,
                    $"character-{index}",
                    Noon,
                    Noon,
                    "eu",
                    "maps/queensdale",
                    1,
                    ReadOnlyMemory<byte>.Empty
                ),
                TestContext.Current.CancellationToken
            );

            foreach (var asset in Assets) {
                await store.Ledger.AppendAsync(
                    LedgerIntent.Transfer(
                        new(who, "seed", asset.Address),
                        1,
                        Noon,
                        LedgerAccount.Of(LedgerAccount.Loot),
                        LedgerAccount.Of(who),
                        asset,
                        1_000
                    ),
                    TestContext.Current.CancellationToken
                );
            }

            builder.Add(who);
        }

        return builder.MoveToImmutable();
    }

    /// <summary>Generates the run: transfers, drops, sales, duplicates and stale epochs.</summary>
    static ImmutableArray<LedgerIntent> Plan(Random random, ImmutableArray<PlayerKey> cast) {
        var work = ImmutableArray.CreateBuilder<LedgerIntent>(Operations);

        for (var index = 0; index < Operations; index++) {
            var asset = Assets[random.Next(Assets.Length)];
            var actor = cast[random.Next(cast.Length)];
            var other = cast[random.Next(cast.Length)];
            var quantity = random.Next(1, 40);

            // One in twenty names an epoch below the fence — a realm still flushing after a transfer
            // took its lease. Every one of these must be declined without moving anything.
            var epoch = random.Next(20) == 0 ? 0 : 1;

            LedgerIntent intent = random.Next(4) switch {
                0 => LedgerIntent.Transfer(
                    new(actor, "loot", index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    epoch,
                    Noon,
                    LedgerAccount.Of(LedgerAccount.Loot),
                    LedgerAccount.Of(actor),
                    asset,
                    quantity
                ),
                1 => LedgerIntent.Transfer(
                    new(actor, "vendor", index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    epoch,
                    Noon,
                    LedgerAccount.Of(actor),
                    LedgerAccount.Of(LedgerAccount.Vendor),
                    asset,
                    quantity
                ),
                2 => LedgerIntent.Transfer(
                    new(actor, "give", index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    epoch,
                    Noon,
                    LedgerAccount.Of(actor),
                    LedgerAccount.Of(other),
                    asset,
                    quantity
                ),

                // A trade: four legs, two assets, and the shape that a half-applied append would
                // turn into a duplicated sword.
                _ => new LedgerIntent {
                    Key = new(actor, "trade", index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    LeaseEpoch = epoch,
                    At = Noon,
                    Movements = [
                        new(LedgerAccount.Of(actor), Sword, -1),
                        new(LedgerAccount.Of(other), Sword, 1),
                        new(LedgerAccount.Of(other), Gold, -quantity),
                        new(LedgerAccount.Of(actor), Gold, quantity)
                    ]
                }
            };

            work.Add(intent);

            // One in six is delivered twice, and the copies are separated in the schedule so the
            // duplicate usually lands on a different lane than the original.
            if (random.Next(6) == 0) {
                work.Add(intent with { Detail = "duplicate delivery" });
            }
        }

        return work.ToImmutable();
    }
}
