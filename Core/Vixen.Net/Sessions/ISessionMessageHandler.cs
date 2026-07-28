// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Sessions;

/// <summary>Where the payloads a session does not understand itself are handed.</summary>
/// <remarks>
///     The session owns the handshake, the clock and the player list, and nothing else. Everything
///     the game sends — and, later, everything replication and RPC send — arrives here, addressed by
///     player rather than by connection, and only from a peer that finished its handshake.
/// </remarks>
public interface ISessionMessageHandler {
    /// <summary>A payload arrived from a peer that is in the session.</summary>
    /// <param name="from">
    ///     Who sent it. On a client this is <see cref="PlayerId.None" />, because the server is not a
    ///     player.
    /// </param>
    /// <param name="channel">The channel it came on.</param>
    /// <param name="payload">The bytes, valid until this call returns.</param>
    void OnMessage(PlayerId from, Channel channel, ReadOnlySpan<byte> payload);
}

/// <summary>Why a player is no longer in the session at all.</summary>
public enum PlayerLeaveReason : byte {
    /// <summary>They disconnected, and reconnection is not on.</summary>
    Disconnected = 0,

    /// <summary>They stopped answering.</summary>
    TimedOut = 1,

    /// <summary>The server removed them.</summary>
    Kicked = 2,

    /// <summary>They dropped, and did not come back before the window closed.</summary>
    ReconnectWindowExpired = 3,

    /// <summary>The session stopped underneath them.</summary>
    SessionStopped = 4
}
