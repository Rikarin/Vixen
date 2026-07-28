// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net;

/// <summary>Why a connection ended.</summary>
/// <remarks>
///     Reported from the point of view of the half that observes it, so the same disconnection is
///     <see cref="Requested" /> on the side that asked and <see cref="RemoteRequested" /> on the
///     side that was told. A game that reconnects on <see cref="Timeout" /> but not on
///     <see cref="Kicked" /> needs exactly that distinction, and a single "disconnected" event
///     without it is what makes reconnect logic guesswork.
/// </remarks>
public enum DisconnectReason : byte {
    /// <summary>This side asked for it — <c>StopClient</c>, or a server disconnecting a client.</summary>
    Requested = 0,

    /// <summary>The peer asked for it.</summary>
    RemoteRequested = 1,

    /// <summary>The server stopped listening, taking every connection with it.</summary>
    ServerStopped = 2,

    /// <summary>The server closed this connection specifically, and the client is the one told.</summary>
    Kicked = 3,

    /// <summary>Nothing was listening, or the listener refused the connection.</summary>
    ConnectionRefused = 4,

    /// <summary>The peer stopped answering.</summary>
    Timeout = 5,

    /// <summary>The transport itself failed — a socket error, a relay that went away.</summary>
    TransportError = 6
}
