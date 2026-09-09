// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Sessions;

/// <summary>The first byte of every packet: what kind of message this is.</summary>
/// <remarks>
///     <para>
///         One byte rather than a variable-length id, because there will never be enough of these to
///         need more and a fixed offset makes the receive path a jump table.
///         <see cref="User" /> is the door everything above the session goes through, and the
///         replication and RPC layers will divide the space behind it rather than adding members
///         here.
///     </para>
///     <para>
///         The numbers are part of the wire format: a value is never reused for something else, and
///         a client and server that disagree about what a number means are exactly what the protocol
///         version in the handshake exists to catch.
///     </para>
/// </remarks>
enum SystemMessage : byte {
    /// <summary>Nothing. What a zero byte decodes to, and never sent.</summary>
    None = 0,

    /// <summary>Client to server: protocol version, content hash, credentials, reconnect token.</summary>
    ConnectRequest = 1,

    /// <summary>Server to client: your player id, the tick, and the token to come back with.</summary>
    ConnectAccepted = 2,

    /// <summary>Server to client: why not.</summary>
    ConnectRejected = 3,

    /// <summary>Either way: answer this so I can measure the trip.</summary>
    Ping = 4,

    /// <summary>The answer, carrying the sender's tick.</summary>
    Pong = 5,

    /// <summary>Everything above the session.</summary>
    User = 6,

    /// <summary>
    ///     Either way: what I did not receive of what you sent me — the sender's inbound counters,
    ///     which are the receiver's outbound loss.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A new value rather than a longer <see cref="Pong" />, and the difference is
    ///     compatibility.</b> Both dispatch switches end in a <c>default:</c> that drops an unknown
    ///     message without comment, so a peer that has never heard of this ignores it and loses only
    ///     the measurement. Lengthening <see cref="Pong" /> breaks the other way and breaks silently:
    ///     its fields are read in one <c>&amp;&amp;</c> chain with <c>Clock.Synchronize</c> inside it
    ///     and <c>PacketReader</c>'s first failure is sticky, so a lengthened read of an older peer's
    ///     <see cref="Pong" /> loses tick synchronisation — a symptom that shows up as interpolation
    ///     drifting and has no error channel at all. "A value is never reused" is a rule about
    ///     recycling a number, not about adding one.
    /// </remarks>
    LinkReport = 7
}
