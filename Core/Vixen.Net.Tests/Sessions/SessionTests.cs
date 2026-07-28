// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Messaging;
using Vixen.Net.Sessions;
using Vixen.Net.Transport;
using Vixen.Net.Transport.Local;
using Xunit;

namespace Vixen.Net.Tests.Sessions;

/// <summary>The session: the handshake, who is in it, and what happens when somebody drops.</summary>
public sealed class SessionTests {
    [Fact]
    public void AClientThatConnects_IsAPlayerOnBothSides() {
        using var harness = new SessionHarness();
        var server = harness.StartServer();
        var client = harness.StartClient();

        harness.Pump();

        Assert.Equal(SessionState.Running, server.State);
        Assert.Equal(SessionState.Running, client.State);
        Assert.Single(server.Players);
        Assert.NotNull(client.LocalPlayer);
        Assert.Equal(server.Players[0].Id, client.LocalPlayer.Id);
        Assert.True(client.LocalPlayer.IsLocal);
        Assert.True(server.Players[0].IsConnected);
        Assert.False(client.ReconnectToken.IsEmpty);
    }

    [Fact]
    public void EachClientGetsItsOwnPlayerId() {
        using var harness = new SessionHarness();
        var server = harness.StartServer();
        var first = harness.StartClient();
        var second = harness.StartClient();

        harness.Pump();

        Assert.Equal(2, server.Players.Count);
        Assert.NotNull(first.LocalPlayer);
        Assert.NotNull(second.LocalPlayer);
        Assert.NotEqual(first.LocalPlayer.Id, second.LocalPlayer.Id);

        // And a client knows only about itself: the player list is the server's to hand out, and
        // handing it out is a decision for the layer above rather than a side effect of connecting.
        Assert.Single(first.Players);
    }

    [Fact]
    public void AClientSpeakingADifferentProtocol_IsRefusedAndTold() {
        using var harness = new SessionHarness();
        var server = harness.StartServer(new() { ProtocolVersion = 7 });
        var client = harness.StartClient(new() { ProtocolVersion = 8 });
        var reason = SessionRejectReason.None;
        client.Rejected += (why, _) => reason = why;

        harness.Pump();

        Assert.Equal(SessionRejectReason.ProtocolMismatch, reason);
        Assert.Empty(server.Players);
        Assert.Null(client.LocalPlayer);
    }

    [Fact]
    public void AClientRunningDifferentContent_IsRefusedAndTold() {
        using var harness = new SessionHarness();
        var server = harness.StartServer(new() { ContentHash = 0xABCDEF });
        var client = harness.StartClient(new() { ContentHash = 0x123456 });
        var reason = SessionRejectReason.None;
        client.Rejected += (why, _) => reason = why;

        harness.Pump();

        Assert.Equal(SessionRejectReason.ContentMismatch, reason);
        Assert.Empty(server.Players);
    }

    [Fact]
    public void AFullServer_RefusesTheNextOne() {
        using var harness = new SessionHarness();
        var server = harness.StartServer(new() { MaxPlayers = 1 });
        harness.StartClient();
        var turnedAway = harness.StartClient();
        var reason = SessionRejectReason.None;
        turnedAway.Rejected += (why, _) => reason = why;

        harness.Pump();

        Assert.Single(server.Players);
        Assert.Equal(SessionRejectReason.ServerFull, reason);
    }

    [Fact]
    public void AnAuthenticatorThatSaysNo_KeepsThemOut() {
        var authenticator = new ScriptedAuthenticator(AuthenticationDecision.Refuse("Not on the list."));
        using var harness = new SessionHarness();
        var server = harness.StartServer(authenticator: authenticator);
        var client = harness.StartClient();
        var told = "";
        client.Rejected += (_, text) => told = text;

        harness.Pump();

        Assert.Empty(server.Players);
        Assert.Equal("Not on the list.", told);
        Assert.Equal(1, authenticator.Asked);
    }

