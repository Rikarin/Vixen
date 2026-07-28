// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Channels = System.Threading.Channels;

namespace Vixen.Net.Transport.WebSocket;

/// <summary>Real WebSockets, over real sockets.</summary>
/// <remarks>
///     <para>
///         Thin on purpose, like the UDP transport's socket adapter: everything that could be got
///         wrong lives above the seam and is tested over an in-memory pair. What is left here is an
///         upgrade handshake and the async-to-polled adaptation, and both are the kind of thing that
///         is either right or obviously broken.
///     </para>
///     <para>
///         <b>A <see cref="TcpListener" /> and the framework's own framing, rather than
///         <c>HttpListener</c>.</b> The upgrade is thirty lines of RFC 6455 — a SHA-1 of the client's
///         key and a fixed GUID — and doing it here avoids <c>HttpListener</c>'s URL-reservation
///         behaviour on Windows and its uneven support elsewhere. The framing underneath is
///         <c>WebSocket.CreateFromStream</c>, which is the part worth not writing.
///     </para>
///     <para>
///         <b>The threads stop at this interface.</b> A WebSocket is asynchronous and the transport
///         contract says nothing is delivered outside <c>Poll</c>, so each channel runs its own
///         receive and send loops and hands over queues. Nothing above ever sees a task.
///     </para>
/// </remarks>
public sealed class SystemWebSocketFactory : IWebSocketFactory {
    /// <inheritdoc />
    public IWebSocketListener Listen(Uri address) {
        ArgumentNullException.ThrowIfNull(address);

        return new SystemWebSocketListener(address);
    }

    /// <inheritdoc />
    public IWebSocketChannel Connect(Uri address) {
        ArgumentNullException.ThrowIfNull(address);

        return SystemWebSocketChannel.Connecting(address);
    }
}

sealed class SystemWebSocketListener : IWebSocketListener {
    const string UpgradeGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    readonly TcpListener listener;
    readonly ConcurrentQueue<IWebSocketChannel> ready = new();
    readonly CancellationTokenSource stopping = new();

    public Uri Address { get; }

    public SystemWebSocketListener(Uri address) {
        var endPoint = new IPEndPoint(Resolve(address.Host), address.Port);
        listener = new(endPoint);
        listener.Start();

        var bound = (IPEndPoint)listener.LocalEndpoint;
        Address = new($"ws://{bound.Address}:{bound.Port.ToString(CultureInfo.InvariantCulture)}/");

        _ = AcceptLoop(stopping.Token);
    }

    public bool TryAccept(out IWebSocketChannel? channel) => ready.TryDequeue(out channel);

    public void Pump() { }

    public void Dispose() {
        stopping.Cancel();
        listener.Stop();

        while (ready.TryDequeue(out var channel)) {
            channel.Dispose();
        }

        stopping.Dispose();
    }

    static IPAddress Resolve(string host) =>
        IPAddress.TryParse(host, out var address) ? address : IPAddress.Loopback;

    async Task AcceptLoop(CancellationToken token) {
        while (!token.IsCancellationRequested) {
            TcpClient client;

            try {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                return;
            } catch (SocketException) {
                return;
            } catch (ObjectDisposedException) {
                return;
            }

            // Each upgrade on its own, so one client that opens a socket and says nothing cannot
            // hold up everybody behind it in the accept queue.
            _ = Upgrade(client, token);
        }
    }

    async Task Upgrade(TcpClient client, CancellationToken token) {
        try {
            client.NoDelay = true;

            var stream = client.GetStream();
            var key = await ReadKey(stream, token).ConfigureAwait(false);

            if (key is null) {
                client.Dispose();

                return;
            }

            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + UpgradeGuid)));

            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n"
                + "Upgrade: websocket\r\n"
                + "Connection: Upgrade\r\n"
                + $"Sec-WebSocket-Accept: {accept}\r\n\r\n"
            );

            await stream.WriteAsync(response, token).ConfigureAwait(false);

            var socket = System.Net.WebSockets.WebSocket.CreateFromStream(
                stream,
                new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(30) }
            );

            ready.Enqueue(SystemWebSocketChannel.Accepted(socket, client));
        } catch (Exception exception) when (exception is IOException or WebSocketException or ObjectDisposedException) {
            // A half-finished upgrade is a client that went away, which is not an error here.
            client.Dispose();
        }
    }

    static async Task<string?> ReadKey(NetworkStream stream, CancellationToken token) {
        var buffer = new byte[4096];
        var read = 0;

        while (read < buffer.Length) {
            var got = await stream.ReadAsync(buffer.AsMemory(read), token).ConfigureAwait(false);

            if (got == 0) {
                return null;
            }

            read += got;
            var text = Encoding.ASCII.GetString(buffer, 0, read);

            if (!text.Contains("\r\n\r\n", StringComparison.Ordinal)) {
                continue;
            }

            foreach (var line in text.Split("\r\n")) {
                if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)) {
                    return line["Sec-WebSocket-Key:".Length..].Trim();
                }
            }

            return null;
        }

        return null;
    }
}

