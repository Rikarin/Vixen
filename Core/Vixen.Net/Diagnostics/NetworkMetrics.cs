// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.Metrics;
using Vixen.Net.Replication;
using Vixen.Net.Rpc;
using Vixen.Net.Sessions;
using Vixen.Net.Transport;

namespace Vixen.Net.Diagnostics;

/// <summary>What a dedicated server is asked how it is doing, published where anything can read it.</summary>
/// <remarks>
///     <para>
///         <b>Instrumented with <c>System.Diagnostics.Metrics</c>, which is not a third choice
///         alongside OpenTelemetry and Prometheus — it <i>is</i> OpenTelemetry's metrics API in
///         .NET.</b> The BCL types are the specification's API surface; the SDK is only needed to
///         export. So this file takes no dependency at all, and a server that already has an
///         OpenTelemetry pipeline gets every number below by adding <c>"Vixen.Net"</c> to its meter
///         list. <c>Vixen.Net.Telemetry</c> is there for a server that does not, and is a separate
///         package for exactly that reason: the runtime library must not drag an exporter into a
///         game that never asked for one.
///     </para>
///     <para>
///         <b>The game publishes and the meter reads, rather than the meter reaching into the
///         game.</b> Observable instruments are called back on the SDK's collection thread, and the
///         things worth reporting live in a session, a replication server and a router that are all
///         single-threaded frame code — so a callback that walked <c>Session.Players</c> would
///         eventually walk it while a player was joining, which is an exception on a background
///         thread in a process whose whole point is to stay up. <see cref="Sample" /> is called once
///         a tick from the loop that owns those objects and copies what it finds into plain fields;
///         the callbacks read the fields. That is thread-safe by construction rather than by
///         locking, and it costs a few dozen bytes of copying a tick.
///     </para>
///     <para>
///         <b>Counters are cumulative and gauges are current, which is the contract the collector
///         relies on.</b> Everything sourced from a running total is an <c>ObservableCounter</c>, so
///         a scrape that is missed loses resolution rather than data — the next one still carries
///         the whole. Rates are the collector's job and deliberately not computed here; a metric
///         that has already been differenced cannot be re-aggregated across three servers.
///     </para>
/// </remarks>
public sealed class NetworkMetrics : IDisposable {
    /// <summary>The meter's name, which is what a collector is configured with.</summary>
    public const string MeterName = "Vixen.Net";

    readonly Meter meter;
    readonly Histogram<double> tickDuration;
    readonly Histogram<int> snapshotBytes;

    /// <summary>The high-water mark of every link a peer is still reporting on, keyed by player.</summary>
    /// <remarks>
    ///     ⚠ <b>Key zero is this session's own report and not a player</b> — <c>PlayerId.None</c> is
    ///     zero and no player ever has it, so a client's <c>NetworkSession.ObservedOutbound</c> sits
    ///     beside the server's per-player ones without colliding with either. A host has both, which
    ///     is two real links in two directions and is exactly what <c>ITransport.Loss</c> already
    ///     adds up for the same process.
    /// </remarks>
    readonly Dictionary<uint, PeerLink> peers = [];

    Reading reading;
    long retiredPeerExpected;
    long retiredPeerMissing;
    int stamp;

