// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Net.Tests.Transport;
using Vixen.Net.Transport.Local;
using Vixen.Net.Transport.WebSocket;
using Vixen.Net.Transport.WebSocket.Tests;
using Xunit;

namespace Vixen.Net.Transport.Composite.Tests;

/// <summary>A composite over the in-process transport, held to the contract like anything else.</summary>
/// <remarks>
///     Wrapping one transport is the degenerate case and it is exactly what the suite should be run
///     against: everything it asserts has to survive the wrapping, and any of it that does not is a
///     bug in the wrapper rather than in the thing wrapped.
/// </remarks>
public sealed class CompositeOverLocalConformanceTests : TransportConformance {
    readonly LocalNetwork network = new();

    /// <inheritdoc />
    protected override ITransport CreateServer() => new CompositeTransport([new LocalTransport(network)]);

    /// <inheritdoc />
    protected override ITransport CreateClient() => new CompositeTransport([new LocalTransport(network)]);
}

/// <summary>Two transports at once, which is what the composite is actually for.</summary>
public sealed class CompositeTransportTests : IDisposable {
    static readonly Uri Address = new("ws://127.0.0.1:45200/");
    static readonly TimeSpan Step = TimeSpan.FromMilliseconds(16);

    readonly LocalNetwork network = new();
    readonly WebSocketLoop loop = new();
    readonly CompositeTransport server;
    readonly LocalTransport overLocal;
    readonly WebSocketTransport overWebSocket;
    readonly EventRecorder serverEvents = new();

    public CompositeTransportTests() {
        server = new([new LocalTransport(network), new WebSocketTransport(loop, new() { ListenAddress = Address })]);
        overLocal = new(network);
        overWebSocket = new(loop, new() { RemoteAddress = Address });
    }

    public void Dispose() {
        overLocal.Dispose();
        overWebSocket.Dispose();
        server.Dispose();
    }

    /// <summary>
    ///     Two clients on two different transports arrive at one server with different numbers.
    /// </summary>
    /// <remarks>
    ///     The bug this class exists to not have. Each inner transport numbers its own connections
    ///     from one, so without translation the second client to arrive would be handed a number the
    ///     first is already using — and every layer above keys players, ownership and baselines by it.
    /// </remarks>
    [Fact]
    public void ClientsOnDifferentTransports_AreNumberedApart() {
        server.StartServer();
        overLocal.StartClient();
        overWebSocket.StartClient();

        Pump();

        var connects = serverEvents.Connects(TransportRole.Server);

        Assert.Equal(2, connects.Count);
        Assert.Equal(2, connects.Distinct().Count());
        Assert.All(connects, id => Assert.True(id.IsValid));
    }

    [Fact]
    public void APayloadFromEitherTransport_ArrivesAtTheOneServer() {
        server.StartServer();
        overLocal.StartClient();
        overWebSocket.StartClient();
        Pump();

        overLocal.SendToServer(Encoding.UTF8.GetBytes("from the local one"), Channel.Reliable);
        overWebSocket.SendToServer(Encoding.UTF8.GetBytes("from the websocket"), Channel.Reliable);
        Pump();

        var texts = serverEvents.Texts(TransportRole.Server);

        Assert.Equal(2, texts.Count);
        Assert.Contains("from the local one", texts);
        Assert.Contains("from the websocket", texts);
    }

    [Fact]
    public void AReplyGoesBackOutTheTransportItCameInOn() {
        server.StartServer();
        overLocal.StartClient();
        overWebSocket.StartClient();
        Pump();

        var localEvents = new EventRecorder();
        var socketEvents = new EventRecorder();

        // Whichever number the composite gave each of them, replying by it has to reach that client
        // and only that client — the map is the whole feature.
        foreach (var id in serverEvents.Connects(TransportRole.Server)) {
            server.SendToClient(id, Encoding.UTF8.GetBytes($"for {id.Value}"), Channel.Reliable);
        }

        Pump(localEvents, socketEvents);

        Assert.Single(localEvents.Texts(TransportRole.Client));
        Assert.Single(socketEvents.Texts(TransportRole.Client));
        Assert.NotEqual(localEvents.Texts(TransportRole.Client)[0], socketEvents.Texts(TransportRole.Client)[0]);
    }

    [Fact]
    public void TheSmallestPayloadAnyOfThemCarries_IsWhatTheCompositePromises() {
        var smallest = Math.Min(LocalTransport.MaxPayloadBytes, WebSocketTransport.MaxPayloadBytes);

        Assert.Equal(smallest, server.Capabilities.MaxPayloadBytes);

        // One of them has a socket in it, so the composite is not in-process even though half of it
        // is. A caller that skipped serialisation on the strength of it would be wrong for half its
        // players.
        Assert.False(server.Capabilities.IsInProcess);
    }

    [Fact]
    public void ACompositeOfNothing_IsRefused() =>
        Assert.Throws<ArgumentException>(() => new CompositeTransport([]));

    void Pump(EventRecorder? local = null, EventRecorder? socket = null) {
        for (var round = 0; round < 8; round++) {
            overLocal.Poll(Step, local ?? new EventRecorder());
            overWebSocket.Poll(Step, socket ?? new EventRecorder());
            server.Poll(Step, serverEvents);
        }
    }
}