sealed class SystemWebSocketChannel : IWebSocketChannel {
    readonly ConcurrentQueue<byte[]> inbound = new();
    // Asynchronous, not a BlockingCollection, and that distinction cost an afternoon. A blocking
    // enumeration inside an async method with no await before it runs on the *caller's* thread —
    // so starting the send loop from the accept path blocked the accept path, and a connection that
    // had completed its handshake was never handed over. Nothing threw; it simply stopped.
    readonly Channels.Channel<byte[]> outbound =
        Channels.Channel.CreateUnbounded<byte[]>(new() { SingleReader = true });
    readonly CancellationTokenSource stopping = new();

    System.Net.WebSockets.WebSocket? socket;
    TcpClient? owned;
    volatile int state = (int)WebSocketChannelState.Connecting;

    public WebSocketChannelState State => (WebSocketChannelState)state;

    SystemWebSocketChannel() { }

    public static SystemWebSocketChannel Accepted(System.Net.WebSockets.WebSocket socket, TcpClient owned) {
        var channel = new SystemWebSocketChannel { socket = socket, owned = owned };
        channel.state = (int)WebSocketChannelState.Open;
        channel.Start();

        return channel;
    }

    public static SystemWebSocketChannel Connecting(Uri address) {
        var channel = new SystemWebSocketChannel();
        _ = channel.Dial(address);

        return channel;
    }

    public void Send(ReadOnlySpan<byte> message) {
        if (State == WebSocketChannelState.Open) {
            outbound.Writer.TryWrite(message.ToArray());
        }
    }

    public bool TryReceive(Span<byte> buffer, out int length) {
        length = 0;

        if (!inbound.TryDequeue(out var message)) {
            return false;
        }

        if (message.Length > buffer.Length) {
            return false;
        }

        message.CopyTo(buffer);
        length = message.Length;

        return true;
    }

    public void Close() => outbound.Writer.TryComplete();

    public void Pump() { }

    public void Dispose() {
        state = (int)WebSocketChannelState.Closed;
        Close();
        stopping.Cancel();
        socket?.Dispose();
        owned?.Dispose();
        stopping.Dispose();
    }

    async Task Dial(Uri address) {
        var client = new ClientWebSocket();

        try {
            await client.ConnectAsync(address, stopping.Token).ConfigureAwait(false);
            socket = client;
            state = (int)WebSocketChannelState.Open;
            Start();
        } catch (Exception exception) when (exception is WebSocketException or OperationCanceledException
                                                or ObjectDisposedException or SocketException) {
            // Nobody there, or they refused. Closed rather than thrown: the transport reports being
            // refused as an event, and an exception on a background task has nowhere to go.
            client.Dispose();
            state = (int)WebSocketChannelState.Closed;
        }
    }

    void Start() {
        _ = ReceiveLoop(stopping.Token);
        _ = SendLoop(stopping.Token);
    }

    async Task ReceiveLoop(CancellationToken token) {
        var buffer = new byte[WebSocketTransport.MaxPayloadBytes + 64];

        try {
            while (!token.IsCancellationRequested && socket is { State: WebSocketState.Open }) {
                var offset = 0;
                ValueWebSocketReceiveResult result;

                do {
                    result = await socket.ReceiveAsync(buffer.AsMemory(offset), token).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close) {
                        state = (int)WebSocketChannelState.Closed;

                        return;
                    }

                    offset += result.Count;
                } while (!result.EndOfMessage && offset < buffer.Length);

                inbound.Enqueue(buffer[..offset]);
            }
        } catch (Exception exception) when (exception is WebSocketException or OperationCanceledException
                                                or ObjectDisposedException or InvalidOperationException) {
            // Every one of these means the same thing to the layer above: it ended.
        } finally {
            state = (int)WebSocketChannelState.Closed;
        }
    }

    async Task SendLoop(CancellationToken token) {
        try {
            await foreach (var message in outbound.Reader.ReadAllAsync(token).ConfigureAwait(false)) {
                if (socket is not { State: WebSocketState.Open }) {
                    return;
                }

                await socket.SendAsync(message, WebSocketMessageType.Binary, endOfMessage: true, token)
                    .ConfigureAwait(false);
            }

            if (socket is { State: WebSocketState.Open }) {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, token).ConfigureAwait(false);
            }
        } catch (Exception exception) when (exception is WebSocketException or OperationCanceledException
                                                or ObjectDisposedException or InvalidOperationException) {
            state = (int)WebSocketChannelState.Closed;
        }
    }
}
