// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Sessions;
using Vixen.Net.Transport;
using Xunit;

namespace Vixen.Net.Tests.Sessions;

/// <summary>
///     The seam doc 16's "on by default in dev builds" needs: a session that wraps its own transport
///     from the record a host already configures.
/// </summary>
/// <remarks>
///     ⚠ <b>The claim under test is an ordering one.</b> Two audits recorded that
///     <c>SessionOptions</c> could not carry this because "a profile on the options record would
///     arrive after the decision it is meant to make". The transport and the options are two
///     parameters of one constructor call; what these assert is that the wrapping actually happened
///     before anything used the transport, which is the only sense in which "too late" would have
///     been true.
/// </remarks>
public sealed class SessionSimulationTests {
    static ulong Seed => 20260909;

    [Fact]
    public void ASessionWithNoSimulationRunsOnTheTransportItWasHanded() {
        using var harness = new SessionHarness();
        var raw = harness.RawTransport();
        var session = harness.Add(raw, "server");

        Assert.Null(session.Simulation);
        Assert.Same(raw, session.Transport);
    }

    [Fact]
    public void ASessionWithASimulationRunsOnTheDecoratorAndSaysSo() {
        using var harness = new SessionHarness();
        var raw = harness.RawTransport();

        var session = harness.Add(
            raw,
            "server",
            new() { Simulation = new(NetworkSimulationProfile.Awful, Seed) }
        );

        var simulation = session.Simulation;

        Assert.NotNull(simulation);

        // The announcement: what a host prints, and the way down to the real transport.
        Assert.Same(simulation, session.Transport);
        Assert.Same(raw, simulation.Inner);
        Assert.Equal(NetworkSimulationProfile.Awful, simulation.Profile);
    }

    /// <summary>
    ///     ⚠ The one that fails if the wrapping is done anywhere but before first use: a session that
    ///     wrapped after sizing its scratch buffer, or that kept the bare transport in its own field
    ///     and only published the wrapper, would pass every identity assertion above and send nothing
    ///     through the simulation at all.
    /// </summary>
    [Fact]
    public void TheSimulationIsInThePathAndNotOnlyInTheProperty() {
        using var harness = new SessionHarness();
        var server = harness.StartServer();

        // Latency with no loss: the handshake is delayed, never dropped, so the assertion is about
        // *when* a player appears rather than about a flake.
        var slow = new NetworkSimulationSettings(
            new() { Latency = TimeSpan.FromMilliseconds(500) },
            Seed
        );

        var client = harness.Add(harness.RawTransport(), "client", new() { Simulation = slow });
        client.StartClient();

        // Ten 16 ms steps is 160 ms of the session's clock — well short of a 500 ms one-way delay,
        // and a count of Update calls rather than a wall-clock budget.
        harness.Pump(10);

        Assert.Empty(server.Players);
        Assert.Null(client.LocalPlayer);

        // Sixty more carries the handshake both ways with room to spare.
        harness.Pump(60);

        Assert.Single(server.Players);
        Assert.NotNull(client.LocalPlayer);

        // And the payloads went through the decorator rather than round it.
        Assert.NotNull(client.Simulation);
        Assert.True(client.Simulation.SentPayloadCount > 0);
    }

    /// <summary>
    ///     The same seed and the same profile give the same deliveries, which is the property the
    ///     required-rather-than-defaulted seed exists to buy.
    /// </summary>
    [Fact]
    public void TheSameSeedLosesTheSamePackets() {
        Assert.Equal(Run(Seed), Run(Seed));

        // And a different one does not, or the assertion above would be about a simulation that
        // drops nothing.
        Assert.NotEqual(Run(Seed), Run(Seed + 1));
    }

    /// <summary>
    ///     ⚠ <c>Development</c> names <c>Broadband</c> because that profile's own summary already
    ///     says it is the one a development build should run with. A sixth named profile would be
    ///     surface with no caller.
    /// </summary>
    [Fact]
    public void TheDevelopmentSettingsAreTheProfileThatSaysItIsForDevelopment() {
        var settings = NetworkSimulationSettings.Development(Seed);

        Assert.Same(NetworkSimulationProfile.Broadband, settings.Profile);
        Assert.Equal(Seed, settings.Seed);
    }

    [Fact]
    public void ASessionThatDoesNotOwnItsTransportStillDoesNotDisposeIt() {
        using var harness = new SessionHarness();
        var raw = harness.RawTransport();

        // Not through the harness, which owns everything it makes: this one is the caller's.
        using var session = new NetworkSession(
            raw,
            new() { Simulation = new(NetworkSimulationProfile.Lan, Seed) },
            ownsTransport: false
        );

        session.StartServer();
        session.Dispose();

        // ⚠ The wrapper the session made is the session's and is disposed; the transport underneath
        // is the caller's and is not. A transport this still works on is the observable — a disposed
        // LocalTransport throws.
        raw.StartServer();
        raw.Poll(TimeSpan.FromMilliseconds(16), new SilentEvents());
    }

    /// <summary>How many payloads a lossy link threw away over a fixed number of steps.</summary>
    /// <param name="seed">The seed to draw with.</param>
    /// <returns>The count, which is a function of the seed and of nothing else.</returns>
    static long Run(ulong seed) {
        using var harness = new SessionHarness();
        var server = harness.StartServer();

        var client = harness.Add(
            harness.RawTransport(),
            "client",
            new() { Simulation = new(NetworkSimulationProfile.Awful, seed) }
        );

        client.StartClient();

        // Awful is 200 ms one way, so the handshake needs most of a second of the session's clock —
        // sixty-four 16 ms steps, counted rather than waited for.
        harness.Pump(64);

        Assert.NotNull(client.LocalPlayer);

        // ⚠ The session's own traffic is a handshake and a ping a second, which is far too few trials
        // for a 20 % chance to say anything. Two hundred unreliable payloads is a population, and
        // unreliable is the channel a simulation is allowed to drop at all — dropping a Reliable one
        // would be simulating a broken transport rather than a bad network.
        for (var i = 0; i < 200; i++) {
            client.SendToServer(SessionHarness.Bytes("tick"), Channel.Unreliable);
            harness.Pump(1);
        }

        Assert.NotNull(client.Simulation);
        Assert.True(client.Simulation.DroppedPayloadCount > 0, "nothing was dropped, so the seed decided nothing");

        return client.Simulation.DroppedPayloadCount;
    }
}

/// <summary>A transport events sink that wants nothing.</summary>
sealed class SilentEvents : ITransportEvents {
    /// <inheritdoc />
    public void OnConnected(TransportRole role, ConnectionId connection) { }

    /// <inheritdoc />
    public void OnDisconnected(TransportRole role, ConnectionId connection, DisconnectReason reason) { }

    /// <inheritdoc />
    public void OnData(TransportRole role, ConnectionId connection, Channel channel, ReadOnlySpan<byte> payload) { }
}
