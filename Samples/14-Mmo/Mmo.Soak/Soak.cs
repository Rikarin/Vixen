// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using Vixen.Gameplay;
using Vixen.Gameplay.Economy;
using Vixen.Gameplay.Instances;
using Vixen.Live;
using Vixen.Samples.Mmo.Contracts;
using Vixen.Samples.Mmo.Rules;

namespace Vixen.Samples.Mmo.Soak;

/// <summary>What to run.</summary>
/// <param name="Shards">How many realms.</param>
/// <param name="Players">How many connections.</param>
/// <param name="Ticks">How many ticks. 30 Hz, so 54 000 is thirty minutes.</param>
/// <param name="Seed">Which run. The same seed is the same run, which is what makes a failure reportable.</param>
/// <param name="Upgrade">Whether to roll the fleet onto a new build halfway.</param>
public readonly record struct SoakSettings(int Shards, int Players, int Ticks, ulong Seed, bool Upgrade) {
    /// <summary>Doc 27 § Testing's numbers: 8 realms, 3 maps, 500 connections, 30 minutes.</summary>
    public static SoakSettings Default => new(8, 500, 54_000, 0x50AC, true);
}

/// <summary>The run.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>It exercises the fleet, not eight engine hosts, and that boundary is deliberate.</b>
///         Eight <c>RealmApp</c>s in one process would mostly measure eight engine hosts, which
///         <c>13-ThirdPersonShooter</c> already does one of properly. What the budgets in doc 27 §
///         Testing are about is the part that only exists when there is more than one shard:
///         conservation across the fleet, the transfer path through it, and what a shard costs per
///         tick to keep a player's durable state straight.
///     </para>
///     <para>
///         <b>The conservation oracle is the reason this exists at all.</b> Doc 27 calls it "the test
///         the whole design exists to pass": total currency across the whole fleet, every tick, over
///         thousands of transfers, aborts and drains. Everything else here is scaffolding to make it
///         mean something.
///     </para>
///     <para>
///         ⚠ <b>Deterministic, from one seed.</b> A soak that cannot be re-run identically is a soak
///         whose failures are anecdotes.
///     </para>
/// </remarks>
public sealed class Soak(SoakSettings settings) {
    static readonly string[] Maps = [MmoMaps.Greenmarch, MmoMaps.Thornwood, MmoMaps.Barrowdeep];

    /// <summary>Tick zero, as a wall clock. Fixed, so the run's retention is part of the seed.</summary>
    static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Every account in the run.</summary>
    readonly List<PlayerKey> roster = [];

    /// <summary>What the world started with, and therefore what it must still have.</summary>
    long minted;

    /// <summary>Runs it.</summary>
    /// <returns>Zero if every budget held.</returns>
    public int Run() {
        var fleet = new Fleet(settings.Shards, Maps);
        var random = new GameplayRandom(settings.Seed);
        var durations = new long[settings.Ticks];

        Write(
            $"{settings.Shards} shards over {Maps.Length} maps, {settings.Players:N0} connections, "
            + $"{settings.Ticks:N0} ticks at 30 Hz ({settings.Ticks / 1800.0:N1} minutes)"
            + (settings.Upgrade ? ", with a rolling upgrade at the halfway mark" : string.Empty)
        );

        for (var index = 0; index < settings.Players; index++) {
            var key = new PlayerKey(Deterministic(index, 1), Deterministic(index, 2));
            var purse = 1_000L + random.NextInt(0, 9_000);

            roster.Add(key);
            minted += purse;
            fleet.Arrive(key, purse);
        }

        Write($"minted {minted:N0} gold across {fleet.Population:N0} players");

        // Everything below is steady state, so the build above is not counted as the run's.
        var settled = GC.GetTotalMemory(forceFullCollection: true);
        var beforeAllocated = GC.GetAllocatedBytesForCurrentThread();
        var beforeGen0 = GC.CollectionCount(0);
        var watch = new Stopwatch();
        var rollout = new Rollout(version: 2);
        var violations = 0;
        var upgraded = 0;

        for (var tick = 0; tick < settings.Ticks; tick++) {
            watch.Restart();

            Step(fleet, ref random, tick);

            watch.Stop();
            durations[tick] = watch.ElapsedTicks;

            // ⚠ Outside the clock, because it is the harness and not the fleet. Walking five hundred
            // balances a tick is the oracle's cost, and 09-NetworkSoak makes the same separation for
            // the same reason: measuring the measurement is how a budget becomes meaningless.
            //
            // ⚠ Every tick and not at the end. A conservation bug that self-corrects — a duplicate
            // that is later spent — is invisible to a final total and is still a duplicate.
            if (fleet.Total(MmoAddresses.Gold) != minted) {
                violations++;

                if (violations == 1) {
                    Write($"⚠ conservation broken at tick {tick:N0}: {fleet.Total(MmoAddresses.Gold):N0} of {minted:N0}");
                }
            }

            // ⚠ Outside the clock, and this one is a judgement call worth stating: a rollout step is
            // not a tick. Leaving it in would put a handful of very large samples in the histogram
            // and say something true about the rollout and nothing at all about the tick.
            if (settings.Upgrade && tick >= settings.Ticks / 2) {
                upgraded = rollout.Step(fleet);
            }
        }

        return Report(
            fleet,
            durations,
            violations,
            upgraded,
            new Cost {
                Settled = settled,
                Live = GC.GetTotalMemory(forceFullCollection: true),
                Allocated = GC.GetAllocatedBytesForCurrentThread() - beforeAllocated,
                Gen0 = GC.CollectionCount(0) - beforeGen0
            }
        );
    }

