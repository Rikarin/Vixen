// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay;
using Vixen.Gameplay.Economy;
using Vixen.Live;
using Vixen.Live.Gameplay;
using Vixen.Live.Persistence;
using Vixen.Samples.Mmo.Contracts;
using Vixen.Samples.Mmo.Rules;
using NetworkPlayerId = Vixen.Net.Sessions.PlayerId;

namespace Vixen.Samples.Mmo.Soak;

/// <summary>One shard: a map, a lease epoch, and the four bridges a realm holds.</summary>
/// <remarks>
///     ⚠ <b>Every one of these is a <em>separate</em> set of bridges over <em>one</em> ledger.</b>
///     That is the arrangement the whole soak is about: eight partial views of a world, each
///     authoritative for the frame about the players it holds, all writing into one journal. If the
///     conservation oracle can be broken, it is broken here.
/// </remarks>
public sealed class Shard {
    readonly MemoryEconomyLedger projection = new();

    long sequence;

    /// <summary>Makes one.</summary>
    /// <param name="id">Which shard.</param>
    /// <param name="map">Which map, by address.</param>
    /// <param name="version">Which build it is running. A rolling upgrade changes this.</param>
    /// <param name="epoch">Its lease epoch.</param>
    public Shard(int id, string map, int version, long epoch) {
        Id = id;
        Map = map;
        Version = version;
        Identity = new();
        Ledger = new(Identity, projection, epoch);
        Lockouts = new(Identity);
        Social = new(Identity);
    }

    /// <summary>Which shard.</summary>
    public int Id { get; }

    /// <summary>Which map.</summary>
    public string Map { get; }

    /// <summary>Which build. Only a rolling upgrade changes it, and only while drained.</summary>
    public int Version { get; private set; }

    /// <summary>Whether it is taking new players.</summary>
    public bool Draining { get; private set; }

    /// <summary>The join every durable write starts from.</summary>
    public GameplayIdentityMap Identity { get; }

    /// <summary>Doc 28's economy, applied here and written down later.</summary>
    public LedgerBridge Ledger { get; }

    /// <summary>Doc 28's lockouts over the fleet's.</summary>
    public LockoutBridge Lockouts { get; }

    /// <summary>Doc 28's guilds and graphs.</summary>
    public SocialBridge Social { get; }

    /// <summary>How many players it holds.</summary>
    public int Population => Identity.Count;

    /// <summary>Writes the outbox down and settles what landed. What a realm does off the frame path.</summary>
    /// <returns>How many writes were settled.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The soak grew two hundred megabytes over thirty minutes before this existed, and
    ///         that was the bridge behaving exactly as documented.</b> "A drained write is not
    ///         removed — in flight is not done, and losing a ledger intent is losing an item." Nothing
    ///         was settling them, so the outbox was every intent the fleet had ever posted.
    ///     </para>
    ///     <para>
    ///         Which makes it worth saying plainly: <b>a realm that drains and forgets to settle has
    ///         an unbounded leak that looks like nothing at all for the first ten minutes.</b>
    ///         <see cref="LedgerBridge.Pending" /> is the number that says so, and a fleet should
    ///         alarm on it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Settling here is a stand-in for a database round trip and does not pretend
    ///         otherwise.</b> What a real realm settles with is the verdict the journal came back
    ///         with, and the interesting case is <c>Insufficient</c> — which is a <em>defect</em>
    ///         rather than a refusal, because the projection already checked. The soak asserts
    ///         <see cref="LedgerBridge.Divergences" /> stays at zero, which is that check.
    ///     </para>
    /// </remarks>
    public int Flush() {
        var settled = 0;

        foreach (var write in Ledger.Drain()) {
            Ledger.Settle(write.Key, new(LedgerVerdict.Applied, ++sequence));
            settled++;
        }

        return settled;
    }

    /// <summary>How many idempotency keys the projection is still holding.</summary>
    /// <remarks>
    ///     ⚠ <b>It only ever goes up, and over thirty minutes that is what the soak's memory growth
    ///     turns out to be.</b> The set is what makes a retried trade write nothing the second time,
    ///     so it cannot simply be cleared — but a key older than the longest retry anybody will
    ///     attempt is a key guarding against nothing. A shard that runs for a week keeps every key of
    ///     that week.
    /// </remarks>
    public int Keys => projection.Keys;