    /// <summary>Creates the meter and registers every instrument.</summary>
    /// <param name="version">
    ///     The build's version, reported as the meter's. A collector uses it to tell two versions of
    ///     the same fleet apart, which is the question a rollout is asking.
    /// </param>
    public NetworkMetrics(string? version = null) {
        meter = new(MeterName, version);

        tickDuration = meter.CreateHistogram<double>(
            "vixen.net.tick.duration",
            unit: "s",
            description: "How long a server tick took, from the loop that ran it."
        );

        snapshotBytes = meter.CreateHistogram<int>(
            "vixen.net.snapshot.size",
            unit: "By",
            description: "How large each snapshot written to a connection was."
        );

        meter.CreateObservableGauge(
            "vixen.net.players",
            () => reading.ConnectedPlayers,
            description: "Players currently connected."
        );

        meter.CreateObservableGauge(
            "vixen.net.players.awaiting_reconnect",
            () => reading.AwaitingPlayers,
            description: "Players inside their reconnect window, holding a seat but not connected."
        );

        // A long, because a uint is not a type an instrument may be — and widening rather than
        // casting keeps the wrap visible: a tick is modular and goes back to zero after about two
        // years of uptime, which a chart should show as a chart of a tick doing that.
        meter.CreateObservableGauge(
            "vixen.net.tick",
            () => reading.Tick,
            description: "The tick the session is on. Wraps, because a tick does."
        );

        // A gauge of the mean rather than a histogram of the samples, because the samples are
        // already smoothed: RoundTripEstimator is an RFC 6298 filter, and putting a filtered value
        // in a histogram would report the filter's distribution rather than the network's. The
        // spread that matters is published beside it as the jitter.
        meter.CreateObservableGauge(
            "vixen.net.rtt.mean",
            () => reading.MeanRoundTripSeconds,
            unit: "s",
            description: "Mean round trip across connected players, as the estimator smooths it."
        );

        meter.CreateObservableGauge(
            "vixen.net.rtt.worst",
            () => reading.WorstRoundTripSeconds,
            unit: "s",
            description: "The worst round trip any connected player has — the one somebody is complaining about."
        );

        meter.CreateObservableGauge(
            "vixen.net.jitter.worst",
            () => reading.WorstJitterSeconds,
            unit: "s",
            description: "The worst jitter any connected player has, which is what sizes their interpolation buffer."
        );

        meter.CreateObservableCounter(
            "vixen.net.bandwidth",
            () => reading.Bytes,
            unit: "By",
            description: "Everything the ledger has accounted for, replication and remote calls together."
        );

        meter.CreateObservableCounter(
            "vixen.net.snapshot.records",
            Records,
            description: "Replicated values sent, by whether they went as a difference or whole."
        );

        meter.CreateObservableCounter(
            "vixen.net.snapshot.suppressed",
            () => reading.Suppressed,
            description: "Values not sent because the same value was already on its way to that connection."
        );

        meter.CreateObservableCounter(
            "vixen.net.rpc.calls",
            Calls,
            description: "Inbound remote calls, by what happened to them."
        );

        // Four counters and no ratio, for this file's own reason: a share is a difference of two of
        // these and a collector can take it, while a share published here could not be re-aggregated
        // across three servers. They are also four counters rather than one instrument tagged by
        // direction, because the two directions do not share a population — the outbound pair counts
        // reliable datagrams this server sent and the inbound pair counts sequences its peers sent
        // it, and a chart that summed them would be adding up two different things.
        meter.CreateObservableCounter(
            "vixen.net.datagrams.sent",
            () => reading.Sent,
            description: "Reliable datagrams sent for the first time — the denominator the retransmit count needs."
        );

        meter.CreateObservableCounter(
            "vixen.net.datagrams.retransmitted",
            () => reading.Retransmitted,
            description: "Datagrams sent again because no acknowledgement came — a consequence of loss, not a count of it."
        );

        meter.CreateObservableCounter(
            "vixen.net.datagrams.expected",
            () => reading.Expected,
            description: "Inbound sequences past the acknowledgement window, which either arrived or never will."
        );

        meter.CreateObservableCounter(
            "vixen.net.datagrams.lost",
            () => reading.Missing,
            description: "How many of those never arrived. Over the expected count, this is observed inbound loss."
        );

        // The fifth measurement, and the only honest outbound loss there is: what this end's peers
        // say they did not receive of what it sent them. Two counters and no share, on the same
        // argument as the four above — and it is *not* a fifth field on TransportLoss, for the three
        // reasons LinkReport gives.
        //
        // ⚠ These are not a sum over the live players, and cannot be. NetworkSession clears
        // ObservedOutbound the moment a connection ends, because the totals described that link and
        // a reconnecting player is a new one — so a naive sum falls whenever somebody leaves, which
        // is precisely what a collector reads as a process restart. What is published instead is a
        // running high-water mark per link plus a retired accumulator, the same shape UdpTransport
        // keeps for the same reason.
        meter.CreateObservableCounter(
            "vixen.net.datagrams.peer_expected",
            () => reading.PeerExpected,
            description: "Datagrams this end's peers judged, across every link they have reported on — the denominator observed outbound loss needs."
        );

        meter.CreateObservableCounter(
            "vixen.net.datagrams.peer_lost",
            () => reading.PeerMissing,
            description: "How many of those the peers say never arrived. Over the expected count, this is observed outbound loss."
        );

        // The client's three, and they are the numbers that say a *player* is having a bad time
        // rather than that a server is. A gauge for what it is holding, counters for what went
        // wrong, on the same argument as everything above: a share cannot be re-aggregated.
        meter.CreateObservableGauge(
            "vixen.net.client.entities",
            () => reading.ClientEntities,
            description: "Networked entities this client is holding — what interest management left it."
        );

        meter.CreateObservableCounter(
            "vixen.net.client.snapshots.rejected",
            () => reading.SnapshotsRejected,
            description: "Snapshots that failed to decode. Anything but zero is a peer disagreeing about a wire format."
        );

        meter.CreateObservableCounter(
            "vixen.net.client.snapshots.stale",
            () => reading.SnapshotsStale,
            description: "Snapshots that arrived after a newer one and were dropped — reordering, which is normal in small amounts."
        );
    }

