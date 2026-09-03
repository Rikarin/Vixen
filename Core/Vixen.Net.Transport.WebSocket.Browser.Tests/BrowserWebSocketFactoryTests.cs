// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Net.Tests.Transport;
using Xunit;

namespace Vixen.Net.Transport.WebSocket.Browser.Tests;

/// <summary>The browser client half, against a real server, over loopback.</summary>
/// <remarks>
///     <para>
///         The same bargain <see cref="Vixen.Net.Transport.WebSocket.Tests" />'s real-socket test
///         makes, and the same one the UDP transport's makes: everything about the protocol above
///         the seam is asserted deterministically over an in-memory pair by the conformance suite,
///         and the only claim here is that <c>BrowserWebSocketFactory</c> can connect, upgrade and
///         carry a payload both ways against a server that is not pretending.
///     </para>
///     <para>
///         ⚠ <b>The conformance suite cannot be used, and that is a statement about the transport
///         rather than a gap in the testing.</b> <c>TransportConformance</c> requires
///         <c>CreateServer</c> and <c>CreateClient</c>, and a browser has no server half at all —
///         so the suite would be asserting behaviour this factory is defined not to have. The
///         server here is <c>SystemWebSocketFactory</c>, which is what a browser client would
///         really be talking to, and it is already held to the conformance suite in its own project.
///     </para>
///     <para>
///         It waits in a bounded loop rather than for a fixed time, because a test that waits for a
///         real network is a test that fails on a loaded build machine.
///     </para>
/// </remarks>
public sealed class BrowserWebSocketFactoryTests {
    static readonly TimeSpan Step = TimeSpan.FromMilliseconds(5);

    [Fact]
    public void APayloadCrossesFromABrowserClientToARealServer() {
        using var server = new WebSocketTransport(
            new SystemWebSocketFactory(),
            new() { ListenAddress = new("ws://127.0.0.1:0/") }
        );

        server.StartServer();

        var listening = server.ListeningOn;
        Assert.NotNull(listening);

        // ⚠ The factory under test on the client and only on the client. This is the arrangement a
        // web build is in: a page dialling a dedicated server.
        using var client = new WebSocketTransport(
            new BrowserWebSocketFactory(),
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

        // The number the server gave it. A client learns what it is from the server's hello, so
        // this also says the first inbound message was decoded and not merely counted.
        Assert.Equal(serverEvents.Connects(TransportRole.Server)[0], clientEvents.Connects(TransportRole.Client)[0]);

        client.SendToServer(Encoding.UTF8.GetBytes("from a browser client"), Channel.Reliable);

        Assert.True(
            Until(
                () => {
                    client.Poll(Step, clientEvents);
                    server.Poll(Step, serverEvents);

                    return serverEvents.Texts(TransportRole.Server).Count == 1;
                }
            ),
            "The payload did not reach the server."
        );

        Assert.Equal(["from a browser client"], serverEvents.Texts(TransportRole.Server));

        // ⚠ And back, which the client half is the whole point of. A receive loop that never
        // enqueued would pass every assertion above, because the upgrade and the send are the
        // outbound direction only.
        var connection = serverEvents.Connects(TransportRole.Server)[0];

        server.SendToClient(connection, Encoding.UTF8.GetBytes("and back again"), Channel.Reliable);

        Assert.True(
            Until(
                () => {
                    server.Poll(Step, serverEvents);
                    client.Poll(Step, clientEvents);

                    return clientEvents.Texts(TransportRole.Client).Count == 1;
                }
            ),
            "The reply did not reach the client."
        );

        Assert.Equal(["and back again"], clientEvents.Texts(TransportRole.Client));
    }

    /// <summary>Being refused is an event, not an exception.</summary>
    /// <remarks>
    ///     Port 1 on loopback answers nothing. The contract says a connection nobody answers ends up
    ///     <see cref="WebSocketChannelState.Closed" /> rather than throwing, and that the transport
    ///     reports it through <c>OnDisconnected</c> on a later <c>Poll</c> — never inline.
    /// </remarks>
    [Fact]
    public void ARefusedConnectionIsReportedRatherThanThrown() {
        using var client = new WebSocketTransport(
            new BrowserWebSocketFactory(),
            new() { RemoteAddress = new("ws://127.0.0.1:1/"), ConnectTimeout = TimeSpan.FromSeconds(2) }
        );

        client.StartClient();

        var events = new EventRecorder();

        Assert.True(
            Until(
                () => {
                    client.Poll(Step, events);

                    return events.Disconnects(TransportRole.Client).Count == 1;
                }
            ),
            "A connection nobody answered never reported itself as disconnected."
        );

        Assert.Empty(events.Connects(TransportRole.Client));
    }

    /// <summary>A dial nobody answers ends up <c>Closed</c>, at the channel rather than the transport.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ This exists because the test above it was not enough, and sabotage is what showed
    ///         that. Deleting the line that moves a refused channel from <c>Connecting</c> to
    ///         <c>Closed</c> left the whole suite GREEN: <c>WebSocketTransport</c> has its own
    ///         <c>ConnectTimeout</c>, so a channel stuck in <c>Connecting</c> for ever still
    ///         produces a disconnection event — two seconds later, for the wrong reason. The
    ///         transport-level test could not tell "refused" from "gave up waiting".
    ///     </para>
    ///     <para>
    ///         So this one asks the channel directly, with no transport and therefore no timeout in
    ///         the way: the only thing that can close it is <c>Dial</c>'s own catch.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ARefusedChannelClosesItselfWithNoTimeoutInvolved() {
        var factory = new BrowserWebSocketFactory();

        // Port 1 on loopback answers nothing, and refuses immediately rather than hanging.
        using var channel = factory.Connect(new("ws://127.0.0.1:1/"));

        Assert.True(
            Until(() => channel.State == WebSocketChannelState.Closed),
            $"A refused dial stayed {channel.State} rather than closing itself."
        );
    }

    /// <summary>A page cannot listen, and says so where the caller can act on it.</summary>
    [Fact]
    public void ListeningRefuses() {
        var factory = new BrowserWebSocketFactory();

        var refusal = Assert.Throws<NotSupportedException>(() => factory.Listen(new("ws://127.0.0.1:0/")));

        // ⚠ The message is the whole value of the throw: it fires inside StartServer, where the
        // caller's next question is "then how do I run a server", and the answer is in it.
        Assert.Contains("cannot listen", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Composite", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>A transport whose server half cannot start refuses at StartServer.</summary>
    [Fact]
    public void StartingAServerRefuses() {
        using var transport = new WebSocketTransport(new BrowserWebSocketFactory());

        Assert.ThrowsAny<Exception>(transport.StartServer);
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