    /// <summary>One tick of the whole fleet.</summary>
    /// <remarks>
    ///     ⚠ <b>The rates are per tick and therefore small.</b> At 30 Hz a 2 % transfer chance per
    ///     player per tick would move every player twenty times a minute, which is not a game — it is
    ///     a benchmark of the transfer path with the world removed. These are tuned so that thirty
    ///     minutes produces the traffic thirty minutes of play produces.
    /// </remarks>
    void Step(Fleet fleet, ref GameplayRandom random, int tick) {
        // Trade: two players on the same shard swap gold. Conservation's most direct test — an intent
        // that is not balanced is refused by the ledger before it reaches the projection.
        for (var trade = 0; trade < 8; trade++) {
            var buyer = roster[random.NextInt(0, roster.Count)];
            var seller = roster[random.NextInt(0, roster.Count)];
            var shardId = fleet.Whereabouts(buyer);

            if (buyer == seller || shardId < 0 || shardId != fleet.Whereabouts(seller)) {
                continue;
            }

            var shard = fleet.Shards[shardId];
            var from = new EconomyAccount(shard.Identity.PlayerFor(buyer), string.Empty);
            var to = new EconomyAccount(shard.Identity.PlayerFor(seller), string.Empty);
            var price = 1L + random.NextInt(0, 50);

            shard.Ledger.Post(
                EconomyIntent.Transfer($"trade:{tick}:{trade}", from, to, MmoAddresses.Gold, price, "soak")
            );
        }

        // Travel: a player walks to another map, which is a realm transfer.
        for (var move = 0; move < 3; move++) {
            var key = roster[random.NextInt(0, roster.Count)];

            fleet.Transfer(key, Maps[random.NextInt(0, Maps.Length)]);
        }

        // The dungeon: somebody finishes a run and takes a lockout. It is fleet-wide, so the point of
        // recording it here is that the *next* shard they land on must refuse them.
        if (tick % 90 == 0) {
            var key = roster[random.NextInt(0, roster.Count)];
            var shardId = fleet.Whereabouts(key);

            if (shardId >= 0) {
                var shard = fleet.Shards[shardId];

                shard.Lockouts.Save(
                    new Lockout(shard.Identity.PlayerFor(key), MmoAddresses.Barrowdeep, "normal", tick + 2_700, 1)
                );
            }
        }

        // ⚠ The outbox, every tick. See Shard.Flush: this is off a realm's frame path in a real
        // shard, and the soak's two hundred megabytes of growth was its absence.
        //
        // ⚠ And the key sweep beside it, which was the other hundred and sixty. The clock is derived
        // from the tick rather than read, because a soak whose retention depends on how fast the
        // machine ran it is a soak whose memory number is not reproducible.
        var now = Origin + TimeSpan.FromSeconds(tick / 30.0);

        foreach (var shard in fleet.Shards) {
            shard.Flush();
            shard.Forget(now);
        }

        // Churn: somebody logs out and somebody else logs in, which is what keeps admission and
        // release on the hot path rather than only at the ends of the run.
        if (tick % 30 == 0) {
            var leaving = roster[random.NextInt(0, roster.Count)];
            var purse = fleet.Leave(leaving);

            // ⚠ Their gold comes back with them. A logout that dropped it would make the oracle pass
            // by deleting money, which is the failure mode opposite to the one it is looking for.
            fleet.Arrive(leaving, purse);
        }
    }

