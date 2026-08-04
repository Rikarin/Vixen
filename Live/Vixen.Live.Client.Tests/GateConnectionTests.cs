// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Xunit;

namespace Vixen.Live.Client.Tests;

/// <summary>
///     The socket that is allowed to be down. What is asserted is that being down is ordinary.
/// </summary>
public class GateConnectionTests {
    static readonly Uri Address = new("wss://gate.example/v1/stream");
    static readonly DateTimeOffset Noon = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Events_arrive_in_order_and_the_token_reaches_the_socket() {
        var sockets = new SocketFactory();
        await using var connection = Connect(sockets, out _);
        using var stop = new CancellationTokenSource();

        var heard = Listen(connection, stop.Token);
        var socket = await sockets.Next();

        socket.Say(new("catalog", "0.1.1+deadbeef", Noon));
        socket.Say(new("chat", "hello", Noon));

        Assert.Equal("catalog", (await heard.Reader.ReadAsync(TestContext.Current.CancellationToken)).Kind);
        Assert.Equal("chat", (await heard.Reader.ReadAsync(TestContext.Current.CancellationToken)).Kind);
        Assert.Equal("a.b.c", socket.Token);
        Assert.Equal(1, connection.Connections);

        await stop.CancelAsync();
    }

    /// <summary>
    ///     A socket closing is a reconnect rather than an end, so a loop that stopped when the
    ///     enumeration did would stop the first time a train went into a tunnel.
    /// </summary>
    [Fact]
    public async Task A_closed_socket_is_reopened_and_the_listener_keeps_listening() {
        var sockets = new SocketFactory();
        await using var connection = Connect(sockets, out _);
        using var stop = new CancellationTokenSource();

        var heard = Listen(connection, stop.Token);

        (await sockets.Next()).End();

        var second = await sockets.Next();

        second.Say(new("chat", "still here", Noon));

        Assert.Equal("still here", (await heard.Reader.ReadAsync(TestContext.Current.CancellationToken)).Detail);
        Assert.Equal(2, connection.Connections);

        await stop.CancelAsync();
    }

    [Fact]
    public async Task A_socket_that_refuses_to_open_is_tried_again() {
        var sockets = new SocketFactory { Refusals = 2 };
        await using var connection = Connect(sockets, out _);
        using var stop = new CancellationTokenSource();

        var heard = Listen(connection, stop.Token);

        var socket = await sockets.Next();

        socket.Say(new("chat", "at last", Noon));

        Assert.Equal("at last", (await heard.Reader.ReadAsync(TestContext.Current.CancellationToken)).Detail);
        Assert.Equal(3, sockets.Made);

        await stop.CancelAsync();
    }

    /// <summary>
    ///     A frame this client cannot read is a newer gate saying something newer. Skipping it is what
    ///     makes the socket forward-compatible; failing on it would make every added event kind a
    ///     client update.
    /// </summary>
    [Fact]
    public async Task An_unreadable_frame_is_skipped_rather_than_ending_the_socket() {
        var sockets = new SocketFactory();
        await using var connection = Connect(sockets, out _);
        using var stop = new CancellationTokenSource();

        var heard = Listen(connection, stop.Token);
        var socket = await sockets.Next();

        socket.SayRaw("{ this is not json");
        socket.Say(new("chat", "after the noise", Noon));

        Assert.Equal("after the noise", (await heard.Reader.ReadAsync(TestContext.Current.CancellationToken)).Detail);
        Assert.Equal(1, connection.Connections);

        await stop.CancelAsync();
    }

    /// <summary>
    ///     A client at a sign-in screen holds one of these already and expects it to start working
    ///     when it signs in, so no session is a wait rather than a failure.
    /// </summary>
    [Fact]
    public async Task With_no_session_it_waits_rather_than_opening_anything() {
        var sockets = new SocketFactory();
        var client = new GateClient(new HttpClient { BaseAddress = new("https://gate.example/v1/") });
        await using var connection = new GateConnection(Address, client, sockets.Make) {
            FirstBackoff = TimeSpan.FromMilliseconds(5),
            MaximumBackoff = TimeSpan.FromMilliseconds(20)
        };
        using var stop = new CancellationTokenSource();

        var heard = Listen(connection, stop.Token);

        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(0, sockets.Made);
        Assert.False(connection.Connected);
        Assert.Equal(0, heard.Reader.Count);

        await stop.CancelAsync();
    }

    // ── Plumbing ────────────────────────────────────────────────────────────────────────────────

    static GateConnection Connect(SocketFactory sockets, out GateClient client) {
        var gate = new FakeGate().Answers(
            new SignInResponse("a.b.c", Guid.NewGuid(), Noon.AddHours(12)),
            GateJson.Default.SignInResponse
        );

        client = new(gate.Client);
        client.SignInAsync("development", "alice", CancellationToken.None).GetAwaiter().GetResult();

        return new(Address, client, sockets.Make) {
            FirstBackoff = TimeSpan.FromMilliseconds(1),
            MaximumBackoff = TimeSpan.FromMilliseconds(10)
        };
    }

    static Channel<GateEvent> Listen(GateConnection connection, CancellationToken cancellation) {
        var heard = Channel.CreateUnbounded<GateEvent>();

        _ = Task.Run(
            async () => {
                try {
                    await foreach (var message in connection.ListenAsync(cancellation)) {
                        await heard.Writer.WriteAsync(message, CancellationToken.None);
                    }
                } catch (OperationCanceledException) {
                    // The test is over.
                } finally {
                    heard.Writer.TryComplete();
                }
            },
            CancellationToken.None
        );

        return heard;
    }

    sealed class SocketFactory {
        readonly Channel<FakeSocket> made = Channel.CreateUnbounded<FakeSocket>();

        /// <summary>How many connection attempts to refuse before letting one through.</summary>
        public int Refusals { get; init; }

        /// <summary>How many sockets have been asked for.</summary>
        public int Made { get; private set; }

        public IGateSocket Make() {
            var refuse = Made < Refusals;

            Made++;

            var socket = new FakeSocket(refuse);

            if (!refuse) {
                made.Writer.TryWrite(socket);
            }

            return socket;
        }

        public async Task<FakeSocket> Next() {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            return await made.Reader.ReadAsync(deadline.Token);
        }
    }

    sealed class FakeSocket(bool refuse) : IGateSocket {
        readonly Channel<string?> frames = Channel.CreateUnbounded<string?>();

        public bool Connected { get; private set; }

        public string? Token { get; private set; }

        public Task ConnectAsync(Uri address, string token, CancellationToken cancellation) {
            if (refuse) {
                return Task.FromException(new WebSocketException("no route to the gate"));
            }

            Token = token;
            Connected = true;

            return Task.CompletedTask;
        }

        public async Task<string?> ReceiveAsync(CancellationToken cancellation) =>
            await frames.Reader.ReadAsync(cancellation);

        public Task SendAsync(string text, CancellationToken cancellation) => Task.CompletedTask;

        public ValueTask DisposeAsync() {
            Connected = false;
            frames.Writer.TryComplete();

            return ValueTask.CompletedTask;
        }

        public void Say(GateEvent message) =>
            frames.Writer.TryWrite(JsonSerializer.Serialize(message, GateJson.Default.GateEvent));

        public void SayRaw(string text) => frames.Writer.TryWrite(text);

        /// <summary>The socket closed, as a tunnel or a deploy does it.</summary>
        public void End() => frames.Writer.TryWrite(null);
    }
}
