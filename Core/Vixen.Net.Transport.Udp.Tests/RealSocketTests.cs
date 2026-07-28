// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using Vixen.Net.Tests.Transport;
using Xunit;

namespace Vixen.Net.Transport.Udp.Tests;

/// <summary>
///     The socket adapter, over the loopback interface, with the operating system in the way.
/// </summary>
/// <remarks>
///     <para>
///         Everything else in this project runs over an in-memory bus, because sequencing and
///         reassembly tested against a real socket are tested against a scheduler. This is the other
///         half of that bargain: the protocol is asserted deterministically elsewhere, and here the
///         only claim is that <see cref="UdpDatagramSocket" /> can actually bind, send and receive.
///     </para>
///     <para>
///         So it waits in a bounded loop rather than for a fixed time, and asserts one thing. A test
///         that waits for a real network is a test that fails on a loaded build machine, and the way
///         to keep it honest is to make it small and give it a deadline.
///     </para>
/// </remarks>
public sealed class RealSocketTests {
    static readonly TimeSpan Step = TimeSpan.FromMilliseconds(5);

    [Fact]
    public void APayloadCrossesTwoRealSockets() {
        var factory = new UdpDatagramSocketFactory();

        using var server = new UdpTransport(factory, new() { ListenEndPoint = new(IPAddress.Loopback, 0) });
        server.StartServer();

        var listening = Assert.IsType<IPEndPoint>(server.ListeningOn);

        using var client = new UdpTransport(
            factory,
            new() { ListenEndPoint = new(IPAddress.Loopback, 0), RemoteEndPoint = listening }
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
            "The handshake did not complete over loopback."
        );

        client.SendToServer(Encoding.UTF8.GetBytes("over a real socket"), Channel.Reliable);

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

        Assert.Equal(["over a real socket"], serverEvents.Texts(TransportRole.Server));
    }

    static bool Until(Func<bool> condition) {
        for (var attempt = 0; attempt < 400; attempt++) {
            if (condition()) {
                return true;
            }

            Thread.Sleep(5);
        }

        return false;
    }
}
