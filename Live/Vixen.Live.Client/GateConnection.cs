// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Vixen.Live.Client;

/// <summary>A socket to a gate. The one this abstracts over is allowed to be missing.</summary>
/// <remarks>
///     ⚠ <b>A seam so that a test does not need a server</b> — and, less obviously, so that a
///     platform whose WebSocket is not <c>ClientWebSocket</c> (a console SDK, a browser build) can
///     supply its own without this file knowing.
/// </remarks>
public interface IGateSocket : IAsyncDisposable {
    /// <summary>Whether it is up.</summary>
    bool Connected { get; }

    /// <summary>Opens it.</summary>
    /// <param name="address">Where.</param>
    /// <param name="token">The session token, sent as a bearer header.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>When open.</returns>
    Task ConnectAsync(Uri address, string token, CancellationToken cancellation);

    /// <summary>Waits for the next thing the gate said.</summary>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>The message, or null when the socket ended.</returns>
    Task<string?> ReceiveAsync(CancellationToken cancellation);

    /// <summary>Says something. The gate treats anything as a ping.</summary>
    /// <param name="text">What.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>When sent.</returns>
    Task SendAsync(string text, CancellationToken cancellation);
}

/// <summary>The client's half of the service plane's socket, with the reconnect built in.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This socket being down is a normal state, not an error state.</b> Doc 27 is explicit
///         that nothing a player is waiting on travels here, so a client that showed a modal when it
///         dropped would be showing a modal for a lost whisper. It reconnects with backoff, forever,
///         and says nothing about it.
///     </para>
///     <para>
///         ⚠ <b>Nothing is replayed across a reconnect, and nothing needs to be.</b> A push is a hint
///         to go and ask: <c>catalog</c> means fetch the catalog, <c>draining</c> means ask the gate
///         where to play. A design that queued missed events would be a design where the queue's
///         depth eventually matters.
///     </para>
/// </remarks>
public sealed class GateConnection : IAsyncDisposable {
    readonly Uri address;
    readonly GateClient gate;
    readonly Func<IGateSocket> sockets;

    IGateSocket? socket;

    /// <summary>Opens sockets to a gate.</summary>
    /// <param name="address">The <c>wss://…/v1/stream</c> address.</param>
    /// <param name="gate">Where the session token comes from.</param>
    /// <param name="sockets">How to make one. Defaults to a real <c>ClientWebSocket</c>.</param>
    /// <exception cref="ArgumentNullException">The address or the client is null.</exception>
    public GateConnection(Uri address, GateClient gate, Func<IGateSocket>? sockets = null) {
        this.address = address ?? throw new ArgumentNullException(nameof(address));
        this.gate = gate ?? throw new ArgumentNullException(nameof(gate));
        this.sockets = sockets ?? (() => new WebSocketGateSocket());
    }

