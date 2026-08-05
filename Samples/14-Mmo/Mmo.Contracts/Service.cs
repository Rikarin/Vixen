// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Live;

namespace Vixen.Samples.Mmo.Contracts;

/// <summary>One character on the selection screen.</summary>
/// <remarks>
///     <para>
///         <b>The gate's shape, and the reason this is in Contracts rather than in the gate.</b> Doc
///         27's service plane is HTTPS and WSS, so what crosses it is JSON — and the client and the
///         gate have to agree about it exactly as much as the client and the realm agree about a
///         replicated struct.
///     </para>
///     <para>
///         ⚠ <b>No <c>PlayerId</c> anywhere near it.</b> A gameplay id is a session id widened and
///         means nothing between sessions; a character screen is answered before there is a session
///         at all. <see cref="PlayerKey" /> is what survives.
///     </para>
/// </remarks>
/// <param name="Key">Who they are, durably.</param>
/// <param name="Name">What they are called.</param>
/// <param name="Level">How far along.</param>
/// <param name="Specialisation">Their specialisation, by address.</param>
/// <param name="Map">Where they logged out, by address.</param>
public readonly record struct CharacterSummary(
    PlayerKey Key,
    string Name,
    int Level,
    string Specialisation,
    string Map
);

/// <summary>What the client asks for when it wants to play.</summary>
/// <param name="Character">Which character.</param>
/// <param name="Map">Which map, by address, or empty for wherever they logged out.</param>
public readonly record struct EnterWorldRequest(Guid Character, string Map);

/// <summary>Where to go, and what to say when you get there.</summary>
/// <remarks>
///     ⚠ <b>The ticket is opaque to the client and signed by the cluster.</b> A client that could
///     read it could edit it; a client that could mint one could put itself on any shard it liked.
/// </remarks>
/// <param name="Endpoint">The realm, as host and port.</param>
/// <param name="Ticket">What to present. Opaque, signed, and short-lived.</param>
/// <param name="Map">Which map it turned out to be.</param>
public readonly record struct EnterWorldGrant(string Endpoint, string Ticket, string Map);

/// <summary>How a queued player is doing, pushed over the WSS subscription rather than polled.</summary>
/// <remarks>
///     ⚠ <b>Pushed, because polling a queue is how a queue falls over.</b> Five hundred clients
///     asking "am I in yet" every second is five hundred requests a second the gate does not need to
///     serve; the subscription is one connection each and a message when something changes.
/// </remarks>
/// <param name="Queue">Which queue, by address.</param>
/// <param name="Position">Where they are in it, or zero once a match has formed.</param>
/// <param name="EstimatedSeconds">How long the fleet thinks, or zero when it will not guess.</param>
/// <param name="Formed">Whether a match is waiting for them to accept.</param>
public readonly record struct QueueStatus(string Queue, int Position, int EstimatedSeconds, bool Formed);
