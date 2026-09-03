// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Time;

namespace Vixen.Net.Sessions;

/// <summary>Why a server refused to let somebody in.</summary>
/// <remarks>
///     Refusals are specific, and the client is told which one. "Could not connect" is the error
///     message that generates a support ticket; "this build is 1.4.2 and the server is running
///     1.4.3" is the one that does not.
/// </remarks>
public enum SessionRejectReason : byte {
    /// <summary>No reason given.</summary>
    None = 0,

    /// <summary>The client and server do not speak the same protocol version.</summary>
    ProtocolMismatch = 1,

    /// <summary>They speak the same protocol but do not have the same content built.</summary>
    ContentMismatch = 2,

    /// <summary>The server has as many players as it takes.</summary>
    ServerFull = 3,

    /// <summary>The authenticator said no.</summary>
    AuthenticationFailed = 4,

    /// <summary>The authenticator never said anything.</summary>
    AuthenticationTimedOut = 5,

    /// <summary>The handshake was malformed, or arrived twice on one connection.</summary>
    BadHandshake = 6
}

/// <summary>How a session behaves.</summary>
public sealed record SessionOptions {
    /// <summary>
    ///     The version of the wire protocol and of the game's own messages. A client whose number
    ///     differs is refused at the handshake with <see cref="SessionRejectReason.ProtocolMismatch" />
    ///     rather than being allowed in to misinterpret packets.
    /// </summary>
    public uint ProtocolVersion { get; init; } = 1;

    /// <summary>
    ///     A hash of the content build both sides are running.
    /// </summary>
    /// <remarks>
    ///     The deterministic content build makes this cheap and meaningful: two machines that built
    ///     the same content get the same number, so a mismatch is exactly "you are not running the
    ///     same game as this server", caught in the handshake rather than as an entity referring to a
    ///     prefab id that does not exist here.
    /// </remarks>
    public ulong ContentHash { get; init; }

    /// <summary>The most players allowed at once. Reconnecting players still hold their slot.</summary>
    public int MaxPlayers { get; init; } = 16;

    /// <summary>How often the simulation ticks.</summary>
    public TickRate TickRate { get; init; } = TickRate.Default;

    /// <summary>How long a dropped player's slot, entities and id are held for them.</summary>
    /// <remarks>Zero means a disconnection is final and the player is gone at once.</remarks>
    public TimeSpan ReconnectWindow { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long an authenticator may take before the connection is refused.</summary>
    public TimeSpan AuthenticationTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How often each side measures the round trip.</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long silence from a peer lasts before the connection is treated as gone.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>What a client sends to be let in. Opaque to the session.</summary>
    public byte[] AuthenticationPayload { get; init; } = [];

    /// <summary>The most bytes a client's authentication payload may be.</summary>
    public int MaxAuthenticationPayloadBytes { get; init; } = 512;

    /// <summary>What one peer's own messages may cost, in bytes a second. Zero is no budget at all.</summary>
    /// <remarks>
    ///     <para>
    ///         A token bucket refilled from the <c>elapsed</c> handed to
    ///         <see cref="NetworkSession.Update" />, so it is spent against the session's clock and
    ///         not against the wall's. It covers what a game sends through
    ///         <see cref="NetworkSession.SendToPlayer(PlayerId, ReadOnlySpan{byte}, Channel, int)" />
    ///         and the two beside it, per player and per direction — not replication, which has
    ///         <c>BandwidthBudget</c> of its own one layer down, and not the session's own pings and
    ///         handshakes, which are what tell a struggling connection from a dead one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Zero by default, which is unmetered.</b> A budget that arrived switched on would
    ///         change what an existing game sends, and a message silently not sent is the worst
    ///         failure this stack has. A game opts in and then reads <c>ShedCount</c>.
    ///     </para>
    /// </remarks>
    public int BytesPerSecondPerPlayer { get; init; }

    /// <summary>How much of the budget may be saved up and spent at once.</summary>
    /// <remarks>
    ///     The bucket's depth. Traffic here is bursty by nature — a round starting, a scene loading —
    ///     so a bucket that could not hold a burst would shed the one thing the budget exists to let
    ///     through.
    /// </remarks>
    public int BurstBytes { get; init; } = 64 * 1024;

    /// <summary>How much of the bucket only important messages may spend.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is the shedding, and it is a reserve rather than an ordering.</b> With no queue
    ///         at this layer there is nothing to sort: a send either goes now or does not go at all.
    ///         So the bucket keeps a floor that a message below
    ///         <see cref="ReservedPriority" /> may not draw on, and chatter stops while the traffic
    ///         that matters keeps going — which is the behaviour "priority shedding" names.
    ///     </para>
    ///     <para>A quarter by default. Zero disables the reserve and leaves a plain bucket.</para>
    /// </remarks>
    public double ReservedFraction { get; init; } = 0.25;

    /// <summary>The priority a message needs before it may spend the reserve.</summary>
    /// <remarks>
    ///     Higher is more important, which is the convention <c>[Replicated(Priority = …)]</c> already
    ///     uses — a repository with two directions of priority has one direction and one trap. The
    ///     default send priority is zero, so by default nothing reaches the reserve and a game opts
    ///     traffic in to it explicitly.
    /// </remarks>
    public int ReservedPriority { get; init; } = 1;
}
