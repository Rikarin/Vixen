// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Transport.WebSocket;

/// <summary>Where a channel has got to.</summary>
public enum WebSocketChannelState : byte {
    /// <summary>The handshake has not finished.</summary>
    Connecting = 0,

    /// <summary>Messages can be sent and received.</summary>
    Open = 1,

    /// <summary>It is over, whether it opened or not.</summary>
    Closed = 2
}

/// <summary>One WebSocket, as the transport needs it.</summary>
/// <remarks>
///     <para>
///         The seam, and it is the same bargain <c>IDatagramSocket</c> makes in the UDP transport:
///         everything that could be got wrong lives above it and is tested over an in-memory
///         implementation where a message arrives when the receiver polls and every run is the same
///         run. What is left here is a handshake and a frame codec, which is somebody else's tested
///         code.
///     </para>
///     <para>
///         <b>Message-oriented and polled, where the real thing is stream-oriented and
///         asynchronous.</b> WebSocket already gives message boundaries, so nothing above needs to
///         re-invent them; the polling is what keeps the transport contract's promise that nothing is
///         delivered outside <c>Poll</c>. A real implementation runs its own I/O and queues what
///         arrives — the threads are inside this interface, and the layer above never sees one.
///     </para>
/// </remarks>
public interface IWebSocketChannel : IDisposable {
    /// <summary>Where it has got to.</summary>
    WebSocketChannelState State { get; }

    /// <summary>Queues a message.</summary>
    /// <param name="message">The bytes, which the channel copies.</param>
    void Send(ReadOnlySpan<byte> message);

    /// <summary>Takes the next message that arrived.</summary>
    /// <param name="buffer">Where to put it.</param>
    /// <param name="length">How long it was.</param>
    /// <returns>Whether there was one. False also when the buffer is too small, and it is dropped.</returns>
    bool TryReceive(Span<byte> buffer, out int length);

    /// <summary>Starts closing, politely.</summary>
    void Close();

    /// <summary>Moves whatever this implementation moves on a poll.</summary>
    /// <remarks>
    ///     A no-op for an implementation whose I/O runs on its own; the hook exists for the in-memory
    ///     one, where "the network runs" has to mean something and a background thread would put the
    ///     determinism back where it came from.
    /// </remarks>
    void Pump();
}

/// <summary>Something accepting WebSockets.</summary>
public interface IWebSocketListener : IDisposable {
    /// <summary>Where it is listening, resolved — a port of zero in the request becomes a real one.</summary>
    Uri Address { get; }

    /// <summary>Takes the next channel somebody opened.</summary>
    /// <param name="channel">It, if there was one.</param>
    /// <returns>Whether there was.</returns>
    bool TryAccept(out IWebSocketChannel? channel);

    /// <summary>Moves whatever this implementation moves on a poll.</summary>
    void Pump();
}

/// <summary>Makes listeners and channels.</summary>
/// <remarks>
///     The one thing a test replaces. <c>SystemWebSocketFactory</c> is the real one; the conformance
///     suite runs against an in-memory pair that never touches a socket.
/// </remarks>
public interface IWebSocketFactory {
    /// <summary>Starts listening.</summary>
    /// <param name="address">Where. A port of zero asks the operating system to choose.</param>
    /// <returns>The listener.</returns>
    IWebSocketListener Listen(Uri address);

    /// <summary>Opens a channel.</summary>
    /// <param name="address">Where to.</param>
    /// <returns>
    ///     A channel that is <see cref="WebSocketChannelState.Connecting" /> until it is not. A
    ///     connection nobody answers ends up <see cref="WebSocketChannelState.Closed" /> rather than
    ///     throwing — being refused is an ordinary thing for a client to be.
    /// </returns>
    IWebSocketChannel Connect(Uri address);
}
