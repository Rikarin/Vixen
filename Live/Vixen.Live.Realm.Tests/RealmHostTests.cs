// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net;
using Vixen.Net.Sessions;
using Xunit;

namespace Vixen.Live.Realms.Tests;

/// <summary>A shard's life, from a spec to an empty process, over a real handshake.</summary>
public sealed class RealmHostTests {
    [Fact]
    public void AShardIsNotReadyUntilItsMapIs() {
        using var realm = new RealmFixture();

        realm.Pump(2);

        // Doc 27 § Grains: only Ready is a placement candidate, and a shard that has not finished
        // loading its map never takes an arrival.
        Assert.Equal(ShardState.Starting, realm.Host.State);
        Assert.Empty(realm.Output);

        realm.MapIsUp();
        realm.Pump(1);

        Assert.Equal(ShardState.Ready, realm.Host.State);
        Assert.Contains(RealmSignals.FormatReady(realm.Spec.Endpoint), realm.Output);
    }

    [Fact]
    public void TheReadyLineIsWrittenOnceAndCarriesTheEndpointClientsAreSentTo() {
        using var realm = new RealmFixture();

        realm.MapIsUp();
        realm.Pump(10);

        var ready = realm.Output.Where(line => line.StartsWith(RealmSignals.Ready, StringComparison.Ordinal));

        Assert.Single(ready);
        Assert.True(RealmSignals.TryReadReady(ready.First(), out var endpoint));
        Assert.Equal(realm.Spec.Endpoint, endpoint);
    }

    [Fact]
    public void ATicketedClientIsAdmittedAndBoundToWhoItDurablyIs() {
        using var realm = new RealmFixture();
        var admitted = new List<RealmPlayer>();

        realm.Host.PlayerAdmitted += admitted.Add;
        realm.MapIsUp();

        var ticket = realm.Ticket();

        realm.Connect(ticket);
        realm.Pump();

        var player = Assert.Single(admitted);

        Assert.Equal(ticket.Player, player.Key);
        Assert.Equal(ticket.LeaseEpoch, player.LeaseEpoch);
        Assert.True(player.Id.IsValid);
        Assert.Equal(1, realm.Host.Population);

        // Both directions of the join doc 27 keeps apart: who the database thinks they are, and who
        // this session numbers them as.
        Assert.True(realm.Host.Admission.TryGet(player.Id, out var byId));
        Assert.True(realm.Host.Admission.TryGet(ticket.Player, out var byKey));
        Assert.Same(byId, byKey);
    }

    [Fact]
    public void AClientWithNoTicketIsRefused() {
        using var realm = new RealmFixture();

        realm.MapIsUp();

        var client = realm.Connect(ticket: null);

        realm.Pump();

        Assert.Equal(0, realm.Host.Population);
        Assert.Equal(AdmissionRefusal.NoTicket, realm.Host.Admission.LastRefusal);
        Assert.NotEqual(SessionState.Running, client.State);
    }

    [Fact]
    public void AGenuineTicketForTheShardNextDoorIsRefused() {
        using var realm = new RealmFixture();

        realm.MapIsUp();
        realm.Connect(realm.Ticket(target: ShardId.New()));
        realm.Pump();

        Assert.Equal(0, realm.Host.Population);
        Assert.Equal(AdmissionRefusal.BadTicket, realm.Host.Admission.LastRefusal);
    }

    [Fact]
    public void AnExpiredTicketIsRefused() {
        using var realm = new RealmFixture();

        realm.MapIsUp();

        var ticket = realm.Ticket();

        realm.Now += TimeSpan.FromMinutes(1);
        realm.Connect(ticket);
        realm.Pump();

        Assert.Equal(0, realm.Host.Population);
        Assert.Equal(AdmissionRefusal.BadTicket, realm.Host.Admission.LastRefusal);
    }

    [Fact]
    public void OneCharacterCannotBeOnTheShardTwice() {
        using var realm = new RealmFixture();

        realm.MapIsUp();

        var player = new PlayerKey(Guid.NewGuid(), Guid.NewGuid());

        realm.Connect(realm.Ticket(player));
        realm.Pump();
        realm.Connect(realm.Ticket(player));
        realm.Pump();

        // Refused rather than replaced: a second session for one character is either a transfer that
        // has not finished or an attempt at duplication, and in both the safe answer is that they
        // stay where they already are.
        Assert.Equal(1, realm.Host.Population);
        Assert.Equal(AdmissionRefusal.AlreadyHere, realm.Host.Admission.LastRefusal);
    }

    [Fact]
    public void AFullShardAdmitsNobody() {
        using var realm = new RealmFixture(capacity: new(1, 1));

        realm.MapIsUp();
        realm.Connect(realm.Ticket());
        realm.Pump();

        Assert.Equal(1, realm.Host.Population);

        realm.Connect(realm.Ticket());
        realm.Pump();

        Assert.Equal(1, realm.Host.Population);
        Assert.Equal(AdmissionRefusal.Full, realm.Host.Admission.LastRefusal);
    }

    [Fact]
    public void DrainingStopsArrivalsWithoutDisconnectingAnybody() {
        using var realm = new RealmFixture();

        realm.MapIsUp();
        realm.Connect(realm.Ticket());
        realm.Pump();

        realm.Host.Signal(RealmSignals.Drain);
        realm.Pump(1);

        Assert.Equal(ShardState.Draining, realm.Host.State);
        Assert.Contains(RealmSignals.Draining, realm.Output);
        Assert.Equal(MapState.Quiescing, realm.Host.Map.State);

        // Doc 27 § Drain: nothing is force-disconnected. The player who was already here is still
        // here, and the next one is not let in.
        Assert.Equal(1, realm.Host.Population);

        realm.Connect(realm.Ticket());
        realm.Pump();

        Assert.Equal(1, realm.Host.Population);
        Assert.Equal(AdmissionRefusal.Draining, realm.Host.Admission.LastRefusal);
    }