    /// <summary>What the players on this shard are holding, of one asset.</summary>
    /// <param name="asset">Which.</param>
    /// <returns>The sum over their accounts.</returns>
    /// <remarks>
    ///     ⚠ <b>Player accounts only, and <c>MemoryEconomyLedger.Total</c> is the trap.</b> That
    ///     method sums <em>every</em> account, and a double-entry journal's every-account total is
    ///     zero by construction — every movement has two legs. It is a fine assertion that the ledger
    ///     is balanced and a useless one about whether money was created, because a duplicate is two
    ///     balanced legs. What conservation means here is that the sum over the <em>players</em> is
    ///     what was minted, and the world account is holding the negative of it.
    /// </remarks>
    public long Players(DefId asset) {
        var total = 0L;

        foreach (var (player, _) in Identity.Players) {
            total += Ledger.Balance(new(player, string.Empty), asset);
        }

        return total;
    }

    /// <summary>Takes a player.</summary>
    /// <param name="key">Who, durably.</param>
    /// <param name="session">What the session calls them.</param>
    /// <param name="purse">What they arrive with.</param>
    /// <returns>What gameplay calls them here.</returns>
    /// <remarks>
    ///     ⚠ <b>The purse is <em>seeded</em> and not minted.</b> Doc 27: a balance is seeded from the
    ///     database's balances, never replayed from its journal. Minting here would make every
    ///     transfer create money, which is precisely the thing the oracle is looking for.
    /// </remarks>
    public PlayerId Admit(PlayerKey key, uint session, long purse) {
        var player = Identity.Admit(key, new NetworkPlayerId(session));

        Ledger.Restore(new(player, string.Empty), MmoAddresses.Gold, purse);
        Lockouts.Warmed(player, []);
        Social.Warmed(player, row: null);
        Social.Warmed(key, []);
        Social.Admitted(key, player);

        return player;
    }

    /// <summary>Lets a player go, and says what they are taking with them.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Their purse.</returns>
    /// <remarks>
    ///     ⚠ <b>The purse leaves with them, which is what makes the oracle mean something.</b> A
    ///     handover that left the balance behind and seeded a fresh one on the far side would conserve
    ///     nothing and pass every test that only counted one shard.
    /// </remarks>
    public long Release(PlayerId player) {
        var purse = Ledger.Balance(new(player, string.Empty), MmoAddresses.Gold);

        Ledger.Restore(new(player, string.Empty), MmoAddresses.Gold, 0);
        Lockouts.Forget(player);
        Social.Forget(player);
        Identity.Release(player);

        return purse;
    }

    /// <summary>Stops taking new players. What a rollout does first.</summary>
    public void Drain() => Draining = true;

    /// <summary>Comes back on the new build.</summary>
    /// <param name="version">Which.</param>
    /// <exception cref="InvalidOperationException">It still has players.</exception>
    /// <remarks>
    ///     ⚠ <b>Refused while anybody is still on it.</b> Doc 27's rollout produces a *drain* at every
    ///     step and never a kill; a shard that could restart under a player is a rollout that
    ///     disconnects people, which is the one thing the drain machinery exists to prevent.
    /// </remarks>
    public void Restart(int version) {
        if (Population > 0) {
            throw new InvalidOperationException($"shard {Id} still holds {Population} players.");
        }

        Version = version;
        Draining = false;
    }
}

/// <summary>Eight shards over three maps, and one journal underneath them.</summary>
public sealed class Fleet {
    readonly List<Shard> shards = [];
    readonly Dictionary<PlayerKey, int> whereabouts = [];
    readonly Dictionary<PlayerKey, long> purses = [];
    readonly List<PlayerKey> leaving = [];

    uint sessions;

    /// <summary>Stands one up.</summary>
    /// <param name="shardCount">How many shards.</param>
    /// <param name="maps">Which maps to spread them over.</param>
    public Fleet(int shardCount, IReadOnlyList<string> maps) {
        ArgumentNullException.ThrowIfNull(maps);

        for (var id = 0; id < shardCount; id++) {
            shards.Add(new(id, maps[id % maps.Count], version: 1, epoch: 1));
        }
    }

