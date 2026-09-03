// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Net.WebSockets;
using Channels = System.Threading.Channels;

namespace Vixen.Net.Transport.WebSocket;

/// <summary>WebSockets for a page: the client half, and nothing else.</summary>
/// <remarks>
///     <para>
///         <b>A browser cannot open a UDP socket and cannot listen on anything</b>, so this is the
///         only transport a web build has and it is half a transport. <see cref="Listen" /> refuses
///         rather than returning something that never accepts, because a listener that silently
///         never fires is the failure this repository keeps writing gates against.
///     </para>
///     <para>
///         <b>⚠ It is <c>ClientWebSocket</c>, and that is the finding rather than a shortcut.</b>
///         <c>Vixen.Net.Transport.WebSocket</c>'s README says the browser path is owed because
///         "<c>System.Net.WebSockets</c> is not available" there, and <c>docs/plan/16</c> routes it
///         through a <c>Vixen.Platform.Web</c> <c>ISocket</c> that does not exist in the tree. Both
///         are wrong: the <c>browser-wasm</c> runtime pack ships
///         <c>System.Net.WebSockets.Client.dll</c> containing a real
///         <c>System.Net.WebSockets.BrowserWebSocket</c>, built for <c>net10.0-browser</c> against
///         <c>System.Runtime.InteropServices.JavaScript</c> — it is the page's own
///         <c>WebSocket</c> behind the ordinary API. So no <c>[JSImport]</c> module is needed here,
///         and this project ships no JavaScript at all.
///     </para>
///     <para>
///         <b>⚠ Which is also what makes it testable, and that is why the file is written to have no
///         conditional compilation in it.</b> Every line below compiles and runs unchanged on
///         <c>net10.0</c>, where <c>ClientWebSocket</c> is the desktop implementation of the same
///         API. <c>Vixen.Net.Transport.WebSocket.Browser.Tests</c> links this file as source and
///         drives it against a real <c>SystemWebSocketFactory</c> server over loopback — so the
///         thing a browser will run is exercised end to end by <c>nuke Test</c>, on a machine with
///         no browser. What that does <i>not</i> cover is <c>BrowserWebSocket</c> itself, which is
///         Microsoft's code and not this repository's to prove.
///     </para>
///     <para>
///         <b>No threads, deliberately.</b> The receive and send loops are started with
///         <c>_ = …</c> and never touch <c>Task.Run</c>. A single-threaded WebAssembly build has one
///         thread and a cooperative scheduler tied to the JavaScript event loop, so a continuation
///         runs when the frame yields — which <c>WebFrameLoop</c> does every
///         <c>requestAnimationFrame</c>. A <c>Task.Run</c> here would either deadlock or need the
///         threading build, and the transport contract only asks that nothing is delivered outside
///         <c>Poll</c>, which the queues below satisfy on one thread as well as on two.
///     </para>
/// </remarks>
public sealed class BrowserWebSocketFactory : IWebSocketFactory {
    /// <summary>Refuses. A page cannot listen.</summary>
    /// <param name="address">Ignored.</param>
    /// <returns>Never.</returns>
    /// <exception cref="NotSupportedException">Always.</exception>
    /// <remarks>
    ///     ⚠ A throw and not a listener that accepts nothing. <c>WebSocketTransport.StartServer</c>
    ///     is the only caller, so this fires exactly when somebody has tried to run a server in a
    ///     browser — and the useful answer is to say so at the call, not to leave a game waiting on
    ///     an accept that cannot come.
    /// </remarks>
    public IWebSocketListener Listen(Uri address) =>
        throw new NotSupportedException(
            "A browser cannot listen for connections, so the web build has no server half. Start "
            + "only the client (NetworkSession.StartClient), and run the server on a desktop or "
            + "dedicated build — Vixen.Net.Transport.Composite is how one server takes both."
        );

    /// <inheritdoc />
    public IWebSocketChannel Connect(Uri address) {
        ArgumentNullException.ThrowIfNull(address);

        return BrowserWebSocketChannel.Connecting(address);
    }
}

/// <summary>One <c>ClientWebSocket</c>, adapted from asynchronous to polled.</summary>
sealed class BrowserWebSocketChannel : IWebSocketChannel {
    readonly ConcurrentQueue<byte[]> inbound = new();
    readonly Channels.Channel<byte[]> outbound = Channels.Channel.CreateUnbounded<byte[]>();
    readonly CancellationTokenSource stopping = new();

    ClientWebSocket? socket;
    int state = (int)WebSocketChannelState.Connecting;

    public WebSocketChannelState State => (WebSocketChannelState)Volatile.Read(ref state);

    public static BrowserWebSocketChannel Connecting(Uri address) {
        var channel = new BrowserWebSocketChannel();

        // Not awaited: Connect() returns a channel that is Connecting until it is not, and the
        // transport reports the outcome as an event on a later Poll.
        _ = channel.Dial(address);

        return channel;
    }

    public void Send(ReadOnlySpan<byte> message) => outbound.Writer.TryWrite(message.ToArray());

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

    /// <summary>Nothing: the loops are driven by the runtime's scheduler, not by the caller.</summary>
    public void Pump() { }

    public void Dispose() {
        Volatile.Write(ref state, (int)WebSocketChannelState.Closed);
        Close();
        stopping.Cancel();
        socket?.Dispose();
        stopping.Dispose();
    }

    async Task Dial(Uri address) {
        var client = new ClientWebSocket();

        try {
            await client.ConnectAsync(address, stopping.Token).ConfigureAwait(false);
            socket = client;
            Volatile.Write(ref state, (int)WebSocketChannelState.Open);
            _ = ReceiveLoop(stopping.Token);
            _ = SendLoop(stopping.Token);
        } catch (Exception exception) when (Ended(exception)) {
            // Nobody there, or they refused. Closed rather than thrown: being refused is an
            // ordinary thing for a client to be, and an exception on a loop nobody awaits has
            // nowhere to go — in a browser it would surface as an unhandled rejection in the
            // console and as silence to the game.
            client.Dispose();
            Volatile.Write(ref state, (int)WebSocketChannelState.Closed);
        }
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
                        return;
                    }

                    offset += result.Count;
                } while (!result.EndOfMessage && offset < buffer.Length);

                inbound.Enqueue(buffer[..offset]);
            }
        } catch (Exception exception) when (Ended(exception)) {
            // Every one of these means the same thing to the layer above: it ended.
        } finally {
            Volatile.Write(ref state, (int)WebSocketChannelState.Closed);
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
        } catch (Exception exception) when (Ended(exception)) {
            Volatile.Write(ref state, (int)WebSocketChannelState.Closed);
        }
    }

    /// <summary>Whether an exception just means the connection is over.</summary>
    /// <remarks>
    ///     ⚠ No <c>SocketException</c>, which the desktop factory's filter names. There is no socket
    ///     under a browser WebSocket to raise one, and naming a type from
    ///     <c>System.Net.Sockets</c> in a filter that never matches is how an assembly nobody needs
    ///     ends up in a trimmed web build.
    /// </remarks>
    static bool Ended(Exception exception) =>
        exception is WebSocketException or OperationCanceledException or ObjectDisposedException
            or InvalidOperationException;
}