    [Fact]
    public void ADrainedShardStopsOnceItHasBeenEmptyForTheGrace() {
        using var realm = new RealmFixture(idleGrace: TimeSpan.FromMilliseconds(100));

        realm.MapIsUp();
        realm.Host.Signal(RealmSignals.Drain);
        realm.Pump(1);

        Assert.Equal(ShardState.Draining, realm.Host.State);

        // Not the instant it empties: a player who was moved out may have a reconnect in flight, and
        // a shard that vanished would turn a lost packet into a lost session.
        realm.Pump(3);
        Assert.Equal(ShardState.Draining, realm.Host.State);

        realm.Pump(5);
        Assert.Equal(ShardState.Stopped, realm.Host.State);
        Assert.Contains(RealmSignals.Stopped, realm.Output);
    }

    [Fact]
    public void ADrainedShardWithSomebodyOnItStaysUp() {
        using var realm = new RealmFixture(idleGrace: TimeSpan.FromMilliseconds(50));

        realm.MapIsUp();
        realm.Connect(realm.Ticket());
        realm.Pump();

        realm.Host.Signal(RealmSignals.Drain);
        realm.Pump(20);

        Assert.Equal(ShardState.Draining, realm.Host.State);
        Assert.Equal(1, realm.Host.Population);
    }

    [Fact]
    public void TheStopSignalEndsItWhoeverIsOnIt() {
        using var realm = new RealmFixture();

        realm.MapIsUp();
        realm.Connect(realm.Ticket());
        realm.Pump();

        realm.Host.Signal(RealmSignals.Stop);
        realm.Pump(1);

        Assert.Equal(ShardState.Stopped, realm.Host.State);
    }

    [Fact]
    public void AStrayKeystrokeIsNotACommand() {
        using var realm = new RealmFixture();

        realm.MapIsUp();
        realm.Pump(1);

        realm.Host.Signal("");
        realm.Host.Signal("quit");
        realm.Host.Signal("vixen-realm please stop");
        realm.Pump(1);

        Assert.Equal(ShardState.Ready, realm.Host.State);
    }

    [Fact]
    public void ReadinessIsAskedOfEveryPlayerEveryFrameAndCached() {
        using var realm = new RealmFixture();

        realm.MapIsUp();
        realm.Connect(realm.Ticket());
        realm.Pump();

        realm.Host.Readiness = _ => TransferReadiness.Blocked;
        realm.Pump(1);

        var player = Assert.Single(realm.Host.Admission.Players);

        Assert.Equal(TransferReadiness.Blocked, player.Readiness);
    }

    [Fact]
    public void HealthIsSampledOnTheHeartbeatAndCountsWhatADrainCannotMove() {
        using var realm = new RealmFixture();
        var samples = new List<RealmHealth>();

        realm.Host.Sampled += samples.Add;
        realm.MapIsUp();
        realm.Connect(realm.Ticket());
        realm.Host.Readiness = _ => TransferReadiness.Blocked;
        realm.Pump(16);

        Assert.NotEmpty(samples);

        var last = samples[^1];

        Assert.Equal(realm.Spec.Shard, last.Shard);
        Assert.Equal(ShardState.Ready, last.State);
        Assert.Equal(1, last.Population);
        Assert.Equal(1, last.Blocked);
        Assert.True(last.TickP99Milliseconds > 0);
    }

    [Fact]
    public void AGamesPayloadHandlerReachesTheSessionThroughTheHost() {
        using var realm = new RealmFixture();
        var messages = new RealmFixture.PayloadRecorder();

        realm.MapIsUp();

        var client = realm.Connect(realm.Ticket());

        realm.Pump();
        client.SendToServer("hello"u8, Channel.Reliable);

        // The realm's session is updated once, by the host. A realm that called Session.Update again
        // from its own step to install a handler would advance the session twice a frame.
        realm.Pump(messages: messages);

        Assert.Contains("hello", messages.Texts, StringComparer.Ordinal);
    }

    [Fact]
    public void ASpecThatIsNotRunnableIsRefusedAtConstruction() {
        var failure = Assert.Throws<ArgumentException>(
            () => new RealmHost(new RealmSpec(), _ => throw new InvalidOperationException("never reached"))
        );

        Assert.Contains("not a runnable spec", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDevelopmentSignerIsPerShardRatherThanShared() {
        var first = new RealmSpec {
            Shard = ShardId.New(),
            Key = new("maps/a", "eu", new("0.1.0", 1)),
            Endpoint = new("127.0.0.1", 1),
            Capacity = new(1, 1)
        };

        var second = first with { Shard = ShardId.New() };

        using var one = RealmHost.DevelopmentSigner(first);
        using var other = RealmHost.DevelopmentSigner(second);

        var ticket = one.Sign(
            new() {
                Player = new(Guid.NewGuid(), Guid.NewGuid()),
                Target = first.Shard,
                Endpoint = first.Endpoint,
                Expires = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1)
            }
        );

        // A deployment that forgot to configure a key gets a fleet that refuses everybody, which is
        // loud — rather than one that admits anybody, which is not.
        Assert.Equal(TicketStatus.Valid, one.Validate(ticket, first.Shard, DateTimeOffset.UtcNow));
        Assert.Equal(TicketStatus.Forged, other.Validate(ticket, first.Shard, DateTimeOffset.UtcNow));
    }
}
