// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Sessions;

/// <summary>What role a session is playing.</summary>
/// <remarks>
///     <para>
///         Four names for three mechanisms, deliberately. <see cref="Host" /> and
///         <see cref="Offline" /> are <i>the same thing</i> — a server with a client attached to it —
///         and differ only in what the game means by it. That is the point rather than an
///         embarrassment: single-player is a one-player multiplayer game, it runs the same session,
///         replication and RPC code as a dedicated server, and there is no separate offline path to
///         rot between releases.
///     </para>
/// </remarks>
public enum SessionTopology : byte {
    /// <summary>Not started.</summary>
    None = 0,

    /// <summary>Dedicated server: hosts, does not play.</summary>
    Server = 1,

    /// <summary>Connects to somebody else's server.</summary>
    Client = 2,

    /// <summary>Hosts and plays. One process, both halves, a loopback between them.</summary>
    Host = 3,

    /// <summary>A host nobody else can reach. Single player, told the truth about itself.</summary>
    Offline = 4
}

/// <summary>Where a session is in its life.</summary>
public enum SessionState : byte {
    /// <summary>Not started.</summary>
    Stopped = 0,

    /// <summary>Started, and not yet ready — a client that has not finished its handshake.</summary>
    Starting = 1,

    /// <summary>Running. A server is listening; a client is in and has a player id.</summary>
    Running = 2,

    /// <summary>Shutting down.</summary>
    Stopping = 3
}