    [Fact]
    public void AnAuthenticatorIsGivenWhatTheClientSent() {
        var authenticator = new ScriptedAuthenticator(AuthenticationDecision.As("kim"));
        using var harness = new SessionHarness();
        var server = harness.StartServer(authenticator: authenticator);
        harness.StartClient(new() { AuthenticationPayload = SessionHarness.Bytes("a-ticket") });

        harness.Pump();

        Assert.Equal(SessionHarness.Bytes("a-ticket"), authenticator.LastPayload);
        Assert.False(authenticator.LastWasReconnect);
        Assert.Equal("kim", server.Players[0].Identity);
    }

    [Fact]
    public void AnAuthenticatorThatTakesItsTime_IsAskedAgainUntilItAnswers() {
        var authenticator = new ScriptedAuthenticator(AuthenticationDecision.Pending);
        using var harness = new SessionHarness();
        var server = harness.StartServer(authenticator: authenticator);
        harness.StartClient();

        harness.Pump(4);

        Assert.Empty(server.Players);
        Assert.True(authenticator.Asked > 1, "A pending decision should be asked again.");

        authenticator.Decision = AuthenticationDecision.Accept;
        harness.Pump(4);

        Assert.Single(server.Players);
    }

    [Fact]
    public void AnAuthenticatorThatNeverAnswers_TimesTheConnectionOut() {
        var authenticator = new ScriptedAuthenticator(AuthenticationDecision.Pending);
        using var harness = new SessionHarness();
        var server = harness.StartServer(
            new() { AuthenticationTimeout = TimeSpan.FromMilliseconds(100) },
            authenticator
        );

        var client = harness.StartClient();
        var reason = SessionRejectReason.None;
        client.Rejected += (why, _) => reason = why;

        harness.Pump(20);

        Assert.Empty(server.Players);
        Assert.Equal(SessionRejectReason.AuthenticationTimedOut, reason);
    }

    [Fact]
    public void APayloadFromAConnectionThatNeverHandshook_IsNotDispatched() {
        using var harness = new SessionHarness();
        var server = harness.StartServer();
        using var rogue = harness.RawTransport();
        var recorder = new MessageRecorder();
        var events = new Transport.EventRecorder();

        rogue.StartClient();
        rogue.Poll(SessionHarness.Step, events);

        // A well-formed user message from a connection that never asked to be let in.
        rogue.SendToServer([6, 104, 105], Channel.Reliable);
        harness.Pump(4, recorder);

        Assert.Empty(recorder.Messages);
        Assert.Empty(server.Players);
    }

    [Fact]
    public void AMessageReachesTheServerAndComesBack() {
        using var harness = new SessionHarness();
        var server = harness.StartServer();
        var client = harness.StartClient();
        harness.Pump();

        var recorder = new MessageRecorder();

        Assert.True(client.SendToServer(SessionHarness.Bytes("up"), Channel.Reliable));
        harness.Pump(4, recorder);

        Assert.Equal(1, server.SendToAll(SessionHarness.Bytes("down"), Channel.Unreliable));
        harness.Pump(4, recorder);

        Assert.Equal(["up", "down"], recorder.Texts());
        Assert.Equal(client.LocalPlayer!.Id, recorder.Messages[0].From);
        Assert.Equal(Channel.Reliable, recorder.Messages[0].Channel);

        // From the server, so from nobody in particular: the server is not a player.
        Assert.Equal(PlayerId.None, recorder.Messages[1].From);
        Assert.Equal(Channel.Unreliable, recorder.Messages[1].Channel);
    }

    [Fact]
    public void SendingToAPlayerWhoIsNotThere_IsRefusedRatherThanThrowing() {
        using var harness = new SessionHarness();
        var server = harness.StartServer();
        var client = harness.StartClient();
        harness.Pump();

        Assert.False(server.SendToPlayer(new(999), SessionHarness.Bytes("x"), Channel.Reliable));
        Assert.True(server.SendToPlayer(client.LocalPlayer!.Id, SessionHarness.Bytes("x"), Channel.Reliable));
    }