    /// <summary>The session to read players and the tick from.</summary>
    public NetworkSession? Session { get; set; }

    /// <summary>The replication server to read record counts from.</summary>
    public ReplicationServer? Replication { get; set; }

    /// <summary>The client-side applier to read rejected and stale snapshot counts from.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>A client is worth measuring and it is a different question from a server.</b>
    ///         Everything else here answers "is this server healthy"; these three answer "is this
    ///         player being served", which no server-side number can — a snapshot that was budgeted,
    ///         written and sent is a success on the server and a rejection on the client that could
    ///         not decode it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The route out is the same one, and <c>Vixen.Net.Telemetry</c>'s README saying a
    ///         client "wants a different route out than this one" is half right in a way worth
    ///         correcting.</b> Its argument was that a client is not scrapeable — which is true, and
    ///         is why that package pushes rather than being scraped, so the route it already has is
    ///         precisely the one a client needs. What genuinely differs is volume: a hundred thousand
    ///         clients exporting every fifteen seconds is a decision about interval, sampling and
    ///         whether a game is willing to receive that at all, and those are deployment settings
    ///         rather than a second exporter.
    ///     </para>
    /// </remarks>
    public ReplicationClient? Client { get; set; }

    /// <summary>The router to read call outcomes from.</summary>
    public RpcRouter? Rpc { get; set; }

    /// <summary>The ledger to read bandwidth from.</summary>
    public BandwidthLedger? Ledger { get; set; }

    /// <summary>The transport to read loss counters from, when it counts any.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Absent leaves the instruments at zero, and that is a real limitation of a
    ///         cumulative counter rather than a claim about the link.</b> There is no way for an
    ///         <c>ObservableCounter</c> to say "not measured" — the closest thing is not to register
    ///         it, and registering conditionally would mean a fleet whose scrape schema depended on
    ///         which transport each server happened to be running. So a dashboard built on these
    ///         must read them beside <c>vixen.net.datagrams.sent</c>: all four flat at zero on a
    ///         server that is plainly sending is a transport that does not count, not a clean link.
    ///         The editor's network panel does not have this problem and draws no lane at all.
    ///     </para>
    ///     <para>
    ///         The transport rather than four delegates, because <see cref="ITransport.Loss" /> is
    ///         one read that already adds up both halves and every channel.
    ///     </para>
    /// </remarks>
    public ITransport? Transport { get; set; }