    /// <summary>How long to wait before the first reconnection attempt.</summary>
    public TimeSpan FirstBackoff { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest it will ever wait between attempts.</summary>
    public TimeSpan MaximumBackoff { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How many times a socket has come up. A number for a diagnostics panel.</summary>
    public int Connections { get; private set; }

    /// <summary>Whether a socket is up right now.</summary>
    public bool Connected => socket?.Connected == true;

    /// <summary>Everything the gate says, across as many sockets as it takes.</summary>
    /// <param name="cancellation">Ends the connection for good.</param>
    /// <returns>The events, in the order they arrived.</returns>
    /// <remarks>
    ///     ⚠ <b>Enumerating this never completes on its own.</b> It ends when the caller cancels, and
    ///     that is deliberate: a socket closing is a reconnect rather than an end, so a loop that
    ///     stopped when the enumeration did would stop the first time a train went into a tunnel.
    /// </remarks>
    public async IAsyncEnumerable<GateEvent> ListenAsync([EnumeratorCancellation] CancellationToken cancellation) {
        var backoff = FirstBackoff;

        while (!cancellation.IsCancellationRequested) {
            if (gate.Session is null) {
                // No session, nothing to listen as. Wait rather than fail: a client at a sign-in
                // screen holds one of these already and expects it to start working when it signs in.
                await Wait(backoff, cancellation).ConfigureAwait(false);
                backoff = Longer(backoff);

                continue;
            }

            var open = sockets();

            if (!await TryConnect(open, gate.Session.Token, cancellation).ConfigureAwait(false)) {
                await open.DisposeAsync().ConfigureAwait(false);
                await Wait(backoff, cancellation).ConfigureAwait(false);
                backoff = Longer(backoff);

                continue;
            }

            socket = open;
            Connections++;
            backoff = FirstBackoff;

            while (true) {
                string? text;

                try {
                    text = await open.ReceiveAsync(cancellation).ConfigureAwait(false);
                } catch (Exception failure) when (failure is WebSocketException or ObjectDisposedException or IOException) {
                    break;
                }

                if (text is null) {
                    break;
                }

                GateEvent? message;

                try {
                    message = JsonSerializer.Deserialize(text, GateJson.Default.GateEvent);
                } catch (JsonException) {
                    // A frame this client cannot read is a newer gate saying something newer. Skipping
                    // it is what makes the socket forward-compatible; failing on it would make every
                    // added event kind a client update.
                    continue;
                }

                if (message is not null) {
                    yield return message;
                }
            }

            socket = null;
            await open.DisposeAsync().ConfigureAwait(false);
            await Wait(backoff, cancellation).ConfigureAwait(false);
            backoff = Longer(backoff);
        }
    }

    /// <summary>Closes whatever is open.</summary>
    /// <returns>When closed.</returns>
    public async ValueTask DisposeAsync() {
        if (socket is not null) {
            await socket.DisposeAsync().ConfigureAwait(false);
            socket = null;
        }
    }

    async Task<bool> TryConnect(IGateSocket open, string token, CancellationToken cancellation) {
        try {
            await open.ConnectAsync(address, token, cancellation).ConfigureAwait(false);

            return true;
        } catch (Exception failure) when (failure is WebSocketException or HttpRequestException or IOException) {
            return false;
        }
    }

    TimeSpan Longer(TimeSpan backoff) =>
        TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaximumBackoff.Ticks));

    static async Task Wait(TimeSpan span, CancellationToken cancellation) {
        try {
            await Task.Delay(span, cancellation).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // The caller is done with us. The loop's own condition ends it.
        }
    }
}

/// <summary>A real <c>ClientWebSocket</c>.</summary>
public sealed class WebSocketGateSocket : IGateSocket {
    readonly ClientWebSocket socket = new();
    readonly byte[] buffer = new byte[8 * 1024];

    /// <inheritdoc />
    public bool Connected => socket.State == WebSocketState.Open;

    /// <inheritdoc />
    public async Task ConnectAsync(Uri address, string token, CancellationToken cancellation) {
        // The header, never a query string: a token in a URL is written to every access log and proxy
        // cache between here and the gate, and the gate refuses one there for that reason.
        socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");

        await socket.ConnectAsync(address, cancellation).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string?> ReceiveAsync(CancellationToken cancellation) {
        var text = new StringBuilder();

        while (true) {
            var received = await socket.ReceiveAsync(buffer, cancellation).ConfigureAwait(false);

            if (received.MessageType == WebSocketMessageType.Close) {
                return null;
            }

            text.Append(Encoding.UTF8.GetString(buffer, 0, received.Count));

            if (received.EndOfMessage) {
                return text.ToString();
            }
        }
    }

    /// <inheritdoc />
    public Task SendAsync(string text, CancellationToken cancellation) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellation);

    /// <inheritdoc />
    public ValueTask DisposeAsync() {
        socket.Dispose();

        return ValueTask.CompletedTask;
    }
}