    [Fact]
    public void ADroppedPlayerIsHeldForTheWindow_AndComesBackAsTheSamePlayer() {
        using var harness = new SessionHarness();
        var server = harness.StartServer(new() { ReconnectWindow = TimeSpan.FromSeconds(30) });
        var client = harness.StartClient();
        harness.Pump();

        var id = client.LocalPlayer!.Id;
        var token = client.ReconnectToken.ToArray();

        client.Stop();
        harness.Pump();

        Assert.Single(server.Players);
        Assert.False(server.Players[0].IsConnected);
        Assert.Contains("away", string.Join("|", harness.Log));

        var again = harness.StartClient(token: token);
        harness.Pump();

        Assert.Single(server.Players);
        Assert.True(server.Players[0].IsConnected);
        Assert.Equal(id, again.LocalPlayer!.Id);
        Assert.Equal(1, server.Players[0].ReconnectCount);
    }

    [Fact]
    public void APlayerWhoMissesTheWindow_IsGoneForGood() {
        using var harness = new SessionHarness();
        var server = harness.StartServer(new() { ReconnectWindow = TimeSpan.FromMilliseconds(100) });
        var client = harness.StartClient();
        harness.Pump();

        var token = client.ReconnectToken.ToArray();
        var id = client.LocalPlayer!.Id;
        client.Stop();

        harness.Pump(20);

        Assert.Empty(server.Players);
        Assert.Contains($"{id} left (ReconnectWindowExpired)", string.Join("|", harness.Log));

        // And the token they were issued no longer buys anything.
        var again = harness.StartClient(token: token);
        harness.Pump();

        Assert.NotEqual(id, again.LocalPlayer!.Id);
    }

    [Fact]
    public void WithNoReconnectWindow_ADisconnectIsFinalAtOnce() {
        using var harness = new SessionHarness();
        var server = harness.StartServer(new() { ReconnectWindow = TimeSpan.Zero });
        var client = harness.StartClient();
        harness.Pump();

        var id = client.LocalPlayer!.Id;
        client.Stop();
        harness.Pump();

        Assert.Empty(server.Players);
        Assert.Contains($"{id} left (Disconnected)", string.Join("|", harness.Log));
    }

    [Fact]
    public void ATokenTheServerDoesNotKnow_GetsANewPlayerRatherThanARefusal() {
        using var harness = new SessionHarness();
        var server = harness.StartServer();
        var client = harness.StartClient(token: new byte[16]);

        harness.Pump();

        Assert.Single(server.Players);
        Assert.NotNull(client.LocalPlayer);
    }

    [Fact]
    public void AKickedPlayerIsGoneWithNoWindowToComeBackThrough() {
        using var harness = new SessionHarness();
        var server = harness.StartServer(new() { ReconnectWindow = TimeSpan.FromSeconds(30) });
        var client = harness.StartClient();
        harness.Pump();

        var id = client.LocalPlayer!.Id;
        var token = client.ReconnectToken.ToArray();

        Assert.True(server.Kick(id, "Behave."));
        harness.Pump();

        Assert.Empty(server.Players);
        Assert.Contains($"{id} left (Kicked)", string.Join("|", harness.Log));

        var again = harness.StartClient(token: token);
        harness.Pump();

        Assert.NotEqual(id, again.LocalPlayer!.Id);
    }

    [Fact]
    public void AClientThatStopsAnswering_IsTimedOut() {
        using var harness = new SessionHarness();

        var server = harness.StartServer(
            new() { Timeout = TimeSpan.FromMilliseconds(200), ReconnectWindow = TimeSpan.Zero }
        );

        var client = harness.StartClient();
        harness.Pump();

        Assert.Single(server.Players);

        // The client's process is still there and its socket is still open. It has simply stopped
        // running its frame, which is what a freeze looks like from the other end.
        SessionHarness.PumpOnly(server, 40);

        Assert.Empty(server.Players);
        Assert.Contains("left (TimedOut)", string.Join("|", harness.Log));
        Assert.Equal(SessionState.Running, client.State);
    }