    /// <summary>Reads everything attached, once, from the thread that owns it.</summary>
    /// <remarks>
    ///     Call it from the server's tick, next to the replication capture. Calling it more often
    ///     than the collector scrapes is waste and calling it less is stale numbers; once a tick is
    ///     neither and is the only place all four objects are known to be still.
    /// </remarks>
    public void Sample() {
        var next = default(Reading);

        stamp++;
        var reporting = 0;

        if (Session is not null) {
            next.Tick = Session.Tick.Value;
            var total = 0d;

            foreach (var player in Session.Players) {
                // Before the IsConnected branch, though a disconnected player never has one: the
                // property is cleared on the way out, so reading it here rather than below is what
                // makes that a fact this loop does not have to depend on.
                if (player.ObservedOutbound is { } peer) {
                    Observe(player.Id.Value, peer, ref reporting);
                }

                if (!player.IsConnected) {
                    next.AwaitingPlayers++;

                    continue;
                }

                next.ConnectedPlayers++;

                if (!player.RoundTrip.HasSamples) {
                    continue;
                }

                var trip = player.RoundTrip.RoundTrip.TotalSeconds;
                total += trip;
                next.WorstRoundTripSeconds = Math.Max(next.WorstRoundTripSeconds, trip);
                next.WorstJitterSeconds = Math.Max(next.WorstJitterSeconds, player.RoundTrip.Jitter.TotalSeconds);
            }

            next.MeanRoundTripSeconds = next.ConnectedPlayers == 0 ? 0 : total / next.ConnectedPlayers;

            // A client's own report is not on any player record — the server sets NetworkPlayer's
            // and the client sets the session's — so a meter that only walked Players would publish
            // zero on exactly the half of the fleet that is not a server.
            if (Session.ObservedOutbound is { } own) {
                Observe(PlayerId.None.Value, own, ref reporting);
            }
        }

        // Only when one went away, so the common tick is a lookup and a store per reporting link.
        if (reporting != peers.Count) {
            Retire();
        }

        next.PeerExpected = retiredPeerExpected;
        next.PeerMissing = retiredPeerMissing;

        foreach (var link in peers.Values) {
            next.PeerExpected += link.Expected;
            next.PeerMissing += link.Missing;
        }

        if (Client is not null) {
            next.ClientEntities = Client.EntityCount;
            next.SnapshotsRejected = Client.RejectedSnapshotCount;
            next.SnapshotsStale = Client.StaleSnapshotCount;
        }

        if (Replication is not null) {
            next.Deltas = Replication.DeltaRecordCount;
            next.Wholes = Replication.WholeRecordCount;
            next.Suppressed = Replication.SuppressedRecordCount;
        }

        if (Rpc is not null) {
            next.CallsAccepted = Rpc.AcceptedCount;
            next.RefusedByManifest = Rpc.RefusedByManifestCount;
            next.RefusedByDirection = Rpc.RefusedByDirectionCount;
            next.RefusedByObject = Rpc.RefusedByUnknownObjectCount;
            next.RefusedByOwnership = Rpc.RefusedByOwnershipCount;
            next.RefusedByRateLimit = Rpc.RefusedByRateLimitCount;
            next.RefusedByArguments = Rpc.RefusedByArgumentsCount;
        }

        if (Ledger is not null) {
            next.Bytes = Ledger.TotalBits / 8;
        }

        if (Transport?.Loss is { } loss) {
            next.Sent = loss.Sent;
            next.Retransmitted = loss.Retransmitted;
            next.Expected = loss.Expected;
            next.Missing = loss.Missing;
        }

        // One assignment of one struct, so a collection that lands mid-Sample sees the whole of the
        // previous reading rather than half of each. Not an atomic write and it does not need to
        // be: the fields are read one at a time by the callbacks anyway, and what this buys is that
        // they are not read while the loop above is part-way through counting players.
        reading = next;
    }

    /// <summary>Records how long a tick took.</summary>
    /// <param name="elapsed">The time.</param>
    /// <remarks>
    ///     A histogram rather than a gauge, because the question a dedicated server is asked is not
    ///     what a tick costs on average — it is how often one goes over budget, and a mean cannot be
    ///     asked that. The soak in <c>Samples/09</c> makes the same argument for its p99.
    /// </remarks>
    public void RecordTick(TimeSpan elapsed) => tickDuration.Record(elapsed.TotalSeconds);

    /// <summary>Records how large a snapshot was.</summary>
    /// <param name="bytes">Its size.</param>
    public void RecordSnapshot(int bytes) => snapshotBytes.Record(bytes);

    /// <summary>Closes the meter, so nothing collects from it again.</summary>
    public void Dispose() => meter.Dispose();

