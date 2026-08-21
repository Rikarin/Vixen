// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text;
using Vixen.Live.Cluster;
using Vixen.Net.Sessions;
using Vixen.Net.Transport.Local;
using Xunit;

namespace Vixen.Live.Realms.Cluster.Tests;

/// <summary>The loop closing: a realm reports, hears back, and acts — without ever waiting.</summary>
public sealed class RealmClusterTests : IDisposable {
    static readonly byte[] Key = Encoding.UTF8.GetBytes("a-test-cluster-key-of-32-bytes!!!!!!");

    readonly LocalNetwork network = new();
    readonly List<NetworkSession> clients = [];
    readonly TransferTicketSigner signer = new(Key);
    readonly List<string> output = [];
    readonly FakeCluster cluster = new();
    readonly RealmHost host;
    readonly RealmCluster wiring;

    DateTimeOffset now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    static readonly TimeSpan Step = TimeSpan.FromMilliseconds(16);

    RealmSpec Spec { get; }

    public RealmClusterTests() {
        Spec = new() {
            Shard = ShardId.New(),
            Key = new("maps/queensdale", "eu", new("0.1.0", 0xC0FFEE)),
            Endpoint = new("127.0.0.1", 7777),
            Capacity = new(100, 120),
            TickRate = 30
        };

        host = new(
            Spec,
            admission => new(new LocalTransport(network), Options(), admission, ownsTransport: true),
            signer,
            new() { Output = output.Add, Now = () => now, HeartbeatInterval = TimeSpan.FromMilliseconds(32) }
        );

        // The orchestrator's side of this shard, as it would be after a spawn.
        var lifecycle = cluster.Lifecycle(Spec.Shard);

        lifecycle.Requested(Spec.Key, Spec.Capacity);
        lifecycle.Starting(new("realm-1"), Spec.Endpoint);

        // Renewing every fifty milliseconds rather than every five seconds, so a test can watch a
        // lease be taken away without waiting for one. The cadence is a parameter for exactly this.
        wiring = new(host, cluster, new(TimeSpan.FromMilliseconds(50)));
        host.Start();
    }

    public void Dispose() {
        foreach (var client in clients) {
            client.Dispose();
        }

        wiring.Dispose();
        host.Session.Dispose();
        host.Dispose();
        signer.Dispose();
    }

    // ── Reporting ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AReadyShardTellsTheClusterItIsReady() {
        MapIsUp();
        Pump(2);

