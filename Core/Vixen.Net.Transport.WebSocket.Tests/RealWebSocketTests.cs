// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Net.Tests.Transport;
using Xunit;

namespace Vixen.Net.Transport.WebSocket.Tests;

/// <summary>The socket adapter, over loopback, with a real upgrade handshake in the way.</summary>
/// <remarks>
///     The same bargain the UDP transport's real-socket test makes. Everything about the protocol is
///     asserted deterministically over the in-memory pair; the only claim here is that
///     <see cref="SystemWebSocketFactory" /> can bind, upgrade, and carry a payload both ways. It
///     waits in a bounded loop rather than for a fixed time, because a test that waits for a real
///     network is a test that fails on a loaded build machine.
/// </remarks>
public sealed class RealWebSocketTests {
    static readonly TimeSpan Step = TimeSpan.FromMilliseconds(5);

    [Fact]
    public void APayloadCrossesTwoRealSockets() {
        var factory = new SystemWebSocketFactory();

        using var server = new WebSocketTransport(factory, new() { ListenAddress = new("ws://127.0.0.1:0/") });
        server.StartServer();

        var listening = server.ListeningOn;
        Assert.NotNull(listening);

        using var client = new WebSocketTransport(
            factory,
            new() { RemoteAddress = listening, ConnectTimeout = TimeSpan.FromSeconds(10) }
        );

        client.StartClient();

        var serverEvents = new EventRecorder();
        var clientEvents = new EventRecorder();

        Assert.True(
            Until(
                () => {
                    client.Poll(Step, clientEvents);
                    server.Poll(Step, serverEvents);

                    return clientEvents.Connects(TransportRole.Client).Count == 1;
                }
            ),
            "The upgrade did not complete over loopback."
        );

        // The number the server gave it, over a real socket rather than an in-memory queue.
        Assert.Equal(serverEvents.Connects(TransportRole.Server)[0], clientEvents.Connects(TransportRole.Client)[0]);

        client.SendToServer(Encoding.UTF8.GetBytes("over a real websocket"), Channel.Reliable);

        Assert.True(
            Until(
                () => {
                    client.Poll(Step, clientEvents);
                    server.Poll(Step, serverEvents);

                    return serverEvents.Texts(TransportRole.Server).Count == 1;
                }
            ),
            "The payload did not arrive over loopback."
        );

        Assert.Equal(["over a real websocket"], serverEvents.Texts(TransportRole.Server));
    }

    static bool Until(Func<bool> condition) {
        for (var attempt = 0; attempt < 600; attempt++) {
            if (condition()) {
                return true;
            }

            Thread.Sleep(5);
        }

        return false;
    }
}