    int Report(Fleet fleet, long[] durations, int violations, int upgraded, Cost cost) {
        Array.Sort(durations);

        var frequency = (double)Stopwatch.Frequency;
        var mean = TimeSpan.FromSeconds(durations.Average() / frequency);
        var p99 = TimeSpan.FromSeconds(durations[(int)(durations.Length * 0.99)] / frequency);
        var worst = TimeSpan.FromSeconds(durations[^1] / frequency);
        var perTick = cost.Allocated / (double)settings.Ticks;

        var cold = fleet.Shards.Sum(shard => shard.Lockouts.ColdReads + shard.Social.ColdReads);
        var spread = fleet.Shards.Select(shard => shard.Version).Distinct().Count() - 1;
        var divergences = fleet.Shards.Sum(shard => shard.Ledger.Divergences);
        var stateWrites = fleet.Shards.Sum(shard => shard.Social.StateWrites);

        Write(string.Empty);
        Write($"transfers      {fleet.Transfers:N0} ({fleet.Refused:N0} refused, which is a player who stayed)");
        Write($"upgraded       {upgraded} of {settings.Shards} shards");
        Write($"disconnected   {fleet.Disconnected:N0}");
        Write($"population     {fleet.Population:N0}");
        Write(string.Empty);
        Write($"tick mean      {mean.TotalMicroseconds:N0} µs");
        Write($"tick p99       {p99.TotalMicroseconds:N0} µs");
        Write($"tick worst     {worst.TotalMicroseconds:N0} µs");
        Write($"allocated      {perTick / 1024:N1} KB a tick ({cost.Allocated / 1_048_576.0:N1} MB over the run)");
        Write($"gen0           {cost.Gen0:N0}");
        Write($"live           {(cost.Live - cost.Settled) / 1_048_576.0:N1} MB grown");
        Write($"ledger keys    {fleet.Shards.Sum(shard => shard.Keys):N0} held, {fleet.Shards.Sum(shard => shard.Forgotten):N0} aged out past {Shard.RetryWindow.TotalMinutes:N0} min of retries");
        Write($"outbox         {fleet.Shards.Sum(shard => shard.Ledger.Pending):N0} unsettled");
        Write(string.Empty);

        // ⚠ The four that have to be zero, and they are zero for four different reasons — which is
        // why they are reported separately rather than summed into a "healthy" flag.
        var failed = 0;

        failed += Budget("conservation violations", violations, 0);
        failed += Budget("ledger divergences", divergences, 0);
        failed += Budget("cold reads", cold, 0);
        failed += Budget("state-shaped guild writes", stateWrites, 0);

        // ⚠ Zero because this fleet settles every tick and never re-posts an operation, so nothing
        // should ever be recognised from the outbox instead of the projection. It going non-zero here
        // would mean the horizon had dropped a key before the write carrying it was durable — which
        // LedgerBridge catches, and which is the only reason that would be a counter rather than a
        // purse that had quietly doubled.
        failed += Budget("replays answered by the outbox", fleet.Shards.Sum(shard => shard.Ledger.Deduplicated), 0);

        // ⚠ Not zero — a tick's own writes are legitimately in flight — but bounded. An outbox that
        // grows is a realm that drains and forgets to settle, which is a leak that looks like nothing
        // at all for the first ten minutes. It cost this soak two hundred megabytes to find.
        failed += Budget("outbox left unsettled", fleet.Shards.Sum(shard => shard.Ledger.Pending), settings.Shards * 8);

        // ⚠ Doc 27's own two assertions about a rollout, and they are separate on purpose: a rollout
        // that finished by disconnecting everybody would reach a spread of zero.
        failed += Budget("disconnected by the rollout", fleet.Disconnected, 0);

        if (settings.Upgrade) {
            failed += Budget("version spread at the end", spread, 0);
        }

        // ⚠ A *budget* rather than a measurement, and deliberately generous: this is a laptop, in
        // one process, with eight shards on one core. What it is protecting against is a regression
        // of the kind 09-NetworkSoak found — an allocation per player per tick — and not a
        // millisecond of drift.
        failed += Budget("allocation, KB a tick", (int)(perTick / 1024), 64);
        failed += Budget("tick p99, µs", (int)p99.TotalMicroseconds, 2_000);

        // ⚠ **This is the budget the soak was built to miss, and the one it now holds.** Eight shards
        // holding five hundred players are a fixed working set, so a fleet that has settled should not
        // grow — and this grew by roughly a megabyte a minute, for ever. Two causes, found in that
        // order: the projection's idempotency-key set, which nothing ever removed a key from; and
        // every departed player's balance rows, left behind by a `Restore(…, 0)` that looked like the
        // mirror of admission and did nothing at all.
        failed += Budget("memory grown, MB", (int)((cost.Live - cost.Settled) / 1_048_576), 32);

        Write(string.Empty);
        Write(failed == 0 ? "every budget held." : $"{failed} budgets missed.");

        return failed == 0 ? 0 : 1;
    }

    static int Budget(string what, int measured, int allowed) {
        var held = measured <= allowed;

        Write($"  {(held ? "ok  " : "MISS")} {what,-28} {measured,10:N0}  (budget {allowed:N0})");

        return held ? 0 : 1;
    }

    /// <summary>A stable guid from an index, so a seed is the whole of a run's identity.</summary>
    static Guid Deterministic(int index, int salt) {
        Span<byte> bytes = stackalloc byte[16];

        BitConverter.TryWriteBytes(bytes, index);
        BitConverter.TryWriteBytes(bytes[4..], salt);

        return new(bytes);
    }

    static void Write(string line) => Console.WriteLine(line);

    readonly record struct Cost {
        public long Settled { get; init; }

        public long Live { get; init; }

        public long Allocated { get; init; }

        public int Gen0 { get; init; }
    }
}
