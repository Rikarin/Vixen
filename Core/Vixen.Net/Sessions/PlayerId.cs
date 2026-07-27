// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Net.Sessions;

/// <summary>Who, as the session numbers them.</summary>
/// <remarks>
///     <para>
///         <b>Not a <see cref="ConnectionId" />, and the difference is the whole reason this type
///         exists.</b> A connection is a socket, or a slot in the local transport's table, and it
///         ends the moment a train goes into a tunnel. A player is who was holding it: the same
///         <see cref="PlayerId" /> comes back on the other side of the tunnel with their entities,
///         their ownership and their score, because the reconnect token they present says so.
///     </para>
///     <para>
///         Everything a game cares about is keyed by player. Everything the transport cares about is
///         keyed by connection. Conflating them is how "reconnect support" becomes a rewrite instead
///         of a feature.
///     </para>
/// </remarks>
/// <param name="Value">The number. Zero is <see cref="None" />.</param>
public readonly record struct PlayerId(uint Value) {
    /// <summary>Nobody.</summary>
    public static PlayerId None => default;

    /// <summary>Whether this names a player at all.</summary>
    public bool IsValid => Value != 0;

    /// <inheritdoc />
    public override string ToString() =>
        Value == 0 ? "nobody" : string.Create(CultureInfo.InvariantCulture, $"player {Value}");
}
