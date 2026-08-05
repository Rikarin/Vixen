// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Live.Persistence;
using Vixen.Live.Transfer;
using Vixen.Net.Sessions;
using Vixen.Net.Transport;
using Vixen.Net.Transport.Local;

namespace Vixen.Live.Realms.Tests;

/// <summary>Several realms in one process, and the orchestrator between them.</summary>
/// <remarks>
///     <para>
///         Doc 27 § Testing: <i>"end to end over <c>Vixen.Net.Transport.Local</c> — three realms in
///         one process, players walking a loop between them; assert no duplicate spawn, no lost
///         entity, no state divergence"</i>. This is that, and the handshake each client goes through
///         is byte for byte the one it goes through over UDP.
///     </para>
///     <para>
///         ⚠ <b>The orchestrator here is a dozen lines and that is the point.</b> What is under test
///         is the protocol, the fence and the realms — <c>IMapGrain</c>'s scoring has its own 45 000
///         randomised fleets, and putting a silo in this loop would buy nothing and cost the ability
///         to run it on every push.
///     </para>
/// </remarks>
sealed class TransferFleet : IDisposable {
    static readonly byte[] Key = Encoding.UTF8.GetBytes("a-test-cluster-key-of-32-bytes!!!!!!");
    static readonly TimeSpan Step = TimeSpan.FromMilliseconds(16);

    readonly List<Realm> realms = [];
    readonly Dictionary<PlayerKey, Traveller> travellers = [];
    readonly TransferTicketSigner signer = new(Key);

    /// <summary>The durable half. The fence lives here, and so does the conservation oracle.</summary>
    public MemoryPersistence Store { get; } = new();

    /// <summary>What the wire does to the packets. Perfect unless a test says otherwise.</summary>
    /// <remarks>
    ///     ⚠ <b>doc 27 § Testing specifies this leg "with <c>NetworkSimulation</c>", and the reason is
    ///     that a clean wire cannot fail the interesting way.</b> Every abort path in the protocol is
    ///     driven by a deadline, and a deadline only means anything when something can be late — so an
    ///     oracle on a perfect transport proves the state machines agree with each other and nothing
    ///     about whether they survive a network.
    /// </remarks>
    public NetworkSimulationProfile Wire { get; init; } = NetworkSimulationProfile.Perfect;

    /// <summary>The simulation's seed, so a failure is replayable.</summary>
    public ulong Seed { get; init; } = 20260804;

    /// <summary>How long each step of a transfer gets. Short, so a test does not pump for minutes.</summary>
    public TransferDeadlines Deadlines { get; init; } = new() {
        Placing = TimeSpan.FromSeconds(1),
        Preparing = TimeSpan.FromSeconds(1),
        Overlapping = TimeSpan.FromSeconds(2),
        Committing = TimeSpan.FromSeconds(1),
        HandingOff = TimeSpan.FromSeconds(1)
    };

    /// <summary>The clock every realm and every ticket shares.</summary>
    public DateTimeOffset Now { get; private set; } = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The realms.</summary>
    public IReadOnlyList<Realm> Realms => realms;

    /// <summary>Everybody in the fleet.</summary>
    public IReadOnlyCollection<Traveller> Travellers => travellers.Values;

    /// <summary>How many transfers have committed across the whole fleet.</summary>
    public int Committed { get; private set; }

    /// <summary>How many have been abandoned, for any reason.</summary>
    public int Aborted { get; private set; }

    /// <summary>Adds a realm.</summary>
    /// <param name="map">Which map it simulates.</param>
    /// <returns>It.</returns>
    public Realm AddRealm(string map) {
        var realm = new Realm(map, signer, () => Now, realms.Count, Deadlines, Wire, Seed + (ulong)realms.Count);

        realms.Add(realm);

        return realm;
    }

