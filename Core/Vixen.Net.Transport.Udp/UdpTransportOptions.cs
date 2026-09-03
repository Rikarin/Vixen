// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Net;

namespace Vixen.Net.Transport.Udp;

/// <summary>How a UDP transport behaves.</summary>
public sealed record UdpTransportOptions {
    /// <summary>Where the server half binds. Port zero lets the operating system choose.</summary>
    public IPEndPoint ListenEndPoint { get; init; } = new(IPAddress.Loopback, 0);

    /// <summary>Where the client half connects to.</summary>
    public IPEndPoint RemoteEndPoint { get; init; } = new(IPAddress.Loopback, 7777);

    /// <summary>The most clients the server half accepts.</summary>
    public int MaxConnections { get; init; } = 64;

    /// <summary>How long silence lasts before a connection is taken to be gone.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     How often to send a datagram that says nothing, when nothing else has been sent.
    /// </summary>
    /// <remarks>
    ///     Two jobs. It is how a peer notices the other one has stopped answering, and it is what
    ///     keeps a NAT mapping alive — a home router forgets an idle UDP mapping in about thirty
    ///     seconds, and a game that goes quiet for a menu comes back to find its packets going
    ///     nowhere.
    /// </remarks>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a client tries to connect before giving up.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How often a connection request is repeated while waiting.</summary>
    /// <remarks>
    ///     The handshake is the one exchange with no reliability layer under it yet, so it retries by
    ///     hand. A request that is lost would otherwise mean a client that waits for the full connect
    ///     timeout and then reports that nobody was listening, which is a lie about a dropped packet.
    /// </remarks>
    public TimeSpan ConnectRetryInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The shortest a retransmission timeout may be, whatever the round trip measures.</summary>
    public TimeSpan MinRetransmitTimeout { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>The longest it may be, so a bad measurement cannot stall a connection.</summary>
    public TimeSpan MaxRetransmitTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>What to assume the round trip is before anything has been measured.</summary>
    public TimeSpan InitialRetransmitTimeout { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    ///     How many unacknowledged datagrams one channel may have in flight before the oldest are
    ///     given up on.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A cap on memory rather than congestion control — that is
    ///         <see cref="InitialWindow" /> and the two around it. A peer that has stopped
    ///         acknowledging is about to time out anyway, and until it does this is what stops its
    ///         connection's queue from growing without bound.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Reaching it means a reliable message was not delivered and will not be sent
    ///         again</b>, and the one dropped is the <i>oldest</i> — precisely the one the peer's
    ///         ordered receiver is waiting on. It is counted now (<c>UdpTransport.AbandonedCount</c>);
    ///         it used to happen silently, with every other counter reading healthy.
    ///     </para>
    /// </remarks>
    public int MaxUnacknowledged { get; init; } = 1024;

    /// <summary>How many reliable datagrams may be in flight before anything has been measured.</summary>
    /// <remarks>
    ///     <para>
    ///         The congestion window's starting value: it opens additively as acknowledgements come
    ///         back and halves on a loss event, between <see cref="MinWindow" /> and
    ///         <see cref="MaxWindow" />. A datagram built while the window is full is not dropped and
    ///         not refused — it is held with its sequence already taken and goes out in order as the
    ///         window opens, so a burst is paced rather than lost.
    ///     </para>
    ///     <para>
    ///         Thirty-two is a tick's worth of reliable traffic at the rates this engine sends at,
    ///         which is the point: starting at one, as slow start would, rate-limits the first second
    ///         of every match to discover a number that can simply be stated. Unreliable channels are
    ///         never held back — see <c>UdpTransport.SendMessage</c>.
    ///     </para>
    /// </remarks>
    public int InitialWindow { get; init; } = 32;

    /// <summary>The smallest the congestion window may shrink to.</summary>
    /// <remarks>
    ///     Never zero, or a connection that lost a burst could never send the datagram whose
    ///     acknowledgement would reopen the window.
    /// </remarks>
    public int MinWindow { get; init; } = 2;

    /// <summary>The largest it may grow to.</summary>
    /// <remarks>
    ///     Held at <see cref="MaxUnacknowledged" />'s default, because a window wider than the memory
    ///     cap would put the cap — which abandons messages — in charge of pacing.
    /// </remarks>
    public int MaxWindow { get; init; } = 1024;
}
