// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Live.Persistence;
using Vixen.Live.Transfer;
using Vixen.Net.Transport;
using Vixen.Net.Sessions;
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
///         ⚠ <b>Every traveller holds a real <see cref="NetworkSession" />, and during the overlap
///         they hold two.</b> That is what makes the loss profile mean anything: an earlier version
///         drove <c>SourceTransfer</c> and <c>ClientTransfer</c> directly and never opened a session,
///         so the only traffic on the wire was a session with no peers — a profile changed nothing,
///         and an assertion under one would have passed whatever the network did.
///     </para>
///     <para>
///         ⚠ <b>And residency is the realm's own answer rather than the harness's bookkeeping.</b>
///         <see cref="Realm.Residents" /> is <c>PlayerAdmission</c> minus whoever the
///         <c>TransferBoard</c> still holds as <c>Reserved</c> or <c>Dormant</c>. A
///         <c>HashSet&lt;PlayerKey&gt;</c> the fleet remembered to update is a test of the fleet; this
///         is a test of the realms.
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
    readonly Dictionary<PlayerKey, Realm> evicting = [];
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

    /// <summary>How long a client gets to finish the handshake.</summary>
    /// <remarks>
    ///     ⚠ <b>Thirty seconds, and it is not the same knob as <see cref="Deadlines" />.</b> This
    ///     clock advances with the pump, so on <c>Awful</c> a handshake that takes four hundred steps
    ///     takes six and a half seconds of fleet time — and a five-second timeout would turn "the
    ///     network is bad" into "the ticket was refused", which is a different test passing for a
    ///     reason nobody intended.
    /// </remarks>
    public TimeSpan Handshake { get; init; } = TimeSpan.FromSeconds(30);

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

    /// <summary>The currency every traveller carries.</summary>
    public static AssetId Gold { get; } = new("currency/gold");

    /// <summary>Adds a realm.</summary>
    /// <param name="map">Which map it simulates.</param>
    /// <returns>It.</returns>
    public Realm AddRealm(string map) {
        // ⚠ A thousand streams apart per realm, so no client of one ever shares a random stream with
        // a client of another. One shared stream would drop the same packet on both sides of the same
        // step, which is a synchronised outage rather than a network.
        var realm = new Realm(map, signer, () => Now, realms.Count, Deadlines, Wire, Seed + ((ulong)realms.Count * 1_000), Handshake);

        realms.Add(realm);

        return realm;
    }

    /// <summary>Puts a character into the world, on a realm, with something to their name.</summary>
    /// <param name="home">Which realm.</param>
    /// <param name="gold">How much they start with.</param>
    /// <returns>Them.</returns>
    /// <exception cref="InvalidOperationException">The handshake never completed.</exception>
    /// <remarks>
    ///     ⚠ <b>This pumps, because admission is a handshake and not a method call.</b> A traveller
    ///     that appeared in a realm's roster without one would be the harness's opinion rather than
    ///     the realm's.
    /// </remarks>
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

        var traveller = new Traveller(key, home, 1) {
            Session = home.Connect(Ticket(key, home, epoch: 1))
        };

        travellers[key] = traveller;

        // Generous, and deliberately not a tuned number: the point is that they get in, not how fast.
        for (var attempt = 0; attempt < 2_000 && home.Joined(key) is null; attempt++) {
            Pump();
        }

        if (home.Joined(key) is null) {
            throw new InvalidOperationException($"{key} never completed the handshake with {home.Map}.");
        }

        return traveller;
    }

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
        var ticket = Ticket(traveller.Key, target, epoch);

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
            target.Population,
            target.Spec.Capacity,
            target.Host.State == ShardState.Draining
        );

        if (refusal != ReservationRefusal.None) {
            transfer.Stop(TransferAbort.NoShard, Now);

            return false;
        }

        // A pending eviction for this target is this player's own earlier attempt, and it must not
        // outlive the attempt that replaces it — the reservation is replaced at the higher epoch, so
        // the session about to be admitted is the new one.
        evicting.Remove(traveller.Key);

        transfer.TargetReady(Now);
        traveller.InFlight = new(target, epoch, ticket);
        traveller.Client.Prepared(prepare, target.Spec.Key.Version);

        // t3 begins here: the client opens its *second* session, to the target, carrying the ticket
        // the orchestrator just minted. Everything after this is a handshake over the same wire the
        // first one crossed.
        traveller.Incoming = target.Connect(ticket);

        return true;
    }

    /// <summary>Gives up on a transfer the way a client that closed its laptop does.</summary>
    /// <param name="traveller">Who.</param>
    /// <returns>Whether there was one to give up on.</returns>
    /// <remarks>
    ///     ⚠ <b>It has to take the second session down with it.</b> A client that vanished mid-overlap
    ///     leaves a session admitted on the target, and a target that kept it would eventually hold a
    ///     live player the source is also simulating — the exact duplicate the overlap exists to
    ///     prevent, arrived at through the abort path rather than the happy one.
    /// </remarks>
    public bool GiveUp(Traveller traveller) {
        ArgumentNullException.ThrowIfNull(traveller);

        if (traveller.InFlight is not { } flight) {
            return false;
        }

        if (traveller.Where.Transfers.TryGet(traveller.Key, out var transfer)) {
            transfer?.Stop(TransferAbort.PlayerGone, Now);
        }

        Abandon(traveller, flight);
        Aborted++;

        return true;
    }

    /// <summary>Drives every realm, every session and every transfer forward one step.</summary>
    /// <param name="rounds">How many.</param>
    public void Pump(int rounds = 1) {
        for (var round = 0; round < rounds; round++) {
            Now += Step;

            foreach (var realm in realms) {
                realm.Pump(Step);
            }

            Evict();

            // Both sessions, because during an overlap a traveller has two and the second one is the
            // one whose handshake the transfer is waiting on.
            foreach (var traveller in travellers.Values) {
                traveller.Session?.Update(Step);
                traveller.Incoming?.Update(Step);

                // ⚠ Every step, unconditionally. A prediction loop that only ran outside transfers
                // would make "one reset per transfer" a statement about an empty history.
                traveller.Prediction.Step();
            }

            foreach (var traveller in travellers.Values.ToList()) {
                Advance(traveller);
            }
        }
    }

    /// <summary>Pumps until nothing is in flight.</summary>
    /// <param name="most">How many steps to give it.</param>
    /// <returns>Whether the fleet settled.</returns>
    /// <remarks>
    ///     ⚠ <b>An assertion about the fence has to come after this, and it did not used to have
    ///     to.</b> The lease moves at <c>Committing</c> and the traveller's own epoch moves at
    ///     <c>Arrive</c>, so a loop that stops between the two leaves a fence one ahead of the player
    ///     — correctly, and briefly. With real handshakes a transfer spans enough steps that stopping
    ///     inside one is ordinary rather than rare, which is a fair description of a real fleet too.
    /// </remarks>
    public bool Settle(int most = 2_000) {
        for (var step = 0; step < most; step++) {
            if (travellers.Values.All(traveller => traveller.InFlight is null)
                && realms.TrueForAll(realm => realm.Transfers.InFlight == 0)) {
                return true;
            }

            Pump();
        }

        return false;
    }

    /// <summary>Whether a realm is simulating somebody — its own answer, not the harness's.</summary>
    /// <param name="realm">Which.</param>
    /// <param name="player">Who.</param>
    /// <returns>Whether they are live there.</returns>
    public static bool Simulates(Realm realm, PlayerKey player) {
        ArgumentNullException.ThrowIfNull(realm);

        return realm.Simulates(player);
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

    public void Dispose() {
        foreach (var traveller in travellers.Values) {
            traveller.Session?.Dispose();
            traveller.Incoming?.Dispose();
            traveller.Prediction.Dispose();
        }

        foreach (var realm in realms) {
            realm.Dispose();
        }

        signer.Dispose();
        Store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    TransferTicket Ticket(PlayerKey player, Realm realm, long epoch) =>
        signer.Sign(
            new() {
                Player = player,
                Target = realm.Spec.Shard,
                Endpoint = realm.Spec.Endpoint,
                LeaseEpoch = epoch,
                Expires = Now + TimeSpan.FromMinutes(5)
            }
        );

    /// <summary>One step of one traveller's transfer, wherever it has got to.</summary>
    void Advance(Traveller traveller) {
        var source = traveller.Where;

        if (!source.Transfers.TryGet(traveller.Key, out var transfer) || transfer is null) {
            // It ended between pumps. Either the realm committed it — in which case `Arrive` already
            // moved them and cleared the flight — or a deadline ran out, and they are still here with
            // a second session that has to go.
            if (traveller.InFlight is { } lapsed) {
                Abandon(traveller, lapsed);
                Aborted++;
            }

            return;
        }

        if (traveller.InFlight is not { } flight) {
            return;
        }

        switch (transfer.Phase) {
            case TransferPhase.Overlapping:
                // t3: the client's second session finishes its handshake and loads. A realm cannot do
                // this for it, and the target's own admission is what says it happened.
                if (traveller.Client.State == ClientTransferState.Connecting) {
                    if (flight.Target.Joined(traveller.Key) is not null) {
                        traveller.Client.Connected(source.Tick);

                        // Admitted and dormant: a session here, receiving interest so the map can
                        // load, and the source still simulating them.
                        flight.Target.Transfers.Arriving.Arrived(traveller.Key, flight.Epoch);
                    }
                } else if (traveller.Client.State == ClientTransferState.Loading) {
                    traveller.Client.Loaded();

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

    /// <summary>The instant the player stops being the source's and starts being the target's.</summary>
    /// <remarks>
    ///     ⚠ <b>Both sides move inside one call, with no pump between them.</b> The source releases and
    ///     the target's arrival goes live in the same step, so there is no instant at which the oracle
    ///     could observe them on two realms — and if the release were deferred to the session
    ///     noticing a closed socket, there would be one, lasting however long the network took.
    /// </remarks>
    void Arrive(Traveller traveller, Flight flight) {
        var source = traveller.Where;

        if (source.Joined(traveller.Key) is { } player) {
            source.Host.Admission.Release(player.Id);
        }

        traveller.Session?.Dispose();
        traveller.Session = traveller.Incoming;
        traveller.Incoming = null;

        // Off the board: they are not arriving any more, they are here.
        flight.Target.Transfers.Arriving.Release(traveller.Key);

        traveller.Client.Committed(new(source.Tick, flight.Target.Spec.Shard));
        traveller.Client.Settle();

        // Doc 27 § Intra-map seams: the history is cleared, because the state to replay from belongs
        // to a simulation that no longer owns this player. Once, here, and nowhere else — an abort
        // costs nothing, which is the half of the claim worth testing.
        traveller.Prediction.Reset();

        source.Transfers.Metrics.RecordResets(traveller.Client.PredictionResets - traveller.CountedResets);
        traveller.CountedResets = traveller.Client.PredictionResets;

        traveller.Where = flight.Target;
        traveller.Epoch = flight.Epoch;
        traveller.InFlight = null;
        traveller.Arrivals++;

        Committed++;
    }

    /// <summary>Takes a half-finished transfer's second session down.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The reservation is left on the board to lapse rather than released.</b> Deleting it
    ///         would turn a late admission — the handshake this client gave up on, finishing from
    ///         packets already on the wire — into what looks like a fresh login, and the target would
    ///         simulate somebody the source still holds. Left there, it stays not-<c>Live</c> and the
    ///         target correctly declines to believe in them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the eviction is queued rather than done once</b>, because at the moment a
    ///         client gives up its handshake has usually not landed yet: releasing an admission that
    ///         does not exist yet does nothing, and the session arrives a few steps later. A real
    ///         realm closes that door on the lapse — <c>TransferBoard.Sweep</c> returns exactly who
    ///         to kick and <c>RealmTransfers.Step</c> currently discards the list, which is worth
    ///         fixing in the realm rather than in a harness.
    ///     </para>
    /// </remarks>
    void Abandon(Traveller traveller, Flight flight) {
        evicting[traveller.Key] = flight.Target;

        traveller.Incoming?.Dispose();
        traveller.Incoming = null;
        traveller.InFlight = null;
        traveller.Client.Abandon();

        Evict();
    }

    /// <summary>Kicks the sessions that belong to transfers nobody finished.</summary>
    void Evict() {
        foreach (var (key, realm) in evicting.ToList()) {
            if (realm.Joined(key) is { } player) {
                realm.Host.Admission.Release(player.Id);
                evicting.Remove(key);
            }
        }
    }

    /// <summary>One realm, its session and the players it believes in.</summary>
    internal sealed class Realm : IDisposable {
        readonly LocalNetwork network = new();
        readonly NetworkSimulationProfile wire;
        readonly ulong seed;
        readonly TimeSpan handshake;
        readonly List<NetworkSession> clients = [];

        int streams;

        public Realm(
            string map,
            TransferTicketSigner signer,
            Func<DateTimeOffset> now,
            int index,
            TransferDeadlines deadlines,
            NetworkSimulationProfile wire,
            ulong seed,
            TimeSpan handshake
        ) {
            Map = map;
            this.wire = wire;
            this.seed = seed;
            this.handshake = handshake;

            Spec = new() {
                Shard = ShardId.New(),
                Key = new(map, "eu", new("0.1.0", 0xC0FFEE)),
                Endpoint = new("127.0.0.1", 7777 + index),
                Capacity = new(100, 120),
                TickRate = 30
            };

            Host = new(
                Spec,
                // ⚠ Stream zero is the realm's. Every client gets its own — see Connect. One shared
                // stream would drop the same packet on both sides of the same step, which is a
                // synchronised outage rather than a network.
                admission => new(Wrap(0), Options(), admission, ownsTransport: true),
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

        /// <summary>Players this realm believes in that no test ever connected.</summary>
        /// <remarks>
        ///     ⚠ <b>Only for filling a shard to its cap.</b> A hundred and twenty handshakes to prove
        ///     that the hundred and twenty-first is refused is a slow way to test arithmetic, and the
        ///     capacity check the orchestrator makes is over a *number* rather than over a roster. They
        ///     are counted by <see cref="Population" /> and are deliberately invisible to
        ///     <see cref="Residents" />, which is what the oracle reads.
        /// </remarks>
        public HashSet<PlayerKey> Ghosts { get; } = [];

        /// <summary>What the orchestrator's capacity check sees.</summary>
        public int Population => Host.Population + Ghosts.Count;

        /// <summary>Who this realm is simulating, which is its own answer and not the harness's.</summary>
        /// <remarks>
        ///     ⚠ <b><c>PlayerAdmission</c> minus the board's <c>Reserved</c> and <c>Dormant</c>.</b>
        ///     A player mid-overlap has a session here and is not being simulated here — dormancy is
        ///     precisely what stops them existing twice — so counting admission alone would report
        ///     every transfer in flight as a duplicate.
        /// </remarks>
        public IEnumerable<PlayerKey> Residents =>
            Host.Admission.Players.Select(player => player.Key).Where(Simulates);

        /// <summary>Its own clock, in ticks.</summary>
        public long Tick { get; private set; }

        /// <summary>Somebody this realm has admitted <em>and</em> given a session id to.</summary>
        /// <param name="player">Who.</param>
        /// <returns>Them, or null.</returns>
        /// <remarks>
        ///     ⚠ <b>Admitted is not joined, and on a clean wire the gap is invisible.</b>
        ///     <c>Authenticate</c> puts them in the roster; <c>Bind</c> gives them the session's
        ///     <c>PlayerId</c> when the join lands. On a lossy wire those are separated by however
        ///     long the network takes — and everything that releases a player releases them
        ///     <em>by id</em>, so treating an unbound admission as a resident produces one that can
        ///     never be released and is resident on that realm for ever.
        /// </remarks>
        public RealmPlayer? Joined(PlayerKey player) =>
            Host.Admission.TryGet(player, out var found) && found is { } bound && bound.Id.Value != 0
                ? bound
                : null;

        /// <summary>Whether this realm is simulating somebody.</summary>
        /// <param name="player">Who.</param>
        /// <returns>Whether they are live here.</returns>
        public bool Simulates(PlayerKey player) {
            if (Joined(player) is null) {
                return false;
            }

            foreach (var arrival in Transfers.Arriving.Arrivals) {
                if (arrival.Player == player) {
                    // ⚠ Live, and not merely "not dormant". An arrival that lapsed is somebody whose
                    // transfer died with a session still open here, and treating that as a resident
                    // is how the abort path produces the duplicate the happy path cannot.
                    return arrival.State == ArrivalState.Live;
                }
            }

            // No record at all is a fresh login rather than an arrival, which is the one case where
            // an admitted session is simulated on sight.
            return true;
        }

        /// <summary>Connects a client presenting a ticket.</summary>
        /// <param name="ticket">What it presents.</param>
        /// <returns>The client's session.</returns>
        public NetworkSession Connect(TransferTicket ticket) {
            var session = new NetworkSession(
                Wrap(++streams),
                Options() with { AuthenticationPayload = Encoding.UTF8.GetBytes(ticket.Encode()) },
                ownsTransport: true
            );

            clients.Add(session);
            session.StartClient();

            return session;
        }

        public void Pump(TimeSpan step) {
            Tick++;
            Host.Update(step);
        }

        public void Dispose() {
            Host.Session.Dispose();
            Host.Dispose();
        }

        /// <summary>Puts the simulation on a transport, or hands the transport straight back.</summary>
        /// <remarks>
        ///     ⚠ <b><c>Perfect</c> is not wrapped at all rather than wrapped in a no-op.</b> The
        ///     simulation queues and re-times every payload even when it drops none, so wrapping the
        ///     default would change the timing of every existing test in this project for no reason.
        /// </remarks>
        ITransport Wrap(int stream) =>
            wire == NetworkSimulationProfile.Perfect
                ? new LocalTransport(network)
                : new NetworkSimulation(new LocalTransport(network), wire, seed + (ulong)stream);

        SessionOptions Options() =>
            new() {
                MaxPlayers = Spec.Capacity.HardCap,
                ContentHash = Spec.Key.Version.Content,
                AuthenticationTimeout = handshake
            };
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

        /// <summary>The session they are playing on.</summary>
        public NetworkSession? Session { get; set; }

        /// <summary>The second one, open to the target for the length of the overlap.</summary>
        public NetworkSession? Incoming { get; set; }

        public ClientTransfer Client { get; } = new();

        /// <summary>What they are guessing about their own movement, and what a transfer costs it.</summary>
        public TravellerPrediction Prediction { get; } = new(key);

        /// <summary>How many of their resets have already been reported to a realm's metrics.</summary>
        public int CountedResets { get; set; }

        /// <summary>How many realms they have arrived at.</summary>
        public int Arrivals { get; set; }
    }
}