    /// <summary>Puts a character into the world, on a realm, with something to their name.</summary>
    /// <param name="home">Which realm.</param>
    /// <param name="gold">How much they start with.</param>
    /// <returns>Them.</returns>
    public Traveller Admit(Realm home, long gold) {
        var key = new PlayerKey(Guid.NewGuid(), Guid.NewGuid());

        Store.Players.CreateAsync(
                new(key, key.Character.ToString("N"), Now, Now, "eu", home.Map, 1, ReadOnlyMemory<byte>.Empty),
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult();

        Store.Ledger.AppendAsync(
                LedgerIntent.Transfer(
                    new(key, "seed", "gold"),
                    1,
                    Now,
                    LedgerAccount.Of(LedgerAccount.Loot),
                    LedgerAccount.Of(key),
                    Gold,
                    gold
                ),
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult();

        var traveller = new Traveller(key, home, 1);

        travellers[key] = traveller;
        home.Residents.Add(key);

        return traveller;
    }

    /// <summary>The currency every traveller carries.</summary>
    public static AssetId Gold { get; } = new("currency/gold");

    /// <summary>Starts moving somebody to another realm.</summary>
    /// <param name="traveller">Who.</param>
    /// <param name="target">Where.</param>
    /// <returns>Whether one was started.</returns>
    /// <remarks>
    ///     The orchestrator's whole job, inlined: place, mint, and tell the target to expect them.
    ///     Anything it refuses leaves the traveller exactly where they were.
    /// </remarks>
    public bool Send(Traveller traveller, Realm target) {
        var source = traveller.Where;

        if (source == target || source.Transfers.IsLeaving(traveller.Key) || traveller.InFlight is not null) {
            return false;
        }

        var transfer = source.Transfers.Begin(traveller.Key, target.Map, Now, "the loop");
        var epoch = traveller.Epoch + 1;

        var ticket = signer.Sign(
            new() {
                Player = traveller.Key,
                Target = target.Spec.Shard,
                Endpoint = target.Spec.Endpoint,
                LeaseEpoch = epoch,
                Expires = Now + TimeSpan.FromMinutes(2)
            }
        );

        var prepare = new TransferPrepare(
            ticket.Encode(),
            target.Spec.Endpoint,
            target.Spec.Shard,
            target.Spec.Key.Version,
            target.Tick
        );

        transfer.Placed(target.Spec.Shard, prepare, epoch, Now);

        var refusal = target.Transfers.Expect(
            ticket,
            epoch,
            Now,
            target.Residents.Count,
            target.Spec.Capacity,
            target.Host.State == ShardState.Draining
        );

        if (refusal != ReservationRefusal.None) {
            transfer.Stop(TransferAbort.NoShard, Now);

            return false;
        }

        transfer.TargetReady(Now);
        traveller.InFlight = new(target, epoch, ticket);
        traveller.Client.Prepared(prepare, target.Spec.Key.Version);

        return true;
    }

    /// <summary>Drives every realm, every client and every transfer forward one step.</summary>
    /// <param name="rounds">How many.</param>
    public void Pump(int rounds = 1) {
        for (var round = 0; round < rounds; round++) {
            Now += Step;

            foreach (var realm in realms) {
                realm.Pump(Step);
            }

            foreach (var traveller in travellers.Values.ToList()) {
                Advance(traveller);
            }
        }
    }

    /// <summary>The whole point: what every account holds, summed.</summary>
    /// <param name="asset">Of what.</param>
    /// <returns>Zero, always, or the design is broken.</returns>
    public long TotalInWorld(AssetId asset) {
        var total = 0L;

        foreach (var row in Store.Ledger.HistoryAsync(new() { Asset = asset, Limit = int.MaxValue }, CancellationToken.None)
                     .GetAwaiter()
                     .GetResult()) {
            total += row.Delta;
        }

        return total;
    }

    /// <summary>What a traveller actually holds.</summary>
    /// <param name="traveller">Who.</param>
    /// <returns>Their balance.</returns>
    public long Holding(Traveller traveller) =>
        Store.Ledger.BalanceAsync(LedgerAccount.Of(traveller.Key), Gold, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    public void Dispose() {
        foreach (var realm in realms) {
            realm.Dispose();
        }

        signer.Dispose();
        Store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>One step of one traveller's transfer, wherever it has got to.</summary>
    void Advance(Traveller traveller) {
        var source = traveller.Where;

        if (!source.Transfers.TryGet(traveller.Key, out var transfer) || transfer is null) {
            // It ended between pumps. Either the realm committed it — in which case `Arrive` already
            // moved them — or it aborted, and they are still here.
            traveller.InFlight = null;
            traveller.Client.Abandon();

            return;
        }

        var flight = traveller.InFlight;

        if (flight is null) {
            return;
        }

        switch (transfer.Phase) {
            case TransferPhase.Overlapping:
                // t3: the client opens its second session and loads. A realm cannot do this for it.
                if (traveller.Client.State == ClientTransferState.Connecting) {
                    traveller.Client.Connected(source.Tick);
                } else if (traveller.Client.State == ClientTransferState.Loading) {
                    traveller.Client.Loaded();
                    flight.Target.Transfers.Arriving.Arrived(traveller.Key, flight.Epoch);

                    // t4 is the CLIENT reporting readiness, which is the only actor that knows.
                    transfer.ClientReady(Now, source.Tick + 2);
                }

                break;

            case TransferPhase.Committing: {
                // The atomic moment: the lease moves. Everything durable is fenced on this.
                var granted = Take(traveller, flight.Epoch);

                if (!transfer.LeaseTaken(granted, Now)) {
                    return;
                }

                break;
            }

            case TransferPhase.HandingOff:
                // t5: the payload crosses, the target applies it, and only then does the source commit.
                flight.Target.Transfers.Arriving.Woke(traveller.Key, flight.Epoch);
                transfer.HandoffAcknowledged(Now);

                Arrive(traveller, flight);

                break;
        }
    }

    /// <summary>Moves the lease, and records what the fence now stands at.</summary>
    long Take(Traveller traveller, long epoch) {
        var record = Store.Players.ReadAsync(traveller.Key, CancellationToken.None).GetAwaiter().GetResult()!;

        var outcome = Store.Players
            .WriteAsync(record with { LeaseEpoch = epoch, LastSeen = Now }, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        // A refused write means somebody else has already taken it, and the transfer aborts rather
        // than continuing into a handoff whose durable half can never land.
        return outcome == WriteOutcome.Written ? epoch : record.LeaseEpoch;
    }

    void Arrive(Traveller traveller, Flight flight) {
        traveller.Where.Residents.Remove(traveller.Key);
        flight.Target.Residents.Add(traveller.Key);

        traveller.Client.Committed(new(traveller.Where.Tick, flight.Target.Spec.Shard));
        traveller.Client.Settle();

        traveller.Where = flight.Target;
        traveller.Epoch = flight.Epoch;
        traveller.InFlight = null;
        traveller.Arrivals++;

        Committed++;
    }

    /// <summary>Spends under a named epoch, which is how a late write from an old realm is injected.</summary>
    /// <param name="traveller">Whose.</param>
    /// <param name="epoch">Which epoch the writer believes it holds.</param>
    /// <param name="amount">How much.</param>
    /// <returns>What the ledger made of it.</returns>
    public LedgerVerdict SpendAt(Traveller traveller, long epoch, long amount) =>
        Store.Ledger.AppendAsync(
                LedgerIntent.Transfer(
                    new(traveller.Key, "vendor", $"{epoch}:{amount}"),
                    epoch,
                    Now,
                    LedgerAccount.Of(traveller.Key),
                    LedgerAccount.Of(LedgerAccount.Vendor),
                    Gold,
                    amount
                ),
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult()
            .Verdict;

    /// <summary>The epoch a traveller's row will accept a write at or above.</summary>
    /// <param name="traveller">Whose.</param>
    /// <returns>The fence.</returns>
    public long Fence(Traveller traveller) =>
        Store.Players.FenceAsync(traveller.Key, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Notes a transfer that did not happen, for the assertions.</summary>
    public void NoteAbort() => Aborted++;

    /// <summary>One realm, its session and the players it believes in.</summary>
    internal sealed class Realm : IDisposable {
        readonly LocalNetwork network = new();

        public Realm(
            string map,
            TransferTicketSigner signer,
            Func<DateTimeOffset> now,
            int index,
            TransferDeadlines deadlines,
            NetworkSimulationProfile wire,
            ulong seed
        ) {
            Map = map;

            Spec = new() {
                Shard = ShardId.New(),
                Key = new(map, "eu", new("0.1.0", 0xC0FFEE)),
                Endpoint = new("127.0.0.1", 7777 + index),
                Capacity = new(100, 120),
                TickRate = 30
            };

            Host = new(
                Spec,
                admission => new(
                    // ⚠ Each realm gets its own seed. One shared stream would make every realm drop
                    // the same packet on the same step, which is a synchronised outage rather than a
                    // network — and the fleet would be tested against a failure mode that does not
                    // happen.
                    new NetworkSimulation(new LocalTransport(network), wire, seed),
                    new() {
                        MaxPlayers = Spec.Capacity.HardCap,
                        ContentHash = Spec.Key.Version.Content,
                        AuthenticationTimeout = TimeSpan.FromSeconds(5)
                    },
                    admission,
                    ownsTransport: true
                ),
                signer,
                new() {
                    Output = _ => { },
                    Now = now,
                    HeartbeatInterval = TimeSpan.FromSeconds(2),
                    Transfers = deadlines
                }
            );

            Host.Start();
            Host.Map.Ready(new(1));
        }

        public string Map { get; }

        public RealmSpec Spec { get; }

        public RealmHost Host { get; }

        public RealmTransfers Transfers => Host.Transfers;

        /// <summary>Who this realm believes it is simulating.</summary>
        public HashSet<PlayerKey> Residents { get; } = [];

        /// <summary>Its own clock, in ticks.</summary>
        public long Tick { get; private set; }

        public void Pump(TimeSpan step) {
            Tick++;
            Host.Update(step);
        }

        public void Dispose() {
            Host.Session.Dispose();
            Host.Dispose();
        }
    }

    /// <summary>A transfer in flight, from the fleet's point of view.</summary>
    /// <param name="Target">Where they are going.</param>
    /// <param name="Epoch">The epoch the target will take.</param>
    /// <param name="Ticket">What they carry.</param>
    internal sealed record Flight(Realm Target, long Epoch, TransferTicket Ticket);

    /// <summary>A character walking the loop.</summary>
    internal sealed class Traveller(PlayerKey key, Realm where, long epoch) {
        public PlayerKey Key { get; } = key;

        public Realm Where { get; set; } = where;

        public long Epoch { get; set; } = epoch;

        public Flight? InFlight { get; set; }

        public ClientTransfer Client { get; } = new();

        /// <summary>How many realms they have arrived at.</summary>
        public int Arrivals { get; set; }
    }
}