    /// <summary>The shards.</summary>
    public IReadOnlyList<Shard> Shards => shards;

    /// <summary>How many players are anywhere.</summary>
    public int Population => whereabouts.Count;

    /// <summary>How many transfers have completed.</summary>
    public int Transfers { get; private set; }

    /// <summary>How many were refused because every candidate was draining.</summary>
    /// <remarks>⚠ A refusal is not a failure — it is a player who stayed where they were.</remarks>
    public int Refused { get; private set; }

    /// <summary>How many players the fleet dropped. Never anything but zero.</summary>
    /// <remarks>
    ///     ⚠ <b>The one number a rollout is really judged on.</b> Doc 27: every step of a rollout is
    ///     a drain and nothing is ever killed, so a player who left the world without logging out is
    ///     a bug in the drain and not a busy fleet.
    /// </remarks>
    public int Disconnected { get; private set; }

    /// <summary>
    ///     What the fleet believes exists, of one asset, across every shard plus everybody in transit.
    /// </summary>
    /// <param name="asset">Which.</param>
    /// <returns>The total.</returns>
    /// <remarks>
    ///     ⚠ <b>"Plus everybody in transit" is the whole of the oracle.</b> A player between two
    ///     shards has been released by one and not yet admitted by the other, and their purse is a
    ///     number this class is holding. Counting only the shards would show a dip on every transfer
    ///     and a total that is right again a moment later — which is exactly what a duplication bug
    ///     looks like from the other side.
    /// </remarks>
    public long Total(DefId asset) => shards.Sum(shard => shard.Players(asset)) + purses.Values.Sum();

    /// <summary>Puts somebody in the world for the first time.</summary>
    /// <param name="key">Who.</param>
    /// <param name="purse">What they start with.</param>
    /// <returns>Whether there was anywhere to put them.</returns>
    public bool Arrive(PlayerKey key, long purse) {
        var shard = Pick(MmoMaps.Greenmarch);

        if (shard is null) {
            Refused++;

            return false;
        }

        shard.Admit(key, ++sessions, purse);
        whereabouts[key] = shard.Id;

        return true;
    }

    /// <summary>Moves somebody to another map.</summary>
    /// <param name="key">Who.</param>
    /// <param name="map">Where.</param>
    /// <returns>Whether they moved.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Released before admitted, with the purse held here in between.</b> That is the
    ///         real ordering and the reason the overlap window exists at all: admitting first would
    ///         put the same player on two shards, which is the state ADR-021's lease is for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A transfer that finds nowhere to go leaves them where they were</b>, purse intact
    ///         and playable. Doc 27's abort paths all end that way; one that ended with a released
    ///         player and no destination would be a player who has left the world.
    ///     </para>
    /// </remarks>
    public bool Transfer(PlayerKey key, string map) {
        if (!whereabouts.TryGetValue(key, out var from)) {
            return false;
        }

        var target = Pick(map);

        if (target is null || target.Id == from) {
            Refused++;

            return false;
        }

        var source = shards[from];
        var player = source.Identity.PlayerFor(key);

        // The purse comes off the source and is held here — in flight, and counted by Total.
        purses[key] = source.Release(player);
        whereabouts.Remove(key);

        target.Admit(key, ++sessions, purses[key]);
        purses.Remove(key);
        whereabouts[key] = target.Id;
        Transfers++;

        return true;
    }

    /// <summary>Takes somebody out of the world entirely.</summary>
    /// <param name="key">Who.</param>
    /// <returns>What they logged out with.</returns>
    public long Leave(PlayerKey key) {
        if (!whereabouts.Remove(key, out var from)) {
            return 0;
        }

        var shard = shards[from];

        return shard.Release(shard.Identity.PlayerFor(key));
    }

