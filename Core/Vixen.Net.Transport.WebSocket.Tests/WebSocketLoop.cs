// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Transport.WebSocket.Tests;

/// <summary>Every WebSocket in a test, with no socket underneath any of them.</summary>
/// <remarks>
///     <para>
///         The counterpart of the UDP transport's <c>DatagramBus</c>, and it exists for the same
///         reason: a message crosses when the receiver polls, so a test's timeline is the sequence of
///         calls it makes rather than a scheduler's opinion, and every run is the same run.
///     </para>
///     <para>
///         It is deliberately not a fake WebSocket — it does not frame, mask or handshake. Those are
///         the parts of the real thing that <c>System.Net.WebSockets</c> already implements and that
///         a re-implementation here would only be able to get wrong in a way the product would not.
///         What this models is the contract the transport was written against: an ordered, reliable,
///         message-shaped pipe that opens, carries, and closes.
///     </para>
/// </remarks>
public sealed class WebSocketLoop : IWebSocketFactory {
    readonly Dictionary<string, Listener> listeners = [];

    /// <summary>How many listeners are up.</summary>
    public int ListenerCount => listeners.Count;

    /// <inheritdoc />
    public IWebSocketListener Listen(Uri address) {
        ArgumentNullException.ThrowIfNull(address);

        var key = Key(address);

        if (listeners.ContainsKey(key)) {
            throw new InvalidOperationException($"Something is already listening on {key}.");
        }

        var listener = new Listener(this, address, key);
        listeners[key] = listener;

        return listener;
    }

    /// <inheritdoc />
    public IWebSocketChannel Connect(Uri address) {
        ArgumentNullException.ThrowIfNull(address);

        var ours = new Pipe();
        var theirs = new Pipe();
        var client = new Channel(ours, theirs);

        if (!listeners.TryGetValue(Key(address), out var listener)) {
            // Nobody there. Closed rather than throwing, because being refused is an ordinary thing
            // for a client to be and the transport has to report it as an event either way.
            client.Refuse();

            return client;
        }

        listener.Offer(new Channel(theirs, ours));
        client.Open();

        return client;
    }

    static string Key(Uri address) => $"{address.Host}:{address.Port}";

    void Forget(string key) => listeners.Remove(key);

    /// <summary>One direction of a pipe: what one end wrote and the other has not taken yet.</summary>
    sealed class Pipe {
        readonly Queue<byte[]> messages = new();

        public bool Closed { get; private set; }

        public void Write(ReadOnlySpan<byte> message) {
            if (!Closed) {
                messages.Enqueue(message.ToArray());
            }
        }

        public bool TryRead(out byte[]? message) => messages.TryDequeue(out message);

        // Closing does not discard what is already in flight, which is what a real close does too:
        // the bytes were handed to the network before the close was.
        public void Close() => Closed = true;

        public bool Drained => messages.Count == 0;
    }

    sealed class Channel(Pipe inbound, Pipe outbound) : IWebSocketChannel {
        public WebSocketChannelState State { get; private set; } = WebSocketChannelState.Connecting;

        public void Open() => State = WebSocketChannelState.Open;

        public void Refuse() => State = WebSocketChannelState.Closed;

        public void Send(ReadOnlySpan<byte> message) {
            if (State == WebSocketChannelState.Open) {
                outbound.Write(message);
            }
        }

        public bool TryReceive(Span<byte> buffer, out int length) {
            length = 0;

            if (!inbound.TryRead(out var message) || message is null) {
                return false;
            }

            if (message.Length > buffer.Length) {
                return false;
            }

            message.CopyTo(buffer);
            length = message.Length;

            return true;
        }

        public void Close() {
            outbound.Close();

            if (State != WebSocketChannelState.Connecting) {
                State = WebSocketChannelState.Open;
            }
        }

        public void Pump() {
            // The far end hung up, and everything it sent before doing so has been taken. Only then
            // is the channel closed — a close that discarded undelivered messages would make "the
            // server said why it was disconnecting me" a race.
            if (State == WebSocketChannelState.Open && inbound.Closed && inbound.Drained) {
                State = WebSocketChannelState.Closed;
            }
        }

        public void Dispose() {
            outbound.Close();
            State = WebSocketChannelState.Closed;
        }
    }

    sealed class Listener(WebSocketLoop loop, Uri address, string key) : IWebSocketListener {
        readonly Queue<IWebSocketChannel> waiting = new();

        public Uri Address => address;

        public void Offer(Channel channel) {
            channel.Open();
            waiting.Enqueue(channel);
        }

        public bool TryAccept(out IWebSocketChannel? channel) => waiting.TryDequeue(out channel);

        public void Pump() { }

        public void Dispose() {
            while (waiting.TryDequeue(out var channel)) {
                channel.Dispose();
            }

            loop.Forget(key);
        }
    }
}
