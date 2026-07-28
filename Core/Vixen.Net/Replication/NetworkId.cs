// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;

namespace Vixen.Net.Replication;

/// <summary>The component that makes an entity one the network knows about.</summary>
/// <remarks>
///     <para>
///         <b>The server allocates these and clients never invent one.</b> A client that could name a
///         new entity could name one the server did not create, which is the shape of half the
///         cheating in a multiplayer game and all of the desyncs.
///     </para>
///     <para>
///         It is not an <c>Entity</c>. An entity handle is an index and a generation into one world's
///         arrays, and two machines' worlds have no reason to agree about either — the same tank is
///         entity 41 here and entity 900 there. The <see cref="NetworkId" /> is what they agree on,
///         and each side keeps its own map from it to its own handle.
///     </para>
/// </remarks>
/// <param name="Value">The number the server gave it. Zero is <see cref="None" />.</param>
[Component]
public readonly record struct NetworkId(uint Value) {
    /// <summary>Not a networked entity.</summary>
    public static NetworkId None => default;

    /// <summary>Whether this names a networked entity at all.</summary>
    public bool IsValid => Value != 0;

    /// <inheritdoc />
    public override string ToString() =>
        Value == 0 ? "not networked" : string.Create(CultureInfo.InvariantCulture, $"net {Value}");
}

/// <summary>Hands out <see cref="NetworkId" />s. Server-side, and the only thing that makes them.</summary>
public sealed class NetworkIdAllocator {
    uint next = 1;

    /// <summary>How many have been handed out.</summary>
    public uint Count => next - 1;

    /// <summary>Takes the next id.</summary>
    /// <returns>An id nothing else has had.</returns>
    /// <remarks>
    ///     Monotonic and never reused within a session. Reuse would let a stale packet about a
    ///     destroyed entity be applied to the entity that took its number, which is a bug that only
    ///     happens under packet loss and is therefore a bug nobody reproduces.
    /// </remarks>
    public NetworkId Next() => new(next++);
}