    /// <summary>Moves a few players off a draining shard, as a drain does.</summary>
    /// <param name="shard">Which.</param>
    /// <param name="most">How many to move this tick.</param>
    /// <returns>How many moved.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A few, and not all of them, which is the difference between a drain and a
    ///         kick.</b> Doc 27: <em>"existing players are moved at safe moments"</em> — a drain that
    ///         emptied a shard in one tick would move two hundred people mid-fight, and it would also
    ///         land two hundred admissions on one target in one tick, which is the spike a rollout is
    ///         supposed to avoid.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nobody is disconnected.</b> A drain that cannot place somebody leaves them where
    ///         they are and the rollout waits — doc 27's grace, and the reason a step is a drain
    ///         rather than a kill.
    ///     </para>
    /// </remarks>
    public int DrainSome(Shard shard, int most) {
        ArgumentNullException.ThrowIfNull(shard);

        shard.Drain();

        var moved = 0;

        // ⚠ Rebuilt each call rather than kept, because Transfer mutates the dictionary it is read
        // from. A cached list would move somebody who had already left of their own accord.
        foreach (var (key, where) in whereabouts) {
            if (where != shard.Id) {
                continue;
            }

            leaving.Add(key);

            if (leaving.Count >= most) {
                break;
            }
        }

        foreach (var key in leaving) {
            if (Transfer(key, shard.Map)) {
                moved++;
            }
        }

        leaving.Clear();

        return moved;
    }

    /// <summary>Where somebody is, or −1.</summary>
    /// <param name="key">Who.</param>
    /// <returns>The shard id.</returns>
    public int Whereabouts(PlayerKey key) => whereabouts.GetValueOrDefault(key, -1);

    Shard? Pick(string map) {
        Shard? best = null;

        // Emptiest first, which is what PlacementDirector settles on for the same reason: it is the
        // one that gives its capacity back soonest and the one least likely to be the next to drain.
        foreach (var shard in shards) {
            if (shard.Draining || !string.Equals(shard.Map, map, StringComparison.Ordinal)) {
                continue;
            }

            if (best is null || shard.Population < best.Population) {
                best = shard;
            }
        }

        return best;
    }
}

/// <summary>A rolling upgrade: one shard at a time, drained and never killed.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Stepped over many ticks rather than done in one, because a rollout that finished
///         inside a tick would not be a rollout.</b> Doc 27's assertion is that it happens
///         <em>with players in flight</em> — the interesting state is the middle, where half the
///         fleet is on the old build, one shard is draining, and players are still trading and
///         travelling across the seam.
///     </para>
///     <para>
///         ⚠ <b>Emptiest first.</b> It gives its capacity back soonest, which is what makes room for
///         the next one — and the next one is where a rollout that drained two at a time would find
///         it had nowhere to put anybody.
///     </para>
///     <para>
///         ⚠ <b>A shard that will not empty is waited on, not killed.</b> Doc 27's grace: every step
///         is a drain, and a player the fleet cannot place stays where they are and keeps playing.
///         Escalating is a live-ops alert, never a disconnect.
///     </para>
/// </remarks>
public sealed class Rollout(int version) {
    Shard? draining;

    /// <summary>How many shards have come back on the new build.</summary>
    public int Upgraded { get; private set; }

    /// <summary>How many ticks were spent waiting for a shard that would not empty.</summary>
    /// <remarks>Not a failure. It is the number a grace is measured against.</remarks>
    public int Waited { get; private set; }

    /// <summary>Advances it by one tick.</summary>
    /// <param name="fleet">The fleet.</param>
    /// <returns>How many shards are on the new build.</returns>
    public int Step(Fleet fleet) {
        ArgumentNullException.ThrowIfNull(fleet);

        if (draining is null) {
            draining = fleet.Shards
                .Where(shard => shard.Version != version)
                .OrderBy(shard => shard.Population)
                .FirstOrDefault();

            if (draining is null) {
                return Upgraded;
            }
        }

        // Four a tick at 30 Hz is a shard of two hundred emptied in under two seconds of wall clock,
        // which is faster than a real grace and slow enough that the fleet is never asked to admit a
        // crowd in one tick.
        fleet.DrainSome(draining, most: 4);

        if (draining.Population > 0) {
            Waited++;

            return Upgraded;
        }

        draining.Restart(version);
        Upgraded++;
        draining = null;

        return Upgraded;
    }
}
