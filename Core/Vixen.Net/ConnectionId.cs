// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Net;

/// <summary>
///     A connection, as the server numbers it.
/// </summary>
/// <remarks>
///     <para>
///         The server allocates these and every peer uses the server's number for a connection —
///         including the client whose connection it is, which learns its own id when the connection
///         completes. A client never invents one, which is the same rule
///         <c>NetworkId</c> follows for entities and for the same reason: two authorities numbering
///         the same thing is a desync waiting for a race to expose it.
///     </para>
///     <para>
///         <see cref="None" /> is the zero value, so a default-constructed field is not a valid
///         connection rather than being connection 0.
///     </para>
/// </remarks>
/// <param name="Value">The server-assigned number. Zero is <see cref="None" />.</param>
public readonly record struct ConnectionId(uint Value) {
    /// <summary>No connection. What a default-constructed <see cref="ConnectionId" /> is.</summary>
    public static ConnectionId None => default;

    /// <summary>Whether this names a connection at all.</summary>
    public bool IsValid => Value != 0;

    /// <inheritdoc />
    public override string ToString() =>
        Value == 0 ? "none" : string.Create(CultureInfo.InvariantCulture, $"#{Value}");
}