    [Fact]
    public void AHostIsItsOwnPlayer_ThroughTheSameHandshakeAnybodyElseUses() {
        using var harness = new SessionHarness();
        var host = harness.StartHost();

        harness.Pump();

        Assert.Equal(SessionTopology.Host, host.Topology);
        Assert.True(host.IsServer);
        Assert.True(host.IsClient);
        Assert.Single(host.Players);
        Assert.NotNull(host.LocalPlayer);
        Assert.True(host.LocalPlayer.IsLocal);
        Assert.True(host.LocalPlayer.IsConnected);

        var joined = harness.StartClient();
        harness.Pump();

        Assert.Equal(2, host.Players.Count);
        Assert.NotEqual(host.LocalPlayer.Id, joined.LocalPlayer!.Id);
    }

    [Fact]
    public void AnOfflineSessionIsAHostThatSaysWhatItIs() {
        using var harness = new SessionHarness();
        var offline = harness.Add(new LocalTransport(new LocalNetwork()), "offline");

        offline.StartOffline();
        SessionHarness.PumpOnly(offline, 8);

        Assert.Equal(SessionTopology.Offline, offline.Topology);
        Assert.True(offline.IsServer);
        Assert.True(offline.IsClient);
        Assert.Single(offline.Players);
        Assert.NotNull(offline.LocalPlayer);
    }

    [Fact]
    public void StoppingASessionEmptiesIt() {
        using var harness = new SessionHarness();
        var server = harness.StartServer();
        harness.StartClient();
        harness.Pump();

        server.Stop();

        Assert.Empty(server.Players);
        Assert.Equal(SessionState.Stopped, server.State);
        Assert.Equal(SessionTopology.None, server.Topology);
        Assert.Contains("left (SessionStopped)", string.Join("|", harness.Log));
    }

    [Fact]
    public void StartingATwiceStartedSession_Throws() {
        using var harness = new SessionHarness();
        var server = harness.StartServer();

        Assert.Throws<InvalidOperationException>(server.StartClient);
    }

    [Fact]
    public void PresentingATokenAfterConnecting_Throws() {
        using var harness = new SessionHarness();
        harness.StartServer();
        var client = harness.StartClient();
        harness.Pump();

        Assert.Throws<InvalidOperationException>(() => client.PresentReconnectToken(new byte[16]));
    }

    [Fact]
    public void TheClockIsSynchronisedFromTheHandshakeAndKeptThereByPings() {
        using var harness = new SessionHarness();

        var server = harness.StartServer(new() { PingInterval = TimeSpan.FromMilliseconds(100) });

        // Fifty milliseconds each way, so a hundred round trip — a plausible link rather than a
        // loopback, which is the only kind that exercises the clock at all.
        var simulated = new NetworkSimulation(
            harness.RawTransport(),
            new() { Latency = TimeSpan.FromMilliseconds(50) },
            seed: 3
        );

        var client = harness.Add(simulated, "far-away", new() { PingInterval = TimeSpan.FromMilliseconds(100) });
        client.StartClient();

        harness.Pump(120);

        Assert.NotNull(client.LocalPlayer);
        Assert.True(client.Clock.IsSynchronized);
        Assert.InRange(client.Clock.RoundTrip.RoundTrip.TotalMilliseconds, 60, 160);

        // The client aims ahead of where it thinks the server is, so its input lands in time.
        Assert.True(client.Clock.LeadTicks > 0);
        Assert.True(client.Clock.TargetTick.IsAfter(client.Clock.EstimatedServerTick));

        // And the server measured the same trip from its end.
        Assert.True(server.Players[0].RoundTrip.HasSamples);
        Assert.InRange(server.Players[0].RoundTrip.RoundTrip.TotalMilliseconds, 40, 160);
    }