    /// <summary>Folds one peer's latest report into the link it belongs to.</summary>
    /// <param name="link">Whose link: a player's id, or zero for this session's own.</param>
    /// <param name="report">What the peer said.</param>
    /// <param name="reporting">How many links this pass has seen, incremented for a new one.</param>
    /// <remarks>
    ///     ⚠ <b>A high-water mark rather than the last reading, and the reason is the channel.</b>
    ///     A report travels unreliable — cheap, superseded by the next one, and therefore free to
    ///     arrive after a newer one. Storing whatever came last would let a reordered report walk a
    ///     link's totals backwards, and the counter above would go down. The totals are cumulative
    ///     for the life of a link, so the largest one seen is the true one; and a link that really
    ///     did restart cannot reach here with smaller numbers, because a connection ending clears
    ///     the property and <see cref="Retire" /> takes the old totals out of the live set first.
    /// </remarks>
    void Observe(uint link, LinkReport report, ref int reporting) {
        if (peers.TryGetValue(link, out var held)) {
            if (held.Stamp != stamp) {
                reporting++;
            }

            held.Expected = Math.Max(held.Expected, report.Expected);
            held.Missing = Math.Max(held.Missing, report.Missing);
        } else {
            reporting++;
            held.Expected = report.Expected;
            held.Missing = report.Missing;
        }

        held.Stamp = stamp;
        peers[link] = held;
    }

    /// <summary>Moves every link nothing reported on this pass into the retired totals.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the whole reason the meter keeps state at all.</b> Without it the published
    ///     counters would be a sum over the live players, and a player leaving would make them fall
    ///     — which an <c>ObservableCounter</c>'s contract says is a process that restarted. The
    ///     counters a departure retires are still counters: what was lost was lost.
    /// </remarks>
    void Retire() {
        foreach (var (link, held) in peers) {
            if (held.Stamp == stamp) {
                continue;
            }

            retiredPeerExpected += held.Expected;
            retiredPeerMissing += held.Missing;
            peers.Remove(link);
        }
    }

    IEnumerable<Measurement<long>> Records() => [
        new(reading.Deltas, new KeyValuePair<string, object?>("kind", "delta")),
        new(reading.Wholes, new KeyValuePair<string, object?>("kind", "whole"))
    ];

    // Tagged by outcome rather than published as seven instruments, because the question is almost
    // always the ratio: refusals are normal traffic — a client whose object was despawned a tick ago
    // is refused and is not misbehaving — and what matters is which refusal is climbing.
    IEnumerable<Measurement<long>> Calls() => [
        new(reading.CallsAccepted, new KeyValuePair<string, object?>("outcome", "accepted")),
        new(reading.RefusedByManifest, new KeyValuePair<string, object?>("outcome", "unknown_method")),
        new(reading.RefusedByDirection, new KeyValuePair<string, object?>("outcome", "wrong_direction")),
        new(reading.RefusedByObject, new KeyValuePair<string, object?>("outcome", "unknown_object")),
        new(reading.RefusedByOwnership, new KeyValuePair<string, object?>("outcome", "not_permitted")),
        new(reading.RefusedByRateLimit, new KeyValuePair<string, object?>("outcome", "rate_limited")),
        new(reading.RefusedByArguments, new KeyValuePair<string, object?>("outcome", "bad_arguments"))
    ];

    /// <summary>One reading of everything attached, taken on the frame's thread.</summary>
    struct Reading {
        public long Tick;
        public int ConnectedPlayers;
        public int AwaitingPlayers;
        public double MeanRoundTripSeconds;
        public double WorstRoundTripSeconds;
        public double WorstJitterSeconds;
        public long Bytes;
        public long Deltas;
        public long Wholes;
        public long Suppressed;
        public long CallsAccepted;
        public long RefusedByManifest;
        public long RefusedByDirection;
        public long RefusedByObject;
        public long RefusedByOwnership;
        public long RefusedByRateLimit;
        public long RefusedByArguments;
        public long Sent;
        public long Retransmitted;
        public long Expected;
        public long Missing;
        public long PeerExpected;
        public long PeerMissing;
        public int ClientEntities;
        public long SnapshotsRejected;
        public long SnapshotsStale;
    }

    /// <summary>One link's peer-reported totals, and the pass they were last confirmed on.</summary>
    struct PeerLink {
        public long Expected;
        public long Missing;
        public int Stamp;
    }
}
