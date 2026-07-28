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
    User = 6
}