    /// <summary>A second acceptance on a connection already in the session is ignored.</summary>
    /// <remarks>
    ///     <para>
    ///         Found by the packet fuzzer, which measured a client allocating fifty kilobytes from a
    ///         thirty-two byte packet. A server of ours sends exactly one <c>ConnectAccepted</c>; a
    ///         peer that sends more than one costs the client a player record and two dictionary
    ///         entries per packet, kept for ever, for any id the sender cares to invent.
    ///     </para>
    ///     <para>
    ///         The mirror of the rule the server half already keeps for a second handshake on a live
    ///         connection, and it is written straight into the receive path here rather than through
    ///         a transport because that is where the packet arrives.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ASecondAcceptance_IsIgnoredRatherThanBelieved() {
        using var harness = new SessionHarness();
        harness.StartServer();

        var client = harness.StartClient();
        harness.Pump();

        var first = client.LocalPlayer;
        Assert.NotNull(first);
        Assert.Single(client.Players);

        // Ten more acceptances, each naming a player this client has never heard of. A real server
        // does not do this; something in the middle of the connection might.
        for (var id = 100u; id < 110; id++) {
            ((ITransportEvents)client).OnData(TransportRole.Client, ConnectionId.None, Channel.Reliable, Accepted(id));
        }

        Assert.Same(first, client.LocalPlayer);
        Assert.Single(client.Players);
    }

    /// <summary>A client that reconnects holds one player, not one per attempt.</summary>
    /// <remarks>
    ///     The retention half of the same finding, and the older defect of the two: a pure client's
    ///     player list is exactly itself, but losing the connection only cleared
    ///     <c>LocalPlayer</c> and left the record in the list. The next acceptance gets whatever id
    ///     that server hands out — which need not be the one before — and adds a second. Nothing
    ///     ever looks either up again.
    /// </remarks>
    [Fact]
    public void AClientThatReconnectsRepeatedly_HoldsOnePlayer() {
        using var harness = new SessionHarness();
        harness.StartServer();

        var client = harness.StartClient();
        harness.Pump();
        Assert.Single(client.Players);

        var events = (ITransportEvents)client;

        for (var id = 100u; id < 110; id++) {
            events.OnDisconnected(TransportRole.Client, ConnectionId.None, DisconnectReason.Requested);

            // The record goes with the connection. A client's list is exactly itself, and the seat
            // it held is not one it is coming back to — the next server decides that.
            Assert.Null(client.LocalPlayer);
            Assert.Empty(client.Players);

            events.OnData(TransportRole.Client, ConnectionId.None, Channel.Reliable, Accepted(id));

            Assert.NotNull(client.LocalPlayer);
            Assert.Equal(new PlayerId(id), client.LocalPlayer.Id);
            Assert.Single(client.Players);
        }
    }

    [Fact]
    public void TheServerTickAdvancesWithTheFramesItIsGiven() {
        using var harness = new SessionHarness();
        var server = harness.StartServer(new() { TickRate = new(30) });

        var ticks = 0;

        for (var i = 0; i < 60; i++) {
            ticks += server.Update(TimeSpan.FromMilliseconds(16));
        }

        // Just under a second of frames at sixty a second is just under thirty ticks at thirty.
        Assert.InRange(ticks, 28, 30);
        Assert.Equal(new Tick((uint)ticks), server.Tick);
    }

    /// <summary>Encodes the acceptance a server sends, for a test that wants to send a bad one.</summary>
    static byte[] Accepted(uint playerId) {
        var buffer = new byte[128];
        var writer = new PacketWriter(buffer);

        writer.WriteByte((byte)SystemMessage.ConnectAccepted);
        writer.WriteVariable(playerId);
        writer.WriteTick(new(1));
        writer.WriteBlob(new byte[16]);
        writer.WriteString("impostor");

        return writer.TryFinish(out var packet) ? packet.ToArray() : [];
    }
}