        Assert.Equal(ShardState.Ready, cluster.Lifecycle(Spec.Shard).State);
        Assert.Equal(Spec.Endpoint, cluster.Lifecycle(Spec.Shard).Report().Endpoint);
    }

    [Fact]
    public void TheHeartbeatCarriesWhatTheClusterWatches() {
        MapIsUp();
        Connect(Ticket());
        Pump(10);

        var report = cluster.Lifecycle(Spec.Shard).Report();

        Assert.True(wiring.HeartbeatCount > 0);
        Assert.Equal(1, report.Population);
    }

    [Fact]
    public void ARealmLearnsItShouldDrainFromTheAnswerToItsOwnHeartbeat() {
        MapIsUp();
        Pump(2);

        Assert.Equal(ShardState.Ready, host.State);

        // The orchestrator decides, on its own, that this shard should empty. It does not call the
        // realm — there is no way for it to, and that is the design.
        cluster.Lifecycle(Spec.Shard).Drain();

        Pump(10);

        // ⚠ The realm found out from the reply to a heartbeat it was sending anyway.
        Assert.Equal(ShardState.Draining, host.State);
        Assert.Contains(RealmSignals.Draining, output);
    }

    // ── Leases ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnAdmittedPlayerHasTheirLeaseTaken() {
        MapIsUp();

        var ticket = Ticket();

        Connect(ticket);
        Pump(10);

        Assert.Equal(1, wiring.LeaseCount);
        Assert.Equal(Spec.Shard, cluster.Lease(ticket.Player).Holder);
    }

    [Fact]
    public void ALeaseTakenAwayIsNoticedOnTheNextRenewalRatherThanNever() {
        MapIsUp();

        var ticket = Ticket();

        Connect(ticket);
        Pump(10);

        Assert.Equal(1, wiring.LeaseCount);

        // Another shard takes it — a transfer, or a realm the cluster believes has died.
        cluster.Lease(ticket.Player).Acquire(ShardId.New());

        Pump(60);

        // ⚠ Doc 27 ADR-021: the realm keeps simulating, and stops writing durable state. Losing the
        // lease is survivable; not noticing is what produces two copies of a sword.
        Assert.Equal(0, wiring.LeaseCount);
        Assert.Equal(1, wiring.LeasesLost);
        Assert.Equal(ShardState.Ready, host.State);
    }

    [Fact]
    public void APlayerWhoLeavesGivesTheirLeaseBackAndIsTakenOffTheRoster() {
        MapIsUp();

        var ticket = Ticket();
        var client = Connect(ticket);

        Pump(10);
        Assert.Equal(1, wiring.LeaseCount);

        client.Stop();
        Pump(40);

        Assert.Equal(0, wiring.LeaseCount);
        Assert.False(cluster.Lease(ticket.Player).IsHeld);

        // And the map's roster, so the next placement's affinity counts are honest.
        Assert.Contains((ticket.Player, Spec.Shard), cluster.Departures);
    }

    // ── The rule the whole design rests on ──────────────────────────────────────────────────────

    /// <summary>How long the slow cluster takes to answer, and the margin allowed for it.</summary>
    static readonly TimeSpan Answer = TimeSpan.FromMilliseconds(250);

    /// <summary>How many frames one measured run pumps. Doc 27 § Slice two's twenty.</summary>
    const int Frames = 20;

    /// <summary>How many times the pair is measured. The first pass is thrown away.</summary>
    const int Passes = 4;

    [Fact]
    public void ASlowClusterDoesNotSlowTheRealmDown() {
        // ⚠ Doc 27 M1: a grain call reaching the frame path is the single way this design fails, and
        // it will not look like a bug — it will look like occasional stutter. Every call here goes
        // through RealmDirectory, so a cluster taking a quarter of a second to answer costs the realm
        // nothing at all.
        //
        // ⚠ The measurement is against this machine, not against a number of milliseconds. Pump is a
        // simulated clock with no sleeps in it: twenty frames cost two or three milliseconds when the
        // process is being scheduled and two hundred when a parallel `dotnet test` is using all ten
        // cores. An absolute budget small enough to look strict is therefore a reading of the build
        // agent — it fails a healthy realm under load, and it would still pass the regression it
        // exists to catch, because a frame path that waits on this cluster costs *seconds*. So the
        // same twenty frames are run twice, against a cluster answering instantly and against one
        // taking a quarter of a second, and the two are compared to each other.
        //
        // Set before the map comes up so the realm's `Ready` is one of the calls that has to wait,
        // which is what the last line of this test then watches arrive.
        cluster.Latency = Answer;

        MapIsUp();

        var slow = TimeSpan.MaxValue;
        var free = TimeSpan.MaxValue;

        for (var pass = 0; pass < Passes; pass++) {
            // Interleaved, so both figures see the same machine at the same moment — a baseline taken
            // before the load arrives is worth no more than an absolute budget. Slow first within
            // each pass, so that whatever a warmed path is worth is worth it to the *baseline*: an
            // ordering that can only make this assertion harder to pass, never easier.
            cluster.Latency = Answer;

            var waited = TwentyFrames();

            cluster.Latency = TimeSpan.Zero;

            var instant = TwentyFrames();

            // The first pass pays for the JIT of everything below Pump, on whichever of the two runs
            // happened to go first. Reduced by minimum after that, because the smallest of several
            // samples is the one that was preempted least, and preemption is exactly the noise the
            // old budget was measuring.
            if (pass == 0) {
                continue;
            }

            slow = waited < slow ? waited : slow;
            free = instant < free ? instant : free;
        }

        // ⚠ The margin is one whole cluster answer — generous, and still an order of magnitude
        // tighter than the regression it guards. A frame path that awaits its grain calls blocks on
        // every one of them, and twenty frames at this heartbeat cadence make about ten calls, so
        // breaking M1 reads as two and a half seconds rather than as 250 milliseconds. What the
        // margin buys is that the baseline does the arguing about the machine.
        Assert.True(
            slow <= free + Answer,
            $"twenty frames cost {slow.TotalMilliseconds:0.0} ms against a cluster answering in "
            + $"{Answer.TotalMilliseconds:0} ms, and {free.TotalMilliseconds:0.0} ms against one "
            + "answering instantly — measured on the same machine at the same moment, so the "
            + "difference is the realm waiting."
        );

        // And the answers do arrive, on the realm's own thread, once they are ready.
        Eventually(() => cluster.Lifecycle(Spec.Shard).State == ShardState.Ready);
    }

    [Fact]
    public void AClusterThatIsNotAnsweringLeavesTheRealmPlayable() {
        cluster.Unreachable = true;

        MapIsUp();
        Connect(Ticket());
        Pump(30);

        // Nothing was acknowledged and nothing broke: the realm is up, the player is on it, and the
        // faults are counted where somebody can see them.
        Assert.Equal(ShardState.Ready, host.State);
        Assert.Equal(1, host.Population);
        Assert.True(host.Directory.FaultedCount > 0);
        Assert.Equal(0, wiring.LeaseCount);
    }

    [Fact]
    public void DisposingStopsTheRealmTalkingToTheCluster() {
        MapIsUp();
        Pump(4);

        var before = cluster.Calls;

        wiring.Dispose();
        wiring.Dispose();

        Pump(20);

        Assert.Equal(before, cluster.Calls);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    void MapIsUp() => host.Map.Ready(new(1));

    TransferTicket Ticket() =>
        signer.Sign(
            new() {
                Player = new(Guid.NewGuid(), Guid.NewGuid()),
                Target = Spec.Shard,
                Endpoint = Spec.Endpoint,
                LeaseEpoch = 1,
                Expires = now + TimeSpan.FromMinutes(5)
            }
        );

    NetworkSession Connect(TransferTicket ticket) {
        var session = new NetworkSession(
            new LocalTransport(network),
            Options() with { AuthenticationPayload = Encoding.UTF8.GetBytes(ticket.Encode()) },
            ownsTransport: true
        );

        clients.Add(session);
        session.StartClient();

        return session;
    }

    /// <summary>Pumps <see cref="Frames" /> frames and says what they cost in wall-clock time.</summary>
    /// <returns>How long they took.</returns>
    /// <remarks>
    ///     <see cref="Stopwatch" /> rather than <see cref="DateTime.UtcNow" />: this is a duration on
    ///     one machine, and the wall clock is allowed to step sideways underneath one.
    /// </remarks>
    TimeSpan TwentyFrames() {
        var started = Stopwatch.GetTimestamp();

        Pump(Frames);

        return Stopwatch.GetElapsedTime(started);
    }

    void Pump(int rounds) {
        for (var round = 0; round < rounds; round++) {
            now += Step;
            host.Update(Step);
            wiring.Update(Step);

            foreach (var client in clients) {
                client.Update(Step);
            }
        }
    }

    void Eventually(Func<bool> condition) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (DateTime.UtcNow < deadline) {
            Pump(1);

            if (condition()) {
                return;
            }

            Thread.Sleep(1);
        }

        Assert.Fail("The condition was still false after five seconds.");
    }

    SessionOptions Options() =>
        new() {
            MaxPlayers = Spec.Capacity.HardCap,
            ContentHash = Spec.Key.Version.Content,
            AuthenticationTimeout = TimeSpan.FromSeconds(5),

            // A player who disconnects is gone in fifty milliseconds rather than thirty seconds.
            // The window is doc 16's and it is right; what a test cannot afford is waiting it out.
            ReconnectWindow = TimeSpan.FromMilliseconds(50)
        };
}
